using Android.Content;
using Android.Provider;
using AnMusic.Models;
using AnMusic.Services.Metadata;
using AnMusic.Services.Providers;

namespace AnMusic.Android.Services;

/// <summary>
/// 安卓本地音乐扫描器。
/// </summary>
/// <remarks>
/// 两条路径并用：
/// 1. <b>MediaStore</b>（主）：通过系统媒体库查询音频，天然规避分区存储的目录遍历限制，
///    且元数据（标题/歌手/专辑/时长）由系统索引提供，无需逐个解码文件。
/// 2. <b>目录枚举</b>（辅）：对明确可读的目录（应用私有目录、下载目录）直接枚举文件，
///    补上 MediaStore 尚未索引的新文件。
/// 两者按文件路径去重。
///
/// <para><b>封面</b>：MediaStore 不提供内嵌封面，所以首屏先用轻量占位返回，
/// 再由 <see cref="EnrichCoversAsync"/> 后台逐首用 TagLib 读内嵌图并写入
/// <see cref="CoverCacheService"/>，通过回调增量刷新 UI（避免扫描时同步解码几百个文件卡死）。</para>
/// </remarks>
public sealed class LocalMusicScanner
{
    private static readonly string[] AudioExtensions =
        [".mp3", ".flac", ".wav", ".m4a", ".m4s", ".aac", ".ogg", ".opus", ".wma"];

    private readonly IMetadataReader _metadata;
    private readonly CoverCacheService _covers;

    public LocalMusicScanner(IMetadataReader metadata, CoverCacheService covers)
    {
        _metadata = metadata;
        _covers = covers;
    }

    /// <summary>扫描全部本地音乐（MediaStore + 目录枚举去重）。</summary>
    public IReadOnlyList<Track> Scan(IReadOnlyList<string> extraDirs, CancellationToken ct = default)
    {
        var byPath = new Dictionary<string, Track>(StringComparer.Ordinal);

        foreach (var track in QueryMediaStore())
            byPath[track.FilePath] = track;

        foreach (var dir in extraDirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in EnumerateAudio(dir))
            {
                ct.ThrowIfCancellationRequested();
                if (byPath.ContainsKey(file)) continue;
                byPath[file] = BuildTrackFromFile(file);
            }
        }

