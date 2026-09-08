using AnMusic.Models;
using NAudio.Wave;
using PlaybackState = AnMusic.Models.PlaybackState;

namespace AnMusic.Services.Audio;

/// <summary>
/// 音频播放引擎抽象，封装 NAudio 的播放/暂停/进度/音量等能力。
/// </summary>
public interface IAudioEngine : IAsyncDisposable
{
    /// <summary>当前加载的曲目。</summary>
    Track? CurrentTrack { get; }

    /// <summary>当前播放状态。</summary>
    PlaybackState State { get; }

    /// <summary>当前播放位置（可写，用于 Seek）。</summary>
    TimeSpan Position { get; set; }

    /// <summary>当前曲目总时长。</summary>
    TimeSpan Duration { get; }

    /// <summary>音量 0..1。</summary>
    float Volume { get; set; }

    /// <summary>是否静音。</summary>
    bool IsMuted { get; set; }

    /// <summary>当前音频格式。</summary>
    WaveFormat? WaveFormat { get; }

    /// <summary>加载曲目并准备播放（不自动播放）。</summary>
    Task LoadAsync(Track track, CancellationToken ct = default);

    /// <summary>开始播放。</summary>
    void Play();

    /// <summary>暂停。</summary>
    void Pause();

    /// <summary>停止并重置位置。</summary>
    void Stop();

    /// <summary>跳转到指定位置。</summary>
    void Seek(TimeSpan position);

    /// <summary>播放状态变化。</summary>
    event EventHandler<PlaybackState>? StateChanged;

    /// <summary>播放位置变化（约 10Hz）。</summary>
    event EventHandler<TimeSpan>? PositionChanged;

    /// <summary>当前曲目自然播完。</summary>
    event EventHandler<Track>? TrackEnded;

    /// <summary>播放失败（解码错误等）。</summary>
    event EventHandler<Exception>? PlaybackFailed;
}
