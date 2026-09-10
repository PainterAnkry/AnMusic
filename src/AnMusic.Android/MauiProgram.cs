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
using Platform = AnMusic.Services.Platform;

namespace AnMusic.Android;

public static class MauiProgram
{
    /// <summary>
    /// 应用级服务容器：与桌面端 App.xaml.cs 的注册保持同一套 Core 服务，
    /// 只有平台差异项（音频引擎、平台上下文）换成安卓实现。
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

        builder.Services.AddTransient<MainPage>();
        builder.Services.AddTransient<PlayerPage>();
        builder.Services.AddTransient<SearchPage>();
        builder.Services.AddTransient<SettingsPage>();

        var app = builder.Build();
        Services = app.Services;

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
        services.AddSingleton<IAudioEngine, AndroidAudioEngine>();

        services.AddSingleton<IPlaylistQueue, PlaylistQueue>();
        services.AddSingleton<IMetadataReader, TagLibMetadataReader>();
        services.AddSingleton<CoverCacheService>();
        services.AddSingleton<ILrcParser, LrcParser>();
        services.AddSingleton<LrclibLyricProvider>();
        services.AddSingleton<IOnlineLyricProvider>(sp => sp.GetRequiredService<LrclibLyricProvider>());
        services.AddSingleton<UserSettingsService>();
        services.AddSingleton(_ => UserDataStore.Load());

        // ── 音乐源与插件（修复此前插件搜索不可用：安卓端根本没注册这套）──
        services.AddSingleton<ProviderRegistry>();
        services.AddSingleton<JsPluginLoader>();
    }

    private static void RegisterAndroidServices(IServiceCollection services)
    {
        services.AddSingleton<LocalMusicScanner>();
        services.AddSingleton<PlayerViewModel>();
        services.AddSingleton<LibraryViewModel>();
        services.AddSingleton<SearchViewModel>();
        services.AddSingleton<SettingsViewModel>();
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

            platform_log($"已加载 {loader.Plugins.Count} 个音源插件");
        }
        catch (Exception ex)
        {
            AppPaths.LogError("加载音源插件", ex);
        }

        static void platform_log(string message) => Platform.Current.Log(message);
    }
}
