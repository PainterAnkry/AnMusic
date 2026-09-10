using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using AnMusic.Models;
using AnMusic.Services.Providers;

namespace AnMusic.Services.Lyrics;

/// <summary>
/// LRCLIB 在线歌词源（https://lrclib.net，开源社区歌词库，免费、无需密钥）。
/// 优先精确匹配（api/get），失败后模糊搜索（api/search）。
/// </summary>
public sealed class LrclibLyricProvider : IOnlineLyricProvider, ILyricProvider
{
    private const string BaseUrl = "https://lrclib.net";

    private readonly HttpClient _http;
    private readonly ILrcParser _parser;

    public string Id => "lrclib";
    public string DisplayName => "LRCLIB 开源歌词库";

    public LrclibLyricProvider(ILrcParser parser)
    {
        _parser = parser;
        _http = Services.Net.HttpService.Client; // 统一出口：含 UA / 超时 / 代理设置
    }

    /// <summary>按曲目信息获取 LRC 文本（未找到返回 null）。</summary>
    public async Task<string?> GetLrcAsync(string title, string artist, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;

        // 0) 先看本地歌词缓存（命中即用，断网/限流也能显示上次拿到的歌词）
        if (TryReadCache(title, artist) is { Length: > 0 } cached)
            return cached;

        // 1) 精确匹配
        var url = $"{BaseUrl}/api/get?track_name={Uri.EscapeDataString(title)}" +
                  (string.IsNullOrWhiteSpace(artist) ? "" : $"&artist_name={Uri.EscapeDataString(artist)}");
        var result = await TryGetJsonAsync(url, ct);
        var lrc = ExtractSynced(result) ?? ExtractPlain(result);
        if (!string.IsNullOrEmpty(lrc))
            return WriteCache(title, artist, lrc);

        // 2) 模糊搜索，取第一条含同步歌词的结果
        var searchUrl = $"{BaseUrl}/api/search?track_name={Uri.EscapeDataString(title)}" +
                        (string.IsNullOrWhiteSpace(artist) ? "" : $"&artist_name={Uri.EscapeDataString(artist)}");
        using var resp = await _http.GetAsync(searchUrl, ct);
        if (!resp.IsSuccessStatusCode)
            return null;

        var items = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement[]>(cancellationToken: ct);
        if (items is { Length: > 0 })
        {
            foreach (var item in items)
            {
                lrc = ExtractSynced(item);
                if (!string.IsNullOrEmpty(lrc))
                    return WriteCache(title, artist, lrc);
            }
            lrc = ExtractPlain(items[0]);
            if (!string.IsNullOrEmpty(lrc))
                return WriteCache(title, artist, lrc);
        }
        return null;
    }

    #region 歌词落盘缓存（%AppData%\AnMusic\lyrics）

