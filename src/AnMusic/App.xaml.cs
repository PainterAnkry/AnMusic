using System.Diagnostics;
using System.IO;
using System.Windows;
using AnMusic.Services.Audio;
using AnMusic.Services.Lyrics;
using AnMusic.Services.Metadata;
using AnMusic.Services.Playlist;
using AnMusic.Services.Providers;
using AnMusic.Services.Providers.Bilibili;
using AnMusic.Services.Providers.JsPlugin;
using AnMusic.Services.Providers.NetEase;
using AnMusic.Services.Providers.QQMusic;
using AnMusic.Services.Settings;
using AnMusic.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace AnMusic;

/// <summary>
/// 应用入口：配置 DI 容器。
/// </summary>
public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        // 应用已保存的主题与强调色
        var settings = _serviceProvider.GetRequiredService<UserSettingsService>().Settings;
        ThemeService.Apply(settings.Theme != "Light");
        ThemeService.ApplyAccent(settings.AccentColorIndex);

        // 加载外部 .js 音源插件（含 plugins.json 清单远程下载）并注册进 ProviderRegistry。
        // 注意：必须异步执行——UI 线程同步等待会与 await 的 SynchronizationContext 死锁。
        var registry = _serviceProvider.GetRequiredService<ProviderRegistry>();
        var pluginLoader = _serviceProvider.GetRequiredService<JsPluginLoader>();
        _ = LoadPluginsAsync(registry, pluginLoader);

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();

        // 恢复上次的音乐目录扫描
        if (settings.MusicDirectory is { Length: > 0 } musicDir && Directory.Exists(musicDir))
        {
            _ = _serviceProvider.GetRequiredService<LibraryViewModel>().ScanDirectoryAsync(musicDir);
        }
    }

    /// <summary>后台加载插件并注册（下载远程清单插件 + 扫描本地 .js）。</summary>
    private static async Task LoadPluginsAsync(ProviderRegistry registry, JsPluginLoader pluginLoader)
    {
        try
        {
            var errors = await pluginLoader.LoadAllAsync();
            foreach (var err in errors)
                Trace.WriteLine($"[JsPlugin] {err}");
            foreach (var plugin in pluginLoader.Plugins)
                registry.Register(plugin);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[JsPlugin] 插件加载异常: {ex.Message}");
        }
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // 基础设施
        services.AddSingleton<UserSettingsService>();
        services.AddSingleton<EqualizerService>();
        services.AddSingleton<IAudioEngine>(sp => new NAudioEngine(sp.GetRequiredService<EqualizerService>()));
        services.AddSingleton<IMetadataReader, TagLibMetadataReader>();
        services.AddSingleton<CoverCacheService>();
        services.AddSingleton<IPlaylistQueue, PlaylistQueue>();
        services.AddSingleton<ILrcParser, LrcParser>();
        services.AddSingleton<LocalLyricProvider>();
        // 从磁盘加载用户数据（歌单/我喜欢/最近播放/搜索历史）；此前用无参构造导致加载数据永远为空
        services.AddSingleton(_ => UserDataStore.Load());

        // LRCLIB 在线歌词源（开源歌词库，免费无需密钥；设置页开关控制）
        services.AddSingleton<LrclibLyricProvider>();
        services.AddSingleton<IOnlineLyricProvider>(sp => sp.GetRequiredService<LrclibLyricProvider>());

        // 音乐源
        services.AddSingleton<LocalFileProvider>();
        services.AddSingleton<IMusicProvider>(sp => sp.GetRequiredService<LocalFileProvider>());

        // B 站在线音乐源（仅音频缓存播放，遵循 B 站用户协议）
        services.AddSingleton<BilibiliApiClient>();
        services.AddSingleton<BilibiliMusicProvider>();
        services.AddSingleton<IMusicProvider>(sp => sp.GetRequiredService<BilibiliMusicProvider>());
        services.AddSingleton<IOnlineMusicProvider>(sp => sp.GetRequiredService<BilibiliMusicProvider>());

        // 网易云 / QQ 音乐已改为外部 .js 插件源（plugins.json 清单自动下载），不再注册内置实现

        // 第三方实现注册进 DI（IMusicProvider / IOnlineMusicProvider / IOnlineLyricProvider）
        // 后会自动被 ProviderRegistry 聚合，无需改动其他代码。
        services.AddSingleton<ProviderRegistry>();

        // 外部 .js 音源插件加载器（%AppData%\AnMusic\plugins\*.js）
        services.AddSingleton<JsPluginLoader>();

        // ViewModels
        services.AddSingleton<PlaybackBarViewModel>();
        services.AddSingleton<LibraryViewModel>();
        services.AddSingleton<LyricViewModel>();
        services.AddSingleton<EqualizerViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<ListenTogetherViewModel>();
        services.AddSingleton<MainViewModel>();

        // 窗口
        services.AddSingleton<MainWindow>();
        // 桌面歌词窗口：用工厂模式每次解析新建实例（关闭后无法复用同一实例）
        services.AddTransient<Views.DesktopLyricsWindow>();
        services.AddSingleton<Func<Views.DesktopLyricsWindow>>(sp => () => sp.GetRequiredService<Views.DesktopLyricsWindow>());
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_serviceProvider is { } sp)
        {
            var engine = sp.GetService<IAudioEngine>();
            engine?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        }
        base.OnExit(e);
    }
}
