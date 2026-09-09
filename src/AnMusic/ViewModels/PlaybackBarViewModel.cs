using System.Windows;
using AnMusic.Models;
using AnMusic.Services.Audio;
using AnMusic.Services.Playlist;
using AnMusic.Services.Providers;
using AnMusic.Services.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnMusic.ViewModels;

/// <summary>
/// 底部播放控制栏 ViewModel：播放/暂停/上一首/下一首、进度、音量、循环/随机。
/// </summary>
public partial class PlaybackBarViewModel : ObservableObject
{
    private readonly IAudioEngine _engine;
    private readonly IPlaylistQueue _queue;
    private readonly ProviderRegistry _registry;
    private bool _isDragging;

    [ObservableProperty]
    private string _currentTitle = "未加载曲目";

    [ObservableProperty]
    private string _currentArtist = "";

    /// <summary>当前播放的曲目（用于收藏状态判断等）。</summary>
    [ObservableProperty]
    private Track? _currentTrack;

    [ObservableProperty]
    private string? _coverPath;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private bool _isBuffering;

    [ObservableProperty]
    private double _positionSeconds;

    [ObservableProperty]
    private double _durationSeconds = 1;

    [ObservableProperty]
    private double _volume = 1.0;

    [ObservableProperty]
    private bool _isLoaded;

    /// <summary>播放模式：顺序 → 列表循环 → 单曲循环 → 随机。</summary>
    public enum PlayModeKind
    {
        /// <summary>顺序播放（播完即停）。</summary>
        Sequential,
        /// <summary>列表循环。</summary>
        LoopAll,
        /// <summary>单曲循环。</summary>
        LoopOne,
        /// <summary>随机播放。</summary>
        Shuffle
    }

    [ObservableProperty]
    private PlayModeKind _playMode = PlayModeKind.Sequential;

    /// <summary>播放模式按钮图标。</summary>
    public string PlayModeIcon => PlayMode switch
    {
        PlayModeKind.LoopAll => "🔁",
        PlayModeKind.LoopOne => "🔂",
        PlayModeKind.Shuffle => "🔀",
        _ => "➡"
    };

    /// <summary>播放模式提示文本。</summary>
    public string PlayModeTip => PlayMode switch
    {
        PlayModeKind.LoopAll => "列表循环",
        PlayModeKind.LoopOne => "单曲循环",
        PlayModeKind.Shuffle => "随机播放",
        _ => "顺序播放"
    };

    /// <summary>接下来将播放的曲目（播放列表面板展示前 5 首）。</summary>
    public System.Collections.ObjectModel.ObservableCollection<Track> UpNext { get; } = [];

    partial void OnPlayModeChanged(PlayModeKind value)
    {
        // 模式映射到队列：随机 = 洗牌 + 列表循环；单曲循环 = One；顺序 = None
        _queue.Repeat = value switch
        {
            PlayModeKind.LoopAll => RepeatMode.All,
            PlayModeKind.LoopOne => RepeatMode.One,
            PlayModeKind.Shuffle => RepeatMode.All,
            _ => RepeatMode.None
        };
        _queue.Shuffle = value == PlayModeKind.Shuffle;
        OnPropertyChanged(nameof(PlayModeIcon));
        OnPropertyChanged(nameof(PlayModeTip));
        RefreshUpNext();
    }

    /// <summary>刷新"接下来播放"列表。</summary>
    public void RefreshUpNext()
    {
        UpNext.Clear();
        foreach (var t in _queue.PeekNext(5))
            UpNext.Add(t);
    }

    /// <summary>点击切换播放模式：顺序 → 列表循环 → 单曲循环 → 随机 → 顺序。</summary>
    [RelayCommand]
    private void CyclePlayMode()
    {
        PlayMode = PlayMode switch
        {
            PlayModeKind.Sequential => PlayModeKind.LoopAll,
            PlayModeKind.LoopAll => PlayModeKind.LoopOne,
            PlayModeKind.LoopOne => PlayModeKind.Shuffle,
            _ => PlayModeKind.Sequential
        };
    }

    public PlaybackBarViewModel(IAudioEngine engine, IPlaylistQueue queue, UserSettingsService settingsService, ProviderRegistry registry)
    {
        _engine = engine;
        _queue = queue;
        _registry = registry;

        // 恢复默认音量
        _volume = Math.Clamp(settingsService.Settings.DefaultVolume, 0, 1);
        _engine.Volume = (float)_volume;

        _engine.StateChanged += OnStateChanged;
        _engine.PositionChanged += OnPositionChanged;
        _engine.TrackEnded += OnTrackEnded;
        _engine.PlaybackFailed += OnPlaybackFailed;
    }

    /// <summary>队列播完且无下一首时的自动补充回调（个性电台无限续播）；返回 true 表示已追加新曲目。</summary>
    public Func<Task<bool>>? AutoRefillHandler { get; set; }

    /// <summary>用户主动 Seek（拖动进度条 / 点击进度条）时触发，参数为目标秒数。</summary>
    /// <remarks>供"在线一起听"房主端转发 seek 指令使用；不会因自然播放进度推进而触发。</remarks>
    public event Action<double>? Seeked;

