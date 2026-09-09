using System.Collections.ObjectModel;
using System.Windows;
using AnMusic.Models;
using AnMusic.Services.Audio;
using AnMusic.Services.ListenTogether;
using AnMusic.Services.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnMusic.ViewModels;

/// <summary>
/// "在线一起听" ViewModel：负责房主/成员两种角色的生命周期与播放指令同步。
/// 房主侧订阅 <see cref="PlaybackBarViewModel"/> 的播放事件并转发到 <see cref="ListenTogetherHost"/>；
/// 成员侧从 <see cref="ListenTogetherClient"/> 接收指令并应用到本地播放器。
/// </summary>
public partial class ListenTogetherViewModel : ObservableObject
{
    private const int PortStart = 58000;
    private const int PortEnd = 58100;
    private const int MaxMembers = 4;

    private readonly PlaybackBarViewModel _playbackBar;
    private readonly UserSettingsService _settingsService;

    private ListenTogetherHost? _host;
    private ListenTogetherClient? _client;
    private bool _applyingRemote; // 应用远端指令期间禁止回环转发（防御性，仅房主转发）

    /// <summary>房间成员名列表（含房主，房主位于第一项）。</summary>
    public ObservableCollection<string> Members { get; } = [];

    [ObservableProperty]
    private bool _isHost;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _inviteLink = string.Empty;

    [ObservableProperty]
    private string _joinLinkInput = string.Empty;

    [ObservableProperty]
    private string _hostName = string.Empty;

    [ObservableProperty]
    private string _statusText = "未连接";

    /// <summary>成员模式（已连接但非房主）；用于 XAML 切换成员视图。</summary>
    public bool IsClient => IsConnected && !IsHost;

    /// <summary>未连接（房主关闭/成员离开后）；用于 XAML 切换未连接视图。</summary>
    public bool IsDisconnected => !IsConnected;

    /// <summary>房间人数（含房主）。</summary>
    public int MemberCount => Members.Count;

    /// <summary>房间人数文本（"房间人数 N / 4"）。</summary>
    public string MemberCountText => $"房间人数 {Members.Count} / {MaxMembers}";

    /// <summary>成员名拼接文本（用于面板内一行展示）。</summary>
    public string MembersText => Members.Count == 0 ? "（暂无其他成员）" : string.Join("、", Members);

    /// <summary>请求切换/关闭面板弹窗（由 MainWindow 订阅并操作 Popup）。</summary>
    public event Action? PanelToggleRequested;

    public ListenTogetherViewModel(PlaybackBarViewModel playbackBar, UserSettingsService settingsService)
    {
        _playbackBar = playbackBar;
        _settingsService = settingsService;
    }

    partial void OnIsHostChanged(bool value) => RefreshDerived();

    partial void OnIsConnectedChanged(bool value)
    {
        if (!value) Members.Clear();
        RefreshDerived();
    }

    partial void OnStatusTextChanged(string value) { /* 占位，便于未来扩展 */ }

    private void RefreshDerived()
    {
        OnPropertyChanged(nameof(IsClient));
        OnPropertyChanged(nameof(IsDisconnected));
        OnPropertyChanged(nameof(MemberCount));
        OnPropertyChanged(nameof(MemberCountText));
        OnPropertyChanged(nameof(MembersText));
    }

    /// <summary>创建房间：寻找空闲端口，启动 HttpListener，生成邀请链接。</summary>
    [RelayCommand]
    private async Task CreateRoomAsync()
    {
        if (IsConnected) return;
        var name = string.IsNullOrWhiteSpace(_settingsService.Settings.UserNickname)
            ? Environment.UserName
            : _settingsService.Settings.UserNickname;

        var roomId = GenerateRoomId();
        for (var port = PortStart; port < PortEnd; port++)
        {
            var host = new ListenTogetherHost(roomId, port, name);
            if (await host.StartAsync())
            {
                _host = host;
                HostName = name;
                IsHost = true;
                IsConnected = true;
                InviteLink = host.InviteLink;
                Members.Clear();
                foreach (var m in host.MemberNames) Members.Add(m);
                StatusText = $"房间已创建，{Members.Count}/{MaxMembers} 人";
                AttachHostForwarding();
                return;
            }
        }
        StatusText = "创建房间失败：找不到可用端口";
    }

