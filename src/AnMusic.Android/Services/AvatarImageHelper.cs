using Android.Graphics;

namespace AnMusic.Android.Services;

/// <summary>
/// 头像图片处理：解码、按 EXIF 摆正、缩放到可控尺寸、方形裁剪。
/// </summary>
/// <remarks>
/// 之所以先做一次「归一化」（摆正 + 降采样）再让用户裁剪，
/// 是为了把 EXIF 旋转这件事一次性解决掉：裁剪页与最终裁剪都基于同一张已摆正的图，
/// 不用在裁剪数学里再考虑方向，也不会因为原图过大而 OOM。
/// </remarks>
public static class AvatarImageHelper
{
    /// <summary>相册原图可能几十兆，先降采样再处理。EXIF 方向常量（与系统一致）。</summary>
    private const int ExifOrientationNormal = 1;
    private const int ExifOrientationFlipHorizontal = 2;
    private const int ExifOrientationRotate180 = 3;
    private const int ExifOrientationFlipVertical = 4;
    private const int ExifOrientationTranspose = 5;
    private const int ExifOrientationRotate90 = 6;
    private const int ExifOrientationTransverse = 7;
    private const int ExifOrientationRotate270 = 8;

    /// <summary>
    /// 把任意来源图片归一化成「已按 EXIF 摆正 + 最长边不超过 <paramref name="maxSize"/>」的 PNG。
    /// </summary>
    /// <returns>成功返回输出路径，失败返回 null。</returns>
    public static Task<string?> NormalizeAsync(string sourcePath, string outputPath, int maxSize = 1200)
        => Task.Run(() =>
        {
            try
            {
                var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
                BitmapFactory.DecodeFile(sourcePath, bounds);
                if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0) return null;

                // 逐步 2 倍降采样，避免一次性加载超大位图
                var sample = 1;
                while (bounds.OutWidth / (sample * 2) >= maxSize || bounds.OutHeight / (sample * 2) >= maxSize)
                    sample *= 2;

                using var decoded = BitmapFactory.DecodeFile(
                    sourcePath, new BitmapFactory.Options { InSampleSize = sample });
                if (decoded is null) return null;

                using var oriented = ApplyExifOrientation(decoded, ReadExifOrientation(sourcePath));

                var longest = Math.Max(oriented.Width, oriented.Height);
                var final = oriented;
                var owned = false;

                if (longest > maxSize)
                {
                    var scale = (double)maxSize / longest;
                    final = Bitmap.CreateScaledBitmap(
                        oriented,
                        Math.Max(1, (int)(oriented.Width * scale)),
                        Math.Max(1, (int)(oriented.Height * scale)),
                        true);
                    owned = true;
                }

                try
                {
                    using var stream = File.Create(outputPath);
                    final.Compress(Bitmap.CompressFormat.Png!, 100, stream);
                }
                finally
                {
                    if (owned) final.Recycle();
                }

                return outputPath;
            }
            catch (Exception ex)
            {
                AnMusic.Services.AppPaths.LogError("归一化头像图片", ex, sourcePath);
                return null;
            }
        });

    /// <summary>
    /// 按「归一化图上的像素矩形」裁剪出正方形并缩放到 <paramref name="outputSize"/>。
    /// </summary>
    /// <param name="sourcePath">已归一化的图片路径。</param>
    /// <param name="outputPath">输出路径。</param>
    /// <param name="cropX">裁剪左上角 X（源图像素）。</param>
    /// <param name="cropY">裁剪左上角 Y（源图像素）。</param>
    /// <param name="cropSize">裁剪边长（源图像素）。</param>
    /// <param name="outputSize">输出边长。</param>
    public static Task<bool> CropSquareAsync(
        string sourcePath,
        string outputPath,
        double cropX,
        double cropY,
        double cropSize,
        int outputSize = 512)
        => Task.Run(() =>
        {
            try
            {
                using var source = BitmapFactory.DecodeFile(sourcePath);
                if (source is null) return false;

                // 把矩形夹进图片内部，并保证至少 1 像素
                var size = (int)Math.Round(Math.Min(cropSize, Math.Min(source.Width, source.Height)));
                size = Math.Max(1, size);

                var x = (int)Math.Round(Math.Clamp(cropX, 0, source.Width - size));
                var y = (int)Math.Round(Math.Clamp(cropY, 0, source.Height - size));

                using var cropped = Bitmap.CreateBitmap(source, x, y, size, size);
                if (cropped is null) return false;

                using var scaled = Bitmap.CreateScaledBitmap(cropped, outputSize, outputSize, true);
                if (scaled is null) return false;

                using var stream = File.Create(outputPath);
                scaled.Compress(Bitmap.CompressFormat.Png!, 100, stream);

                return true;
            }
            catch (Exception ex)
            {
                AnMusic.Services.AppPaths.LogError("裁剪头像", ex, sourcePath);
                return false;
            }
        });

    /// <summary>读取图片的 EXIF 方向标记；读不到按「正常」处理。</summary>
    private static int ReadExifOrientation(string path)
    {
        try
        {
            // 注意：本文件命名空间是 AnMusic.Android.Services，
            // 直接写 Android.Media 会被解析成 AnMusic.Android.Media，必须用 global:: 前缀
            using var exif = new global::Android.Media.ExifInterface(path);
            return exif.GetAttributeInt("Orientation", ExifOrientationNormal);
        }
        catch
        {
            // 不是 JPEG、或文件没有 EXIF 段：按正常方向处理
            return ExifOrientationNormal;
        }
    }

    /// <summary>按 EXIF 方向把位图摆正。方向正常时直接返回原对象（不额外分配）。</summary>
    private static Bitmap ApplyExifOrientation(Bitmap source, int orientation)
    {
        if (orientation == ExifOrientationNormal) return source;

        var matrix = new Matrix();
        switch (orientation)
        {
            case ExifOrientationFlipHorizontal:
                matrix.SetScale(-1, 1);
                break;
            case ExifOrientationRotate180:
                matrix.SetRotate(180);
                break;
            case ExifOrientationFlipVertical:
                matrix.SetScale(1, -1);
                break;
            case ExifOrientationTranspose:
                matrix.SetRotate(90);
                matrix.PostScale(-1, 1);
                break;
            case ExifOrientationRotate90:
                matrix.SetRotate(90);
                break;
            case ExifOrientationTransverse:
                matrix.SetRotate(-90);
                matrix.PostScale(-1, 1);
                break;
            case ExifOrientationRotate270:
                matrix.SetRotate(-90);
                break;
            default:
                return source;
        }

        try
        {
            var rotated = Bitmap.CreateBitmap(source, 0, 0, source.Width, source.Height, matrix, true);
            return rotated ?? source;
        }
        catch
        {
            return source;
        }
    }
}
