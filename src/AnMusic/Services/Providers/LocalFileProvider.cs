using System.Diagnostics;
using System.IO;
using AnMusic.Models;
using AnMusic.Services.Metadata;

namespace AnMusic.Services.Providers;

/// <summary>
/// 本地文件音乐源：递归扫描目录，读取元数据并缓存封面。
/// </summary>
public sealed class LocalFileProvider : IMusicProvider
{
    private readonly IMetadataReader _metadataReader;
    private readonly CoverCacheService _coverCache;

    public string Id => "local-file";
    public string DisplayName => "本地音乐";
    public bool IsOnline => false;

    public LocalFileProvider(IMetadataReader metadataReader, CoverCacheService coverCache)
    {
        _metadataReader = metadataReader;
        _coverCache = coverCache;
    }

    public async Task<IReadOnlyList<Track>> SearchAsync(string keywordOrPath, CancellationToken ct = default)
    {
        if (!Directory.Exists(keywordOrPath))
            return Array.Empty<Track>();

        var files = await Task.Run(() =>
        {
            return EnumerateAudioFiles(keywordOrPath).ToList();
        }, ct);

        var tracks = new List<Track>(files.Count);
        await Task.Run(() =>
        {
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                tracks.Add(CreateTrackFromFile(file));
            }
        }, ct);

        return tracks;
    }

    /// <summary>读取单个音频文件元数据生成 Track；元数据不可读时按文件名回退。</summary>
    public Track CreateTrackFromFile(string file)
    {
        try
        {
            var meta = _metadataReader.Read(file);
            var coverPath = _coverCache.GetOrCreate(meta.CoverBytes, meta.CoverMime);
            return new Track
            {
                Id = file,
                FilePath = file,
                Title = meta.Title,
                Artist = meta.Artist,
                Album = meta.Album,
                Duration = meta.Duration,
                CoverKey = coverPath,
                ProviderId = Id
            };
        }
        catch (Exception ex)
        {
            // 元数据读取失败时按文件名回退（如 B 站下载的 fMP4 音频缺 ftyp 头，TagLib 无法解析）
            Trace.WriteLine($"读取元数据失败: {file} - {ex.Message}");
            var fileName = Path.GetFileNameWithoutExtension(file);
            var sep = fileName.IndexOf(" - ", StringComparison.Ordinal);
            var title = sep > 0 ? fileName[(sep + 3)..].Trim() : fileName;
            var artist = sep > 0 ? fileName[..sep].Trim() : "未知艺术家";
            return new Track
            {
                Id = file,
                FilePath = file,
                Title = string.IsNullOrEmpty(title) ? fileName : title,
                Artist = string.IsNullOrEmpty(artist) ? "未知艺术家" : artist,
                Album = "",
                Duration = TimeSpan.Zero,
                ProviderId = Id
            };
        }
    }

    private static IEnumerable<string> EnumerateAudioFiles(string rootDir)
    {
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".mp3", ".flac", ".wav", ".m4a", ".m4s", ".aac", ".wma", ".ogg" };

        var enumOptions = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.Device | FileAttributes.Hidden
        };

        return Directory.EnumerateFiles(rootDir, "*.*", enumOptions)
            .Where(f => extensions.Contains(Path.GetExtension(f)));
    }
}
