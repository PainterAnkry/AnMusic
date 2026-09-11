using System.IO;
using System.Net;
using System.Text.Json;
using AnMusic.Models;

namespace AnMusic.Services.ListenTogether;

/// <summary>一起听播放指令类型。</summary>
public enum ListenTogetherCommandType
{
    Play,
    Pause,
    Seek,
    ChangeTrack
}

/// <summary>一起听播放指令：仅同步播放意图，不携带音频数据。</summary>
public sealed class ListenTogetherCommand
{
    /// <summary>序列号，单调递增，客户端按此去重/补齐。</summary>
    public long Seq { get; set; }

    /// <summary>指令类型：play / pause / seek / changeTrack。</summary>
    public string Type { get; set; } = "play";

    /// <summary>房主发送指令时的播放位置（秒）；用于 seek/play 同步。</summary>
    public double PositionSeconds { get; set; }

    /// <summary>指令发送时刻（Unix 毫秒），成员端按此补偿网络延迟。</summary>
    public long SentAtMs { get; set; }

    /// <summary>changeTrack 指令时携带的曲目信息。</summary>
    public ListenTogetherTrackInfo? Track { get; set; }

    /// <summary>是否正在播放（changeTrack 指令使用，决定加载后是否自动播放）。</summary>
    public bool IsPlaying { get; set; } = true;

    public static string Serialize(ListenTogetherCommand cmd) =>
        JsonSerializer.Serialize(cmd, ListenTogetherJsonOptions.Default);

    public static ListenTogetherCommand? Deserialize(string json) =>
        string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<ListenTogetherCommand>(json, ListenTogetherJsonOptions.Default);
}

/// <summary>用于在指令中传输的曲目元数据（不含音频流，仅用于成员端定位/重建 Track）。</summary>
public sealed class ListenTogetherTrackInfo
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = "未知艺术家";
    public string Album { get; set; } = string.Empty;
    public double DurationSeconds { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string CoverUrl { get; set; } = string.Empty;
    public string? CoverKey { get; set; }
    public string ProviderId { get; set; } = "local-file";
    public string SourceUrl { get; set; } = string.Empty;

    public static ListenTogetherTrackInfo FromTrack(Track t) => new()
    {
        Id = t.Id,
        Title = t.Title,
        Artist = t.Artist,
        Album = t.Album,
        DurationSeconds = t.Duration.TotalSeconds,
        // 只传本地曲目的路径：在线曲目传过去的会是房主机器上的播放缓冲路径，
        // 成员端会误以为本地已有文件而跳过缓冲，结果播不出来
        FilePath = t.IsLocalTrack ? t.FilePath ?? string.Empty : string.Empty,
        CoverUrl = t.CoverUrl ?? string.Empty,
        CoverKey = t.CoverKey,
        ProviderId = t.ProviderId ?? "local-file",
        SourceUrl = t.SourceUrl ?? string.Empty
    };

    public Track ToTrack() => new()
    {
        Id = Id,
        Title = Title,
        Artist = Artist,
        Album = Album,
        Duration = TimeSpan.FromSeconds(DurationSeconds),
        FilePath = FilePath,
        CoverUrl = CoverUrl,
        CoverKey = CoverKey,
        ProviderId = string.IsNullOrEmpty(ProviderId) ? "local-file" : ProviderId,
        SourceUrl = SourceUrl
    };
}

/// <summary>房间加入响应：成员ID + 当前播放状态快照。</summary>
public sealed class ListenTogetherJoinResponse
{
    public string MemberId { get; set; } = string.Empty;
    public string MemberName { get; set; } = string.Empty;
    public List<string> Members { get; set; } = [];
    public ListenTogetherTrackInfo? CurrentTrack { get; set; }
    public double PositionSeconds { get; set; }
    public bool IsPlaying { get; set; }
    public long LastSeq { get; set; }
}

/// <summary>长轮询响应：自 lastSeq 之后的所有指令（可能为空，表示超时）。</summary>
public sealed class ListenTogetherPollResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public List<ListenTogetherCommand> Commands { get; set; } = [];
    public List<string> Members { get; set; } = [];
}

internal static class ListenTogetherJsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false
    };
}

/// <summary>
/// 房主端轻量级 HTTP 服务器：基于 HttpListener + 长轮询。
/// 维护房间成员（最多 4 人含房主）、播放状态快照与指令队列。
/// </summary>
public sealed class ListenTogetherHost : IAsyncDisposable
{
    private const int MaxMembers = 4;
    private const int PollTimeoutMs = 30_000;

