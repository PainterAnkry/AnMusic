using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace AnMusic.Services.Providers.Bilibili;

/// <summary>B 站搜索结果视频项。</summary>
public sealed record BiliVideo(string Bvid, string Title, string Author, TimeSpan Duration, string CoverUrl);

/// <summary>B 站 API 调用异常（含风控提示）。</summary>
public sealed class BilibiliApiException : Exception
{
    public BilibiliApiException(string message) : base(message) { }
}

/// <summary>
/// B 站 Web API 客户端：wbi 签名搜索、视频信息、DASH 音频流地址、音频缓存下载。
/// 未登录即可使用（音频清晰度受限为 64~192kbps，足够听歌）。
/// </summary>
public sealed class BilibiliApiClient
{
    private const string BaseUrl = "https://api.bilibili.com";
    private const string Referer = "https://www.bilibili.com/";
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnMusic", "audio-cache");

    private readonly HttpClient _http;
    private readonly HttpClientHandler _handler;
    private (string ImgKey, string SubKey, DateTime FetchedAt)? _wbiKeys;
    private bool _primed;

    public BilibiliApiClient()
    {
        _handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            CookieContainer = new System.Net.CookieContainer(),
            UseCookies = true
        };
        _http = new HttpClient(_handler) { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", Referer);
    }

    /// <summary>
    /// 预热：先访问一次主站，让服务器下发真实 buvid3/b_nut 等 Cookie（由 CookieContainer 持有）。
    /// 这是对抗 412 风控的关键 —— 缺少真实 Cookie 指纹时 view/playurl 接口会直接 412。
    /// </summary>
    private async Task EnsurePrimedAsync(CancellationToken ct)
    {
        if (_primed) return;
        _primed = true; // 失败也不重试，避免每次请求都卡
        try
        {
            using var resp = await _http.GetAsync("https://www.bilibili.com/", ct);
        }
        catch
        {
            // 预热失败不阻塞后续请求
        }
    }

    /// <summary>调用 API 并解析 JSON；code != 0 且非已知"无需登录"接口时抛出友好异常。referer 可指定视频页防 412。</summary>
    private async Task<JsonElement> GetDataAsync(string pathAndQuery, CancellationToken ct, string? referer = null)
    {
        await EnsurePrimedAsync(ct);

        using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}{pathAndQuery}");
        if (referer is not null)
            req.Headers.TryAddWithoutValidation("Referer", referer);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;
        var code = root.TryGetProperty("code", out var c) ? c.GetInt32() : -1;

        if (code == -412 || code == -352)
            throw new BilibiliApiException("B 站请求被风控限制，请稍后再试");

