using System.IO;
using AnMusic.Models;
using AnMusic.Services.Providers.Bilibili;

namespace AnMusic.Services.Providers;

/// <summary>
/// B 站在线音乐源：搜索视频 → 提取 DASH 音频流 → 缓存为本地文件播放。
/// 仅缓存音频用于播放，不提供视频下载（遵循 B 站用户协议）。
/// </summary>
public sealed class BilibiliMusicProvider : IOnlineMusicProvider
{
    private readonly BilibiliApiClient _api;
    private readonly CoverCacheService _covers;

    public string Id => "bilibili";
    public string DisplayName => "B站音频";
    public bool IsOnline => true;

    public BilibiliMusicProvider(BilibiliApiClient api, CoverCacheService covers)
    {
        _api = api;
        _covers = covers;
    }

    /// <summary>关键词搜索 B 站视频第 1 页，映射为 Track 列表（带视频封面缩略图）。</summary>
    public async Task<IReadOnlyList<Track>> SearchAsync(string keyword, CancellationToken ct = default)
        => await SearchPageAsync(keyword, 1, ct);

    /// <summary>分页搜索（page 从 1 开始，每页约 20 条；供搜索"加载更多"逐页拉取）。</summary>
    public async Task<IReadOnlyList<Track>> SearchPageAsync(string keyword, int page, CancellationToken ct = default)
    {
        var videos = await _api.SearchVideosAsync(keyword, Math.Max(1, page), ct);
        var tracks = new List<Track>(videos.Count);
        foreach (var v in videos)
        {
            tracks.Add(new Track
            {
                Id = v.Bvid,
                Title = v.Title,
                Artist = v.Author,
                Album = AlbumOf(v.Parts),
                Duration = v.Duration,
                FilePath = "", // 播放时经 ResolveToLocalAsync 缓冲
                ProviderId = Id,
                SourceUrl = BiliTrackId.ToVideoUrl(v.Bvid, 1),
                CoverUrl = v.CoverUrl
            });
        }
        LoadCoversInBackground(tracks);
        return tracks;
    }

    /// <summary>专辑列：多分P 视频标注分P 数，用户据此知道可以右键展开全集。</summary>
    private static string AlbumOf(int parts) => parts > 1 ? $"B站视频 · 共 {parts}P" : "B站视频";

    /// <summary>
    /// 获取视频的分P（全集）列表并映射为可播放曲目。单分P 视频返回该视频本身（1 条）。
    /// </summary>
    /// <remarks>
    /// 分P 曲目与普通曲目的区别只体现在 <see cref="Track.Id"/> 上（见 <see cref="BiliTrackId"/>）：
    /// 第 1 P 沿用裸 bvid，其余为 <c>{bvid}_p{N}</c>，于是收藏/歌单/队列能把各分P 区分开，
    /// 同时第 1 P 仍与搜索结果里的同一条曲目合并。
    /// </remarks>
    public async Task<IReadOnlyList<Track>> GetVideoPartsAsync(string bvid, CancellationToken ct = default)
    {
        var info = await _api.GetVideoInfoAsync(bvid, ct);

        // 没有分P 信息（老接口/单P）时按"该视频本身"返回一条
        if (info.Parts.Count == 0)
        {
            var single = new Track
            {
                Id = bvid,
                Title = info.Title,
                Artist = info.Author,
                Album = AlbumOf(1),
                Duration = info.Duration,
                FilePath = "",
                ProviderId = Id,
                SourceUrl = BiliTrackId.ToVideoUrl(bvid, 1),
                CoverUrl = info.Cover
            };
            LoadCoversInBackground([single]);
            return [single];
        }

        var parts = new List<Track>(info.Parts.Count);
        foreach (var part in info.Parts)
        {
            // 分P 标题为空时退化成 P{序号}，否则用户只看到一片空白
            var title = string.IsNullOrWhiteSpace(part.Title) ? $"P{part.Page}" : part.Title.Trim();
            parts.Add(new Track
            {
                Id = BiliTrackId.Build(bvid, part.Page),
                Title = title,
                Artist = info.Author,
                Album = info.Parts.Count > 1 ? $"{info.Title}（{info.Parts.Count}P）" : info.Title,
                Duration = part.Duration > TimeSpan.Zero ? part.Duration : info.Duration,
                FilePath = "",
                ProviderId = Id,
                SourceUrl = BiliTrackId.ToVideoUrl(bvid, part.Page),
                CoverUrl = info.Cover
            });
        }
        LoadCoversInBackground(parts);
        return parts;
    }

