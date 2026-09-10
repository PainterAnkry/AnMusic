using AnMusic.Services;

namespace AnMusic.Android.Services;

/// <summary>
/// 安卓平台上下文：把 Core 需要的平台差异（数据根、本地音乐目录、权限、图片 URI）落到安卓语义上。
/// </summary>
public sealed class AndroidPlatformContext : IPlatformContext
{
    /// <summary>应用私有数据根。安卓上必须落在应用沙盒内，卸载即清除、无需任何权限。</summary>
    public string DataRoot { get; }

    public AndroidPlatformContext()
    {
        var baseDir = FileSystem.AppDataDirectory; // /data/data/<包名>/files
        DataRoot = Path.Combine(baseDir, "AnMusic");
        Directory.CreateDirectory(DataRoot);
    }

    /// <summary>
    /// 可扫描的公共音乐目录。
    /// 受分区存储限制，安卓 11+ 直接枚举 /storage/emulated/0 下任意目录可能被拒；
    /// 这里给出标准公共音乐目录，并由 <see cref="LocalMusicScanner"/> 用 MediaStore 兜底。
    /// </summary>
    public IReadOnlyList<string> LocalMusicDirectories
    {
        get
        {
            var list = new List<string>();
            var external = global::Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath;
            if (!string.IsNullOrEmpty(external))
            {
                foreach (var sub in new[] { "Music", "Download", "Documents", "Music/AnMusic" })
                {
                    var dir = Path.Combine(external, sub);
                    if (Directory.Exists(dir)) list.Add(dir);
                }
            }
            return list;
        }
    }

    /// <summary>
    /// 申请读取音频权限。
    /// 安卓 13+（API 33）细分为 READ_MEDIA_AUDIO；更低版本用 READ_EXTERNAL_STORAGE。
    /// </summary>
    public async Task<bool> RequestMediaReadPermissionAsync()
    {
        try
        {
            var status = await Permissions.CheckStatusAsync<AudioReadPermission>();
            if (status == PermissionStatus.Granted) return true;
            status = await Permissions.RequestAsync<AudioReadPermission>();
            return status == PermissionStatus.Granted;
        }
        catch (Exception ex)
        {
            AppPaths.LogError("申请音频读取权限", ex);
            return false;
        }
    }

    /// <summary>
    /// 本地文件路径转图片源 URI。
    /// MAUI 的 Image 需要用 file:// 开头的 URI 才能加载绝对路径的本地图片。
    /// </summary>
    public string ToImageSourceUri(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return string.Empty;
        if (filePath.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return filePath;
        if (filePath.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return filePath;
        return "file://" + filePath;
    }

    public void Log(string message) => global::Android.Util.Log.Info("AnMusic", message);
}

/// <summary>按系统版本自动选择正确的音频读取权限（安卓 13 起改为 READ_MEDIA_AUDIO）。</summary>
public sealed class AudioReadPermission : Permissions.BasePlatformPermission
{
    public override (string androidPermission, bool isRuntime)[] RequiredPermissions
    {
        get
        {
            var list = new List<(string, bool)>();
            if (OperatingSystem.IsAndroidVersionAtLeast(33))
            {
                list.Add((global::Android.Manifest.Permission.ReadMediaAudio, true));
            }
            else
            {
                list.Add((global::Android.Manifest.Permission.ReadExternalStorage, true));
            }
            return list.ToArray();
        }
    }
}
