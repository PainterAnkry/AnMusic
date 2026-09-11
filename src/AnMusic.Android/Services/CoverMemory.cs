using System.IO;
using AndroidBitmap = global::Android.Graphics.Bitmap;
using AndroidBitmapFactory = global::Android.Graphics.BitmapFactory;

namespace AnMusic.Android.Services;

/// <summary>
/// 封面内存治理：落盘前把大图压进 800px，从源头控制 UI 解码内存与磁盘占用。
/// </summary>
/// <remarks>
/// 现实：MAUI 的 <c>Image</c> 在安卓上不会按显示尺寸自动降采样，
/// 把 4000×4000 的 JPEG 直接读进内存，等于每次列表滑过一张就吞 ~64MB。
/// 这里在写盘前压成 ≤ 800×800（约 0.5MB），同时启动后台一次性把历史上
/// 已经缓存的大图原地压缩——既治标（不写新大图）也治本（清旧大图）。
/// </remarks>
public static class CoverMemory
{
    /// <summary>落盘上限；播放页 292dp、列表 40dp，800 足够且仍有充足清晰度。</summary>
    public const int MaxDim = 800;

    /// <summary>JPEG 压缩质量：肉眼几乎无差，但能再压掉 30%+。</summary>
    private const int JpegQuality = 87;

    /// <summary>
    /// 字节级降采样：先按边界读出尺寸算出 <c>InSampleSize</c>（2 的幂次），
    /// 再解码；最后用 JPEG 重压一次统一格式和大小。
    /// </summary>
    public static byte[] Downsample(byte[] bytes)
    {
        if (bytes.Length == 0) return bytes;

        var bounds = new AndroidBitmapFactory.Options { InJustDecodeBounds = true };
        AndroidBitmapFactory.DecodeByteArray(bytes, 0, bytes.Length, bounds);

        var longest = Math.Max(bounds.OutWidth, bounds.OutHeight);
        // 不是位图（可能是 SVG/webp 解码失败等），原样落盘
        if (longest <= 0) return bytes;

        // 已经在限内且字节数不大——直接放过，避免每次扫描都重新编码徒增开销
        if (longest <= MaxDim && bytes.Length < 400 * 1024) return bytes;

        var sample = 1;
        while (longest / sample > MaxDim) sample *= 2;

        var decodeOpts = new AndroidBitmapFactory.Options { InSampleSize = sample };
        using var bmp = AndroidBitmapFactory.DecodeByteArray(bytes, 0, bytes.Length, decodeOpts);
        if (bmp is null) return bytes;

        using var ms = new MemoryStream();
        bmp.Compress(AndroidBitmap.CompressFormat.Jpeg, JpegQuality, ms);
        var outBytes = ms.ToArray();

        // 仅在确实变小的情况下替换；极少数情况下原始 PNG 更小就保留
        return outBytes.Length < bytes.Length ? outBytes : bytes;
    }

    /// <summary>
    /// 启动时后台把历史上已经缓存的大封面原地压缩一次。
    /// 原地改写，引用路径不变（<see cref="Track.CoverKey"/> 的路径仍然有效），
    /// 只是磁盘占用和解码内存都降下来。失败单文件吞掉，不影响其他文件。
    /// </summary>
    public static Task ShrinkExistingAsync(string dir) => Task.Run(() =>
    {
        try
        {
            if (!Directory.Exists(dir)) return;

            foreach (var file in Directory.EnumerateFiles(dir))
            {
                try
                {
                    var info = new FileInfo(file);
                    // 只动明显过大的：> 600KB 几乎一定是没降过样的大图
                    if (info.Length <= 600 * 1024) continue;

                    var shrunk = Downsample(File.ReadAllBytes(file));
                    if (shrunk.Length < info.Length)
                        File.WriteAllBytes(file, shrunk);
                }
                catch
                {
                    // 单文件失败不传染（极个别文件 IO 阻塞/被占用等）
                }
            }
        }
        catch
        {
            // 后台任务整体失败也不能影响启动
        }
    });
}