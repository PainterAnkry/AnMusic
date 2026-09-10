namespace AnMusic.Services;

/// <summary>
/// 平台差异抽象：各端（桌面 WPF / 安卓 MAUI）在启动时注入自己的实现。
/// Core 内的共享代码只依赖本接口，不直接触碰平台 API，从而保证一份业务逻辑多端复用。
/// </summary>
public interface IPlatformContext
{
    /// <summary>应用私有数据根目录（桌面 %AppData%\AnMusic，安卓 /data/data/&lt;包名&gt;/files/AnMusic）。</summary>
    string DataRoot { get; }

    /// <summary>
    /// 用户本地音乐的可扫描目录列表。
    /// 桌面端为用户「音乐」库等固定目录；安卓端为可访问的公共音乐目录（受分区存储限制）。
    /// </summary>
    IReadOnlyList<string> LocalMusicDirectories { get; }

    /// <summary>是否具备读取本地音乐库的权限（安卓需运行时申请，桌面恒为 true）。</summary>
    Task<bool> RequestMediaReadPermissionAsync();

    /// <summary>把文件路径转换为可用于 <c>Image.Source</c> 的 URI（安卓需 file:// 前缀）。</summary>
    string ToImageSourceUri(string filePath);

    /// <summary>日志输出出口（桌面写 Trace，安卓写系统日志）。</summary>
    void Log(string message);
}

/// <summary>
/// 平台上下文静态入口：Core 内需要读取平台信息的少数场景通过它访问。
/// 未注册时使用桌面默认实现，保证桌面端行为不变。
/// </summary>
public static class Platform
{
    private static IPlatformContext? _current;

    /// <summary>当前平台上下文（未显式设置时为桌面默认实现）。</summary>
    public static IPlatformContext Current => _current ??= new DesktopPlatformContext();

    /// <summary>宿主启动最早阶段调用，注入本端实现。</summary>
    public static void SetContext(IPlatformContext context)
    {
        _current = context ?? throw new ArgumentNullException(nameof(context));
        // 数据根同步切换，保证后续所有路径拼装正确
        AppPaths.SetDataRoot(context.DataRoot);
    }

    /// <summary>默认桌面实现：路径语义与历史版本完全一致。</summary>
    private sealed class DesktopPlatformContext : IPlatformContext
    {
        public string DataRoot { get; } = AppPaths.DataRoot;

        public IReadOnlyList<string> LocalMusicDirectories { get; } = BuildMusicDirs();

        public Task<bool> RequestMediaReadPermissionAsync() => Task.FromResult(true);

        public string ToImageSourceUri(string filePath) => filePath;

        public void Log(string message) => System.Diagnostics.Trace.WriteLine(message);

        private static List<string> BuildMusicDirs()
        {
            var dirs = new List<string>();
            void Add(Environment.SpecialFolder folder)
            {
                var path = Environment.GetFolderPath(folder);
                if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) dirs.Add(path);
            }
            Add(Environment.SpecialFolder.MyMusic);
            Add(Environment.SpecialFolder.MyDocuments);
            return dirs;
        }
    }
}
