using System.IO;
using System.Threading;

namespace AnMusic.Services;

/// <summary>
/// 应用数据路径统一入口：所有用户数据/缓存/插件集中到 %AppData%\AnMusic 单一目录。
/// 历史版本曾把 userdata 与各缓存散落在 %LocalAppData%\AnMusic（与 %AppData%\AnMusic 并存成
/// 两个 AnMusic 目录），App 启动最早阶段调用 <see cref="MigrateLegacyLocalLayout"/> 一次性搬移。
/// </summary>
public static class AppPaths
{
    /// <summary>统一数据根目录（%AppData%\AnMusic）。</summary>
    public static readonly string DataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AnMusic");

    public static readonly string UserDataFile = Path.Combine(DataRoot, "userdata.json");
    public static readonly string PluginsDir = Path.Combine(DataRoot, "plugins");
    public static readonly string CoversDir = Path.Combine(DataRoot, "covers");
    public static readonly string AudioCacheDir = Path.Combine(DataRoot, "audio-cache");
    public static readonly string PluginAudioCacheDir = Path.Combine(DataRoot, "plugin-cache");
    public static readonly string NeteaseCacheDir = Path.Combine(DataRoot, "netease-cache");
    public static readonly string QqmusicCacheDir = Path.Combine(DataRoot, "qqmusic-cache");
    public static readonly string BuvidCookieFile = Path.Combine(DataRoot, "buvid3.txt");

    /// <summary>在线歌词缓存目录（按 歌名-歌手 哈希落盘，避免每次播放都请求歌词库）。</summary>
    public static readonly string LyricsDir = Path.Combine(DataRoot, "lyrics");

    /// <summary>错误日志：静默 catch 统一记在这里，便于排查"设置没生效"这类隐形故障。</summary>
    public static readonly string ErrorLogFile = Path.Combine(DataRoot, "error.log");

    /// <summary>
    /// 记录一条错误到 error.log（自身失败绝不抛出，避免"记日志把功能搞挂"）。
    /// 单文件超过 1MB 时滚动一次，防止无限增长。
    /// </summary>
    public static void LogError(string context, Exception? ex = null, string? detail = null)
    {
        try
        {
            Directory.CreateDirectory(DataRoot);
            var info = new FileInfo(ErrorLogFile);
            if (info.Exists && info.Length > 1024 * 1024)
            {
                try { File.Delete(ErrorLogFile + ".1"); } catch { }
                try { File.Move(ErrorLogFile, ErrorLogFile + ".1"); } catch { }
            }

            var head = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}";
            if (!string.IsNullOrEmpty(detail)) head += $" | {detail}";
            if (ex is not null) head += $" | {ex.GetType().Name}: {ex.Message}";
            File.AppendAllText(ErrorLogFile, head + Environment.NewLine + (ex?.StackTrace ?? "") + Environment.NewLine);
        }
        catch
        {
            // 磁盘满/无权限等情况只能放弃记录
        }
    }

    /// <summary>历史版本的数据根（%LocalAppData%\AnMusic）。</summary>
    private static readonly string LegacyLocalRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnMusic");

    private static int _migrated;

    /// <summary>把历史版本散落在 %LocalAppData%\AnMusic 的数据一次性搬入新根（幂等；单项失败不影响其余）。</summary>
    public static void MigrateLegacyLocalLayout()
    {
        if (Interlocked.Exchange(ref _migrated, 1) != 0) return;
        try
        {
            if (!Directory.Exists(LegacyLocalRoot)) return;

            var mapping = new (string SourceName, string Target)[]
            {
                ("userdata.json", UserDataFile),
                ("covers", CoversDir),
                ("audio-cache", AudioCacheDir),
                ("plugin-cache", PluginAudioCacheDir),
                ("netease-cache", NeteaseCacheDir),
                ("qqmusic-cache", QqmusicCacheDir),
                ("buvid3.txt", BuvidCookieFile),
            };
            foreach (var (name, target) in mapping)
            {
                try
                {
                    var src = Path.Combine(LegacyLocalRoot, name);
                    if (File.Exists(src)) File.Move(src, target, overwrite: true);
                    else if (Directory.Exists(src)) Directory.Move(src, target);
                }
                catch
                {
                    // 单项迁移失败（如被占用）不影响其余；新代码按新路径读写
                }
            }

            // 旧根已搬空则删除，避免残留空 AnMusic 目录
            try
            {
                if (!Directory.EnumerateFileSystemEntries(LegacyLocalRoot).Any())
                    Directory.Delete(LegacyLocalRoot);
            }
            catch { }
        }
        catch { /* 迁移整体失败不阻断启动 */ }
    }
}