    /// <summary>缓存键：歌名 + 歌手（小写、去空白）的 SHA1，避免文件名非法字符。</summary>
    private static string CacheKey(string title, string artist)
    {
        var raw = (title.Trim() + "|" + (artist ?? "").Trim()).ToLowerInvariant();
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(raw)));
    }

    /// <summary>读取缓存歌词（不存在/读失败返回 null）。</summary>
    public static string? TryReadCache(string title, string artist)
    {
        try
        {
            var path = Path.Combine(Services.AppPaths.LyricsDir, CacheKey(title, artist) + ".lrc");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex)
        {
            Services.AppPaths.LogError("读取歌词缓存", ex, title);
            return null;
        }
    }

    /// <summary>写入缓存并返回原文（写失败不影响本次返回）。</summary>
    private static string WriteCache(string title, string artist, string lrc)
    {
        try
        {
            Directory.CreateDirectory(Services.AppPaths.LyricsDir);
            File.WriteAllText(Path.Combine(Services.AppPaths.LyricsDir, CacheKey(title, artist) + ".lrc"), lrc);
        }
        catch (Exception ex)
        {
            Services.AppPaths.LogError("写入歌词缓存", ex, title);
        }
        return lrc;
    }

    /// <summary>清空歌词缓存。</summary>
    public static void ClearCache()
    {
        try
        {
            if (!Directory.Exists(Services.AppPaths.LyricsDir)) return;
            foreach (var file in Directory.EnumerateFiles(Services.AppPaths.LyricsDir))
            {
                try { File.Delete(file); } catch { /* 占用跳过 */ }
            }
        }
        catch (Exception ex)
        {
            Services.AppPaths.LogError("清空歌词缓存", ex);
        }
    }

    #endregion

    /// <summary>ILyricProvider：直接返回解析后的歌词文档。</summary>
    public async Task<LyricDocument?> FetchAsync(Track track, CancellationToken ct = default)
    {
        var lrc = await GetLrcAsync(track.Title, track.Artist, ct);
        return string.IsNullOrEmpty(lrc) ? null : _parser.Parse(lrc);
    }

    /// <summary>模糊搜索的歌词命中项（元数据供展示/判断是否匹配当前曲目）。</summary>
    public sealed record LyricSearchHit(string Title, string Artist, string Lrc);

    /// <summary>按用户输入搜索歌词（手动歌词搜索）：返回最佳命中，优先含时间戳的同步歌词。
    /// 建议输入“歌名”或“歌名 - 歌手”，title/artist 由调用方拆分后传入。</summary>
    public async Task<LyricSearchHit?> SearchBestAsync(string title, string? artist = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;
        title = title.Trim();
        artist = artist?.Trim();

        // 命中缓存直接返回（手动搜索同样受益）
        if (TryReadCache(title, artist ?? "") is { Length: > 0 } cachedLrc)
            return new LyricSearchHit(title, artist ?? "", cachedLrc);

        // 有歌手时双字段搜索更精确；仅歌名时用 q 模糊匹配（跨 歌名/歌手）
        var url = string.IsNullOrEmpty(artist)
            ? $"{BaseUrl}/api/search?q={Uri.EscapeDataString(title)}"
            : $"{BaseUrl}/api/search?track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}";

        using var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
            return null;
        var items = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement[]>(cancellationToken: ct);
        if (items is not { Length: > 0 })
            return null;

        // 评分选最优：同步歌词优先；歌名/歌手越接近分数越高
        LyricSearchHit? best = null;
        var bestScore = int.MinValue;
        foreach (var item in items)
        {
            if (item.ValueKind != System.Text.Json.JsonValueKind.Object)
                continue;
            var hitTitle = GetString(item, "trackName");
            var hitArtist = GetString(item, "artistName");
            if (hitTitle.Length == 0)
                continue;

            var synced = ExtractSynced(item);
            var plain = ExtractPlain(item);
            if (string.IsNullOrEmpty(synced) && string.IsNullOrEmpty(plain))
                continue; // 无歌词文本的条目直接跳过

            var score = 0;
            if (!string.IsNullOrEmpty(synced)) score += 2;
            var t = title.ToLowerInvariant();
            var ht = hitTitle.ToLowerInvariant();
            if (ht == t) score += 3;
            else if (ht.Contains(t) || t.Contains(ht)) score += 1;
            if (!string.IsNullOrEmpty(artist))
            {
                var a = artist.ToLowerInvariant();
                var ha = hitArtist.ToLowerInvariant();
                if (ha == a) score += 2;
                else if (ha.Contains(a) || a.Contains(ha)) score += 1;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = new LyricSearchHit(hitTitle, hitArtist, !string.IsNullOrEmpty(synced) ? synced! : plain!);
            }
        }
        return best;
    }

    private static string GetString(System.Text.Json.JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == System.Text.Json.JsonValueKind.String
            ? p.GetString() ?? ""
            : "";

    private async Task<System.Text.Json.JsonElement?> TryGetJsonAsync(string url, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
                return null;
            return await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken: ct);
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractSynced(System.Text.Json.JsonElement? el) =>
        el is { } e && e.ValueKind == System.Text.Json.JsonValueKind.Object
            && e.TryGetProperty("syncedLyrics", out var s)
            && s.ValueKind == System.Text.Json.JsonValueKind.String
            ? s.GetString()
            : null;

    private static string? ExtractPlain(System.Text.Json.JsonElement? el) =>
        el is { } e && e.ValueKind == System.Text.Json.JsonValueKind.Object
            && e.TryGetProperty("plainLyrics", out var p)
            && p.ValueKind == System.Text.Json.JsonValueKind.String
            ? p.GetString()
            : null;
}