    /// <summary>当前曲目被替换（LoadAndPlayAsync 加载新曲目）时触发；供"在线一起听"房主端转发 changeTrack 使用。</summary>
    public event Action<Track>? TrackChanged;

    /// <summary>播放状态切换（play ↔ pause）时触发；供"在线一起听"房主端转发 play/pause 使用。</summary>
    /// <remarks>仅在外部主动切换（用户点击或远程指令回放）时触发，IsPlaying 属性变化即代表一次状态切换。</remarks>
    public event Action<bool>? PlayStateChanged;

    /// <summary>从队列播放指定索引的曲目。</summary>
    public async Task PlayFromQueueAsync()
    {
        if (_queue.Current is Track track)
            await LoadAndPlayAsync(track);
    }

    /// <summary>加载曲目并播放。在线曲目先经 Provider 缓冲为本地文件。</summary>
    public async Task LoadAndPlayAsync(Track track)
    {
        try
        {
            // 在线源：FilePath 为空时先缓冲到本地
            if (string.IsNullOrEmpty(track.FilePath))
            {
                if (_registry.Find(track.ProviderId) is IOnlineMusicProvider online)
                {
                    IsBuffering = true;
                    try
                    {
                        await online.ResolveToLocalAsync(track);
                    }
                    finally
                    {
                        IsBuffering = false;
                    }
                }
                else
                {
                    MessageBox.Show("该曲目缺少本地文件且无对应在线源", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            await _engine.LoadAsync(track);
            IsLoaded = true;
            CurrentTrack = track;
            CurrentTitle = track.Title;
            CurrentArtist = track.Artist;
            CoverPath = track.CoverKey;
            DurationSeconds = _engine.Duration.TotalSeconds;
            PositionSeconds = 0;
            RefreshUpNext();
            TrackChanged?.Invoke(track);
            _engine.Play();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"加载失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBuffering = false;
        }
    }

    [RelayCommand]
    private void PlayPause()
    {
        if (!IsLoaded) return;
        if (_engine.State == PlaybackState.Playing)
            _engine.Pause();
        else
            _engine.Play();
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        var next = _queue.MoveNext();
        if (next is not null)
            await LoadAndPlayAsync(next);
    }

    [RelayCommand]
    private async Task PreviousAsync()
    {
        var prev = _queue.MovePrevious();
        if (prev is not null)
            await LoadAndPlayAsync(prev);
    }

    /// <summary>进度条拖动/点击开始。</summary>
    public void BeginDrag() => _isDragging = true;

    /// <summary>进度条拖动/点击结束，执行 Seek（未处于按下状态时忽略，避免重复 Seek）。</summary>
    public void EndDrag(double targetSeconds)
    {
        if (!_isDragging) return;
        _isDragging = false;
        if (!IsLoaded) return;
        var target = TimeSpan.FromSeconds(Math.Clamp(targetSeconds, 0, DurationSeconds));
        _engine.Seek(target);
        PositionSeconds = target.TotalSeconds;
        Seeked?.Invoke(target.TotalSeconds);
    }

    /// <summary>点击进度条直接跳转（不改变拖动状态，便于抓着滑块继续拖动）。</summary>
    public void SeekTo(double targetSeconds)
    {
        if (!IsLoaded) return;
        var target = TimeSpan.FromSeconds(Math.Clamp(targetSeconds, 0, DurationSeconds));
        _engine.Seek(target);
        PositionSeconds = target.TotalSeconds;
        Seeked?.Invoke(target.TotalSeconds);
    }

    private async void OnTrackEnded(object? sender, Track track)
    {
        var next = _queue.MoveNext();

        // 队列播完且电台续播回调就绪：先补充一批推荐曲目再继续
        if (next is null && AutoRefillHandler is { } refill)
        {
            try
            {
                if (await refill())
                    next = _queue.MoveNext();
            }
            catch { /* 补充失败则按无下一首处理 */ }
        }

        if (next is not null)
        {
            await Application.Current.Dispatcher.InvokeAsync(async () =>
                await LoadAndPlayAsync(next));
        }
        else
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                PositionSeconds = 0;
                IsPlaying = false;
            });
        }
    }

    private void OnStateChanged(object? sender, PlaybackState state)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            var wasPlaying = IsPlaying;
            IsPlaying = state == PlaybackState.Playing;
            if (wasPlaying != IsPlaying)
                PlayStateChanged?.Invoke(IsPlaying);
        });
    }

    private void OnPositionChanged(object? sender, TimeSpan position)
    {
        if (_isDragging) return;
        Application.Current?.Dispatcher.Invoke(() => PositionSeconds = position.TotalSeconds);
    }

    private void OnPlaybackFailed(object? sender, Exception ex)
    {
        Application.Current?.Dispatcher.Invoke(() =>
            MessageBox.Show($"播放失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error));
    }

    partial void OnVolumeChanged(double value) => _engine.Volume = (float)value;
}
