using System.Collections.ObjectModel;
using AnMusic.Android.Services;
using AnMusic.Models;
using AnMusic.Services;
using AnMusic.Services.Audio;
using AnMusic.Services.Lyrics;
using AnMusic.Services.Playlist;
using AnMusic.Services.Providers;
using AnMusic.Services.Settings;
using AnMusic.Services.Stats;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlaybackState = AnMusic.Models.PlaybackState;
using Platform = AnMusic.Services.Platform;

namespace AnMusic.Android.ViewModels;

/// <summary>播放模式：顺序 → 列表循环 → 单曲循环 → 随机（与桌面端一致）。</summary>
public enum PlayModeKind
{
    Sequential,
    LoopAll,
    LoopOne,
    Shuffle,
}

/// <summary>
/// 安卓播放控制 ViewModel：装载队列、驱动音频引擎、对外暴露可绑定的播放状态。
/// 逻辑上对应桌面端的 <c>PlaybackBarViewModel</c>，但去掉了 WPF 依赖（Dispatcher/UiDialog），
/// 改由页面订阅状态变化。
/// </summary>
public sealed partial class PlayerViewModel : ObservableObject
{
    private readonly IAudioEngine _engine;
    private readonly IPlaylistQueue _queue;
    private readonly UserSettingsService _settings;
    private readonly LrclibLyricProvider _lyrics;
    private readonly ILrcParser _parser;
    private readonly IPlatformContext _platform;
    private readonly ListeningStatsService _stats;
    private readonly ProviderRegistry _registry;

    /// <summary>歌词翻译（Core 共享实现，会套用用户配置的代理）。</summary>
    private readonly LyricTranslationService _translator;

    /// <summary>进度拖动中：此时忽略引擎上报的位置，避免滑块被"拽回去"。</summary>
    private bool _userSeeking;

    /// <summary>被系统临时压低音量前的音量，焦点恢复后还原。</summary>
    private double? _volumeBeforeDuck;

    /// <summary>通知权限只在首次播放时申请一次（API 33+ 才需要）。</summary>
    private static bool _notificationPermissionAsked;

    public PlayerViewModel(
        IAudioEngine engine,
        IPlaylistQueue queue,
        UserSettingsService settings,
        LrclibLyricProvider lyrics,
        ILrcParser parser,
        IPlatformContext platform,
        ListeningStatsService stats,
        LyricTranslationService translator,
        ProviderRegistry registry)
    {
        _engine = engine;
        _queue = queue;
        _settings = settings;
        _lyrics = lyrics;
        _parser = parser;
        _platform = platform;
        _stats = stats;
        _translator = translator;
        _registry = registry;

        Volume = Math.Clamp(settings.Settings.DefaultVolume, 0, 1);
        _engine.Volume = (float)Volume;

        // 播放页封面是否旋转（安卓端展示偏好，用 Preferences 存，不污染跨端 settings.json）
        _coverSpinEnabled = Preferences.Default.Get(CoverSpinKey, true);

        _engine.StateChanged += (_, state) => MainThread.BeginInvokeOnMainThread(() =>
        {
            IsPlaying = state == PlaybackState.Playing;
            IsBuffering = state == PlaybackState.Loading;
        });

        _engine.PositionChanged += (_, pos) => MainThread.BeginInvokeOnMainThread(() =>
        {
            // 听歌时长按位置差值累加，必须在 _userSeeking 过滤之前调用：
            // 拖动进度条时位置会跳变，统计服务内部已用 0~5 秒的增量窗口滤掉跳变
            _stats.OnPositionChanged(CurrentTrack, pos.TotalSeconds,
                _engine.State == PlaybackState.Playing);

            if (_userSeeking) return;
            PositionSeconds = pos.TotalSeconds;
            UpdateCurrentLyricIndex(pos);
        });

        _engine.TrackEnded += (_, track) => MainThread.BeginInvokeOnMainThread(async () => await OnTrackEndedAsync(track));

        _engine.PlaybackFailed += (_, ex) => MainThread.BeginInvokeOnMainThread(() =>
        {
            StatusMessage = $"播放失败：{ex.Message}";
            IsPlaying = false;
        });

        // 歌词配色：初值取自设置，"跟随主题"那一档还要随换肤实时变
        ApplyLyricColor();
        ThemeService.Changed += () => MainThread.BeginInvokeOnMainThread(ApplyLyricColor);
    }

