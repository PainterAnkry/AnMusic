using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace AnMusic.Services.Providers;

/// <summary>
/// 封面图缓存服务，将封面字节写入磁盘缓存目录避免重复读取。
/// 缓存设有容量上限，超限按"最久未写入"淘汰，避免长期使用后无限增长。
/// </summary>
public sealed class CoverCacheService
{
    // 必须延迟求值：安卓端宿主会在启动时切换 DataRoot，静态 readonly 字段会在类型首次
    // 被触碰（往往早于宿主初始化）时就把路径钉死在旧根上。
    public static string CacheDir => Services.AppPaths.CoversDir;

    /// <summary>封面缓存容量上限（256MB）。</summary>
    public const long MaxCacheBytes = 256L * 1024 * 1024;

    /// <summary>
    /// 宿主注入的封面降采样钩子（输入输出均为图片字节）：
    /// 安卓端注入 BitmapFactory 实现把大图压到 <see cref="MaxCoverDimension"/> 内，
    /// 从源头控制 UI 解码内存；桌面端保持 null（WPF 按需解码，无需处理）。
    /// 写入磁盘之前会先把原字节喂给这个钩子，因此缓存目录里只有压缩后的副本。
    /// </summary>
    public static Func<byte[], byte[]>? CoverDownsampler { get; set; }

    /// <summary>写入磁盘前若尺寸仍超此值就触发降采样；同时是通知栏/列表显示的上限参考。</summary>
    public const int MaxCoverDimension = 800;

    private static readonly HttpClient Http = Services.Net.HttpService.Client;

    /// <summary>距上次容量检查的写入次数（每 20 次检查一次，避免频繁遍历目录）。</summary>
    private static int _writesSinceTrim;

    /// <summary>将封面字节缓存到磁盘，返回缓存文件路径（无封面返回 null）。</summary>
    public string? GetOrCreate(byte[]? coverBytes, string? mime)
    {
        if (coverBytes is null || coverBytes.Length == 0)
            return null;

        // 写入磁盘前交给宿主端做一次降采样；原图经常来自嵌入音频的封面，
        // 上千 × 上千的 JPEG 不降样直接解码会瞬间把播放页内存撑到几百 MB。
        if (CoverDownsampler is { } downsample)
        {
            try { coverBytes = downsample(coverBytes); }
            catch { /* 降采样失败用原图，不能让封面把功能带挂 */ }
        }

        var ext = mime?.Contains("png", StringComparison.OrdinalIgnoreCase) == true ? ".png" : ".jpg";
        var hash = Convert.ToHexStringLower(SHA1.HashData(coverBytes));
        var path = Path.Combine(CacheDir, hash + ext);

        if (!File.Exists(path))
        {
            Directory.CreateDirectory(CacheDir);
            File.WriteAllBytes(path, coverBytes);
            TrimIfNeeded();
        }

        return path;
    }

    /// <summary>下载远程封面 URL 到缓存并返回本地路径；失败返回 null（结果按 URL 哈希落盘去重）。</summary>
    public async Task<string?> GetOrCreateFromUrlAsync(string? url)
    {
        if (string.IsNullOrEmpty(url))
            return null;

        // 音源常给出协议相对地址（B 站 //i0.hdslb.com/...，MusicFree 插件同样常见），
        // 直接交给 HttpClient 会以 "invalid request URI" 失败 —— 这里统一兜底补全协议
        url = CoverUrl.Normalize(url);
        if (url.Length == 0)
            return null;

        try
        {
            var fileName = "url_" + Convert.ToHexStringLower(SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(url))) + ".jpg";
            var path = Path.Combine(CacheDir, fileName);
            if (File.Exists(path) && new FileInfo(path).Length > 0)
                return path;

            var bytes = await Http.GetByteArrayAsync(url);
            if (bytes.Length == 0) return null;

            // 在线封面经常是 2000×2000 以上，写盘前同样压一次
            if (CoverDownsampler is { } downsample)
            {
                try { bytes = downsample(bytes); }
                catch { }
            }

            Directory.CreateDirectory(CacheDir);
            File.WriteAllBytes(path, bytes);
            TrimIfNeeded();
            return path;
        }
        catch (Exception ex)
        {
            Services.AppPaths.LogError("下载封面", ex, url);
            return null;
        }
    }

    /// <summary>累计写入若干次后检查一次容量（多线程下计数不精确也无妨）。</summary>
    private static void TrimIfNeeded()
    {
        if (Interlocked.Increment(ref _writesSinceTrim) % 20 != 0) return;
        EnforceLimit();
    }

    /// <summary>
    /// 缓存超限时按最后写入时间从旧到新删除，直到降到上限的 80%。
    /// 正被界面占用的图片可能删不掉，跳过即可（下次再清）。
    /// </summary>
    public static void EnforceLimit(string? dir = null, long maxBytes = MaxCacheBytes)
    {
        try
        {
            dir ??= CacheDir;
            if (!Directory.Exists(dir)) return;

            var files = new DirectoryInfo(dir).GetFiles().OrderBy(f => f.LastWriteTimeUtc).ToList();
            var total = files.Sum(f => f.Length);
            if (total <= maxBytes) return;

            var target = (long)(maxBytes * 0.8); // 一次多清一些，减少后续遍历频率
            foreach (var file in files)
            {
                if (total <= target) break;
                try
                {
                    var size = file.Length;
                    file.Delete();
                    total -= size;
                }
                catch { /* 文件被占用：跳过 */ }
            }
        }
        catch (Exception ex)
        {
            Services.AppPaths.LogError("清理封面缓存", ex);
        }
    }

    /// <summary>缓存当前占用字节数（设置页展示用）。</summary>
    public static long GetCacheSize()
    {
        try
        {
            return Directory.Exists(CacheDir) ? new DirectoryInfo(CacheDir).GetFiles().Sum(f => f.Length) : 0;
        }
        catch
        {
            return 0;
        }
    }
}
