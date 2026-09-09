using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace AnMusic.Services.Providers.NetEase;

/// <summary>网易云搜索结果曲目项。</summary>
public sealed record NetEaseSong(string Id, string Name, string Artist, string Album, TimeSpan Duration, string? CoverUrl);

/// <summary>网易云 API 调用异常。</summary>
public sealed class NetEaseApiException : Exception
{
    public NetEaseApiException(string message) : base(message) { }
}

/// <summary>
/// 网易云音乐 Web API 客户端：搜索、获取播放地址、下载音频缓存。
/// 使用公开接口，未登录可搜索与试听（部分高音质需登录，自动降级）。
/// </summary>
public sealed class NetEaseApiClient
{
    private const string BaseUrl = "https://music.163.com";
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnMusic", "netease-cache");

    private readonly HttpClient _http;

    public NetEaseApiClient()
    {
        _http = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            UseCookies = true,
            CookieContainer = new System.Net.CookieContainer()
        }) { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        _http.DefaultRequestHeaders.Referrer = new Uri("https://music.163.com/");
    }

    /// <summary>按关键词搜索歌曲（type=1 单曲）。</summary>
    public async Task<IReadOnlyList<NetEaseSong>> SearchSongsAsync(string keyword, int limit = 30, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/api/search/get?s={Uri.EscapeDataString(keyword)}&type=1&offset=0&limit={limit}";
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;

        var songs = new List<NetEaseSong>();
        if (!root.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("songs", out var songsArr) ||
            songsArr.ValueKind != JsonValueKind.Array)
            return songs;

        foreach (var s in songsArr.EnumerateArray())
        {
            var id = s.TryGetProperty("id", out var idEl) ? idEl.GetInt64().ToString() : "";
            if (string.IsNullOrEmpty(id)) continue;
            var name = s.TryGetProperty("name", out var n) ? n.GetString() ?? "未知标题" : "未知标题";
            var artists = s.TryGetProperty("artists", out var a) && a.ValueKind == JsonValueKind.Array
                ? string.Join("/", a.EnumerateArray().Select(x => x.TryGetProperty("name", out var an) ? an.GetString() ?? "" : ""))
                : "未知艺术家";
            var album = s.TryGetProperty("album", out var al) && al.TryGetProperty("name", out var aln)
                ? aln.GetString() ?? "" : "";
            var durationMs = s.TryGetProperty("duration", out var d) ? d.GetInt64() : 0;
            var cover = s.TryGetProperty("album", out var al2) && al2.TryGetProperty("picUrl", out var pic)
                ? pic.GetString() : null;

            songs.Add(new NetEaseSong(id, name, artists, album, TimeSpan.FromMilliseconds(durationMs), cover));
        }
        return songs;
    }

    /// <summary>获取歌曲播放地址（br 为码率，默认 320000）。返回 null 表示无可用音源。</summary>
    public async Task<string?> GetPlayUrlAsync(string songId, int br = 320000, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/api/song/enhance/player/url?ids=[{songId}]&br={br}";
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            return null;

        var item = data[0];
        if (item.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
            return u.GetString();
        return null;
    }

    /// <summary>获取歌曲详情（封面、专辑等）。</summary>
    public async Task<(string? CoverUrl, string Album)> GetSongDetailAsync(string songId, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/api/song/detail?ids=[{songId}]";
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (!doc.RootElement.TryGetProperty("songs", out var songs) ||
            songs.ValueKind != JsonValueKind.Array || songs.GetArrayLength() == 0)
            return (null, "");

        var s = songs[0];
        var cover = s.TryGetProperty("album", out var al) && al.TryGetProperty("picUrl", out var pic)
            ? pic.GetString() : null;
        var album = s.TryGetProperty("album", out var al2) && al2.TryGetProperty("name", out var aln)
            ? aln.GetString() ?? "" : "";
        return (cover, album);
    }

    /// <summary>下载音频到本地缓存，返回文件路径（已缓存直接返回）。</summary>
    public async Task<string> DownloadAudioAsync(string audioUrl, string songId, CancellationToken ct = default)
    {
        Directory.CreateDirectory(CacheDir);
        var ext = audioUrl.Contains(".flac", StringComparison.OrdinalIgnoreCase) ? "flac" : "mp3";
        var destPath = Path.Combine(CacheDir, $"{songId}.{ext}");
        if (File.Exists(destPath) && new FileInfo(destPath).Length > 0)
            return destPath;

        var tmpPath = destPath + ".tmp";
        using var req = new HttpRequestMessage(HttpMethod.Get, audioUrl);
        req.Headers.TryAddWithoutValidation("Referer", "https://music.163.com/");
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        await using var respStream = await resp.Content.ReadAsStreamAsync(ct);
        await using var fileStream = File.Create(tmpPath);
        await respStream.CopyToAsync(fileStream, ct);
        File.Move(tmpPath, destPath, true);
        return destPath;
    }

    /// <summary>获取排行榜曲目列表。</summary>
    public async Task<IReadOnlyList<NetEaseSong>> GetTopListAsync(long topListId, int limit = 50, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/api/playlist/detail?id={topListId}&limit={limit}";
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var songs = new List<NetEaseSong>();
        if (!doc.RootElement.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("tracks", out var tracks) ||
            tracks.ValueKind != JsonValueKind.Array)
            return songs;

        foreach (var s in tracks.EnumerateArray().Take(limit))
        {
            var id = s.TryGetProperty("id", out var idEl) ? idEl.GetInt64().ToString() : "";
            if (string.IsNullOrEmpty(id)) continue;
            var name = s.TryGetProperty("name", out var n) ? n.GetString() ?? "未知标题" : "未知标题";
            var artists = s.TryGetProperty("ar", out var a) && a.ValueKind == JsonValueKind.Array
                ? string.Join("/", a.EnumerateArray().Select(x => x.TryGetProperty("name", out var an) ? an.GetString() ?? "" : ""))
                : "未知艺术家";
            var album = s.TryGetProperty("al", out var al) && al.TryGetProperty("name", out var aln)
                ? aln.GetString() ?? "" : "";
            var durationMs = s.TryGetProperty("dt", out var d) ? d.GetInt64() : 0;
            var cover = s.TryGetProperty("al", out var al2) && al2.TryGetProperty("picUrl", out var pic)
                ? pic.GetString() : null;
            songs.Add(new NetEaseSong(id, name, artists, album, TimeSpan.FromMilliseconds(durationMs), cover));
        }
        return songs;
    }

    /// <summary>清理缓存目录。</summary>
    public static void ClearCache()
    {
        if (Directory.Exists(CacheDir))
            Directory.Delete(CacheDir, true);
    }
}
