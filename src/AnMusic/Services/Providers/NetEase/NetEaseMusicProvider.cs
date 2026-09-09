using System.IO;
using AnMusic.Models;

namespace AnMusic.Services.Providers.NetEase;

/// <summary>
/// 网易云音乐在线源：搜索歌曲 → 获取播放地址 → 缓存为本地文件播放。
/// 支持多音质选择（标准/较高/极高/无损，自动降级）。
/// </summary>
public sealed class NetEaseMusicProvider : IOnlineMusicProvider
{
    private readonly NetEaseApiClient _api;
    private readonly CoverCacheService _covers;

    public string Id => "netease";
    public string DisplayName => "网易云音乐";
    public bool IsOnline => true;

    public NetEaseMusicProvider(NetEaseApiClient api, CoverCacheService covers)
    {
        _api = api;
        _covers = covers;
    }

    /// <summary>关键词搜索网易云歌曲，映射为 Track 列表（带封面缩略图）。</summary>
    public async Task<IReadOnlyList<Track>> SearchAsync(string keyword, CancellationToken ct = default)
    {
        var songs = await _api.SearchSongsAsync(keyword, ct: ct);
        var tracks = new List<Track>(songs.Count);
        foreach (var s in songs)
        {
            tracks.Add(new Track
            {
                Id = s.Id,
                Title = s.Name,
                Artist = s.Artist,
                Album = s.Album,
                Duration = s.Duration,
                FilePath = "",
                ProviderId = Id,
                SourceUrl = $"https://music.163.com/song?id={s.Id}",
                CoverUrl = s.CoverUrl ?? ""
            });
        }
        LoadCoversInBackground(tracks);
        return tracks;
    }

    /// <summary>获取排行榜曲目（供侧边栏排行榜使用）。</summary>
    public async Task<IReadOnlyList<Track>> GetTopListAsync(long topListId, CancellationToken ct = default)
    {
        var songs = await _api.GetTopListAsync(topListId, ct: ct);
        var tracks = songs.Select(s => new Track
        {
            Id = s.Id,
            Title = s.Name,
            Artist = s.Artist,
            Album = s.Album,
            Duration = s.Duration,
            FilePath = "",
            ProviderId = Id,
            SourceUrl = $"https://music.163.com/song?id={s.Id}",
            CoverUrl = s.CoverUrl ?? ""
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

    /// <summary>将网易云曲目缓冲为本地可播放文件（默认极高音质，失败降级）。</summary>
    public async Task<string> ResolveToLocalAsync(Track track, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(track.FilePath) && File.Exists(track.FilePath))
            return track.FilePath;

        // 尝试 320kbps，失败降级到 128kbps
        var url = await _api.GetPlayUrlAsync(track.Id, (int)AudioQuality.ExHigh, ct)
                  ?? await _api.GetPlayUrlAsync(track.Id, (int)AudioQuality.Standard, ct);
        if (string.IsNullOrEmpty(url))
            throw new NetEaseApiException("该曲目暂无可用音源（可能为 VIP 专属）");

        var localPath = await _api.DownloadAudioAsync(url, track.Id, ct);
        track.FilePath = localPath;
        return localPath;
    }

    /// <summary>获取可选音质列表。</summary>
    public Task<IReadOnlyList<AudioQuality>> GetAvailableQualitiesAsync(Track track, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AudioQuality>>(
            [AudioQuality.Standard, AudioQuality.Higher, AudioQuality.ExHigh, AudioQuality.Lossless]);

    /// <summary>按指定音质下载。</summary>
    public async Task<string> DownloadAsync(Track track, AudioQuality quality, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(track.FilePath) && File.Exists(track.FilePath))
            return track.FilePath;

        var url = await _api.GetPlayUrlAsync(track.Id, (int)quality, ct);
        if (string.IsNullOrEmpty(url))
            throw new NetEaseApiException($"该曲目不支持所选音质，尝试其他音质");

        var localPath = await _api.DownloadAudioAsync(url, $"{track.Id}_{(int)quality}", ct);
        track.FilePath = localPath;
        return localPath;
    }
}
