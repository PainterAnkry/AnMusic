using System.Collections.ObjectModel;
using AnMusic.Models;
using AnMusic.Services;
using AnMusic.Services.Audio;
using AnMusic.Services.Lyrics;
using AnMusic.Services.Playlist;
using AnMusic.Services.Settings;
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

    /// <summary>进度拖动中：此时忽略引擎上报的位置，避免滑块被"拽回去"。</summary>
    private bool _userSeeking;

    public PlayerViewModel(
        IAudioEngine engine,
        IPlaylistQueue queue,
        UserSettingsService settings,
        LrclibLyricProvider lyrics,
        ILrcParser parser,
        IPlatformContext platform)
    {
        _engine = engine;
        _queue = queue;
        _settings = settings;
        _lyrics = lyrics;
        _parser = parser;
        _platform = platform;

        Volume = Math.Clamp(settings.Settings.DefaultVolume, 0, 1);
        _engine.Volume = (float)Volume;

        _engine.StateChanged += (_, state) => MainThread.BeginInvokeOnMainThread(() =>
        {
            IsPlaying = state == PlaybackState.Playing;
            IsBuffering = state == PlaybackState.Loading;
        });

        _engine.PositionChanged += (_, pos) => MainThread.BeginInvokeOnMainThread(() =>
        {
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

    /// <summary>进度条最大/最小值，供 Slider 直接绑定。</summary>
    public double DurationForSlider => Math.Max(1, DurationSeconds);

    partial void OnDurationSecondsChanged(double value)
    {
        OnPropertyChanged(nameof(DurationForSlider));
        OnPropertyChanged(nameof(ProgressFraction));
        OnPropertyChanged(nameof(ProgressWidth));
        DurationText = FormatTime(TimeSpan.FromSeconds(value));
    }

    /// <summary>播放/暂停按钮图标。</summary>
    public string PlayPauseIcon => IsPlaying ? "⏸" : "▶";

    partial void OnIsPlayingChanged(bool value)
    {
        OnPropertyChanged(nameof(PlayPauseIcon));
        OnPropertyChanged(nameof(PlayStateText));
    }

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
        OnPropertyChanged(nameof(ProgressWidth));
    }

    /// <summary>播放进度比例（0~1）。</summary>
    public double ProgressFraction => DurationSeconds <= 0
        ? 0
        : Math.Clamp(PositionSeconds / DurationSeconds, 0, 1);

    /// <summary>
    /// 迷你播放条上那条细进度线的像素宽度。
    /// MAUI 没有「百分比宽度」，这里按 360dp 的典型手机宽度近似换算；
    /// 只是装饰性指示，偏差几个像素无影响。
    /// </summary>
    public double ProgressWidth => ProgressFraction * 360;

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

    /// <summary>当前曲目的歌词行（含时间戳与文本），供歌词页渲染。</summary>
    public ObservableCollection<LyricLine> LyricLines { get; } = [];

    [ObservableProperty] private int _currentLyricIndex = -1;
    [ObservableProperty] private string _lyricStatus = string.Empty;

    /// <summary>是否有歌词可显示。</summary>
    public bool HasLyrics => LyricLines.Count > 0;

    /// <summary>队列长度说明。</summary>
    public string QueueCountText => $"{Queue.Count} 首";

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
        Queue.Clear();
        foreach (var t in list) Queue.Add(t);
        OnPropertyChanged(nameof(QueueCountText));

        if (_queue.Current is { } current)
            await LoadAndPlayAsync(current);
    }

    /// <summary>加载曲目并开始播放。</summary>
    public async Task LoadAndPlayAsync(Track track)
    {
        try
        {
            StatusMessage = string.Empty;
            IsBuffering = true;

            await _engine.LoadAsync(track);

            CurrentTrack = track;
            Title = track.Title;
            Artist = track.Artist;
            CoverPath = string.IsNullOrEmpty(track.CoverKey)
                ? null
                : _platform.ToImageSourceUri(track.CoverKey);
            HasTrack = true;

            DurationSeconds = _engine.Duration.TotalSeconds > 0
                ? _engine.Duration.TotalSeconds
                : Math.Max(1, track.Duration.TotalSeconds);
            PositionSeconds = 0;

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

    [RelayCommand]
    private async Task PlayPauseAsync()
    {
        if (!HasTrack)
        {
            if (_queue.Current is { } t) await LoadAndPlayAsync(t);
            return;
        }

        if (_engine.State == PlaybackState.Playing) _engine.Pause();
        else _engine.Play();
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        if (_queue.MoveNext() is { } next) await LoadAndPlayAsync(next);
        else
        {
            StatusMessage = "已经是最后一首";
            IsPlaying = false;
        }
    }

    [RelayCommand]
    private async Task PreviousAsync()
    {
        if (_queue.MovePrevious() is { } prev) await LoadAndPlayAsync(prev);
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

    /// <summary>切到队列中指定位置（播放列表弹层点选用）。</summary>
    [RelayCommand]
    private async Task PlayQueueItemAsync(Track? track)
    {
        if (track is null) return;
        await LoadAndPlayAsync(track);
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
    }

    private async Task OnTrackEndedAsync(Track track)
    {
        if (_queue.MoveNext() is { } next)
            await LoadAndPlayAsync(next);
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

        // 2) LRCLIB 在线歌词（走 Core 的缓存与兜底逻辑）
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
            LyricLines.Add(line);
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
