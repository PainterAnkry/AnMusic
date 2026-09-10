namespace AnMusic.Models;

/// <summary>
/// 音频播放状态。
/// </summary>
public enum PlaybackState
{
    /// <summary>已停止（未加载或播完）。</summary>
    Stopped,

    /// <summary>正在播放。</summary>
    Playing,

    /// <summary>已暂停。</summary>
    Paused,

    /// <summary>
    /// 正在加载/缓冲（仅在线曲目缓冲或解码器准备阶段使用）。
    /// 桌面端 NAudio 为同步加载，通常不会进入此状态；安卓端 PrepareAsync 期间会进入。
    /// </summary>
    Loading
}