    private HttpListener _listener = new();
    private readonly object _gate = new();
    private readonly List<Member> _members = [];
    private readonly List<ListenTogetherCommand> _commands = [];
    private readonly List<TaskCompletionSource<ListenTogetherCommand[]>> _waiters = [];
    private long _seqCounter;

    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    /// <summary>房间ID（短码，用于邀请链接）。</summary>
    public string RoomId { get; }

    /// <summary>监听端口。</summary>
    public int Port { get; }

    /// <summary>
    /// 实际对外通告的主机地址：优先局域网 IP（其它设备才连得上），
    /// 局域网地址绑定失败时退回 localhost（仅同机可用）。
    /// </summary>
    public string Host { get; private set; } = "localhost";

    /// <summary>房主昵称（房主也是成员之一，占用第一个名额）。</summary>
    public string HostName { get; }

    /// <summary>邀请链接：anmusic://jointogether/{host}:{port}/{roomId}。</summary>
    public string InviteLink => $"anmusic://jointogether/{Host}:{Port}/{RoomId}";

    /// <summary>邀请链接是否为局域网可达（false = 只绑到了 localhost，其它设备连不上）。</summary>
    public bool IsLanReachable => !IsLoopback(Host);

    /// <summary>房主端构造时指定的绑定地址（空 = 自动探测局域网 IP）。</summary>
    private readonly string? _preferredBind;

    /// <summary>房间当前人数（含房主）。</summary>
    public int MemberCount
    {
        get
        {
            lock (_gate) return _members.Count;
        }
    }

    /// <summary>房间成员名列表（含房主，第一项为房主）。</summary>
    public IReadOnlyList<string> MemberNames
    {
        get
        {
            lock (_gate) return _members.Select(m => m.Name).ToArray();
        }
    }

    /// <summary>当前播放曲目快照。</summary>
    public ListenTogetherTrackInfo? CurrentTrackSnapshot { get; private set; }

    /// <summary>当前播放位置快照（秒，最近一次指令时刻）。</summary>
    public double CurrentPositionSeconds { get; private set; }

    /// <summary>当前是否在播放（快照）。</summary>
    public bool CurrentIsPlaying { get; private set; }

    public ListenTogetherHost(string roomId, int port, string hostName, string? bindAddress = null)
    {
        RoomId = roomId;
        Port = port;
        HostName = string.IsNullOrWhiteSpace(hostName) ? "房主" : hostName;
        _preferredBind = string.IsNullOrWhiteSpace(bindAddress) ? null : bindAddress;
    }

    /// <summary>
    /// 启动 HTTP 监听。返回是否成功。
    /// </summary>
    /// <remarks>
    /// 依次尝试：用户指定地址 → 局域网 IPv4 → localhost。
    /// 绑到局域网地址才能让其它设备加入（手机 ↔ 电脑互听）；
    /// Windows 上非 localhost 前缀可能因缺少 URL ACL 而 Access Denied，
    /// 此时自动退回 localhost，功能降级为同机可用而不是直接失败。
    /// </remarks>
    public async Task<bool> StartAsync()
    {
        var started = false;

        foreach (var candidate in BindCandidates())
        {
            // 每次尝试都用全新的监听器：Start 失败后实例状态不可靠，复用会留下脏前缀
            var listener = new HttpListener();
            try
            {
                listener.Prefixes.Clear();
                listener.Prefixes.Add($"http://{candidate}:{Port}/room/{RoomId}/");
                listener.Start();

                _listener = listener;
                Host = candidate;
                started = true;
                break;
            }
            catch (Exception)
            {
                try { listener.Close(); } catch { }
            }
        }

        if (!started) return false;

        lock (_gate)
        {
            // 房主作为第一个成员（memberId 固定为 "host"）
            _members.Clear();
            _members.Add(new Member { Id = "host", Name = HostName, LastSeenMs = Environment.TickCount64 });
            _commands.Clear();
            _waiters.Clear();
            _seqCounter = 0;
        }

        _cts = new CancellationTokenSource();
        _loopTask = AcceptLoopAsync(_cts.Token);
        await Task.CompletedTask;
        return true;
    }

