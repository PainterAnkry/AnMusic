using Android.Content;
using Android.Provider;
using AnMusic.Services;

namespace AnMusic.Android.Services;

/// <summary>
/// 把下载/缓冲好的音频文件落到用户可见的音乐目录。
/// </summary>
/// <remarks>
/// 安卓 10（API 29）起分区存储生效，不能再直接往 /sdcard/Music 写文件，
/// 必须通过 MediaStore 插入。走 MediaStore 还有个额外好处：文件立即被系统媒体库索引，
/// 本应用的本地扫描（也是查 MediaStore）下一次就能看到，不需要手动触发扫描。
///
/// API 29 以下退回直接写文件，此路径需要 WRITE_EXTERNAL_STORAGE（清单里已按 maxSdkVersion=28 声明）。
/// </remarks>
public static class DownloadStorage
{
    /// <summary>在音乐目录下建一个子文件夹，避免和用户自己的音乐混在一起。</summary>
    private const string RelativeFolder = "Music/AnMusic";

    /// <summary>下载保存的展示用路径说明。</summary>
    public static string DisplayFolder => "Music/AnMusic";

    /// <summary>
    /// 把 <paramref name="sourcePath"/> 保存为音乐库中的 <paramref name="displayName"/>。
    /// </summary>
    /// <returns>保存后的真实文件路径；失败返回 null。</returns>
    public static async Task<string?> PublishAsync(
        string sourcePath, string displayName, CancellationToken ct = default)
    {
        if (!File.Exists(sourcePath)) return null;

        var ext = Path.GetExtension(displayName);
        if (string.IsNullOrEmpty(ext)) ext = Path.GetExtension(sourcePath);
        if (string.IsNullOrEmpty(ext)) ext = ".mp3";

        var fileName = Sanitize(Path.GetFileNameWithoutExtension(displayName)) + ext;
        var mime = MimeTypeOf(ext);

        return OperatingSystem.IsAndroidVersionAtLeast(29)
            ? await PublishViaMediaStoreAsync(sourcePath, fileName, mime, ct)
            : await PublishToLegacyFolderAsync(sourcePath, fileName, ct);
    }

    /// <summary>API 29+：插入媒体库（IS_PENDING 过渡，写完再置 0 对外可见）。</summary>
    private static async Task<string?> PublishViaMediaStoreAsync(
        string sourcePath, string fileName, string mime, CancellationToken ct)
    {
        var context = global::Android.App.Application.Context;
        var resolver = context.ContentResolver;
        if (resolver is null) return null;

        global::Android.Net.Uri? uri = null;
        try
        {
            var values = new ContentValues();
            values.Put(MediaStore.Audio.Media.InterfaceConsts.DisplayName, fileName);
            values.Put(MediaStore.Audio.Media.InterfaceConsts.MimeType, mime);
            values.Put(MediaStore.Audio.Media.InterfaceConsts.RelativePath, RelativeFolder);
            values.Put(MediaStore.Audio.Media.InterfaceConsts.IsMusic, 1);
            values.Put(MediaStore.Audio.Media.InterfaceConsts.IsPending, 1);

            uri = resolver.Insert(MediaStore.Audio.Media.ExternalContentUri!, values);
            if (uri is null) return null;

            await using (var src = File.OpenRead(sourcePath))
            await using (var dst = resolver.OpenOutputStream(uri))
            {
                if (dst is null) return null;
                await src.CopyToAsync(dst, ct);
            }

            // 解除 pending，文件才会出现在系统音乐库里
            var done = new ContentValues();
            done.Put(MediaStore.Audio.Media.InterfaceConsts.IsPending, 0);
            resolver.Update(uri, done, null, null);

            return QueryPath(resolver, uri) ?? uri.ToString();
        }
        catch (Exception ex)
        {
            AppPaths.LogError("写入媒体库", ex, fileName);
            // 失败时清掉可能已插入的空条目，避免库里留下坏记录
            if (uri is not null)
            {
                try { resolver.Delete(uri, null, null); } catch { }
            }
            return null;
        }
    }

    /// <summary>API 24-28：直接写公共音乐目录（带重名顺延）。</summary>
    private static async Task<string?> PublishToLegacyFolderAsync(
        string sourcePath, string fileName, CancellationToken ct)
    {
        try
        {
            var root = global::Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath;
            if (string.IsNullOrEmpty(root)) return null;

            var dir = Path.Combine(root, "Music", "AnMusic");
            Directory.CreateDirectory(dir);

            var target = UniquePath(Path.Combine(dir, fileName));
            await using (var src = File.OpenRead(sourcePath))
            await using (var dst = File.Create(target))
            {
                await src.CopyToAsync(dst, ct);
            }

            // 通知媒体库索引新文件，否则本地扫描看不到。
            // 必须写 global::Android.Media —— 本文件命名空间是 AnMusic.Android.Services，
            // 不加限定符时 Android.Media 会被逐级解析成 AnMusic.Android.Media。
            global::Android.Media.MediaScannerConnection.ScanFile(
                global::Android.App.Application.Context, [target], null, null);

            return target;
        }
        catch (Exception ex)
        {
            AppPaths.LogError("写入音乐目录", ex, fileName);
            return null;
        }
    }

    /// <summary>从媒体库条目反查真实文件路径（扫描器按路径去重，必须拿到 _data）。</summary>
    private static string? QueryPath(ContentResolver resolver, global::Android.Net.Uri uri)
    {
        try
        {
            using var cursor = resolver.Query(
                uri, [MediaStore.Audio.Media.InterfaceConsts.Data], null, null, null);
            if (cursor is not null && cursor.MoveToFirst())
                return cursor.GetString(0);
        }
        catch (Exception ex)
        {
            AppPaths.LogError("反查下载文件路径", ex);
        }
        return null;
    }

    private static string MimeTypeOf(string ext)
    {
        try
        {
            var mime = global::Android.Webkit.MimeTypeMap.Singleton?
                .GetMimeTypeFromExtension(ext.TrimStart('.').ToLowerInvariant());
            if (!string.IsNullOrEmpty(mime)) return mime;
        }
        catch { /* 落到下面的兜底 */ }
        return "audio/mpeg";
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        var trimmed = name.Trim();
        return trimmed.Length == 0 ? "track" : trimmed;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
