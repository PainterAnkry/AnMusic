using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace AnMusic.Services.Providers;

/// <summary>
/// 封面图缓存服务，将封面字节写入磁盘缓存目录避免重复读取。
/// </summary>
public sealed class CoverCacheService
{
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AnMusic", "covers");

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>将封面字节缓存到磁盘，返回缓存文件路径（无封面返回 null）。</summary>
    public string? GetOrCreate(byte[]? coverBytes, string? mime)
    {
        if (coverBytes is null || coverBytes.Length == 0)
            return null;

        var ext = mime?.Contains("png", StringComparison.OrdinalIgnoreCase) == true ? ".png" : ".jpg";
        var hash = Convert.ToHexStringLower(SHA1.HashData(coverBytes));
        var path = Path.Combine(CacheDir, hash + ext);

        if (!File.Exists(path))
        {
            Directory.CreateDirectory(CacheDir);
            File.WriteAllBytes(path, coverBytes);
        }

        return path;
    }

    /// <summary>下载远程封面 URL 到缓存并返回本地路径；失败返回 null（结果按 URL 哈希落盘去重）。</summary>
    public async Task<string?> GetOrCreateFromUrlAsync(string? url)
    {
        if (string.IsNullOrEmpty(url))
            return null;

        try
        {
            var path = Path.Combine(CacheDir, "url_" + Convert.ToHexStringLower(SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(url))) + ".jpg");
            if (File.Exists(path) && new FileInfo(path).Length > 0)
                return path;

            var bytes = await Http.GetByteArrayAsync(url);
            if (bytes.Length == 0) return null;

            Directory.CreateDirectory(CacheDir);
            File.WriteAllBytes(path, bytes);
            return path;
        }
        catch
        {
            return null;
        }
    }
}
