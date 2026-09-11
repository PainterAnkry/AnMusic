using System.Collections.ObjectModel;
using AnMusic.Models;
using AnMusic.Services;
using AnMusic.Services.ListenTogether;
using AnMusic.Services.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnMusic.Android.ViewModels;

/// <summary>
/// "一起听" ViewModel（安卓端）：房主 / 成员两种角色的生命周期与播放指令同步。
/// </summary>
/// <remarks>
/// 复用 Core 的 <see cref="ListenTogetherHost"/> / <see cref="ListenTogetherClient"/>，
/// 与桌面端是同一套协议，因此手机与电脑可以互相加入同一个房间（同一局域网内）。
///
/// 房主侧订阅 <see cref="PlayerViewModel"/> 的播放事件并转发给成员；
/// 成员侧接收指令并应用到本地播放器，过程中用 <c>_applyingRemote</c> 抑制回环。
/// </remarks>
public sealed partial class ListenTogetherViewModel : ObservableObject
{
    /// <summary>监听端口范围：与桌面端保持一致，便于同机多端同时开房。</summary>
    private const int PortStart = 58000;
    private const int PortEnd = 58100;

    private const int MaxMembers = 4;

    private readonly PlayerViewModel _player;
    private readonly UserSettingsService _settings;

    private ListenTogetherHost? _host;
    private ListenTogetherClient? _client;

    /// <summary>应用远端指令期间置位：房主端据此抑制自身事件的回环转发。</summary>
    private bool _applyingRemote;

    public ListenTogetherViewModel(PlayerViewModel player, UserSettingsService settings)
    {
        _player = player;
        _settings = settings;
    }

    #region 状态

    /// <summary>房间成员名（含房主，房主在第一位）。</summary>
    public ObservableCollection<string> Members { get; } = [];

    [ObservableProperty] private bool _isHost;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _inviteLink = string.Empty;
    [ObservableProperty] private string _joinLinkInput = string.Empty;
    [ObservableProperty] private string _statusText = "未连接";
    [ObservableProperty] private bool _isBusy;

    /// <summary>成员模式（已连接但非房主）。</summary>
    public bool IsClient => IsConnected && !IsHost;

    /// <summary>未连接。</summary>
    public bool IsDisconnected => !IsConnected;

    /// <summary>房间人数文案。</summary>
    public string MemberCountText => $"房间人数 {Members.Count} / {MaxMembers}";

    /// <summary>成员名拼接文案。</summary>
    public string MembersText => Members.Count == 0 ? "（暂无成员）" : string.Join("、", Members);

    /// <summary>邀请链接是否可跨设备使用（只绑到 localhost 时给出提示）。</summary>
    public bool IsInviteLanReachable => _host?.IsLanReachable ?? false;

    /// <summary>成员视图里的同步说明。</summary>
    public string ClientHint =>
        "已进入同步模式：房主切歌、暂停与拖动进度都会自动跟到本机。播放用的音频需本机也能获取（本地音乐或同一音源）。";

    partial void OnIsConnectedChanged(bool value)
    {
        if (!value) Members.Clear();
        RefreshDerived();
    }

    partial void OnIsHostChanged(bool value) => RefreshDerived();

    private void RefreshDerived()
    {
        OnPropertyChanged(nameof(IsClient));
        OnPropertyChanged(nameof(IsDisconnected));
        OnPropertyChanged(nameof(MemberCountText));
        OnPropertyChanged(nameof(MembersText));
        OnPropertyChanged(nameof(IsInviteLanReachable));
    }

    #endregion

    #region 房主