        return byPath.Values
            .OrderBy(t => t.Title, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>
    /// 后台为曲目补齐内嵌封面（本地文件专有）。
    /// 每处理一首即通过 <paramref name="onCoverReady"/> 回调通知 UI 增量刷新，
    /// 用户先看到列表、封面随后逐张出现，而不是空等。
    /// </summary>
    /// <param name="tracks">待补封面的曲目（通常是 MediaStore 返回的、CoverKey 为空的项）。</param>
    /// <param name="onCoverReady">某首曲目的封面就绪时触发（在后台线程调用，UI 侧需自行切主线程）。</param>
    public async Task EnrichCoversAsync(
        IEnumerable<Track> tracks,
        Action<Track> onCoverReady,
        CancellationToken ct = default)
    {
        var pending = tracks
            .Where(t => !string.IsNullOrEmpty(t.FilePath) && string.IsNullOrEmpty(t.CoverKey))
            .ToList();

        if (pending.Count == 0) return;

        await Task.Run(() =>
        {
            foreach (var track in pending)
            {
                if (ct.IsCancellationRequested) return;

                try
                {
                    var meta = _metadata.Read(track.FilePath);
                    var coverPath = _covers.GetOrCreate(meta.CoverBytes, meta.CoverMime);
                    if (string.IsNullOrEmpty(coverPath)) continue;

                    track.CoverKey = coverPath;   // 属性 setter 会发 PropertyChanged
                    onCoverReady(track);
                }
                catch (Exception ex)
                {
                    // 单个文件封面失败不影响其余曲目（B 站下载的 fMP4 等本身无标准标签）
                    AnMusic.Services.AppPaths.LogError("读取内嵌封面", ex, track.FilePath);
                }
            }
        }, ct);
    }

    /// <summary>从系统媒体库查询音频文件。</summary>
    private static IEnumerable<Track> QueryMediaStore()
    {
        var results = new List<Track>();
        var resolver = global::Android.App.Application.Context.ContentResolver;
        if (resolver is null) return results;

        string[] projection =
        [
            MediaStore.Audio.Media.InterfaceConsts.Id,
            MediaStore.Audio.Media.InterfaceConsts.Title,
            MediaStore.Audio.Media.InterfaceConsts.Artist,
            MediaStore.Audio.Media.InterfaceConsts.Album,
            MediaStore.Audio.Media.InterfaceConsts.Duration,
            MediaStore.Audio.Media.InterfaceConsts.Data,
        ];

        var selection = $"{MediaStore.Audio.Media.InterfaceConsts.IsMusic} != 0";
        var order = $"{MediaStore.Audio.Media.InterfaceConsts.Title} COLLATE NOCASE ASC";

        try
        {
            using var cursor = resolver.Query(
                MediaStore.Audio.Media.ExternalContentUri!, projection, selection, null, order);
            if (cursor is null) return results;

            var idCol = cursor.GetColumnIndexOrThrow(MediaStore.Audio.Media.InterfaceConsts.Id);
            var titleCol = cursor.GetColumnIndexOrThrow(MediaStore.Audio.Media.InterfaceConsts.Title);
            var artistCol = cursor.GetColumnIndexOrThrow(MediaStore.Audio.Media.InterfaceConsts.Artist);
            var albumCol = cursor.GetColumnIndexOrThrow(MediaStore.Audio.Media.InterfaceConsts.Album);
            var durCol = cursor.GetColumnIndexOrThrow(MediaStore.Audio.Media.InterfaceConsts.Duration);
            var dataCol = cursor.GetColumnIndexOrThrow(MediaStore.Audio.Media.InterfaceConsts.Data);

            while (cursor.MoveToNext())
            {
                var path = cursor.GetString(dataCol);
                if (string.IsNullOrEmpty(path)) continue;

                var title = cursor.GetString(titleCol);
                var artist = cursor.GetString(artistCol);
                var album = cursor.GetString(albumCol);
                var durationMs = cursor.GetLong(durCol);

                results.Add(new Track
                {
                    Id = path,
                    FilePath = path,
                    Title = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(path) : title,
                    Artist = string.IsNullOrWhiteSpace(artist) ? "未知艺术家" : artist,
                    Album = album ?? string.Empty,
                    Duration = TimeSpan.FromMilliseconds(Math.Max(0, durationMs)),
                    ProviderId = "local-file",
                });
            }
        }
        catch (Exception ex)
        {
            AnMusic.Services.AppPaths.LogError("查询媒体库", ex);
        }

        return results;
    }

    private static IEnumerable<string> EnumerateAudio(string rootDir)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.Device | FileAttributes.Hidden,
        };

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(rootDir, "*.*", options);
        }
        catch
        {
            return [];
        }

        return files.Where(f => AudioExtensions.Contains(
            Path.GetExtension(f).ToLowerInvariant()));
    }

    /// <summary>用 TagLib 读取元数据生成 Track（含内嵌封面）；失败时按文件名回退。</summary>
    private Track BuildTrackFromFile(string file)
    {
        try
        {
            var meta = _metadata.Read(file);
            return new Track
            {
                Id = file,
                FilePath = file,
                Title = meta.Title,
                Artist = meta.Artist,
                Album = meta.Album,
                Duration = meta.Duration,
                CoverKey = _covers.GetOrCreate(meta.CoverBytes, meta.CoverMime),
                ProviderId = "local-file",
            };
        }
        catch (Exception ex)
        {
            AnMusic.Services.AppPaths.LogError("读取音频元数据", ex, file);
            var fileName = Path.GetFileNameWithoutExtension(file);
            return new Track
            {
                Id = file,
                FilePath = file,
                Title = fileName,
                Artist = "未知艺术家",
                Album = string.Empty,
                Duration = TimeSpan.Zero,
                ProviderId = "local-file",
            };
        }
    }
}
