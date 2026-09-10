using System.Net;
using System.Net.Http;

namespace AnMusic.Services.Net;

/// <summary>
/// 统一 HTTP 出口：集中设置 User-Agent、超时与代理，避免到处 new HttpClient。
/// 代理来自设置（空 = 跟随系统）；<see cref="ConfigureProxy"/> 变更后会重建共享客户端，
/// 并触发 <see cref="ProxyChanged"/>，让持有自有 CookieContainer 的音源客户端同步重建连接。
/// </summary>
public static class HttpService
{
    private static readonly object Gate = new();
    private static HttpClient? _shared;
    private static string? _proxyUrl;

    /// <summary>统一 UA（部分接口对匿名 UA 有限制）。</summary>
    public const string UserAgent = "AnMusic/3.0 (desktop music player)";

    /// <summary>代理变更通知：自建 HttpClient 的持有者据此重建（保留各自的 Cookie/Header 配置）。</summary>
    public static event Action? ProxyChanged;

    /// <summary>当前代理地址（空 = 跟随系统）。</summary>
    public static string? ProxyUrl
    {
        get { lock (Gate) return _proxyUrl; }
    }

    /// <summary>共享客户端（线程安全，可长期持有）。</summary>
    public static HttpClient Client
    {
        get
        {
            lock (Gate)
            {
                return _shared ??= Build(_proxyUrl);
            }
        }
    }

    /// <summary>应用代理设置；地址变化时重建共享客户端并广播通知。</summary>
    public static void ConfigureProxy(string? proxyUrl)
    {
        var normalized = string.IsNullOrWhiteSpace(proxyUrl) ? null : proxyUrl.Trim();
        lock (Gate)
        {
            if (string.Equals(_proxyUrl, normalized, StringComparison.OrdinalIgnoreCase) && _shared is not null)
                return;
            _proxyUrl = normalized;
            _shared = Build(normalized);
        }
        ProxyChanged?.Invoke();
    }

    /// <summary>给自建 handler 套用当前代理（音源客户端在构造时调用）。</summary>
    public static void ApplyProxy(HttpClientHandler handler)
    {
        var url = ProxyUrl;
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            handler.Proxy = new WebProxy(url);
            handler.UseProxy = true;
        }
        catch (UriFormatException)
        {
            // 代理地址写错就当没配，不让整个音源不可用
        }
    }

    /// <summary>新建一个已套用代理、带统一 UA 的客户端（调用方可替换 handler 以携带 Cookie）。</summary>
    public static HttpClient CreateClient(HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        var client = new HttpClient(handler ?? CreateHandler())
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(20)
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        return client;
    }

    /// <summary>新建默认 handler（跟随当前代理，自动解压）。</summary>
    public static HttpClientHandler CreateHandler()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };
        ApplyProxy(handler);
        return handler;
    }

    private static HttpClient Build(string? proxyUrl) => CreateClient(
        CreateHandlerCore(proxyUrl),
        TimeSpan.FromSeconds(20));

    private static HttpClientHandler CreateHandlerCore(string? proxyUrl)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };
        if (!string.IsNullOrWhiteSpace(proxyUrl))
        {
            try
            {
                handler.Proxy = new WebProxy(proxyUrl);
                handler.UseProxy = true;
            }
            catch (UriFormatException) { /* 无效代理地址：忽略 */ }
        }
        return handler;
    }
}