    #region 可绑定状态

    [ObservableProperty] private string _title = "未在播放";
    [ObservableProperty] private string _artist = string.Empty;
    [ObservableProperty] private Track? _currentTrack;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isBuffering;
    [ObservableProperty] private string? _coverPath;
    [ObservableProperty] private double _positionSeconds;
    [ObservableProperty] private double _durationSeconds = 1;
    [ObservableProperty] private double _volume = 1;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _hasTrack;

    /// <summary>是否已收藏当前曲目（播放页心形按钮）。</summary>
    [ObservableProperty] private bool _isFavorite;

    /// <summary>播放页歌词区是否展开（收起时显示大封面，类似网易云的封面/歌词滑动切换）。</summary>
    [ObservableProperty] private bool _isLyricMode;

    /// <summary>播放页「当前播放」列表是否展开。</summary>
    [ObservableProperty] private bool _isQueueVisible;

    /// <summary>进度条最大/最小值，供 Slider 直接绑定。</summary>
    public double DurationForSlider => Math.Max(1, DurationSeconds);

    partial void OnDurationSecondsChanged(double value)
    {
        OnPropertyChanged(nameof(DurationForSlider));
        OnPropertyChanged(nameof(ProgressFraction));
        DurationText = FormatTime(TimeSpan.FromSeconds(value));
    }

    /// <summary>播放/暂停按钮图标。</summary>
    public string PlayPauseIcon => IsPlaying ? "⏸" : "▶";

    partial void OnIsPlayingChanged(bool value)
    {
        OnPropertyChanged(nameof(PlayPauseIcon));
        OnPropertyChanged(nameof(PlayStateText));
        PlayStateChanged?.Invoke(value);
    }

    /// <summary>播放/暂停状态变化（一起听房主端据此转发指令）。</summary>
    public event Action<bool>? PlayStateChanged;

    /// <summary>曲目切换（一起听房主端据此转发指令）。</summary>
    public event Action<Track>? TrackChanged;

    /// <summary>用户拖动进度条完成跳转（一起听房主端据此转发指令）。</summary>
    public event Action<double>? Seeked;

    /// <summary>播放页顶部的状态文案。</summary>
    public string PlayStateText => IsBuffering ? "缓冲中…" : IsPlaying ? "正在播放" : "已暂停";

    partial void OnIsBufferingChanged(bool value) => OnPropertyChanged(nameof(PlayStateText));

    [ObservableProperty] private PlayModeKind _playMode = PlayModeKind.Sequential;

    public string PlayModeIcon => PlayMode switch
    {
        PlayModeKind.LoopAll => "🔁",
        PlayModeKind.LoopOne => "🔂",
        PlayModeKind.Shuffle => "🔀",
        _ => "➡",
    };

    /// <summary>播放模式名称（点击模式按钮时提示用）。</summary>
    public string PlayModeName => PlayMode switch
    {
        PlayModeKind.LoopAll => "列表循环",
        PlayModeKind.LoopOne => "单曲循环",
        PlayModeKind.Shuffle => "随机播放",
        _ => "顺序播放",
    };

    [ObservableProperty] private string _positionText = "00:00";
    [ObservableProperty] private string _durationText = "00:00";

    partial void OnPositionSecondsChanged(double value)
    {
        PositionText = FormatTime(TimeSpan.FromSeconds(value));
        OnPropertyChanged(nameof(ProgressFraction));
    }

    /// <summary>播放进度比例（0~1），迷你播放条与播放页共用。</summary>
    public double ProgressFraction => DurationSeconds <= 0
        ? 0
        : Math.Clamp(PositionSeconds / DurationSeconds, 0, 1);

    partial void OnPlayModeChanged(PlayModeKind value)
    {
        _queue.Repeat = value switch
        {
            PlayModeKind.LoopAll => RepeatMode.All,
            PlayModeKind.LoopOne => RepeatMode.One,
            PlayModeKind.Shuffle => RepeatMode.All,
            _ => RepeatMode.None,
        };
        _queue.Shuffle = value == PlayModeKind.Shuffle;
        OnPropertyChanged(nameof(PlayModeIcon));
        OnPropertyChanged(nameof(PlayModeName));
    }

    /// <summary>当前播放队列（供播放列表页展示）。</summary>
    public ObservableCollection<Track> Queue { get; } = [];