    /// <summary>创建房间：扫描空闲端口启动 HttpListener，生成邀请链接。</summary>
    [RelayCommand]
    private async Task CreateRoomAsync()
    {
        if (IsConnected || IsBusy) return;
        IsBusy = true;
        StatusText = "正在创建房间…";

        try
        {
            var name = ResolveNickname();
            var roomId = GenerateRoomId();

            for (var port = PortStart; port < PortEnd; port++)
            {
                var host = new ListenTogetherHost(roomId, port, name);
                if (await host.StartAsync())
                {
                    _host = host;
                    IsHost = true;
                    IsConnected = true;
                    InviteLink = host.InviteLink;

                    Members.Clear();
                    foreach (var m in host.MemberNames) Members.Add(m);

                    StatusText = host.IsLanReachable
                        ? $"房间已创建（{Members.Count}/{MaxMembers}），把邀请链接发给好友即可加入"
                        : $"房间已创建，但只绑定到了本机地址（{host.Host}），其它设备可能连不上";

                    AttachHostForwarding();
                    RefreshDerived();
                    return;
                }
            }

            StatusText = "创建房间失败：没有可用端口，或本机网络被限制";
        }
        catch (Exception ex)
        {
            StatusText = $"创建房间异常：{ex.Message}";
            AppPaths.LogError("创建一起听房间", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void AttachHostForwarding()
    {
        _player.PlayStateChanged += OnHostPlayStateChanged;
        _player.TrackChanged += OnHostTrackChanged;
        _player.Seeked += OnHostSeeked;
    }

    private void DetachHostForwarding()
    {
        _player.PlayStateChanged -= OnHostPlayStateChanged;
        _player.TrackChanged -= OnHostTrackChanged;
        _player.Seeked -= OnHostSeeked;
    }

    private void OnHostPlayStateChanged(bool isPlaying)
    {
        if (_applyingRemote || !IsHost || _host is null) return;
        _host.EnqueueCommand(
            isPlaying ? ListenTogetherCommandType.Play : ListenTogetherCommandType.Pause,
            track: _player.CurrentTrack,
            positionSeconds: _player.PositionSeconds,
            isPlaying: isPlaying);
    }

    private void OnHostTrackChanged(Track track)
    {
        if (_applyingRemote || !IsHost || _host is null) return;
        _host.EnqueueCommand(
            ListenTogetherCommandType.ChangeTrack,
            track: track,
            positionSeconds: 0,
            isPlaying: _player.IsPlaying);
    }

    private void OnHostSeeked(double positionSeconds)
    {
        if (_applyingRemote || !IsHost || _host is null) return;
        _host.EnqueueCommand(
            ListenTogetherCommandType.Seek,
            track: _player.CurrentTrack,
            positionSeconds: positionSeconds,
            isPlaying: _player.IsPlaying);
    }

    #endregion

    #region 成员

    /// <summary>加入房间：解析邀请链接并启动长轮询。</summary>
    [RelayCommand]
    private async Task JoinRoomAsync()
    {
        if (IsConnected || IsBusy) return;

        var link = (JoinLinkInput ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(link))
        {
            StatusText = "请先粘贴邀请链接";
            return;
        }

        if (!ListenTogetherHost.TryParseInviteLink(link, out var hostAddr, out var port, out var roomId))
        {
            StatusText = "邀请链接格式无效，示例：anmusic://jointogether/192.168.1.5:58000/abc123";
            return;
        }

        IsBusy = true;
        StatusText = "正在连接…";

        var client = new ListenTogetherClient();
        client.CommandReceived += OnClientCommandReceived;
        client.MembersChanged += OnMembersChanged;
        client.ConnectionLost += OnClientConnectionLost;

        try
        {
            var name = ResolveNickname();
            var baseUrl = $"http://{hostAddr}:{port}/room/{roomId}/";

            if (await client.JoinAsync(baseUrl, name))
            {
                _client = client;
                IsHost = false;
                IsConnected = true;
                InviteLink = link;

                Members.Clear();
                if (client.Snapshot?.Members is { } ms)
                    foreach (var m in ms) Members.Add(m);

                StatusText = $"已加入房间（{Members.Count}/{MaxMembers}）";
                RefreshDerived();

                // 对齐房主当前播放状态
                await ApplySnapshotAsync(client.Snapshot);
            }
            else
            {
                client.CommandReceived -= OnClientCommandReceived;
                client.MembersChanged -= OnMembersChanged;
                client.ConnectionLost -= OnClientConnectionLost;
                await client.DisposeAsync();
                StatusText = "加入失败：房间不存在、已满或网络不通（需与房主在同一局域网）";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"加入异常：{ex.Message}";
            AppPaths.LogError("加入一起听房间", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnClientCommandReceived(IReadOnlyList<ListenTogetherCommand> commands)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            _applyingRemote = true;
            try
            {
                foreach (var cmd in commands.OrderBy(c => c.Seq))
                    await ApplyCommandAsync(cmd);
            }
            catch (Exception ex)
            {
                AppPaths.LogError("应用一起听指令", ex);
            }
            finally
            {
                _applyingRemote = false;
            }
        });
    }

    private void OnMembersChanged(IReadOnlyList<string> members)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            Members.Clear();
            foreach (var m in members) Members.Add(m);
            RefreshDerived();
            StatusText = $"已加入房间（{Members.Count}/{MaxMembers}）";
        });
    }

    private void OnClientConnectionLost(string reason)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            StatusText = reason;
            await DisconnectAsync();
        });
    }

    /// <summary>应用加入时的初始快照（房主当前播放状态）。</summary>
    private async Task ApplySnapshotAsync(ListenTogetherJoinResponse? snap)
    {
        if (snap?.CurrentTrack is null) return;
        try
        {
            _applyingRemote = true;
            await _player.LoadAndPlayAsync(snap.CurrentTrack.ToTrack());
            if (snap.PositionSeconds > 0) _player.CommitSeek(snap.PositionSeconds);
            if (!snap.IsPlaying && _player.IsPlaying) _player.Pause();
        }
        catch (Exception ex)
        {
            AppPaths.LogError("应用一起听快照", ex);
        }
        finally
        {
            _applyingRemote = false;
        }
    }

    private async Task ApplyCommandAsync(ListenTogetherCommand cmd)
    {
        switch (cmd.Type)
        {
            case "play":
            {
                if (cmd.Track is { } t && !IsSameTrack(_player.CurrentTrack, t))
                    await _player.LoadAndPlayAsync(t.ToTrack());

                // 网络延迟补偿：按指令时间戳推算房主此刻应到的位置
                var latencySec = Math.Max(0,
                    (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - cmd.SentAtMs) / 1000.0);
                _player.CommitSeek(Math.Max(0, cmd.PositionSeconds + latencySec));
                if (!_player.IsPlaying) _player.Play();
                break;
            }

            case "pause":
                if (_player.IsPlaying) _player.Pause();
                break;

            case "seek":
                _player.CommitSeek(cmd.PositionSeconds);
                break;

            case "changeTrack":
                if (cmd.Track is { } track)
                {
                    await _player.LoadAndPlayAsync(track.ToTrack());
                    if (cmd.PositionSeconds > 0) _player.CommitSeek(cmd.PositionSeconds);
                    if (!cmd.IsPlaying && _player.IsPlaying) _player.Pause();
                }
                break;
        }
    }

    private static bool IsSameTrack(Track? a, ListenTogetherTrackInfo b)
        => a is not null && a.Id == b.Id && a.ProviderId == b.ProviderId;

    #endregion

    /// <summary>断开连接：房主关闭房间 / 成员离开房间。</summary>
    [RelayCommand]
    private async Task DisconnectAsync()
    {
        IsBusy = true;
        try
        {
            if (_host is { } host)
            {
                DetachHostForwarding();
                await host.StopAsync();
                await host.DisposeAsync();
                _host = null;
            }

            if (_client is { } client)
            {
                client.CommandReceived -= OnClientCommandReceived;
                client.MembersChanged -= OnMembersChanged;
                client.ConnectionLost -= OnClientConnectionLost;
                await client.LeaveAsync();
                await client.DisposeAsync();
                _client = null;
            }
        }
        catch (Exception ex)
        {
            AppPaths.LogError("断开一起听", ex);
        }
        finally
        {
            IsHost = false;
            IsConnected = false;
            InviteLink = string.Empty;
            StatusText = "未连接";
            Members.Clear();
            OnPropertyChanged(nameof(IsInviteLanReachable));
            IsBusy = false;
        }
    }

    /// <summary>复制邀请链接到剪贴板。</summary>
    [RelayCommand]
    private async Task CopyLinkAsync()
    {
        if (string.IsNullOrEmpty(InviteLink)) return;
        try
        {
            await Clipboard.Default.SetTextAsync(InviteLink);
            StatusText = "邀请链接已复制，发给好友即可加入";
        }
        catch (Exception ex)
        {
            StatusText = "复制失败，请手动长按选择文本";
            AppPaths.LogError("复制邀请链接", ex);
        }
    }

    /// <summary>页面离开时清理（避免房间一直挂在后台）。</summary>
    public async Task CleanupAsync()
    {
        if (IsConnected) await DisconnectAsync();
    }

    private string ResolveNickname()
    {
        var nick = _settings.Settings.UserNickname;
        return string.IsNullOrWhiteSpace(nick) ? "音乐爱好者" : nick;
    }

    /// <summary>生成 6 位房间短码（小写字母 + 数字）。</summary>
    private static string GenerateRoomId()
    {
        Span<char> buf = stackalloc char[6];
        var rng = Random.Shared;
        const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
        for (var i = 0; i < buf.Length; i++)
            buf[i] = alphabet[rng.Next(alphabet.Length)];
        return buf.ToString();
    }
}