    /// <summary>候选绑定地址（按优先级）。</summary>
    private IEnumerable<string> BindCandidates()
    {
        if (_preferredBind is { } preferred) yield return preferred;

        foreach (var ip in LanAddresses()) yield return ip;

        yield return "localhost";
    }

    /// <summary>本机可用的局域网 IPv4 地址（排除回环）。</summary>
    private static IEnumerable<string> LanAddresses()
    {
        System.Net.NetworkInformation.NetworkInterface[] interfaces;
        try
        {
            interfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
        }
        catch
        {
            yield break;
        }

        foreach (var ni in interfaces)
        {
            if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;

            System.Net.NetworkInformation.UnicastIPAddressInformationCollection addrs;
            try { addrs = ni.GetIPProperties().UnicastAddresses; }
            catch { continue; }

            foreach (var addr in addrs)
            {
                if (addr.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                if (System.Net.IPAddress.IsLoopback(addr.Address)) continue;
                yield return addr.Address.ToString();
            }
        }
    }

    private static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (System.Net.IPAddress.TryParse(host, out var ip) && System.Net.IPAddress.IsLoopback(ip));

    /// <summary>停止服务器并清理等待中的长轮询。</summary>
    public async Task StopAsync()
    {
        if (_cts is { } cts)
        {
            cts.Cancel();
            try { _listener.Stop(); } catch { /* 忽略 */ }
        }

        lock (_gate)
        {
            foreach (var w in _waiters)
            {
                try { w.TrySetResult(Array.Empty<ListenTogetherCommand>()); } catch { }
            }
            _waiters.Clear();
            _members.Clear();
            _commands.Clear();
        }

        if (_loopTask is { } loop)
        {
            try { await loop; } catch { /* 忽略 */ }
        }
        _cts?.Dispose();
        _cts = null;
        _loopTask = null;
    }

    /// <summary>房主直接入队一条播放指令（不经 HTTP），并通知所有等待的长轮询。</summary>
    public void EnqueueCommand(ListenTogetherCommandType type, Track? track = null,
        double positionSeconds = 0, bool isPlaying = true)
    {
        var cmd = new ListenTogetherCommand
        {
            Seq = Interlocked.Increment(ref _seqCounter),
            Type = type switch
            {
                ListenTogetherCommandType.Play => "play",
                ListenTogetherCommandType.Pause => "pause",
                ListenTogetherCommandType.Seek => "seek",
                ListenTogetherCommandType.ChangeTrack => "changeTrack",
                _ => "play"
            },
            PositionSeconds = positionSeconds,
            SentAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Track = track is null ? null : ListenTogetherTrackInfo.FromTrack(track),
            IsPlaying = isPlaying
        };

        TaskCompletionSource<ListenTogetherCommand[]>[] toRelease;
        lock (_gate)
        {
            // 维护播放状态快照
            switch (type)
            {
                case ListenTogetherCommandType.Play:
                    CurrentIsPlaying = true;
                    CurrentPositionSeconds = positionSeconds;
                    break;
                case ListenTogetherCommandType.Pause:
                    CurrentIsPlaying = false;
                    CurrentPositionSeconds = positionSeconds;
                    break;
                case ListenTogetherCommandType.Seek:
                    CurrentPositionSeconds = positionSeconds;
                    break;
                case ListenTogetherCommandType.ChangeTrack:
                    CurrentTrackSnapshot = track is null ? null : ListenTogetherTrackInfo.FromTrack(track);
                    CurrentPositionSeconds = positionSeconds;
                    CurrentIsPlaying = isPlaying;
                    break;
            }
            _commands.Add(cmd);
            // 限制队列长度，避免无界增长
            if (_commands.Count > 200)
                _commands.RemoveRange(0, _commands.Count - 200);

            toRelease = _waiters.ToArray();
            _waiters.Clear();
        }

        foreach (var w in toRelease)
        {
            try { w.TrySetResult(new[] { cmd }); } catch { }
        }
    }

    /// <summary>房间是否已满（含房主）。</summary>
    public bool IsFull
    {
        get
        {
            lock (_gate) return _members.Count >= MaxMembers;
        }
    }

    /// <summary>从邀请链接解析主机/端口/房间ID。</summary>
    public static bool TryParseInviteLink(string link, out string host, out int port, out string roomId)
    {
        host = "localhost";
        port = 0;
        roomId = "";
        if (string.IsNullOrWhiteSpace(link)) return false;
        link = link.Trim();

        // anmusic://jointogether/{host}:{port}/{roomId}
        const string scheme = "anmusic://jointogether/";
        if (link.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            var rest = link[scheme.Length..].TrimEnd('/');
            var slash = rest.IndexOf('/');
            if (slash <= 0) return false;
            var hostPort = rest[..slash];
            roomId = rest[(slash + 1)..];
            var colon = hostPort.LastIndexOf(':');
            if (colon <= 0) return false;
            host = hostPort[..colon];
            return int.TryParse(hostPort[(colon + 1)..], out port) && !string.IsNullOrEmpty(roomId);
        }

        // 兼容 http://host:port/room/roomId/
        if (link.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(link);
            host = uri.Host;
            port = uri.Port;
            var seg = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (seg.Length >= 2 && seg[0] == "room") { roomId = seg[1]; return true; }
        }
        return false;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
            _ = HandleAsync(ctx, ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var req = ctx.Request;
        var resp = ctx.Response;
        resp.ContentEncoding = System.Text.Encoding.UTF8;

        try
        {
            if (req.Url is null) { resp.StatusCode = 400; return; }
            // /room/{roomId}/{action}
            var seg = req.Url.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (seg.Length < 3 || seg[0] != "room" || seg[1] != RoomId)
            {
                resp.StatusCode = 404;
                return;
            }
            var action = seg[2];

            switch ((req.HttpMethod, action))
            {
                case ("POST", "join"): await HandleJoinAsync(req, resp); break;
                case ("GET", "poll"): await HandlePollAsync(req, resp, ct); break;
                case ("POST", "command"): await HandleCommandAsync(req, resp); break;
                default: resp.StatusCode = 404; break;
            }
        }
        catch (Exception)
        {
            try { resp.StatusCode = 500; } catch { }
        }
        finally
        {
            try { resp.Close(); } catch { }
        }
    }

    private async Task HandleJoinAsync(HttpListenerRequest req, HttpListenerResponse resp)
    {
        using var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? System.Text.Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        string? memberName = null;
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                    memberName = n.GetString();
            }
            catch { /* 解析失败时回退为默认名称 */ }
        }
        memberName = string.IsNullOrWhiteSpace(memberName) ? "成员" : memberName;

        ListenTogetherJoinResponse join;
        lock (_gate)
        {
            if (_members.Count >= MaxMembers)
            {
                resp.StatusCode = 409;
                resp.ContentType = "application/json";
                resp.OutputStream.Write(System.Text.Encoding.UTF8.GetBytes("{\"error\":\"room full\"}"));
                return;
            }
            var id = Guid.NewGuid().ToString("N")[..8];
            _members.Add(new Member { Id = id, Name = memberName, LastSeenMs = Environment.TickCount64 });
            join = new ListenTogetherJoinResponse
            {
                MemberId = id,
                MemberName = memberName,
                Members = _members.Select(m => m.Name).ToList(),
                CurrentTrack = CurrentTrackSnapshot,
                PositionSeconds = CurrentPositionSeconds,
                IsPlaying = CurrentIsPlaying,
                LastSeq = _commands.Count == 0 ? 0 : _commands[^1].Seq
            };
        }

        // 新成员入室 → 房主收到一条"成员变更"的空指令唤醒等待者
        NotifyWaitersEmpty();
        resp.ContentType = "application/json";
        var json = JsonSerializer.Serialize(join, ListenTogetherJsonOptions.Default);
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        resp.ContentLength64 = bytes.Length;
        resp.OutputStream.Write(bytes);
    }