    /// <summary>队列中正在播放的位置，供列表行高亮。</summary>
    [ObservableProperty] private int _currentQueueIndex = -1;

    /// <summary>当前曲目的歌词行（含时间戳与文本），供歌词页渲染。</summary>
    public ObservableCollection<LyricDisplayLine> LyricLines { get; } = [];

    [ObservableProperty] private int _currentLyricIndex = -1;
    [ObservableProperty] private string _lyricStatus = string.Empty;

    /// <summary>
    /// 歌词正文颜色。设置里选「跟随主题」时取主题的次要文字色，
    /// 其余 5 套为固定色 —— 与桌面端 LyricColorIndex 的取值一一对应。
    /// 之所以不用 null 表示"跟随主题"，是因为 MAUI 里绑定到 null 会让文字直接消失。
    /// </summary>
    [ObservableProperty] private Color _lyricTextColor = Colors.Gray;

    /// <summary>当前行高亮色（取主题强调色）。</summary>
    [ObservableProperty] private Color _lyricHighlightColor = Colors.OrangeRed;

    /// <summary>歌词正文字号（来自设置，高亮行在此基础上 +2）。</summary>
    private double LyricBaseFontSize => Math.Clamp(_settings.Settings.LyricFontSize, 12, 24);

    /// <summary>从设置解析歌词颜色；换肤或改设置后需要重新调用。</summary>
    public void ApplyLyricColor()
    {
        LyricTextColor = _settings.Settings.LyricColorIndex switch
        {
            1 => Colors.White,
            2 => Colors.Black,
            3 => Color.FromArgb("#F48FB1"),
            4 => Color.FromArgb("#64B5F6"),
            5 => Color.FromArgb("#81C784"),
            _ => ThemeService.Get("AmTextSecondary"),
        };

        // 当前行统一用强调色：即使歌词配色选了白色/黑色，高亮行也能与普通行区分开
        LyricHighlightColor = ThemeService.Get("AmPrimary");

        // 配色/字号变了，已加载的歌词要重新上色
        for (var i = 0; i < LyricLines.Count; i++)
            LyricLines[i].ApplyVisual(i == CurrentLyricIndex, LyricTextColor, LyricHighlightColor, LyricBaseFontSize);
    }

    /// <summary>是否已显示译文（「译」按钮的开关状态）。</summary>
    [ObservableProperty] private bool _isTranslated;

    /// <summary>翻译进行中。</summary>
    [ObservableProperty] private bool _isTranslating;

    /// <summary>「译」按钮文案：随状态变化，避免用户不知道点没点上。</summary>
    public string TranslationLabel => IsTranslating ? "译…" : IsTranslated ? "原文" : "译";

    partial void OnIsTranslatingChanged(bool value) => OnPropertyChanged(nameof(TranslationLabel));
    partial void OnIsTranslatedChanged(bool value) => OnPropertyChanged(nameof(TranslationLabel));

    /// <summary>缓存译文，切换「译/原文」时不重复请求接口。</summary>
    private string[]? _translations;

    /// <summary>
    /// 切换歌词翻译（机器翻译为中文，原文+译文双行对照）。
    /// 译文按行下标对齐；接口偶尔吞掉空行，Core 侧已做补齐，这里再兜一次。
    /// </summary>
    [RelayCommand]
    private async Task ToggleTranslationAsync()
    {
        if (LyricLines.Count == 0) return;

        if (IsTranslated)
        {
            foreach (var line in LyricLines) line.Translation = null;
            IsTranslated = false;
            return;
        }

        if (_translations is null)
        {
            IsTranslating = true;
            var previousStatus = LyricStatus;
            LyricStatus = "正在翻译歌词…";

            try
            {
                _translations = await _translator.TranslateAsync(
                    LyricLines.Select(l => l.Text).ToList());
            }
            catch (Exception ex)
            {
                LyricStatus = "翻译失败：请检查网络或代理设置";
                AppPaths.LogError("翻译歌词", ex, CurrentTrack?.Title);
                return;
            }
            finally
            {
                IsTranslating = false;
                if (LyricStatus == "正在翻译歌词…") LyricStatus = previousStatus;
            }
        }

        for (var i = 0; i < LyricLines.Count; i++)
        {
            var t = i < _translations.Length ? _translations[i] : null;
            LyricLines[i].Translation = string.IsNullOrWhiteSpace(t) ? null : t;
        }

        IsTranslated = true;
    }

