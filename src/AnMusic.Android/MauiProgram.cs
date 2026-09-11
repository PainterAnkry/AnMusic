using AnMusic.Android.Services;
using AnMusic.Android.ViewModels;
using AnMusic.Services;
using AnMusic.Services.Audio;
using AnMusic.Services.Lyrics;
using AnMusic.Services.Metadata;
using AnMusic.Services.Playlist;
using AnMusic.Services.Providers;
using AnMusic.Services.Providers.JsPlugin;
using AnMusic.Services.Settings;
using AnMusic.Services.Stats;
using Microsoft.Extensions.DependencyInjection;
using Platform = AnMusic.Services.Platform;

namespace AnMusic.Android;

public static class MauiProgram
{
    /// <summary>
    /// 应用级服务容器：与桌面端 App.xaml.cs 的注册保持同一套 Core 服务，
    /// 只有平台差异项（音频引擎、平台上下文、主题、均衡器）换成安卓实现。
    /// </summary>
    public static IServiceProvider Services { get; private set; } = null!;

    public static MauiApp CreateMauiApp()
    {
        // ── 关键顺序：平台上下文必须在任何 Core 服务实例化之前注入，
        //    因为 AppPaths.DataRoot 会被这些服务的构造函数捕获。
        var platform = new AndroidPlatformContext();
        AnMusic.Services.Platform.SetContext(platform);

        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        RegisterCoreServices(builder.Services, platform);
        RegisterAndroidServices(builder.Services);
        RegisterPages(builder.Services);

        var app = builder.Build();
        Services = app.Services;

        // 封面降采样：把宿主钩子挂到 Core 缓存层，从源头控制解码内存。
        // 桌面端不注入（WPF 按需解码，无需处理），行为不变。
        CoverCacheService.CoverDownsampler = CoverMemory.Downsample;
        // 一次性把历史上已经存进缓存目录的大图原地压缩，新老文件一起治。
        _ = CoverMemory.ShrinkExistingAsync(AppPaths.CoversDir);

        // 均衡器要在启动时就挂上监听，否则换歌后音频会话变了没人重新挂载
        _ = Services.GetRequiredService<EqualizerService>();

        // 后台加载 JS 音源插件（本地 .js + plugins.json 清单远程下载），
        // 完成后注册进 ProviderRegistry，搜索结果才能出现插件音源。
        _ = LoadPluginsAsync();

        return app;
    }

    /// <summary>跨平台共享服务（与桌面端同源，均为 Core 内的实现）。</summary>
    private static void RegisterCoreServices(IServiceCollection services, IPlatformContext platform)
    {
        services.AddSingleton(platform);

        // 音频引擎：安卓实现（桌面端为 NAudioEngine）
        services.AddSingleton<AndroidAudioEngine>();
        services.AddSingleton<IAudioEngine>(sp => sp.GetRequiredService<AndroidAudioEngine>());

        services.AddSingleton<IPlaylistQueue, PlaylistQueue>();
        services.AddSingleton<IMetadataReader, TagLibMetadataReader>();
        services.AddSingleton<CoverCacheService>();
        services.AddSingleton<ILrcParser, LrcParser>();
        services.AddSingleton<LrclibLyricProvider>();
        services.AddSingleton<IOnlineLyricProvider>(sp => sp.GetRequiredService<LrclibLyricProvider>());
        services.AddSingleton<LyricTranslationService>();
        services.AddSingleton<UserSettingsService>();
        services.AddSingleton(_ => UserDataStore.Load());
        // 听歌统计：v3.3.0 曾漏注册，导致 UserViewModel/PlayerViewModel/StatsViewModel
        // 构造时 DI 解析失败，AppShell 一建就抛异常——真机上表现为"启动即闪退"。
        services.AddSingleton<ListeningStatsService>();

        // ── 音乐源与插件（修复此前插件搜索不可用：安卓端根本没注册这套）──
        services.AddSingleton<ProviderRegistry>();
        services.AddSingleton<JsPluginLoader>();
    }

    private static void RegisterAndroidServices(IServiceCollection services)
    {
        services.AddSingleton<EqualizerService>();
        services.AddSingleton<LocalMusicScanner>();

        services.AddSingleton<PlayerViewModel>();
        services.AddSingleton<LibraryViewModel>();
        services.AddSingleton<SearchViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<UserViewModel>();
        services.AddSingleton<DownloadViewModel>();
        services.AddSingleton<StatsViewModel>();
        services.AddSingleton<ListenTogetherViewModel>();
        services.AddSingleton<RadioViewModel>();
        services.AddSingleton<PluginsViewModel>();
        services.AddSingleton<ImportViewModel>();
    }

    private static void RegisterPages(IServiceCollection services)
    {
        services.AddTransient<MainPage>();
        services.AddTransient<PlayerPage>();
        services.AddTransient<SearchPage>();
        services.AddTransient<SettingsPage>();
        services.AddTransient<UserPage>();
        services.AddTransient<DiscoverPage>();
        services.AddTransient<DownloadsPage>();
        services.AddTransient<StatsPage>();
        services.AddTransient<ListenTogetherPage>();
        services.AddTransient<PluginsPage>();
        services.AddTransient<ImportPage>();
    }

    /// <summary>后台加载插件并注册，让在线搜索能命中插件音源。</summary>
    private static async Task LoadPluginsAsync()
    {
        try
        {
            var registry = Services.GetRequiredService<ProviderRegistry>();
            var loader = Services.GetRequiredService<JsPluginLoader>();

            var errors = await loader.LoadAllAsync();
            foreach (var err in errors)
                AppPaths.LogError("加载音源插件", new InvalidOperationException(err));

            registry.UnregisterJsPlugins();
            foreach (var plugin in loader.Plugins)
                registry.Register(plugin);

            Platform.Current.Log($"已加载 {loader.Plugins.Count} 个音源插件");
        }
        catch (Exception ex)
        {
            AppPaths.LogError("加载音源插件", ex);
        }
    }
}
