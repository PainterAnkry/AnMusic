using System.IO;
using System.Text;
using JavaThread = global::Java.Lang.Thread;
using JavaThrowable = global::Java.Lang.Throwable;
using AndroidBuild = global::Android.OS.Build;

namespace AnMusic.Android.Services;

/// <summary>
/// 全局崩溃捕获：.NET 侧（AppDomain / TaskScheduler / AndroidEnvironment）与
/// Java 侧（Thread.DefaultUncaughtExceptionHandler）四个入口全部挂钩，
/// 崩溃栈第一时间写入应用私有目录的 crashes/ 文件夹。
/// </summary>
/// <remarks>
/// 动机：真机上"闪退"没有任何现场，logcat 又拿不到（用户没连 adb）。
/// 落盘之后，下次启动 MainPage 会弹窗提示并提供"复制日志"，
/// 用户把日志贴回来即可定位。写入路径刻意用 FilesDir（与 AppPaths.DataRoot 同基），
/// 不依赖 MAUI 初始化完成与否——必须赶在一切业务代码之前挂好。
/// </remarks>
public static class CrashReporter
{
    private static readonly object Gate = new();
    private static string? _dir;

    /// <summary>Java 处理器的强引用：防止被 GC 提前回收导致兜底失效。</summary>
    private static CrashHandler? _handler;

    /// <summary>安装全部钩子。必须在 MAUI 初始化之前调用（MainApplication.OnCreate 的第一句）。</summary>
    public static void Install(string crashDirectory)
    {
        _dir = crashDirectory;
        try { Directory.CreateDirectory(_dir); } catch { }

        // .NET 托管异常（DI 解析失败、构造函数抛异常等都会走这里）
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write("dotnet", e.ExceptionObject as Exception);

        // 未观察的任务异常：吞掉以免进程被杀，但要留下记录
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("task", e.Exception);
            e.SetObserved();
        };

        // Android 运行时把 Java 异常转发给 .NET 的通道
        global::Android.Runtime.AndroidEnvironment.UnhandledExceptionRaiser += (_, e) =>
            Write("androidenv", e.Exception);

        // Java 层兜底：Activity/Application 创建失败等纯 Java 崩溃
        try
        {
            _handler = new CrashHandler(JavaThread.DefaultUncaughtExceptionHandler);
            JavaThread.DefaultUncaughtExceptionHandler = _handler;
        }
        catch { }
    }

    private sealed class CrashHandler : global::Java.Lang.Object, JavaThread.IUncaughtExceptionHandler
    {
        private readonly JavaThread.IUncaughtExceptionHandler? _previous;

        public CrashHandler(JavaThread.IUncaughtExceptionHandler? previous) => _previous = previous;

        public void UncaughtException(JavaThread thread, JavaThrowable ex)
        {
            Write("java", null, raw: $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            // 交给系统默认处理（弹"应用已停止"），不能吞掉真正的崩溃
            _previous?.UncaughtException(thread, ex);
        }
    }

    internal static void Write(string kind, Exception? ex, string? raw = null)
    {
        try
        {
            if (string.IsNullOrEmpty(_dir)) return;

            var sb = new StringBuilder();
            sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] AnMusic 崩溃 ({kind})");
            sb.AppendLine($"Android {AndroidBuild.VERSION.Release} (API {AndroidBuild.VERSION.SdkInt}) / {AndroidBuild.Model}");
            sb.AppendLine();

            if (ex is not null) sb.AppendLine(ex.ToString());
            if (!string.IsNullOrEmpty(raw)) sb.AppendLine(raw);

            lock (Gate)
            {
                var file = Path.Combine(_dir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}-{kind}.log");
                File.WriteAllText(file, sb.ToString());
            }
        }
        catch
        {
            // 崩溃处理器里再抛异常会雪上加霜，必须吞掉
        }
    }

    /// <summary>
    /// 取出最近一次未读的崩溃日志（读后即改名标记，避免每次启动都弹）。
    /// 顺带清理 7 天前的旧日志。没有待读日志时返回 null。
    /// </summary>
    public static (string Path, string Content)? TakePending()
    {
        try
        {
            if (string.IsNullOrEmpty(_dir) || !Directory.Exists(_dir)) return null;

            var file = Directory.EnumerateFiles(_dir, "crash-*.log")
                .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (file is null) return null;

            var content = File.ReadAllText(file);
            File.Move(file, file + ".read");

            foreach (var old in Directory.EnumerateFiles(_dir))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-7))
                        File.Delete(old);
                }
                catch { }
            }

            return (file, content);
        }
        catch
        {
            return null;
        }
    }
}
