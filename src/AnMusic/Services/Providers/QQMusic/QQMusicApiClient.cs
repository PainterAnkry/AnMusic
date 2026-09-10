using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace AnMusic.Services.Providers.QQMusic;

/// <summary>QQ 音乐搜索结果曲目项。</summary>
public sealed record QQSong(string SongMid, string Title, string Artist, string Album, TimeSpan Duration, string? CoverUrl);

/// <summary>QQ 音乐 API 调用异常。</summary>
public sealed class QQMusicApiException : Exception
{
    public QQMusicApiException(string message) : base(message) { }
}

/// <summary>
/// QQ 音乐 Web API 客户端：搜索、获取播放地址、下载音频缓存。
/// 使用公开接口，未登录可搜索与试听。
/// </summary>
public sealed class QQMusicApiClient
{
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    private static readonly string CacheDir = Services.AppPaths.QqmusicCacheDir;

    private HttpClient _http;

    public QQMusicApiClient()
    {
        _http = BuildClient();
        Services.Net.HttpService.ProxyChanged += () => _http = BuildClient(); // 代理变更后重建
    }

    /// <summary>新建带 Cookie 容器的客户端，并套用当前代理设置。</summary>
    private static HttpClient BuildClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            UseCookies = true,
            CookieContainer = new System.Net.CookieContainer()
        };
        Services.Net.HttpService.ApplyProxy(handler);
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        return client;
    }

    /// <summary>按关键词搜索歌曲。</summary>
    public async Task<IReadOnlyList<QQSong>> SearchSongsAsync(string keyword, int limit = 30, CancellationToken ct = default)
    {
        var url = $"https://c.y.qq.com/soso/fcgi-bin/client_search_cp?w={Uri.EscapeDataString(keyword)}&format=json&n={limit}&p=1";
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        // QQ 音乐接口有时返回 callback(...) 包裹，去掉前缀
        if (json.StartsWith("callback(", StringComparison.Ordinal))
            json = json["callback(".Length..^1];

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var songs = new List<QQSong>();
        if (!root.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("song", out var song) ||
            !song.TryGetProperty("list", out var list) ||
            list.ValueKind != JsonValueKind.Array)
            return songs;

        foreach (var s in list.EnumerateArray())
        {
            var songMid = s.TryGetProperty("songmid", out var mid) ? mid.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(songMid)) continue;
            var title = s.TryGetProperty("songname", out var t) ? t.GetString() ?? "未知标题" : "未知标题";
            var singers = s.TryGetProperty("singer", out var sg) && sg.ValueKind == JsonValueKind.Array
                ? string.Join("/", sg.EnumerateArray().Select(x => x.TryGetProperty("name", out var sn) ? sn.GetString() ?? "" : ""))
                : "未知艺术家";
            var album = s.TryGetProperty("albumname", out var al) ? al.GetString() ?? "" : "";
            var interval = s.TryGetProperty("interval", out var iv) ? iv.GetInt32() : 0;
            var cover = $"https://y.qq.com/music/photo_new/T002R300x300M000{songMid}.jpg";

            songs.Add(new QQSong(songMid, title, singers, album, TimeSpan.FromSeconds(interval), cover));
        }
        return songs;
    }

    /// <summary>获取歌曲播放地址。</summary>
    public async Task<string?> GetPlayUrlAsync(string songMid, AudioQuality quality, CancellationToken ct = default)
    {
        var filePrefix = quality switch
        {
            AudioQuality.Lossless => "F000",
            AudioQuality.ExHigh => "M800",
            AudioQuality.Higher => "M500",
            _ => "M500"
        };
        var ext = quality == AudioQuality.Lossless ? "flac" : "mp3";
        var guid = Guid.NewGuid().ToString("N").ToUpper();
        var filename = $"{filePrefix}{songMid}.{ext}";

        var url = $"https://u.y.qq.com/cgi-bin/musicu.fcg?data=%7B%22req%22%3A%7B%22module%22%3A%22CDN.SrfCdnDispatchServer%22%2C%22method%22%3A%22GetCdnDispatch%22%2C%22param%22%3A%7B%22guid%22%3A%22{guid}%22%2C%22calltype%22%3A0%2C%22userip%22%3A%22%22%7D%7D%2C%22req_0%22%3A%7B%22module%22%3A%22vkey.GetVkeyServer%22%2C%22method%22%3A%22CgiGetVkey%22%2C%22param%22%3A%7B%22guid%22%3A%22{guid}%22%2C%22songmid%22%3A%5B%22{songMid}%22%5D%2C%22filename%22%3A%5B%22{filename}%22%5D%2C%22platform%22%3A%2220%22%7D%7D%7D";

        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

        if (!doc.RootElement.TryGetProperty("req_0", out var req0) ||
            !req0.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("midurlinfo", out var midurlinfo) ||
            midurlinfo.ValueKind != JsonValueKind.Array || midurlinfo.GetArrayLength() == 0)
            return null;

        var purl = midurlinfo[0].TryGetProperty("purl", out var p) ? p.GetString() : "";
        if (string.IsNullOrEmpty(purl)) return null;

        // 拼接 CDN 前缀
        var sip = data.TryGetProperty("sip", out var sipArr) && sipArr.ValueKind == JsonValueKind.Array && sipArr.GetArrayLength() > 0
            ? sipArr[0].GetString() ?? "https://dl.stream.qqmusic.qq.com/"
            : "https://dl.stream.qqmusic.qq.com/";
        return sip + purl;
    }

    /// <summary>下载音频到本地缓存。</summary>
    public async Task<string> DownloadAudioAsync(string audioUrl, string songMid, CancellationToken ct = default)
    {
        Directory.CreateDirectory(CacheDir);
        var ext = audioUrl.Contains(".flac", StringComparison.OrdinalIgnoreCase) ? "flac" : "m4a";
        var destPath = Path.Combine(CacheDir, $"{songMid}.{ext}");
        if (File.Exists(destPath) && new FileInfo(destPath).Length > 0)
            return destPath;

        var tmpPath = destPath + ".tmp";
        using var req = new HttpRequestMessage(HttpMethod.Get, audioUrl);
        req.Headers.TryAddWithoutValidation("Referer", "https://y.qq.com/");
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        await using var respStream = await resp.Content.ReadAsStreamAsync(ct);
        await using var fileStream = File.Create(tmpPath);
        await respStream.CopyToAsync(fileStream, ct);
        File.Move(tmpPath, destPath, true);
        return destPath;
    }

    /// <summary>清理缓存目录。</summary>
    public static void ClearCache()
    {
        if (Directory.Exists(CacheDir))
            Directory.Delete(CacheDir, true);
    }
}
