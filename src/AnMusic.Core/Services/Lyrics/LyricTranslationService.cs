using System.Text;
using System.Text.Json;
using AnMusic.Services.Net;

namespace AnMusic.Services.Lyrics;

/// <summary>
/// 歌词翻译：整篇歌词批量译为中文，返回与输入行数对齐的译文数组。
/// </summary>
/// <remarks>
/// 原先这段逻辑内联在桌面端的 <c>LyricViewModel</c> 里，且自己 new 了一个
/// <see cref="HttpClient"/> —— 后果是**翻译请求不走用户配置的代理**，
/// 需要代理才能上外网的机器上翻译必然失败，而这恰好是最需要翻译的场景。
/// 抽到 Core 后统一走 <see cref="HttpService"/>，两端共享同一份实现与代理配置。
///
/// 先试 MyMemory（免密钥、国内直连可用），失败再回退 Google gtx（海外可用）。
/// 两者都按行分块请求，避免超出单次请求长度上限。
/// </remarks>
public sealed class LyricTranslationService
{
    private const int MaxChunkLength = 450;

    /// <summary>
    /// 翻译接口对匿名/非浏览器 UA 有拦截，必须伪装成浏览器；
    /// 这与 <see cref="HttpService.UserAgent"/> 不同，所以自建一个客户端。
    /// </summary>
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    private static readonly object Gate = new();
    private static HttpClient? _client;

    static LyricTranslationService()
    {
        // 用户改代理后要重建，否则翻译继续走旧代理
        HttpService.ProxyChanged += Invalidate;
    }

    private static void Invalidate()
    {
        lock (Gate)
        {
            _client?.Dispose();
            _client = null;
        }
    }

    /// <summary>带浏览器 UA 且已套用用户代理设置的客户端。</summary>
    private static HttpClient Http
    {
        get
        {
            lock (Gate)
            {
                if (_client is not null) return _client;

                // CreateClient 会套用当前代理设置
                var client = HttpService.CreateClient(timeout: TimeSpan.FromSeconds(15));
                client.DefaultRequestHeaders.Remove("User-Agent");
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
                return _client = client;
            }
        }
    }

    /// <summary>把每一行歌词译为中文；空行原样返回空串。</summary>
    public async Task<string[]> TranslateAsync(IReadOnlyList<string> lines, CancellationToken ct = default)
    {
        if (lines.Count == 0) return [];

        try
        {
            return await TranslateViaMyMemoryAsync(lines, ct);
        }
        catch
        {
            // MyMemory 失败（额度用完 / 网络异常 / 被墙），回退 Google
            return await TranslateViaGoogleAsync(lines, ct);
        }
    }

    /// <summary>
    /// MyMemory 翻译：免费免密钥，单次约 500 字符上限，故按行分块。
    /// 自动检测源语言；换行在请求与响应两侧都要保住，否则译文与原文行数对不齐。
    /// </summary>
    private static async Task<string[]> TranslateViaMyMemoryAsync(
        IReadOnlyList<string> lines, CancellationToken ct)
    {
        var parts = new List<string>(lines.Count);

        foreach (var chunk in ChunkLines(lines))
        {
            var url = "https://api.mymemory.translated.net/get?langpair=Autodetect%7Czh-CN&q="
                      + Uri.EscapeDataString(chunk);

            using var resp = await Http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = json.RootElement;

            if (root.TryGetProperty("quotaFinished", out var quota) && quota.ValueKind == JsonValueKind.True)
                throw new HttpRequestException("MyMemory 今日免费额度已用完");

            var status = root.GetProperty("responseStatus");
            if (status.ValueKind != JsonValueKind.Number || status.GetInt32() != 200)
                throw new HttpRequestException("MyMemory 翻译失败");

            var translated = root.GetProperty("responseData")
                .GetProperty("translatedText").GetString() ?? "";

            parts.AddRange(SplitLines(translated));
        }

        return AlignToInput(parts, lines.Count);
    }

    /// <summary>Google 翻译免费接口（translate_a/single，无需密钥）。</summary>
    private static async Task<string[]> TranslateViaGoogleAsync(
        IReadOnlyList<string> lines, CancellationToken ct)
    {
        var parts = new List<string>(lines.Count);

        foreach (var chunk in ChunkLines(lines))
        {
            var url = "https://translate.googleapis.com/translate_a/single"
                      + "?client=gtx&sl=auto&tl=zh-CN&dt=t&q="
                      + Uri.EscapeDataString(chunk);

            using var resp = await Http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            // 响应形如 [[["译文","原文",...],...], ...]，把所有分段拼起来
            var sb = new StringBuilder();
            foreach (var seg in json.RootElement[0].EnumerateArray())
                sb.Append(seg[0].GetString());

            parts.AddRange(SplitLines(sb.ToString()));
        }

        return AlignToInput(parts, lines.Count);
    }

    /// <summary>按行累积成不超过单次请求上限的块。</summary>
    private static IEnumerable<string> ChunkLines(IReadOnlyList<string> lines)
    {
        var sb = new StringBuilder();

        foreach (var line in lines)
        {
            if (sb.Length > 0 && sb.Length + line.Length + 1 > MaxChunkLength)
            {
                yield return sb.ToString();
                sb.Clear();
            }

            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line);
        }

        if (sb.Length > 0) yield return sb.ToString();
    }

    private static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n").Split('\n');

    /// <summary>
    /// 把译文对齐到输入行数：接口偶尔会吞掉或合并空行，
    /// 短了补空串、长了截断，避免下游按下标取译文时越界。
    /// </summary>
    private static string[] AlignToInput(List<string> parts, int expected)
    {
        if (parts.Count == expected) return [.. parts];

        var result = new string[expected];
        for (var i = 0; i < expected; i++)
            result[i] = i < parts.Count ? parts[i] : string.Empty;

        return result;
    }
}