    /// <summary>加入房间：解析邀请链接，发送 join 请求，启动长轮询。</summary>
    [RelayCommand]
    private async Task JoinRoomAsync()
    {
        if (IsConnected) return;
        var link = (JoinLinkInput ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(link))
        {
            StatusText = "请粘贴邀请链接";
            return;
        }
        if (!ListenTogetherHost.TryParseInviteLink(link, out var hostAddr, out var port, out var roomId))
        {
            StatusText = "邀请链接格式无效\n示例：anmusic://jointogether/localhost:58000/abc123";
            return;
        }

        var name = string.IsNullOrWhiteSpace(_settingsService.Settings.UserNickname)
            ? Environment.UserName
            : _settingsService.Settings.UserNickname;
        var baseUrl = $"http://{hostAddr}:{port}/room/{roomId}/";
        var client = new ListenTogetherClient();
        client.CommandReceived += OnClientCommandReceived;
        client.MembersChanged += OnMembersChanged;
        client.ConnectionLost += OnClientConnectionLost;

        StatusText = "正在连接...";
        if (await client.JoinAsync(baseUrl, name))
        {
            _client = client;
            HostName = name;
            IsHost = false;
            IsConnected = true;
            InviteLink = link;
            Members.Clear();
            if (client.Snapshot?.Members is { } ms) foreach (var m in ms) Members.Add(m);
            StatusText = $"已加入房间（{Members.Count}/{MaxMembers}）";
            RefreshDerived();
            // 应用初始快照：让本地播放器与房主当前状态对齐
            await ApplySnapshotAsync(client.Snapshot);
        }
        else
        {
            client.CommandReceived -= OnClientCommandReceived;
            client.MembersChanged -= OnMembersChanged;
            client.ConnectionLost -= OnClientConnectionLost;
            await client.DisposeAsync();
            StatusText = "加入失败";
        }
    }

