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

    public string Id => "bilibili";
    public string DisplayName => "B站音频";
    public bool IsOnline => true;

    public BilibiliMusicProvider(BilibiliApiClient api)
    {
        _api = api;
    }

    /// <summary>关键词搜索 B 站视频，映射为 Track 列表。</summary>
    public async Task<IReadOnlyList<Track>> SearchAsync(string keyword, CancellationToken ct = default)
    {
        var videos = await _api.SearchVideosAsync(keyword, ct: ct);
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
                SourceUrl = $"https://www.bilibili.com/video/{v.Bvid}"
            });
        }
        return tracks;
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
}
