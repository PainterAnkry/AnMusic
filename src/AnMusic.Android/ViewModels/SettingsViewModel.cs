using System.Collections.ObjectModel;
using AnMusic.Services;
using AnMusic.Services.Providers;
using AnMusic.Services.Providers.JsPlugin;
using AnMusic.Services.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnMusic.Android.ViewModels;

/// <summary>已加载插件的展示项。</summary>
public sealed class PluginItem
{
    public required string DisplayName { get; init; }
    public required string FileName { get; init; }
    public string Version { get; init; } = string.Empty;

    /// <summary>名称旁的版本后缀。</summary>
    public string VersionLabel => string.IsNullOrWhiteSpace(Version) ? string.Empty : $"v{Version}";
}

/// <summary>
/// 设置页 ViewModel：播放、歌词、本地音乐、音源插件、缓存、关于。
/// 设置项的持久化统一走 Core 的 <see cref="UserSettingsService"/>，与桌面端共用 settings.json。
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly UserSettingsService _settings;
    private readonly IPlatformContext _platform;
    private readonly ProviderRegistry _registry;
    private readonly JsPluginLoader _loader;
    private readonly PlayerViewModel _player;

    public SettingsViewModel(
        UserSettingsService settings,
        IPlatformContext platform,
        ProviderRegistry registry,
        JsPluginLoader loader,
        PlayerViewModel player)
    {
        _settings = settings;
        _platform = platform;
        _registry = registry;
        _loader = loader;
        _player = player;

        // 从持久化设置读入初始值（避免 OnXxxChanged 回写造成抖动，这里先赋值再挂标志）
        _suppressPersist = true;
        DefaultVolume = Math.Clamp(settings.Settings.DefaultVolume, 0, 1);
        IsOnlineLyrics = settings.Settings.EnableOnlineLyrics;
        LyricFontSize = settings.Settings.LyricFontSize;
        UserNickname = settings.Settings.UserNickname;
        _suppressPersist = false;

        RefreshPlugins();
        RefreshCacheSize();
        LoadPlayModeFromPlayer();
    }

    /// <summary>初始化期间为 true：不把读入的值再写回磁盘。</summary>
    private bool _suppressPersist;

    #region 播放设置

    [ObservableProperty] private double _defaultVolume = 1;
    [ObservableProperty] private string _playModeText = "顺序播放";
    [ObservableProperty] private bool _isOnlineLyrics;
    [ObservableProperty] private double _lyricFontSize = 14;
    [ObservableProperty] private string _userNickname = "音乐爱好者";

    /// <summary>音质选择（预留：安卓端当前由系统解码器决定）。</summary>
    [ObservableProperty] private string _audioQualityText = "自动（系统解码）";

    partial void OnDefaultVolumeChanged(double value)
    {
        if (_suppressPersist) return;
        _settings.Update(s => s.DefaultVolume = value);
        _player.Volume = value;
    }

    partial void OnIsOnlineLyricsChanged(bool value)
    {
        if (_suppressPersist) return;
        _settings.Update(s => s.EnableOnlineLyrics = value);
    }

    partial void OnLyricFontSizeChanged(double value)
    {
        if (_suppressPersist) return;
        _settings.Update(s => s.LyricFontSize = value);
    }

    partial void OnUserNicknameChanged(string value)
    {
        if (_suppressPersist) return;
        _settings.Update(s => s.UserNickname = value);
    }

    private void LoadPlayModeFromPlayer() => PlayModeText = _player.PlayModeName;

    /// <summary>点击「默认播放模式」：循环切换并同步给播放器。</summary>
    [RelayCommand]
    private void CyclePlayMode()
    {
        _player.CyclePlayModeCommand.Execute(null);
        PlayModeText = _player.PlayModeName;
    }

    #endregion

    #region 本地音乐

    [ObservableProperty] private string _musicDirText = string.Empty;

    /// <summary>扫描到的本地曲目数说明。</summary>
    [ObservableProperty] private string _localTrackCountText = "尚未扫描";

    public ObservableCollection<string> MusicDirectories { get; } = [];

    /// <summary>刷新可扫描目录列表。</summary>
    [RelayCommand]
    private void RefreshMusicDirs()
    {
        MusicDirectories.Clear();
        foreach (var dir in _platform.LocalMusicDirectories)
            MusicDirectories.Add(dir);

        MusicDirText = MusicDirectories.Count == 0
            ? "未找到可访问的音乐目录"
            : $"共 {MusicDirectories.Count} 个可扫描目录";
    }

    #endregion

    #region 音源插件

    public ObservableCollection<PluginItem> Plugins { get; } = [];

    [ObservableProperty] private string _pluginStatusText = "尚未加载";
    [ObservableProperty] private bool _isLoadingPlugins;

    /// <summary>是否已加载到插件（用于插件列表的显隐）。</summary>
    public bool HasPlugins => Plugins.Count > 0;

    /// <summary>插件目录路径（提示用户往哪放 .js）。</summary>
    public string PluginDirectory => JsPluginLoader.PluginDir;

    /// <summary>插件清单文件路径（放 plugins.json 可自动下载远程插件）。</summary>
    public string PluginManifestPath => JsPluginLoader.ManifestPath;

    private void RefreshPlugins()
    {
        Plugins.Clear();
        foreach (var p in _loader.Plugins)
        {
            Plugins.Add(new PluginItem
            {
                DisplayName = p.DisplayName,
                FileName = p.FileName,
                Version = p.Version,
            });
        }

        var online = _registry.OnlineMusicProviders.Count;
        PluginStatusText = Plugins.Count == 0
            ? "未加载任何插件音源"
            : $"已加载 {Plugins.Count} 个音源插件，在线音源共 {online} 个";

        OnPropertyChanged(nameof(HasPlugins));
    }

    /// <summary>重新加载插件（用户放入新 .js 后无需重启）。</summary>
    [RelayCommand]
    private async Task ReloadPluginsAsync()
    {
        if (IsLoadingPlugins) return;
        IsLoadingPlugins = true;
        PluginStatusText = "正在加载…";

        try
        {
            var errors = await _loader.LoadAllAsync();

            _registry.UnregisterJsPlugins();
            foreach (var plugin in _loader.Plugins)
                _registry.Register(plugin);

            RefreshPlugins();

            if (errors.Count > 0)
                PluginStatusText += $"\n有 {errors.Count} 个插件加载失败，详见插件目录下的 load-errors.log";
        }
        catch (Exception ex)
        {
            PluginStatusText = $"加载失败：{ex.Message}";
            AppPaths.LogError("重新加载插件", ex);
        }
        finally
        {
            IsLoadingPlugins = false;
        }
    }

    #endregion

    #region 缓存

    [ObservableProperty] private string _coverCacheText = "计算中…";
    [ObservableProperty] private string _totalCacheText = string.Empty;

    private void RefreshCacheSize()
    {
        try
        {
            var coverBytes = CoverCacheService.GetCacheSize();
            CoverCacheText = FormatSize(coverBytes);

            // 音频缓存目录可能不存在，逐个容错累加
            long audioBytes = 0;
            foreach (var dir in new[] { AppPaths.AudioCacheDir, AppPaths.PluginAudioCacheDir })
            {
                try
                {
                    if (Directory.Exists(dir))
                        audioBytes += new DirectoryInfo(dir).GetFiles().Sum(f => f.Length);
                }
                catch (Exception ex)
                {
                    AppPaths.LogError("统计音频缓存", ex, dir);
                }
            }

            TotalCacheText = audioBytes > 0
                ? $"音频缓存 {FormatSize(audioBytes)}"
                : "暂无音频缓存";
        }
        catch (Exception ex)
        {
            CoverCacheText = "统计失败";
            AppPaths.LogError("统计缓存", ex);
        }
    }

    /// <summary>清理封面缓存。</summary>
    [RelayCommand]
    private async Task ClearCoverCacheAsync()
    {
        var confirm = await MainThread.InvokeOnMainThreadAsync(() =>
            Shell.Current.DisplayAlert(
                "清理封面缓存", "将删除所有已缓存的专辑封面，下次浏览时会重新下载。", "清理", "取消"));
        if (!confirm) return;

        try
        {
            var dir = AppPaths.CoversDir;
            if (Directory.Exists(dir))
            {
                foreach (var file in new DirectoryInfo(dir).GetFiles())
                {
                    try { file.Delete(); }
                    catch { /* 被占用的跳过 */ }
                }
            }
            RefreshCacheSize();
            await MainThread.InvokeOnMainThreadAsync(() =>
                Shell.Current.DisplayAlert("完成", "封面缓存已清理。", "好"));
        }
        catch (Exception ex)
        {
            AppPaths.LogError("清理封面缓存", ex);
            await MainThread.InvokeOnMainThreadAsync(() =>
                Shell.Current.DisplayAlert("失败", ex.Message, "好"));
        }
    }

    /// <summary>清理音频缓存（在线音源缓冲文件）。</summary>
    [RelayCommand]
    private async Task ClearAudioCacheAsync()
    {
        var confirm = await MainThread.InvokeOnMainThreadAsync(() =>
            Shell.Current.DisplayAlert(
                "清理音频缓存", "将删除在线音源的缓冲文件，已收藏的本地音乐不受影响。", "清理", "取消"));
        if (!confirm) return;

        try
        {
            foreach (var dir in new[] { AppPaths.AudioCacheDir, AppPaths.PluginAudioCacheDir })
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var file in new DirectoryInfo(dir).GetFiles())
                {
                    try { file.Delete(); }
                    catch { /* 正在播放的文件删不掉，跳过 */ }
                }
            }
            RefreshCacheSize();
            await MainThread.InvokeOnMainThreadAsync(() =>
                Shell.Current.DisplayAlert("完成", "音频缓存已清理。", "好"));
        }
        catch (Exception ex)
        {
            AppPaths.LogError("清理音频缓存", ex);
            await MainThread.InvokeOnMainThreadAsync(() =>
                Shell.Current.DisplayAlert("失败", ex.Message, "好"));
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F2} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F0} KB",
        _ => $"{bytes} B",
    };

    #endregion

    #region 关于

    public string AppVersion => AppInfo.Current.VersionString;
    public string AppBuild => AppInfo.Current.BuildString;

    /// <summary>数据根目录（排查问题用）。</summary>
    public string DataRoot => AppPaths.DataRoot;

    #endregion

    #region 预留功能入口

    /// <summary>后续版本功能的占位提示，统一在这里给出，避免每个占位项各写一遍。</summary>
    [RelayCommand]
    private static async Task ComingSoon(string? feature)
    {
        await Shell.Current.DisplayAlert(
            feature ?? "该功能",
            "此功能正在开发中，将在后续版本提供。",
            "好");
    }

    #endregion

    /// <summary>页面显示时刷新动态数据。</summary>
    public void Refresh()
    {
        RefreshPlugins();
        RefreshCacheSize();
        RefreshMusicDirs();
        LoadPlayModeFromPlayer();
    }
}