    private async Task HandlePollAsync(HttpListenerRequest req, HttpListenerResponse resp, CancellationToken ct)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url?.Query ?? "");
        var memberId = query["memberId"] ?? "";
        var lastSeqStr = query["lastSeq"] ?? "";
        long.TryParse(lastSeqStr, out var lastSeq);

        if (string.IsNullOrEmpty(memberId))
        {
            resp.StatusCode = 400;
            return;
        }

        // 第一阶段：在锁内做身份校验、心跳刷新、立即返回已有未读指令；否则登记 waiter
        TaskCompletionSource<ListenTogetherCommand[]>? tcs;
        lock (_gate)
        {
            var m = _members.FirstOrDefault(x => x.Id == memberId);
            if (m is null)
            {
                resp.StatusCode = 403;
                resp.ContentType = "application/json";
                resp.OutputStream.Write(System.Text.Encoding.UTF8.GetBytes("{\"error\":\"not a member\"}"));
                return;
            }
            m.LastSeenMs = Environment.TickCount64;

            // 清理超过 90s 未心跳的成员（房主不会被清理）
            var now = Environment.TickCount64;
            for (int i = _members.Count - 1; i >= 0; i--)
            {
                if (_members[i].Id != "host" && now - _members[i].LastSeenMs > 90_000)
                    _members.RemoveAt(i);
            }

            // 立即返回已有但未读的指令
            var pending = _commands.Where(c => c.Seq > lastSeq).ToArray();
            if (pending.Length > 0)
            {
                var pr = new ListenTogetherPollResponse
                {
                    Ok = true,
                    Commands = pending.ToList(),
                    Members = _members.Select(m => m.Name).ToList()
                };
                WriteJson(resp, pr);
                return;
            }

            // 没有新指令 → 登记一个等待者，在锁外等待被唤醒或超时
            tcs = new TaskCompletionSource<ListenTogetherCommand[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add(tcs);
        }

        // 第二阶段：锁外等待 30 秒超时或被 EnqueueCommand 唤醒
        var timeoutTask = Task.Delay(PollTimeoutMs, ct);
        var done = await Task.WhenAny(tcs.Task, timeoutTask);
        lock (_gate) { _waiters.Remove(tcs); }
        try { tcs.TrySetResult(Array.Empty<ListenTogetherCommand>()); } catch { }

        ListenTogetherCommand[] cmds = done == tcs.Task && tcs.Task.IsCompleted
            ? await tcs.Task
            : Array.Empty<ListenTogetherCommand>();

        // 超时/被唤醒均返回最新成员列表，便于房主看到当前在线人数
        ListenTogetherPollResponse pr2;
        lock (_gate)
        {
            pr2 = new ListenTogetherPollResponse
            {
                Ok = true,
                Commands = cmds.ToList(),
                Members = _members.Select(m => m.Name).ToList()            };
        }
        WriteJson(resp, pr2);
    }

    private async Task HandleCommandAsync(HttpListenerRequest req, HttpListenerResponse resp)
    {
        // 房主直接调用 EnqueueCommand，不走 HTTP；这里保留接口供调试/扩展。
        using var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? System.Text.Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        var cmd = ListenTogetherCommand.Deserialize(body);
        if (cmd is null) { resp.StatusCode = 400; return; }

        lock (_gate)
        {
            cmd.Seq = ++_seqCounter;
            _commands.Add(cmd);
            if (_commands.Count > 200)
                _commands.RemoveRange(0, _commands.Count - 200);
            foreach (var w in _waiters) { try { w.TrySetResult(new[] { cmd }); } catch { } }
            _waiters.Clear();
        }

        resp.ContentType = "application/json";
        var bytes = System.Text.Encoding.UTF8.GetBytes("{\"ok\":true}");
        resp.ContentLength64 = bytes.Length;
        resp.OutputStream.Write(bytes);
    }

    private void NotifyWaitersEmpty()
    {
        // 不发送指令，只唤醒等待者让其重新查询成员列表
        lock (_gate)
        {
            var ws = _waiters.ToArray();
            _waiters.Clear();
            foreach (var w in ws) { try { w.TrySetResult(Array.Empty<ListenTogetherCommand>()); } catch { } }
        }
    }

    private static void WriteJson<T>(HttpListenerResponse resp, T data)
    {
        resp.ContentType = "application/json";
        var json = JsonSerializer.Serialize(data, ListenTogetherJsonOptions.Default);
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        resp.ContentLength64 = bytes.Length;
        resp.OutputStream.Write(bytes);
    }

    /// <summary>简易查询字符串解析：避免依赖 System.Web.HttpUtility。</summary>
    private static Dictionary<string, string> ParseQueryString(string query)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query)) return dict;
        var q = query.TrimStart('?');
        if (q.Length == 0) return dict;
        foreach (var pair in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            string key, val;
            if (eq < 0) { key = pair; val = ""; }
            else { key = pair[..eq]; val = pair[(eq + 1)..]; }
            key = Uri.UnescapeDataString(key);
            val = Uri.UnescapeDataString(val);
            dict[key] = val;
        }
        return dict;
    }

    private sealed class Member
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public long LastSeenMs { get; set; }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
