using System.IO;
using TagLib;

namespace AnMusic.Services.Metadata;

/// <summary>
/// 基于 taglib-sharp 的元数据读取实现。
/// 注意：必须用 using 释放 TagLib.File，否则会锁文件。
/// </summary>
public sealed class TagLibMetadataReader : IMetadataReader
{
    private static readonly string[] AudioExtensions = { ".mp3", ".flac", ".wav", ".m4a", ".aac", ".wma", ".ogg" };

    public TrackMetadata Read(string filePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath);

        try
        {
            using var tfile = TagLib.File.Create(filePath);
            var tag = tfile.Tag;
            var pic = tag.Pictures.FirstOrDefault(p => p.Type == PictureType.FrontCover)
                      ?? tag.Pictures.FirstOrDefault();

            return new TrackMetadata(
                Title: string.IsNullOrWhiteSpace(tag.Title) ? fileName : tag.Title,
                Artist: tag.FirstPerformer ?? tag.FirstAlbumArtist ?? "未知艺术家",
                Album: string.IsNullOrWhiteSpace(tag.Album) ? "" : tag.Album,
                Duration: tfile.Properties.Duration,
                CoverBytes: pic?.Data.Data,
                CoverMime: pic?.MimeType);
        }
        catch
        {
            // 元数据读取失败时回退为基本信息
            return new TrackMetadata(fileName, "未知艺术家", "", TimeSpan.Zero, null, null);
        }
    }

    public static bool IsAudioFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return Array.IndexOf(AudioExtensions, ext) >= 0;
    }
}