    /// <summary>是否有歌词可显示。</summary>
    public bool HasLyrics => LyricLines.Count > 0;

    /// <summary>队列长度说明。</summary>
    public string QueueCountText => $"{Queue.Count} 首";

    /// <summary>队列为空（用于播放列表页的空状态）。</summary>
    public bool IsQueueEmpty => Queue.Count == 0;

    #endregion

    #region 播放页封面样式

    private const string CoverSpinKey = "player_cover_spin";

    private bool _coverSpinEnabled;

    /// <summary>封面是否随播放旋转（播放页样式偏好）。</summary>
    public bool IsCoverSpinEnabled
    {
        get => _coverSpinEnabled;
        set
        {
            if (_coverSpinEnabled == value) return;
            _coverSpinEnabled = value;
            Preferences.Default.Set(CoverSpinKey, value);
            OnPropertyChanged();
            CoverSpinChanged?.Invoke(this, value);
        }
    }

    /// <summary>封面旋转开关变化（页面据此启停动画）。</summary>
    public event EventHandler<bool>? CoverSpinChanged;

    #endregion

    #region 定时关闭

    private readonly System.Timers.Timer _sleepTimer = new(1000) { AutoReset = true };
    private DateTime _sleepDeadline;
    private bool _sleepStopAfterCurrent;

    [ObservableProperty] private bool _sleepTimerActive;
    [ObservableProperty] private string _sleepTimerText = "未开启";

    /// <summary>是否开启了「播完当前曲目后停止」。</summary>
    [ObservableProperty] private bool _sleepStopAfterTrack;

    partial void OnSleepTimerActiveChanged(bool value) => OnPropertyChanged(nameof(HasSleepTimer));

    partial void OnSleepStopAfterTrackChanged(bool value) => OnPropertyChanged(nameof(HasSleepTimer));

    /// <summary>有任一形式的定时关闭在进行中。</summary>
    public bool HasSleepTimer => SleepTimerActive || SleepStopAfterTrack;

    /// <summary>按分钟启动定时关闭。</summary>
    public void StartSleepTimer(int minutes)
    {
        if (minutes <= 0) return;

        _sleepStopAfterCurrent = false;
        SleepStopAfterTrack = false;
        _sleepDeadline = DateTime.Now.AddMinutes(minutes);

        SleepTimerActive = true;
        UpdateSleepText();

        _sleepTimer.Elapsed -= OnSleepTick;
        _sleepTimer.Elapsed += OnSleepTick;
        _sleepTimer.Start();
    }

    /// <summary>播完当前曲目后自动停止。</summary>
    public void StopAfterCurrentTrack()
    {
        _sleepTimer.Stop();
        _sleepStopAfterCurrent = true;
        SleepTimerActive = false;
        SleepStopAfterTrack = true;
        SleepTimerText = "播完当前曲目后停止";
    }

    /// <summary>取消定时关闭。</summary>
    public void CancelSleepTimer()
    {
        _sleepTimer.Stop();
        _sleepStopAfterCurrent = false;
        SleepTimerActive = false;
        SleepStopAfterTrack = false;
        SleepTimerText = "未开启";
    }

    private void OnSleepTick(object? sender, System.Timers.ElapsedEventArgs e)
    {
        if (DateTime.Now >= _sleepDeadline)
        {
            CancelSleepTimer();
            MainThread.BeginInvokeOnMainThread(() =>
            {
                _engine.Pause();
                StatusMessage = "定时关闭已生效，播放已暂停";
            });
            return;
        }
        MainThread.BeginInvokeOnMainThread(UpdateSleepText);
    }

    private void UpdateSleepText()
    {
        var remain = _sleepDeadline - DateTime.Now;
        if (remain < TimeSpan.Zero) remain = TimeSpan.Zero;
        SleepTimerText = $"剩余 {remain:mm\\:ss}";
    }

    #endregion

    #region 播放控制

    /// <summary>用一批曲目替换队列并从 startIndex 开始播放。</summary>
    public async Task PlayQueueAsync(IEnumerable<Track> tracks, int startIndex = 0)
    {
        var list = tracks.ToList();
        if (list.Count == 0)
        {
            StatusMessage = "没有可播放的曲目";
            return;
        }

        _queue.SetItems(list, startIndex);
        SyncQueueFromEngine();
        await LoadAndPlayAsync(_queue.Current ?? list[0]);
    }

