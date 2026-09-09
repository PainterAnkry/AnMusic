using AnMusic.Models;

namespace AnMusic.Services.Providers;

/// <summary>音频音质等级。</summary>
public enum AudioQuality
{
    /// <summary>标准 128kbps。</summary>
    Standard = 128000,
    /// <summary>较高 192kbps。</summary>
    Higher = 192000,
    /// <summary>极高 320kbps。</summary>
    ExHigh = 320000,
    /// <summary>无损 FLAC。</summary>
    Lossless = 999000
}

/// <summary>
/// 音乐源提供者抽象（可插拔架构）。
/// 本地文件、在线 API 均实现此接口，通过 DI 注册。
/// </summary>
public interface IMusicProvider
{
    string Id { get; }
    string DisplayName { get; }
    bool IsOnline { get; }

    /// <summary>扫描目录（本地源）或搜索关键词（在线源），返回曲目列表。</summary>
    Task<IReadOnlyList<Track>> SearchAsync(string keywordOrPath, CancellationToken ct = default);
}

/// <summary>
/// 在线音乐源接口：搜索 + 将在线曲目缓冲为本地可播放文件。
/// </summary>
public interface IOnlineMusicProvider : IMusicProvider
{
    /// <summary>将在线曲目解析/缓冲为本地文件并返回路径（已缓存则直接复用）。</summary>
    Task<string> ResolveToLocalAsync(Track track, CancellationToken ct = default);

    /// <summary>获取曲目可选音质列表。</summary>
    Task<IReadOnlyList<AudioQuality>> GetAvailableQualitiesAsync(Track track, CancellationToken ct = default);

    /// <summary>按指定音质下载并返回本地路径。</summary>
    Task<string> DownloadAsync(Track track, AudioQuality quality, CancellationToken ct = default);
}
