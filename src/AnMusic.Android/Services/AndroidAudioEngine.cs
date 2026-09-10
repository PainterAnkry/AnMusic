using Android.Content;
using Android.Media;
using Android.OS;
using AnMusic.Models;
using AnMusic.Services.Audio;
using PlaybackState = AnMusic.Models.PlaybackState;

namespace AnMusic.Android.Services;

/// <summary>
/// 安卓音频播放引擎：基于系统 <see cref="MediaPlayer"/> 实现 Core 的 <see cref="IAudioEngine"/>。
/// </summary>
/// <remarks>
/// 选择 MediaPlayer 而非 ExoPlayer 的原因：MVP 阶段只需要"本地文件顺序播放"，
/// 系统 MediaPlayer 无额外依赖、体积小、稳定；在线曲目在 Core 层已由 Provider
/// 缓冲到本地文件（见 <c>IOnlineMusicProvider.ResolveToLocalAsync</c>），因此这里
/// 始终播放本地路径。后续需要 HLS / 无缝切换时再替换为 Media3 ExoPlayer 即可，
/// 业务层不受影响。
/// </remarks>
public sealed class AndroidAudioEngine : IAudioEngine
{
    private readonly MediaPlayer _player = new();
    private readonly object _gate = new();

    private Track? _current;
    private PlaybackState _state = PlaybackState.Stopped;
    private TimeSpan _duration;
    private TimeSpan _pendingSeek = TimeSpan.FromMilliseconds(-1);
    private bool _prepared;
    private float _volume = 1f;
    private bool _muted;
    private bool _disposed;

    /// <summary>进度上报定时器（约 10Hz，与桌面端节奏一致）。</summary>
    private readonly System.Timers.Timer _positionTimer = new(100);

    public AndroidAudioEngine()
    {
        _player.Completion += OnCompletion;
        _player.Error += OnError;
        _player.Prepared += OnPrepared;
        _player.SeekComplete += OnSeekComplete;

        _positionTimer.AutoReset = true;
        _positionTimer.Elapsed += (_, _) => RaisePosition();
    }

    public Track? CurrentTrack => _current;

    public PlaybackState State => _state;

    public TimeSpan Position
    {
        get
        {
            try
            {
                return _prepared && _player.CurrentPosition >= 0
                    ? TimeSpan.FromMilliseconds(_player.CurrentPosition)
                    : TimeSpan.Zero;
            }
            catch
            {
                return TimeSpan.Zero;
            }
        }
        set => Seek(value);
    }

    public TimeSpan Duration => _duration;

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            ApplyVolume();
        }
    }

    public bool IsMuted
    {
        get => _muted;
        set
        {
            _muted = value;
            ApplyVolume();
        }
    }

    /// <summary>MediaPlayer 不暴露采样率等细节，给出可读的占位描述。</summary>
    public string? FormatDescription => _prepared ? "系统解码器（MediaPlayer）" : null;

    public Task LoadAsync(Track track, CancellationToken ct = default)
    {
        var path = track.FilePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            throw new FileNotFoundException("音频文件不存在（在线曲目需先缓冲到本地）", path);

        lock (_gate)
        {
            _current = track;
            _prepared = false;
            _duration = track.Duration;

            try
            {
                _player.Reset();
                _player.SetAudioAttributes(new AudioAttributes.Builder()
                    .SetUsage(AudioUsageKind.Media)!
                    .SetContentType(AudioContentType.Music)!
                    .Build()!);
                _player.SetDataSource(path);
                _player.PrepareAsync(); // 异步准备，避免阻塞调用线程（UI 线程）
                SetState(PlaybackState.Loading);
                ApplyVolume();
            }
            catch (Exception ex)
            {
                SetState(PlaybackState.Stopped);
                PlaybackFailed?.Invoke(this, ex);
                throw;
            }
        }

        return Task.CompletedTask;
    }

    public void Play()
    {
        lock (_gate)
        {
            if (!_prepared)
            {
                // 尚未 Prepare 完成：记下播放意图，Prepared 回调里补播
                _playWhenPrepared = true;
                return;
            }
            _player.Start();
            SetState(PlaybackState.Playing);
            StartTimer();
        }
    }

    private bool _playWhenPrepared;

    public void Pause()
    {
        lock (_gate)
        {
            if (!_prepared) return;
            _player.Pause();
            SetState(PlaybackState.Paused);
            StopTimer();
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_prepared)
            {
                SetState(PlaybackState.Stopped);
                return;
            }
            _player.Pause();
            _player.SeekTo(0);
            SetState(PlaybackState.Stopped);
            StopTimer();
        }
    }

    public void Seek(TimeSpan position)
    {
        lock (_gate)
        {
            if (!_prepared)
            {
                _pendingSeek = position;
                return;
            }
            var ms = (int)Math.Clamp(position.TotalMilliseconds, 0, Math.Max(0, _duration.TotalMilliseconds));
            _player.SeekTo(ms);
            RaisePosition();
        }
    }

    private void ApplyVolume()
    {
        var vol = _muted ? 0f : _volume;
        try
        {
            _player.SetVolume(vol, vol);
        }
        catch
        {
            // Prepare 之前调用会抛，忽略：Prepare 完成后会再次应用
        }
    }

    private void OnPrepared(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            _prepared = true;
            try
            {
                var ms = _player.Duration;
                if (ms > 0) _duration = TimeSpan.FromMilliseconds(ms);
            }
            catch { /* 个别流式文件拿不到时长，沿用元数据时长 */ }

            ApplyVolume();

            if (_pendingSeek >= TimeSpan.Zero)
            {
                _player.SeekTo((int)_pendingSeek.TotalMilliseconds);
                _pendingSeek = TimeSpan.FromMilliseconds(-1);
            }
        }

        if (_playWhenPrepared)
        {
            _playWhenPrepared = false;
            Play();
        }
    }

    private void OnSeekComplete(object? sender, EventArgs e) => RaisePosition();

    private void OnCompletion(object? sender, EventArgs e)
    {
        StopTimer();
        SetState(PlaybackState.Stopped);
        if (_current is { } track)
            TrackEnded?.Invoke(this, track);
    }

    private void OnError(object? sender, MediaPlayer.ErrorEventArgs e)
    {
        StopTimer();
        SetState(PlaybackState.Stopped);
        PlaybackFailed?.Invoke(this, new InvalidOperationException(
            $"MediaPlayer 错误：{e.What} / extra={e.Extra}"));
    }

    private void StartTimer()
    {
        if (!_positionTimer.Enabled) _positionTimer.Start();
    }

    private void StopTimer() => _positionTimer.Stop();

    private void RaisePosition() => PositionChanged?.Invoke(this, Position);

    private void SetState(PlaybackState state)
    {
        if (_state == state) return;
        _state = state;
        StateChanged?.Invoke(this, state);
    }

    public event EventHandler<PlaybackState>? StateChanged;
    public event EventHandler<TimeSpan>? PositionChanged;
    public event EventHandler<Track>? TrackEnded;
    public event EventHandler<Exception>? PlaybackFailed;

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;

        StopTimer();
        _positionTimer.Dispose();

        _player.Completion -= OnCompletion;
        _player.Error -= OnError;
        _player.Prepared -= OnPrepared;
        _player.SeekComplete -= OnSeekComplete;
        try { _player.Release(); } catch { }
        _player.Dispose();

        return ValueTask.CompletedTask;
    }
}