    /// <summary>获取 B 站合集（专辑）内所有视频，映射为 Track 列表。</summary>
    public async Task<IReadOnlyList<Track>> GetCollectionVideosAsync(string seasonId, CancellationToken ct = default)
    {
        var videos = await _api.GetCollectionVideosAsync(seasonId, ct);
        var tracks = videos.Select(v => new Track
        {
            Id = v.Bvid,
            Title = v.Title,
            Artist = v.Author,
            Album = $"B站合集:{seasonId}",
            Duration = v.Duration,
            FilePath = "",
            ProviderId = Id,
            SourceUrl = BiliTrackId.ToVideoUrl(v.Bvid, 1),
            CoverUrl = v.CoverUrl
        }).ToList();
        LoadCoversInBackground(tracks);
        return tracks;
    }

    /// <summary>后台下载封面缩略图到本地缓存，完成后刷新 Track.CoverKey 触发 UI 更新。</summary>
    private void LoadCoversInBackground(IEnumerable<Track> tracks)
    {
        _ = Task.Run(async () =>
        {
            foreach (var t in tracks)
            {
                if (string.IsNullOrEmpty(t.CoverUrl)) continue;
                var local = await _covers.GetOrCreateFromUrlAsync(t.CoverUrl);
                if (local is not null) t.CoverKey = local;
            }
        });
    }

    /// <summary>将 B 站曲目缓冲为本地可播放文件（已缓存直接复用；分P 曲目只缓冲对应的那一 P）。</summary>
    public async Task<string> ResolveToLocalAsync(Track track, CancellationToken ct = default)
    {
        // 只认播放缓冲：FilePath 是"本地音乐文件"的语义，不能被缓冲路径占用
        if (!string.IsNullOrEmpty(track.PlaybackCachePath) && File.Exists(track.PlaybackCachePath))
            return track.PlaybackCachePath;

        // 分P 曲目的 Id 里带着"第几 P"，据此取对应 cid —— 否则会一直播第 1 P
        var (bvid, page) = BiliTrackId.Parse(track.Id);
        if (bvid.Length == 0) throw new BilibiliApiException("B 站曲目缺少视频标识");

        var info = await _api.GetVideoInfoAsync(bvid, ct);

        // 补全元数据（搜索结果可能缺封面/时长；分P 取该 P 自己的时长）
        var part = info.Parts.FirstOrDefault(p => p.Page == page);
        if (part is not null && part.Duration > TimeSpan.Zero)
            track.Duration = part.Duration;
        else if (info.Duration > TimeSpan.Zero)
            track.Duration = info.Duration;

        // 顺带补封面：播放过/下载过的曲目即使来自歌单旧数据也能显示封面
        if (string.IsNullOrEmpty(track.CoverUrl) && !string.IsNullOrEmpty(info.Cover))
        {
            track.CoverUrl = info.Cover;
            if (string.IsNullOrEmpty(track.CoverKey)) LoadCoversInBackground([track]);
        }

        var cid = info.CidOfPage(page);
        var audioUrl = await _api.GetAudioUrlAsync(bvid, cid, ct);
        var localPath = await _api.DownloadAudioAsync(audioUrl, cid, ct);

        track.PlaybackCachePath = localPath;
        return localPath;
    }

    /// <summary>B 站仅提供固定码率音频，返回单选项。</summary>
    public Task<IReadOnlyList<AudioQuality>> GetAvailableQualitiesAsync(Track track, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AudioQuality>>([AudioQuality.Standard]);

    /// <summary>B 站下载（忽略音质参数，码率由接口决定）。</summary>
    public Task<string> DownloadAsync(Track track, AudioQuality quality, CancellationToken ct = default)
        => ResolveToLocalAsync(track, ct);
}
