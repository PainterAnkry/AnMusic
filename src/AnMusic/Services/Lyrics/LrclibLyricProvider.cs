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
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "AnMusic/2.0 (desktop music player; lyric fetch via public LRCLIB API)");
    }

    /// <summary>按曲目信息获取 LRC 文本（未找到返回 null）。</summary>
    public async Task<string?> GetLrcAsync(string title, string artist, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;

        // 1) 精确匹配
        var url = $"{BaseUrl}/api/get?track_name={Uri.EscapeDataString(title)}" +
                  (string.IsNullOrWhiteSpace(artist) ? "" : $"&artist_name={Uri.EscapeDataString(artist)}");
        var result = await TryGetJsonAsync(url, ct);
        var lrc = ExtractSynced(result) ?? ExtractPlain(result);
        if (!string.IsNullOrEmpty(lrc))
            return lrc;

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
                    return lrc;
            }
            lrc = ExtractPlain(items[0]);
            if (!string.IsNullOrEmpty(lrc))
                return lrc;
        }
        return null;
    }

    /// <summary>ILyricProvider：直接返回解析后的歌词文档。</summary>
    public async Task<LyricDocument?> FetchAsync(Track track, CancellationToken ct = default)
    {
        var lrc = await GetLrcAsync(track.Title, track.Artist, ct);
        return string.IsNullOrEmpty(lrc) ? null : _parser.Parse(lrc);
    }

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
