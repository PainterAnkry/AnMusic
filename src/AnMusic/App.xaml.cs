using System.IO;
using System.Windows;
using AnMusic.Services.Audio;
using AnMusic.Services.Lyrics;
using AnMusic.Services.Metadata;
using AnMusic.Services.Playlist;
using AnMusic.Services.Providers;
using AnMusic.Services.Providers.Bilibili;
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

        // 应用已保存的主题
        var settings = _serviceProvider.GetRequiredService<UserSettingsService>().Settings;
        ThemeService.Apply(settings.Theme != "Light");

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();

        // 恢复上次的音乐目录扫描
        if (settings.MusicDirectory is { Length: > 0 } musicDir && Directory.Exists(musicDir))
        {
            _ = _serviceProvider.GetRequiredService<LibraryViewModel>().ScanDirectoryAsync(musicDir);
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
        // 必须同时注册为 IMusicProvider，ProviderRegistry 才能按 Id 聚合到它
        services.AddSingleton<IMusicProvider>(sp => sp.GetRequiredService<BilibiliMusicProvider>());
        services.AddSingleton<IOnlineMusicProvider>(sp => sp.GetRequiredService<BilibiliMusicProvider>());

        // 第三方实现注册进 DI（IMusicProvider / IOnlineMusicProvider / IOnlineLyricProvider）
        // 后会自动被 ProviderRegistry 聚合，无需改动其他代码。
        services.AddSingleton<ProviderRegistry>();

        // ViewModels
        services.AddSingleton<PlaybackBarViewModel>();
        services.AddSingleton<LibraryViewModel>();
        services.AddSingleton<LyricViewModel>();
        services.AddSingleton<EqualizerViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MainViewModel>();

        // 窗口
        services.AddSingleton<MainWindow>();
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
