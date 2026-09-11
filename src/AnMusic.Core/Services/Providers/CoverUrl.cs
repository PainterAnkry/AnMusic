namespace AnMusic.Services.Providers;

/// <summary>
/// 封面地址规整：把各音源返回的封面 URL 变成宿主一定能下载的绝对地址。
/// </summary>
/// <remarks>
/// 需要处理的两种真实情况：
/// <list type="bullet">
/// <item><b>协议相对地址</b>：B 站搜索接口的 <c>pic</c> 是 <c>//i2.hdslb.com/bfs/archive/xxx.jpg</c>，
/// 直接交给 HttpClient 会抛 "An invalid request URI was provided"（表现为"B站歌曲全都没封面"）；
/// MusicFree 插件也常返回这种写法。</item>
/// <item><b>CDN 处理后缀</b>：<c>...jpg@480w_270h_1c.webp</c> 这类后缀会让 CDN 转码成 WebP，
/// 去掉才能取到原始 jpg/png（WPF 解码兼容性最好）。</item>
/// </list>
/// </remarks>
public static class CoverUrl
{
    /// <summary>规整封面 URL；无法规整时原样返回，空值返回空串。</summary>
    public static string Normalize(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;

        var u = url.Trim();

        // 协议相对 → 补全 https（封面 CDN 都支持 https）
        if (u.StartsWith("//", StringComparison.Ordinal))
            u = "https:" + u;

        // 去掉 CDN 处理后缀：只认"路径最后一段里的 @"，避免误伤 query 里的 @
        var slash = u.LastIndexOf('/');
        var query = u.IndexOf('?', slash + 1);
        var at = u.LastIndexOf('@');
        if (at > slash && query < 0)
            u = u[..at];

        return u;
    }
}
