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
                Album = "B站视频",
                Duration = v.Duration,
                FilePath = "", // 播放时经 ResolveToLocalAsync 缓冲
                ProviderId = Id,
                SourceUrl = $"https://www.bilibili.com/video/{v.Bvid}",
                CoverUrl = v.CoverUrl
            });
        }
        LoadCoversInBackground(tracks);
        return tracks;
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
            SourceUrl = $"https://www.bilibili.com/video/{v.Bvid}",
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

    /// <summary>将 B 站曲目缓冲为本地可播放文件（已缓存直接复用）。</summary>
    public async Task<string> ResolveToLocalAsync(Track track, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(track.FilePath) && File.Exists(track.FilePath))
            return track.FilePath;

        var bvid = track.Id;
        var info = await _api.GetVideoInfoAsync(bvid, ct);
        // 补全元数据（搜索结果可能缺封面/时长）
        track.Duration = info.Duration > TimeSpan.Zero ? info.Duration : track.Duration;

        var audioUrl = await _api.GetAudioUrlAsync(bvid, info.Cid, ct);
        var localPath = await _api.DownloadAudioAsync(audioUrl, bvid, ct);

        track.FilePath = localPath;
        return localPath;
    }

    /// <summary>B 站仅提供固定码率音频，返回单选项。</summary>
    public Task<IReadOnlyList<AudioQuality>> GetAvailableQualitiesAsync(Track track, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AudioQuality>>([AudioQuality.Standard]);

    /// <summary>B 站下载（忽略音质参数，码率由接口决定）。</summary>
    public Task<string> DownloadAsync(Track track, AudioQuality quality, CancellationToken ct = default)
        => ResolveToLocalAsync(track, ct);
}