        // -101 = 未登录，但 nav 接口仍返回 wbi_img 数据，应放行
        if (code != 0 && code != -101)
        {
            var msg = root.TryGetProperty("message", out var m) ? m.GetString() : null;
            throw new BilibiliApiException($"B 站接口错误 ({code}): {msg}");
        }
        // 复制出独立元素（doc 即将释放）
        return root.GetProperty("data").Clone();
    }

    /// <summary>获取（并缓存 2 小时）wbi 签名密钥。</summary>
    private async Task<(string, string)> GetWbiKeysAsync(CancellationToken ct)
    {
        if (_wbiKeys is { } keys && DateTime.UtcNow - keys.FetchedAt < TimeSpan.FromHours(2))
            return (keys.ImgKey, keys.SubKey);

        var data = await GetDataAsync("/x/web-interface/nav", ct);
        var wbi = data.GetProperty("wbi_img");
        var imgKey = Path.GetFileNameWithoutExtension(new Uri(wbi.GetProperty("img_url").GetString()!).LocalPath);
        var subKey = Path.GetFileNameWithoutExtension(new Uri(wbi.GetProperty("sub_url").GetString()!).LocalPath);
        _wbiKeys = (imgKey, subKey, DateTime.UtcNow);
        return (imgKey, subKey);
    }

    /// <summary>发起 wbi 签名 GET 请求。</summary>
    private async Task<JsonElement> GetWbiSignedAsync(string path, Dictionary<string, string> parameters, CancellationToken ct)
    {
        var (imgKey, subKey) = await GetWbiKeysAsync(ct);
        var query = WbiSign.Sign(parameters, WbiSign.GetMixinKey(imgKey, subKey));
        return await GetDataAsync($"{path}?{query}", ct);
    }

    /// <summary>按关键词搜索视频（返回音频可播的曲目列表）。</summary>
    public async Task<IReadOnlyList<BiliVideo>> SearchVideosAsync(string keyword, int page = 1, CancellationToken ct = default)
    {
        var data = await GetWbiSignedAsync("/x/web-interface/wbi/search/type", new Dictionary<string, string>
        {
            ["search_type"] = "video",
            ["keyword"] = keyword,
            ["page"] = page.ToString()
        }, ct);

        var videos = new List<BiliVideo>();
        if (!data.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
            return videos;

        foreach (var item in result.EnumerateArray())
        {
            if (!item.TryGetProperty("bvid", out var bvidEl)) continue;
            var bvid = bvidEl.GetString() ?? "";
            if (bvid.Length == 0) continue;

            var title = CleanTitle(item.TryGetProperty("title", out var t) ? t.GetString() : null);
            var author = item.TryGetProperty("author", out var a) ? a.GetString() ?? "" : "";
            var length = ParseLength(item.TryGetProperty("length", out var l) ? l.GetString() : null);
            var cover = item.TryGetProperty("pic", out var p) ? p.GetString() : null;

            videos.Add(new BiliVideo(bvid, title, author, length, cover ?? ""));
        }
        return videos;
    }

    /// <summary>获取视频详情（cid、标题、UP 主、时长、封面）。</summary>
    public async Task<(string Cid, string Title, string Author, TimeSpan Duration, string Cover)> GetVideoInfoAsync(string bvid, CancellationToken ct = default)
    {
        var referer = $"https://www.bilibili.com/video/{bvid}/";
        var data = await GetDataAsync($"/x/web-interface/view?bvid={Uri.EscapeDataString(bvid)}", ct, referer);
        var cid = data.GetProperty("cid").GetInt64().ToString();
        var title = CleanTitle(data.TryGetProperty("title", out var t) ? t.GetString() : null);
        var author = data.TryGetProperty("owner", out var o) && o.TryGetProperty("name", out var n)
            ? n.GetString() ?? "" : "";
        var durationSec = data.TryGetProperty("duration", out var d) ? d.GetInt32() : 0;
        var cover = data.TryGetProperty("pic", out var p) ? p.GetString() ?? "" : "";
        return (cid, title, author, TimeSpan.FromSeconds(durationSec), cover);
    }

    /// <summary>取 DASH 音频流中码率最高的 baseUrl（未登录可用）。</summary>
    public async Task<string> GetAudioUrlAsync(string bvid, string cid, CancellationToken ct = default)
    {
        var referer = $"https://www.bilibili.com/video/{bvid}/";
        var data = await GetDataAsync($"/x/player/playurl?bvid={Uri.EscapeDataString(bvid)}&cid={Uri.EscapeDataString(cid)}&fnval=16", ct, referer);

        if (!data.TryGetProperty("dash", out var dash) ||
            !dash.TryGetProperty("audio", out var audios) ||
            audios.ValueKind != JsonValueKind.Array || audios.GetArrayLength() == 0)
            throw new BilibiliApiException("该视频没有可用的音频流");

        JsonElement best = default;
        long bestBandwidth = -1;
        foreach (var a in audios.EnumerateArray())
        {
            var bw = a.TryGetProperty("bandwidth", out var b) ? b.GetInt64() : 0;
            if (bw > bestBandwidth)
            {
                bestBandwidth = bw;
                best = a.Clone();
            }
        }

        var url = best.TryGetProperty("baseUrl", out var bu) ? bu.GetString()
                : best.TryGetProperty("base_url", out var bu2) ? bu2.GetString() : null;
        if (string.IsNullOrEmpty(url))
            throw new BilibiliApiException("音频流地址获取失败");
        return url;
    }

    /// <summary>下载音频流到本地缓存，返回文件路径（已缓存直接返回）。</summary>
    public async Task<string> DownloadAudioAsync(string audioUrl, string bvid, CancellationToken ct = default)
    {
        Directory.CreateDirectory(CacheDir);
        var destPath = Path.Combine(CacheDir, $"{bvid}.m4s");
        if (File.Exists(destPath) && new FileInfo(destPath).Length > 0)
            return destPath;

        var tmpPath = destPath + ".tmp";
        await using (var respStream = await _http.GetStreamAsync(audioUrl, ct))
        await using (var fileStream = File.Create(tmpPath))
        {
            await respStream.CopyToAsync(fileStream, ct);
        }
        File.Move(tmpPath, destPath, true);
        return destPath;
    }

    /// <summary>获取合集（专辑）视频列表，返回该合集内所有视频。</summary>
    public async Task<IReadOnlyList<BiliVideo>> GetCollectionVideosAsync(string seasonId, CancellationToken ct = default)
    {
        var videos = new List<BiliVideo>();
        var page = 1;
        while (true)
        {
            var data = await GetDataAsync(
                $"/x/polymer/web-space/seasons_archives_list?season_id={Uri.EscapeDataString(seasonId)}&sort_reverse=false&page_num={page}&page_size=30", ct);

            if (!data.TryGetProperty("archives", out var archives) || archives.ValueKind != JsonValueKind.Array)
                break;

            foreach (var item in archives.EnumerateArray())
            {
                if (!item.TryGetProperty("bvid", out var bvidEl)) continue;
                var bvid = bvidEl.GetString() ?? "";
                if (bvid.Length == 0) continue;

                var title = CleanTitle(item.TryGetProperty("title", out var t) ? t.GetString() : null);
                var author = item.TryGetProperty("owner", out var o) && o.TryGetProperty("name", out var n)
                    ? n.GetString() ?? "" : "";
                var length = ParseLength(item.TryGetProperty("duration", out var l) ? l.GetInt32().ToString() : null);
                var cover = item.TryGetProperty("cover", out var p) ? p.GetString() : null;

                videos.Add(new BiliVideo(bvid, title, author, length, cover ?? ""));
            }

            // 检查是否有下一页
            if (!data.TryGetProperty("page", out var pageInfo) ||
                !pageInfo.TryGetProperty("page_num", out var pn) || pn.GetInt32() >= pageInfo.GetProperty("total_page").GetInt32())
                break;
            page++;
        }
        return videos;
    }

    /// <summary>清理音频缓存目录。</summary>
    public static void ClearAudioCache()
    {
        if (Directory.Exists(CacheDir))
            Directory.Delete(CacheDir, true);
    }

    /// <summary>去除搜索标题中的关键词高亮标签。</summary>
    private static string CleanTitle(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "未知标题";
        return raw
            .Replace("<em class=\"keyword\">", "")
            .Replace("</em>", "")
            .Trim();
    }

    /// <summary>解析 "MM:SS" 或 "HH:MM:SS" 时长。</summary>
    private static TimeSpan ParseLength(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return TimeSpan.Zero;
        var parts = raw.Split(':');
        return parts.Length switch
        {
            2 => TimeSpan.ParseExact(raw, @"mm\:ss", null),
            3 => TimeSpan.ParseExact(raw, @"hh\:mm\:ss", null),
            _ => TimeSpan.Zero
        };
    }
}
