using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace AnMusic.Services.ListenTogether;

/// <summary>
/// 成员端：通过 HttpClient 连接房主服务器，长轮询获取播放指令并对外抛出事件。
/// 调用方（ViewModel）订阅 <see cref="CommandReceived"/> 与 <see cref="MembersChanged"/>，
/// 在回调中把指令同步到本地播放器。
/// </summary>
public sealed class ListenTogetherClient : IAsyncDisposable
{
    private static readonly HttpClient SharedClient = new()
    {
        Timeout = TimeSpan.FromSeconds(35) // 略大于服务器 30s 长轮询超时
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _pollTask;

    /// <summary>成员ID（加入成功后由服务器分配）。</summary>
    public string MemberId { get; private set; } = string.Empty;

    /// <summary>成员昵称。</summary>
    public string MemberName { get; private set; } = string.Empty;

    /// <summary>房主基础 URL，形如 http://host:port/room/roomId/。</summary>
    public string BaseUrl { get; private set; } = string.Empty;

    /// <summary>当前已确认的最大 seq，用于断点续拉。</summary>
    public long LastSeq { get; private set; }

    /// <summary>是否已成功加入房间。</summary>
    public bool IsConnected { get; private set; }

    /// <summary>初始加入快照：包含当前曲目/位置/播放状态，用于成员端建立同步基线。</summary>
    public ListenTogetherJoinResponse? Snapshot { get; private set; }

    /// <summary>收到新的播放指令（参数：所有未读指令）。</summary>
    public event Action<IReadOnlyList<ListenTogetherCommand>>? CommandReceived;

    /// <summary>成员列表变化（房主侧也会通过快照/轮询响应驱动）。</summary>
    public event Action<IReadOnlyList<string>>? MembersChanged;

    /// <summary>连接异常或被房主踢出时触发，参数为友好提示。</summary>
    public event Action<string>? ConnectionLost;

    /// <summary>
    /// 加入房间：POST /join 拿到 member_id 与初始快照，然后启动长轮询。
    /// </summary>
    public async Task<bool> JoinAsync(string baseUrl, string memberName, CancellationToken ct = default)
    {
        BaseUrl = baseUrl.TrimEnd('/') + '/';
        MemberName = string.IsNullOrWhiteSpace(memberName) ? "成员" : memberName;

        try
        {
            var joinUrl = BaseUrl + "join";
            var payload = JsonSerializer.Serialize(new { name = MemberName }, ListenTogetherJsonOptions.Default);
            var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = await SharedClient.PostAsync(joinUrl, content, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                ConnectionLost?.Invoke("房间已满（最多 4 人）");
                return false;
            }
            if (!resp.IsSuccessStatusCode)
            {
                ConnectionLost?.Invoke($"加入失败: HTTP {(int)resp.StatusCode}");
                return false;
            }
            var json = await resp.Content.ReadAsStringAsync(ct);
            var snap = JsonSerializer.Deserialize<ListenTogetherJoinResponse>(json, ListenTogetherJsonOptions.Default);
            if (snap is null) { ConnectionLost?.Invoke("加入失败: 响应解析失败"); return false; }

            Snapshot = snap;
            MemberId = snap.MemberId;
            LastSeq = snap.LastSeq;
            IsConnected = true;
            MembersChanged?.Invoke(snap.Members);

            // 启动后台长轮询
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _pollTask = PollLoopAsync(_cts.Token);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            ConnectionLost?.Invoke($"加入异常: {ex.Message}");
            return false;
        }
    }

    /// <summary>主动离开房间：停止轮询即可，房主侧通过心跳超时清理。</summary>
    public async Task LeaveAsync()
    {
        IsConnected = false;
        if (_cts is { } cts)
        {
            try { cts.Cancel(); } catch { }
        }
        if (_pollTask is { } t)
        {
            try { await t; } catch { /* 忽略 */ }
        }
        _cts?.Dispose();
        _cts = null;
        _pollTask = null;
        MemberId = string.Empty;
        LastSeq = 0;
        Snapshot = null;
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && IsConnected)
        {
            try
            {
                var url = $"{BaseUrl}poll?memberId={Uri.EscapeDataString(MemberId)}&lastSeq={LastSeq}";
                using var resp = await SharedClient.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    {
                        // 已被房主清理或房间关闭
                        IsConnected = false;
                        ConnectionLost?.Invoke("已被移出房间或房间已关闭");
                        return;
                    }
                    // 其他错误：短暂退避后重试
                    await Task.Delay(1000, ct);
                    continue;
                }

                var json = await resp.Content.ReadAsStringAsync(ct);
                ListenTogetherPollResponse? pr;
                try
                {
                    pr = JsonSerializer.Deserialize<ListenTogetherPollResponse>(json, ListenTogetherJsonOptions.Default);
                }
                catch
                {
                    await Task.Delay(500, ct);
                    continue;
                }
                if (pr is null) { await Task.Delay(500, ct); continue; }

                if (pr.Members.Count > 0)
                    MembersChanged?.Invoke(pr.Members);

                if (pr.Commands.Count > 0)
                {
                    // 按 seq 升序处理；成员端只关心未读
                    foreach (var c in pr.Commands.OrderBy(c => c.Seq))
                    {
                        if (c.Seq > LastSeq) LastSeq = c.Seq;
                    }
                    CommandReceived?.Invoke(pr.Commands);
                }
                // 立即发起下一次轮询（无 backoff，长轮询本身已起到节流作用）
            }
            catch (OperationCanceledException) { return; }
            catch (IOException) { await Task.Delay(500, ct); }
            catch (HttpRequestException) { await Task.Delay(1000, ct); }
            catch (Exception) { await Task.Delay(1000, ct); }
        }
    }

    public async ValueTask DisposeAsync() => await LeaveAsync();
}
