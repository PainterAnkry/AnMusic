using System.IO;
using System.Windows.Threading;
using AnMusic.Models;
using NAudio.Wave;
using PlaybackState = AnMusic.Models.PlaybackState;

namespace AnMusic.Services.Audio;

/// <summary>
/// 基于 NAudio 的音频播放引擎实现。
/// SampleProvider 链：AudioFileReader → WaveOut（Phase 5 将插入 SampleChannel + Equalizer）。
/// </summary>
public sealed class NAudioEngine : IAudioEngine
{
    private WaveOut? _waveOut;
    private AudioFileReader? _audioFileReader;
    private MediaFoundationReader? _mfReader;
    private readonly DispatcherTimer _positionTimer;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    private PlaybackState _state = PlaybackState.Stopped;
    private float _volume = 1.0f;
    private bool _isMuted;
    private bool _isDisposing;

    public Track? CurrentTrack { get; private set; }

    public PlaybackState State => _state;

    public TimeSpan Position
    {
        get
        {
            if (_audioFileReader is not null)
                return _audioFileReader.CurrentTime;
            if (_mfReader is not null)
                return BytesToTime(_mfReader.Position);
            return TimeSpan.Zero;
        }
        set
        {
            if (_audioFileReader is not null)
                _audioFileReader.CurrentTime = value;
            else if (_mfReader is not null)
                _mfReader.Position = TimeToBytes(value);
        }
    }

    public TimeSpan Duration =>
        _audioFileReader?.TotalTime ?? _mfReader?.TotalTime ?? TimeSpan.Zero;

