using System.IO;
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
}