    /// <summary>断开连接：房主关闭房间 / 成员离开房间，统一入口。</summary>
    [RelayCommand]
    private async Task DisconnectAsync()
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
        IsHost = false;
        IsConnected = false;
        InviteLink = string.Empty;
        JoinLinkInput = string.Empty;
        StatusText = "未连接";
        Members.Clear();
    }

    /// <summary>复制邀请链接到剪贴板。</summary>
    [RelayCommand]
    private void CopyLink()
    {
        if (string.IsNullOrEmpty(InviteLink)) return;
        try
        {
            Clipboard.SetText(InviteLink);
            StatusText = "邀请链接已复制到剪贴板";
        }
        catch
        {
            StatusText = "复制失败，请手动选择文本";
        }
    }

    /// <summary>面板内 ✕ 按钮：请求 MainWindow 关闭弹窗（弹窗本体在 MainWindow 中）。</summary>
    [RelayCommand]
    private void TogglePanel() => PanelToggleRequested?.Invoke();

    #region 房主侧：转发本地播放事件到客户端

    private void AttachHostForwarding()
    {
        _playbackBar.PlayStateChanged += OnHostPlayStateChanged;
        _playbackBar.TrackChanged += OnHostTrackChanged;
        _playbackBar.Seeked += OnHostSeeked;
    }

    private void DetachHostForwarding()
    {
        _playbackBar.PlayStateChanged -= OnHostPlayStateChanged;
        _playbackBar.TrackChanged -= OnHostTrackChanged;
        _playbackBar.Seeked -= OnHostSeeked;
    }

    private void OnHostPlayStateChanged(bool isPlaying)
    {
        if (_applyingRemote || !IsHost || _host is null) return;
        var pos = _playbackBar.PositionSeconds;
        var current = _playbackBar.CurrentTrack;
        _host.EnqueueCommand(
            isPlaying ? ListenTogetherCommandType.Play : ListenTogetherCommandType.Pause,
            track: current,
            positionSeconds: pos,
            isPlaying: isPlaying);
    }

    private void OnHostTrackChanged(Track track)
    {
        if (_applyingRemote || !IsHost || _host is null) return;
        var pos = _playbackBar.PositionSeconds;
        var playing = _playbackBar.IsPlaying;
        _host.EnqueueCommand(
            ListenTogetherCommandType.ChangeTrack,
            track: track,
            positionSeconds: pos,
            isPlaying: playing);
    }

    private void OnHostSeeked(double positionSeconds)
    {
        if (_applyingRemote || !IsHost || _host is null) return;
        _host.EnqueueCommand(
            ListenTogetherCommandType.Seek,
            track: _playbackBar.CurrentTrack,
            positionSeconds: positionSeconds,
            isPlaying: _playbackBar.IsPlaying);
    }

    #endregion

    #region 成员侧：接收远端指令并应用到本地播放器

    private void OnClientCommandReceived(IReadOnlyList<ListenTogetherCommand> commands)
    {
        // 切到 UI 线程执行（PlaybackBarViewModel 内部依赖 Dispatcher）
        Application.Current?.Dispatcher.InvokeAsync(async () =>
        {
            _applyingRemote = true;
            try
            {
                foreach (var cmd in commands.OrderBy(c => c.Seq))
                    await ApplyCommandAsync(cmd);
            }
            finally
            {
                _applyingRemote = false;
            }
        });
    }

    private void OnMembersChanged(IReadOnlyList<string> members)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            Members.Clear();
            foreach (var m in members) Members.Add(m);
            RefreshDerived();
            StatusText = $"已加入房间（{Members.Count}/{MaxMembers}）";
        });
    }

    private void OnClientConnectionLost(string reason)
    {
        Application.Current?.Dispatcher.Invoke(async () =>
        {
            StatusText = reason;
            await DisconnectAsync();
        });
    }

    /// <summary>应用加入时的初始快照（房主当前播放状态）。</summary>
    private async Task ApplySnapshotAsync(ListenTogetherJoinResponse? snap)
    {
        if (snap is null || snap.CurrentTrack is null) return;
        try
        {
            _applyingRemote = true;
            await _playbackBar.LoadAndPlayAsync(snap.CurrentTrack.ToTrack());
            // 等待加载完成后再 Seek（LoadAndPlayAsync 内部已 await LoadAsync）
            if (snap.PositionSeconds > 0)
                _playbackBar.SeekTo(snap.PositionSeconds);
            if (!snap.IsPlaying && _playbackBar.IsPlaying)
                _playbackBar.PlayPauseCommand.Execute(null);
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
                // 若指令携带曲目且与当前不同，先加载；否则仅 Seek + 播放
                if (cmd.Track is { } t && !IsSameTrack(_playbackBar.CurrentTrack, t))
                    await _playbackBar.LoadAndPlayAsync(t.ToTrack());
                // 网络延迟补偿：按指令时间戳推算房主当前应到的位置
                var latencySec = Math.Max(0, (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - cmd.SentAtMs) / 1000.0);
                var target = Math.Max(0, cmd.PositionSeconds + latencySec);
                _playbackBar.SeekTo(target);
                if (!_playbackBar.IsPlaying)
                    _playbackBar.PlayPauseCommand.Execute(null);
                break;
            }
            case "pause":
                if (_playbackBar.IsPlaying)
                    _playbackBar.PlayPauseCommand.Execute(null);
                break;
            case "seek":
                _playbackBar.SeekTo(cmd.PositionSeconds);
                break;
            case "changeTrack":
                if (cmd.Track is { } track)
                {
                    await _playbackBar.LoadAndPlayAsync(track.ToTrack());
                    if (cmd.PositionSeconds > 0)
                        _playbackBar.SeekTo(cmd.PositionSeconds);
                    if (!cmd.IsPlaying && _playbackBar.IsPlaying)
                        _playbackBar.PlayPauseCommand.Execute(null);
                }
                break;
        }
    }

    private static bool IsSameTrack(Track? a, ListenTogetherTrackInfo b)
        => a is not null && a.Id == b.Id && a.ProviderId == b.ProviderId;

    #endregion

    /// <summary>生成 6 位房间短码（小写字母数字）。</summary>
    private static string GenerateRoomId()
    {
        Span<char> buf = stackalloc char[6];
        var rng = Random.Shared;
        const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
        for (int i = 0; i < buf.Length; i++)
            buf[i] = alphabet[rng.Next(alphabet.Length)];
        return buf.ToString();
    }
}