    public float Volume
    {
        get => _isMuted ? 0f : _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            ApplyVolume();
        }
    }

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            _isMuted = value;
            ApplyVolume();
        }
    }

    public WaveFormat? WaveFormat => _audioFileReader?.WaveFormat ?? _mfReader?.WaveFormat;

    private void ApplyVolume()
    {
        var vol = _isMuted ? 0f : _volume;
        if (_audioFileReader is not null)
            _audioFileReader.Volume = vol;
        // MediaFoundationReader 无音量属性，统一用 WaveOut 设备音量兜底
        if (_waveOut is not null)
            _waveOut.Volume = vol;
    }

    private TimeSpan BytesToTime(long bytes) =>
        _mfReader is null || _mfReader.WaveFormat is null
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds((double)bytes / _mfReader.WaveFormat.AverageBytesPerSecond);

    private long TimeToBytes(TimeSpan time) =>
        _mfReader?.WaveFormat is null
            ? 0
            : (long)(time.TotalSeconds * _mfReader.WaveFormat.AverageBytesPerSecond);

    public event EventHandler<PlaybackState>? StateChanged;
    public event EventHandler<TimeSpan>? PositionChanged;
    public event EventHandler<Track>? TrackEnded;
    public event EventHandler<Exception>? PlaybackFailed;

    private readonly EqualizerService? _equalizerService;

    public NAudioEngine(EqualizerService? equalizerService = null)
    {
        _equalizerService = equalizerService;
        _positionTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _positionTimer.Tick += OnPositionTimerTick;
    }

    public async Task LoadAsync(Track track, CancellationToken ct = default)
    {
        await _loadLock.WaitAsync(ct);
        try
        {
            CleanupPlayback();
            CurrentTrack = track;

            if (string.IsNullOrEmpty(track.FilePath) || !File.Exists(track.FilePath))
                throw new FileNotFoundException("音频文件不存在", track.FilePath);

            ISampleProvider sampleSource;
            if (IsSupportedByAudioFileReader(track.FilePath))
            {
                _audioFileReader = new AudioFileReader(track.FilePath)
                {
                    Volume = _isMuted ? 0f : _volume
                };
                sampleSource = _audioFileReader;
            }
            else
            {
                // m4s/m4a/aac 等 → Windows Media Foundation（net10.0-windows 可用）
                _mfReader = new MediaFoundationReader(track.FilePath);
                sampleSource = _mfReader.ToSampleProvider();
            }

            _waveOut = new WaveOut();
            _waveOut.Volume = _isMuted ? 0f : _volume;
            _waveOut.PlaybackStopped += OnPlaybackStopped;

            // EQ 链：Source(ISampleProvider) → Equalizer → IWaveProvider → WaveOut
            if (_equalizerService is not null)
            {
                var sampleProvider = _equalizerService.CreateChain(sampleSource);
                _waveOut.Init(sampleProvider.ToWaveProvider());
            }
            else
            {
                _waveOut.Init(sampleSource.ToWaveProvider());
            }
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>AudioFileReader（跨平台版）只支持 wav/mp3/aiff/flac，其余走 MediaFoundation。</summary>
    private static bool IsSupportedByAudioFileReader(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".wav" or ".mp3" or ".aif" or ".aiff" or ".flac";
    }

    public void Play()
    {
        if (_waveOut is null) return;
        _waveOut.Play();
        SetState(PlaybackState.Playing);
        _positionTimer.Start();
    }

    public void Pause()
    {
        if (_waveOut is null) return;
        _waveOut.Pause();
        SetState(PlaybackState.Paused);
        _positionTimer.Stop();
    }

    public void Stop()
    {
        if (_waveOut is null) return;
        _positionTimer.Stop();
        _waveOut.Stop();
        if (_audioFileReader is not null)
            _audioFileReader.CurrentTime = TimeSpan.Zero;
        else if (_mfReader is not null)
            _mfReader.Position = 0;
        SetState(PlaybackState.Stopped);
    }

    public void Seek(TimeSpan position)
    {
        if (_audioFileReader is not null)
            _audioFileReader.CurrentTime = position;
        else if (_mfReader is not null)
            _mfReader.Position = TimeToBytes(position);
        PositionChanged?.Invoke(this, position);
    }

    private void SetState(PlaybackState newState)
    {
        _state = newState;
        StateChanged?.Invoke(this, newState);
    }

    private void OnPositionTimerTick(object? sender, EventArgs e)
    {
        var pos = Position;
        if (pos != TimeSpan.Zero || _audioFileReader is not null || _mfReader is not null)
            PositionChanged?.Invoke(this, pos);
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        _positionTimer.Stop();

        // 区分"自然播完"与"手动停止"
        bool reachedEnd = !_isDisposing && (
            (_audioFileReader is not null && _audioFileReader.Position >= _audioFileReader.Length)
            || (_mfReader is not null && _mfReader.Position >= _mfReader.Length));

        if (e.Exception is not null)
        {
            PlaybackFailed?.Invoke(this, e.Exception);
            SetState(PlaybackState.Stopped);
            return;
        }

        if (reachedEnd && CurrentTrack is not null)
        {
            // 重置位置，触发 TrackEnded
            if (_audioFileReader is not null)
                _audioFileReader.CurrentTime = TimeSpan.Zero;
            else if (_mfReader is not null)
                _mfReader.Position = 0;
            SetState(PlaybackState.Stopped);
            TrackEnded?.Invoke(this, CurrentTrack);
        }
        else if (!_isDisposing)
        {
            SetState(PlaybackState.Stopped);
        }
    }

    private void CleanupPlayback()
    {
        _positionTimer.Stop();
        _isDisposing = true;
        if (_waveOut is not null)
        {
            _waveOut.PlaybackStopped -= OnPlaybackStopped;
            _waveOut.Stop();
            _waveOut.Dispose();
            _waveOut = null;
        }
        _audioFileReader?.Dispose();
        _audioFileReader = null;
        _mfReader?.Dispose();
        _mfReader = null;
        _isDisposing = false;
        SetState(PlaybackState.Stopped);
    }

    public async ValueTask DisposeAsync()
    {
        await Task.Run(() => CleanupPlayback());
        _loadLock.Dispose();
    }
}
