using System.ComponentModel;
using System.IO;
using System.Windows.Threading;
using AnMusic.ViewModels;
using Windows.Media;
using Windows.Storage.Streams;

namespace AnMusic.Services.Media;

/// <summary>
/// Windows 系统媒体会话（SMTC）：Win10/11 音量浮层的媒体卡片中显示 曲目/歌手/专辑/封面/进度，
/// 并支持卡片上的 播放 / 暂停 / 上一首 / 下一首 控制（与底部栏同一 ViewModel 命令）。
/// </summary>
public sealed class MediaSessionService : IDisposable
{
    private readonly SystemMediaTransportControls _smtc;
    private readonly PlaybackBarViewModel _bar;
    private readonly Dispatcher _dispatcher;
    private DateTime _lastTimelineUpdate = DateTime.MinValue;
    private bool _disposed;

    public MediaSessionService(IntPtr hwnd, PlaybackBarViewModel bar, Dispatcher dispatcher)
    {
        _bar = bar;
        _dispatcher = dispatcher;
        try
        {
            _smtc = SystemMediaTransportControlsInterop.GetForWindow(hwnd);
            _smtc.IsEnabled = true;
            _smtc.IsPlayEnabled = true;
            _smtc.IsPauseEnabled = true;
            _smtc.IsNextEnabled = true;
            _smtc.IsPreviousEnabled = true;
            _smtc.PlaybackStatus = MediaPlaybackStatus.Stopped;
            _smtc.ButtonPressed += OnButtonPressed;

            _bar.TrackChanged += OnTrackChanged;
            _bar.PlayStateChanged += OnPlayStateChanged;
            _bar.Seeked += OnSeeked;
            _bar.PropertyChanged += OnBarPropertyChanged;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[SMTC] 初始化失败: {ex.Message}");
        }
    }

    private void OnTrackChanged(Models.Track track) => OnUi(() => UpdateTrackDisplay(forceTimeline: true));

    private void OnPlayStateChanged(bool playing) => OnUi(() =>
    {
        try { _smtc.PlaybackStatus = playing ? MediaPlaybackStatus.Playing
                                             : MediaPlaybackStatus.Paused; }
        catch { }
    });

    private void OnSeeked(double seconds) => OnUi(() => UpdateTimeline(TimeSpan.FromSeconds(seconds)));

    private void OnBarPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed) return;
        switch (e.PropertyName)
        {
            case nameof(PlaybackBarViewModel.PositionSeconds):
                OnUi(UpdatePosition, throttled: true);
                break;
            case nameof(PlaybackBarViewModel.CoverPath):
            case nameof(PlaybackBarViewModel.CurrentTrack):
                OnUi(() => UpdateTrackDisplay(forceTimeline: false));
                break;
        }
    }

    /// <summary>曲目信息变化 → 刷新媒体卡片的标题/歌手/封面与时间轴。</summary>
    private void UpdateTrackDisplay(bool forceTimeline)
    {
        var track = _bar.CurrentTrack;
        try
        {
            var updater = _smtc.DisplayUpdater;
            updater.Type = MediaPlaybackType.Music;
            updater.MusicProperties.Title = track?.Title ?? "";
            updater.MusicProperties.Artist = track?.Artist ?? "";
            updater.MusicProperties.AlbumTitle = track?.Album ?? "";
            updater.Thumbnail = null;

            var cover = _bar.CoverPath;
            if (track is not null && !string.IsNullOrWhiteSpace(cover) && File.Exists(cover))
            {
                try { updater.Thumbnail = RandomAccessStreamReference.CreateFromUri(new Uri(cover)); }
                catch { /* 封面不可读时保持无图 */ }
            }
            updater.Update();

            if (track is null)
                _smtc.PlaybackStatus = MediaPlaybackStatus.Stopped;
            else if (forceTimeline)
                UpdateTimeline(TimeSpan.FromSeconds(Math.Max(0, _bar.PositionSeconds)));
        }
        catch { /* SMTC 更新失败不影响播放 */ }
    }

    /// <summary>进度推进：时间轴整体信息约每 1 秒同步一次，避免高频互操作（媒体卡片据此显示进度）。</summary>
    private void UpdatePosition()
    {
        if (_bar.CurrentTrack is null) return;
        if ((DateTime.UtcNow - _lastTimelineUpdate).TotalMilliseconds >= 1000)
            UpdateTimeline(TimeSpan.FromSeconds(Math.Max(0, _bar.PositionSeconds)));
    }

    private void UpdateTimeline(TimeSpan position)
    {
        try
        {
            var dur = TimeSpan.FromSeconds(Math.Max(0, _bar.DurationSeconds));
            _smtc.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
            {
                StartTime = TimeSpan.Zero,
                EndTime = dur,
                MinSeekTime = TimeSpan.Zero,
                MaxSeekTime = dur,
                Position = position
            });
            _lastTimelineUpdate = DateTime.UtcNow;
        }
        catch { }
    }

    /// <summary>系统媒体卡片的按钮事件（可能来自后台线程）→ 统一回 UI 线程执行播放命令。</summary>
    private void OnButtonPressed(SystemMediaTransportControls sender,
        SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        _dispatcher.BeginInvoke(() =>
        {
            var bar = _bar;
            switch (args.Button)
            {
                case SystemMediaTransportControlsButton.Play:
                    if (!bar.IsPlaying && bar.PlayPauseCommand.CanExecute(null))
                        bar.PlayPauseCommand.Execute(null);
                    break;
                case SystemMediaTransportControlsButton.Pause:
                    if (bar.IsPlaying && bar.PlayPauseCommand.CanExecute(null))
                        bar.PlayPauseCommand.Execute(null);
                    break;
                case SystemMediaTransportControlsButton.Next:
                    if (bar.NextCommand.CanExecute(null)) bar.NextCommand.Execute(null);
                    break;
                case SystemMediaTransportControlsButton.Previous:
                    if (bar.PreviousCommand.CanExecute(null)) bar.PreviousCommand.Execute(null);
                    break;
                case SystemMediaTransportControlsButton.Stop:
                    if (bar.IsPlaying && bar.PlayPauseCommand.CanExecute(null))
                        bar.PlayPauseCommand.Execute(null); // 停止≈暂停
                    break;
            }
        });
    }

    /// <summary>确保动作在 UI 线程执行（throttled 用于高频进度刷新时跳过低价值更新）。</summary>
    private void OnUi(Action action, bool throttled = false)
    {
        if (_disposed) return;
        if (throttled && (DateTime.UtcNow - _lastTimelineUpdate).TotalMilliseconds < 200) return;
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(action);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _bar.TrackChanged -= OnTrackChanged;
            _bar.PlayStateChanged -= OnPlayStateChanged;
            _bar.Seeked -= OnSeeked;
            _bar.PropertyChanged -= OnBarPropertyChanged;
            if (_smtc is not null)
            {
                _smtc.ButtonPressed -= OnButtonPressed;
                _smtc.IsEnabled = false;
            }
        }
        catch { }
    }
}
