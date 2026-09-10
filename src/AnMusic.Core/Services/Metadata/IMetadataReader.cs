namespace AnMusic.Services.Metadata;

/// <summary>
/// 音频元数据读取器抽象。
/// </summary>
public interface IMetadataReader
{
    TrackMetadata Read(string filePath);
}
