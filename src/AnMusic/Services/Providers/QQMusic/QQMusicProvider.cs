using System.IO;
using AnMusic.Models;

namespace AnMusic.Services.Providers.QQMusic;

/// <summary>
/// QQ 音乐在线源：搜索歌曲 → 获取播放地址 → 缓存为本地文件播放。
/// 支持多音质选择。
/// </summary>
public sealed class QQMusicProvider : IOnlineMusicProvider
{
    private readonly QQMusicApiClient _api;
    private readonly CoverCacheService _covers;

    public string Id => "qqmusic";
    public string DisplayName => "QQ音乐";
    public bool IsOnline => true;

    public QQMusicProvider(QQMusicApiClient api, CoverCacheService covers)
    {
        _api = api;
        _covers = covers;
    }

    /// <summary>关键词搜索 QQ 音乐歌曲，映射为 Track 列表（带封面缩略图）。</summary>
    public async Task<IReadOnlyList<Track>> SearchAsync(string keyword, CancellationToken ct = default)
    {
        var songs = await _api.SearchSongsAsync(keyword, ct: ct);
        var tracks = new List<Track>(songs.Count);
        foreach (var s in songs)
        {
            tracks.Add(new Track
            {
                Id = s.SongMid,
                Title = s.Title,
                Artist = s.Artist,
                Album = s.Album,
                Duration = s.Duration,
                FilePath = "",
                ProviderId = Id,
                SourceUrl = $"https://y.qq.com/n/ryqq/songDetail/{s.SongMid}",
                CoverUrl = s.CoverUrl ?? ""
            });
        }
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

    /// <summary>将 QQ 音乐曲目缓冲为本地可播放文件（默认极高音质，失败降级）。</summary>
    public async Task<string> ResolveToLocalAsync(Track track, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(track.FilePath) && File.Exists(track.FilePath))
            return track.FilePath;

        var url = await _api.GetPlayUrlAsync(track.Id, AudioQuality.ExHigh, ct)
                  ?? await _api.GetPlayUrlAsync(track.Id, AudioQuality.Standard, ct);
        if (string.IsNullOrEmpty(url))
            throw new QQMusicApiException("该曲目暂无可用音源（可能为 VIP 专属）");

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

        var url = await _api.GetPlayUrlAsync(track.Id, quality, ct);
        if (string.IsNullOrEmpty(url))
            throw new QQMusicApiException($"该曲目不支持所选音质，尝试其他音质");

        var localPath = await _api.DownloadAudioAsync(url, $"{track.Id}_{(int)quality}", ct);
        track.FilePath = localPath;
        return localPath;
    }
}