    /// <summary>把 Core 队列同步进可绑定的 <see cref="Queue"/> 集合。</summary>
    private void SyncQueueFromEngine()
    {
        Queue.Clear();
        foreach (var t in _queue.Queue) Queue.Add(t);

        CurrentQueueIndex = _queue.CurrentIndex;
        OnPropertyChanged(nameof(QueueCountText));
        OnPropertyChanged(nameof(IsQueueEmpty));
    }

    /// <summary>加载曲目并开始播放。在线曲目先用对应音源缓冲成本地文件（与桌面端一致）。</summary>
    public async Task LoadAndPlayAsync(Track track)
    {
        try
        {
            StatusMessage = string.Empty;
            IsBuffering = true;

            // 本地曲目取 FilePath，在线曲目取播放缓冲；缓冲被清理过就重新缓冲
            var playPath = track.PlayablePath;
            if (string.IsNullOrEmpty(playPath) || !File.Exists(playPath))
            {
                if (_registry.Find(track.ProviderId) is IOnlineMusicProvider online)
                {
                    await online.ResolveToLocalAsync(track);
                }
                else
                {
                    StatusMessage = "该曲目缺少本地文件且无对应在线源";
                    IsPlaying = false;
                    IsBuffering = false;
                    return;
                }
            }

            await _engine.LoadAsync(track);

            CurrentTrack = track;
            Title = track.Title;
            Artist = track.Artist;
            CoverPath = string.IsNullOrEmpty(track.CoverKey)
                ? null
                : _platform.ToImageSourceUri(track.CoverKey);
            HasTrack = true;

            // 队列里若已存在该曲目，同步高亮位置
            var idx = IndexInQueue(track);
            if (idx >= 0) CurrentQueueIndex = idx;

            DurationSeconds = _engine.Duration.TotalSeconds > 0
                ? _engine.Duration.TotalSeconds
                : Math.Max(1, track.Duration.TotalSeconds);
            PositionSeconds = 0;

            // 听歌统计 + 后台播放服务：都在「真正开始播」的这一刻挂上
            _stats.ResetPositionBaseline();
            _stats.OnTrackStarted(track);
            StartBackgroundPlayback();

            _engine.Play();

            _ = LoadLyricsAsync(track);
        }
        catch (Exception ex)
        {
            StatusMessage = $"无法播放：{ex.Message}";
            IsPlaying = false;
            IsBuffering = false;
        }
    }

