namespace AnMusic.Services.Metadata;

/// <summary>
/// 从音频文件读取的元数据。
/// </summary>
public sealed record TrackMetadata(
    string Title,
    string Artist,
    string Album,
    TimeSpan Duration,
    byte[]? CoverBytes,
    string? CoverMime);