    private int IndexInQueue(Track track)
    {
        var items = _queue.Queue;
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Id == track.Id && items[i].ProviderId == track.ProviderId) return i;
        }
        return -1;
    }

    [RelayCommand]
    private async Task PlayPauseAsync()
    {
        if (!HasTrack)
        {
            if (_queue.Current is { } t) await LoadAndPlayAsync(t);
            return;
        }

        if (_engine.State == PlaybackState.Playing) Pause();
        else
        {
            StartBackgroundPlayback();
            _engine.Play();
        }
    }

    /// <summary>暂停播放（通知栏 / 耳机 / 蓝牙 / 音频焦点丢失都走这里）。</summary>
    public void Pause() => _engine.Pause();

    /// <summary>开始或恢复播放。</summary>
    public void Play()
    {
        if (!HasTrack) return;
        StartBackgroundPlayback();
        _engine.Play();
    }

    /// <summary>被系统临时压低音量（导航播报等）。</summary>
    public void Duck()
    {
        if (_volumeBeforeDuck is not null) return;
        _volumeBeforeDuck = Volume;
        _engine.Volume = (float)(Volume * 0.2);
    }

    /// <summary>恢复被压低前的音量。</summary>
    public void Unduck()
    {
        if (_volumeBeforeDuck is not { } v) return;
        _volumeBeforeDuck = null;
        _engine.Volume = (float)v;
    }

    /// <summary>
    /// 启动后台播放前台服务并确保通知权限已授予。
    /// 前台服务是「切后台/锁屏不被系统回收」的前提，通知权限决定通知栏控制是否可见。
    /// </summary>
    private void StartBackgroundPlayback()
    {
        PlaybackService.EnsureStarted();
        _ = EnsureNotificationPermissionAsync();
    }

    private static async Task EnsureNotificationPermissionAsync()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33) || _notificationPermissionAsked) return;
        _notificationPermissionAsked = true;
        try
        {
            var status = await Permissions.CheckStatusAsync<PostNotificationPermission>();
            if (status != PermissionStatus.Granted)
                await Permissions.RequestAsync<PostNotificationPermission>();
        }
        catch (Exception ex)
        {
            AppPaths.LogError("申请通知权限", ex);
        }
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        if (_queue.MoveNext() is { } next)
        {
            CurrentQueueIndex = _queue.CurrentIndex;
            await LoadAndPlayAsync(next);
        }
        else
        {
            StatusMessage = "已经是最后一首";
            IsPlaying = false;
        }
    }

    [RelayCommand]
    private async Task PreviousAsync()
    {
        if (_queue.MovePrevious() is { } prev)
        {
            CurrentQueueIndex = _queue.CurrentIndex;
            await LoadAndPlayAsync(prev);
        }
    }

    /// <summary>点击播放模式按钮：循环切换四种模式。</summary>
    [RelayCommand]
    private void CyclePlayMode()
    {
        PlayMode = PlayMode switch
        {
            PlayModeKind.Sequential => PlayModeKind.LoopAll,
            PlayModeKind.LoopAll => PlayModeKind.LoopOne,
            PlayModeKind.LoopOne => PlayModeKind.Shuffle,
            _ => PlayModeKind.Sequential,
        };
        StatusMessage = PlayModeName;
    }

    /// <summary>切到队列中指定位置（播放列表点选用）。</summary>
    [RelayCommand]
    private async Task PlayQueueItemAsync(Track? track)
    {
        if (track is null) return;

        var idx = IndexInQueue(track);
        if (idx >= 0)
        {
            _queue.JumpTo(idx);
            CurrentQueueIndex = _queue.CurrentIndex;
        }
        await LoadAndPlayAsync(track);
    }

    /// <summary>把曲目插到当前曲目之后播放。</summary>
    [RelayCommand]
    private void InsertNext(Track? track)
    {
        if (track is null) return;
        _queue.InsertNext(track);
        SyncQueueFromEngine();
        StatusMessage = $"「{track.Title}」将在下一首播放";
    }

    /// <summary>从队列移除曲目。</summary>
    [RelayCommand]
    private void RemoveFromQueue(Track? track)
    {
        if (track is null) return;
        if (!_queue.RemoveTrack(track))
        {
            StatusMessage = "正在播放的曲目不能从队列移除";
            return;
        }
        SyncQueueFromEngine();
    }

    /// <summary>队列内上移 / 下移（delta = -1 上移，1 下移）。</summary>
    public void MoveQueueItem(Track track, int delta)
    {
        if (_queue.MoveTrack(track, delta)) SyncQueueFromEngine();
    }

    /// <summary>清空队列（不影响正在播放的曲目）。</summary>
    [RelayCommand]
    private void ClearQueue()
    {
        var current = CurrentTrack;
        if (current is null)
        {
            _queue.SetItems([], 0);
            SyncQueueFromEngine();
            return;
        }
        _queue.SetItems([current], 0);
        SyncQueueFromEngine();
        StatusMessage = "已清空播放列表";
    }

    partial void OnVolumeChanged(double value)
    {
        _engine.Volume = (float)Math.Clamp(value, 0, 1);
        _settings.Update(s => s.DefaultVolume = value);
    }

    /// <summary>收藏状态由外部（页面）同步，因为收藏数据归 LibraryViewModel 管。</summary>
    public void SetFavoriteState(bool value) => IsFavorite = value;

    /// <summary>收藏/取消收藏按钮的回调，由页面注入（需要访问曲库数据）。</summary>
    public event EventHandler<Track>? FavoriteToggled;

    [RelayCommand]
    private void ToggleFavorite()
    {
        if (CurrentTrack is not { } track) return;
        FavoriteToggled?.Invoke(this, track);
    }

    /// <summary>进度条拖动开始。</summary>
    public void BeginSeek() => _userSeeking = true;

    /// <summary>进度条拖动结束并提交跳转。</summary>
    public void CommitSeek(double seconds)
    {
        _userSeeking = false;
        var target = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, DurationSeconds));
        _engine.Seek(target);
        PositionSeconds = target.TotalSeconds;
        Seeked?.Invoke(target.TotalSeconds);
    }

    private async Task OnTrackEndedAsync(Track track)
    {
        // 「播完当前曲目后停止」：不再续播
        if (_sleepStopAfterCurrent)
        {
            CancelSleepTimer();
            IsPlaying = false;
            PositionSeconds = 0;
            StatusMessage = "定时关闭已生效，播放已停止";
            return;
        }

        if (_queue.MoveNext() is { } next)
        {
            CurrentQueueIndex = _queue.CurrentIndex;
            await LoadAndPlayAsync(next);
        }
        else
        {
            IsPlaying = false;
            PositionSeconds = 0;
            StatusMessage = "播放列表已结束";
        }
    }

    #endregion

    #region 歌词

    private async Task LoadLyricsAsync(Track track)
    {
        LyricLines.Clear();
        CurrentLyricIndex = -1;

        // 换歌必须一并清掉上一篇的译文缓存，否则「译」会显示成上一首的翻译
        _translations = null;
        IsTranslated = false;

        OnPropertyChanged(nameof(HasLyrics));

        // 1) 本地同名 .lrc
        try
        {
            var local = new LocalLyricProvider(_parser).FetchAsync(track);
            var doc = local.IsCompleted ? local.Result : await local;
            if (doc is { Lines.Count: > 0 })
            {
                FillLyrics(doc);
                LyricStatus = string.Empty;
                return;
            }
        }
        catch (Exception ex)
        {
            AppPaths.LogError("读取本地歌词", ex, track.FilePath);
        }

        // 2) LRCLIB 在线歌词（走 Core 的缓存与兜底逻辑；关掉开关则只显示提示）
        if (!_settings.Settings.EnableOnlineLyrics)
        {
            LyricStatus = "未找到本地歌词（可在设置中开启在线歌词）";
            return;
        }

        LyricStatus = "正在匹配歌词…";
        try
        {
            var doc = await _lyrics.FetchAsync(track);
            if (doc is { Lines.Count: > 0 })
            {
                FillLyrics(doc);
                LyricStatus = string.Empty;
            }
            else
            {
                LyricStatus = "纯音乐，请欣赏";
            }
        }
        catch (Exception ex)
        {
            LyricStatus = "歌词匹配失败";
            AppPaths.LogError("在线歌词", ex, track.ToString());
        }
    }

    private void FillLyrics(LyricDocument doc)
    {
        foreach (var line in doc.Lines)
        {
            LyricLines.Add(new LyricDisplayLine
            {
                Time = line.Time,
                Text = line.Text,
                Translation = line.Translation,
            });
        }

        // 新加载的歌词先按当前配色上色（加载是异步的，可能发生在设置变更之后）
        for (var i = 0; i < LyricLines.Count; i++)
            LyricLines[i].ApplyVisual(
                i == CurrentLyricIndex, LyricTextColor, LyricHighlightColor, LyricBaseFontSize);

        OnPropertyChanged(nameof(HasLyrics));
    }

    private void UpdateCurrentLyricIndex(TimeSpan position)
    {
        if (LyricLines.Count == 0) return;

        // 歌词行已按时间排序；用当前索引向邻近推进，避免每帧全表扫描
        var idx = CurrentLyricIndex;
        if (idx < 0) idx = 0;

        while (idx + 1 < LyricLines.Count && LyricLines[idx + 1].Time <= position) idx++;
        while (idx > 0 && LyricLines[idx].Time > position) idx--;

        if (idx == CurrentLyricIndex) return;

        // 只翻转变化的两行，不整表重刷 —— 位置回调是 100ms 级的
        if (CurrentLyricIndex >= 0 && CurrentLyricIndex < LyricLines.Count)
            LyricLines[CurrentLyricIndex].ApplyVisual(
                false, LyricTextColor, LyricHighlightColor, LyricBaseFontSize);

        LyricLines[idx].ApplyVisual(
            true, LyricTextColor, LyricHighlightColor, LyricBaseFontSize);

        CurrentLyricIndex = idx;
    }

    #endregion

    private static string FormatTime(TimeSpan time) =>
        time.TotalHours >= 1
            ? $"{(int)time.TotalHours:D2}:{time.Minutes:D2}:{time.Seconds:D2}"
            : $"{time.Minutes:D2}:{time.Seconds:D2}";

    /// <summary>由页面在恢复/退出时同步当前曲目信息。</summary>
    public void SyncFromQueue()
    {
        if (_queue.Current is { } t && CurrentTrack is null)
        {
            CurrentTrack = t;
            Title = t.Title;
            Artist = t.Artist;
            HasTrack = true;
        }
    }
}
