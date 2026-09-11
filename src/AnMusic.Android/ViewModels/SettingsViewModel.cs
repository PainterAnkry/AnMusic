using System.Collections.ObjectModel;
using AnMusic.Android.Services;
using AnMusic.Services;
using AnMusic.Services.Providers;
using AnMusic.Services.Providers.JsPlugin;
using AnMusic.Services.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnMusic.Android.ViewModels;

/// <summary>
/// 插件入口已迁移到独立 PluginsPage（侧边栏 → 音源插件），
/// 这里保留皮肤选择项即可。旧的 PluginItem 类型已删除以避免冲突。
/// </summary>

/// <summary>皮肤选择项：底色 + 配套强调色的预览。</summary>
public sealed class SkinOption
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required Color Background { get; init; }
    public required Color Accent { get; init; }

    /// <summary>深色皮肤在名称后加个月亮，便于一眼分辨。</summary>
    public string DisplayName => IsDark ? $"🌙 {Name}" : Name;

    public bool IsDark { get; init; }
}

/// <summary>强调色选择项。</summary>
public sealed class AccentOption
{
    public required int Index { get; init; }
    public required string Name { get; init; }
    public required Color Color { get; init; }
}

/// <summary>
/// 设置页 ViewModel：外观皮肤、播放、歌词、本地音乐、音源插件、缓存、更多功能、关于。
/// 设置项的持久化统一走 Core 的 <see cref="UserSettingsService"/>，与桌面端共用 settings.json。
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly UserSettingsService _settings;
    private readonly IPlatformContext _platform;
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
        _player = player;

        BuildThemeOptions();

        // 从持久化设置读入初始值（避免 OnXxxChanged 回写造成抖动，这里先赋值再挂标志）
        _suppressPersist = true;
        DefaultVolume = Math.Clamp(settings.Settings.DefaultVolume, 0, 1);
        IsOnlineLyrics = settings.Settings.EnableOnlineLyrics;
        LyricFontSize = settings.Settings.LyricFontSize;
        UserNickname = settings.Settings.UserNickname;
        ProxyUrl = settings.Settings.ProxyUrl ?? string.Empty;
        _lyricColorIndex = Math.Clamp(settings.Settings.LyricColorIndex, 0, LyricColorNames.Length - 1);
        _suppressPersist = false;

        RefreshCacheSize();
        RefreshMusicDirs();
        LoadPlayModeFromPlayer();

        // 播放中的定时关闭状态实时显示在设置页
        _player.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PlayerViewModel.SleepTimerText) or nameof(PlayerViewModel.HasSleepTimer))
                OnPropertyChanged(nameof(SleepTimerText));
        };
    }

    /// <summary>初始化期间为 true：不把读入的值再写回磁盘。</summary>
    private bool _suppressPersist;

    #region 外观（皮肤 + 强调色）

    public ObservableCollection<SkinOption> Skins { get; } = [];

    public ObservableCollection<AccentOption> Accents { get; } = [];

    [ObservableProperty] private string _currentThemeText = "浅色 · 科技蓝";

    private void BuildThemeOptions()
    {
        Skins.Clear();
        foreach (var skin in ThemeService.Skins)
        {
            Skins.Add(new SkinOption
            {
                Id = skin.Id,
                Name = skin.Name,
                IsDark = skin.IsDark,
                Background = Color.FromArgb(skin.BgHex),
                Accent = Color.FromArgb(skin.AccentHex),
            });
        }

        Accents.Clear();
        for (var i = 0; i < ThemeService.AccentNames.Count; i++)
        {
            Accents.Add(new AccentOption
            {
                Index = i,
                Name = ThemeService.AccentNames[i],
                Color = ThemeService.AccentColor(i),
            });
        }

        RefreshThemeText();
    }

    private void RefreshThemeText() =>
        CurrentThemeText = $"{ThemeService.Current.Name} · {ThemeService.AccentNames[ThemeService.CurrentAccentIndex]}";

    /// <summary>选用某套皮肤（同时套用它自带的配套强调色，与桌面端一致）。</summary>
    public void SelectSkin(string skinId)
    {
        var skin = ThemeService.Find(skinId);
        if (skin is null) return;

        ThemeService.Apply(skin.Id, skin.AccentIndex);
        _settings.Update(s =>
        {
            s.Theme = skin.Id;
            s.AccentColorIndex = skin.AccentIndex;
        });

        OnPropertyChanged(nameof(CurrentSkinId));
        OnPropertyChanged(nameof(CurrentAccentIndex));
        RefreshThemeText();
    }

    /// <summary>只换强调色，底色皮肤保持不变。</summary>
    public void SelectAccent(int index)
    {
        ThemeService.Apply(ThemeService.CurrentSkinId, index);
        _settings.Update(s => s.AccentColorIndex = index);

        OnPropertyChanged(nameof(CurrentAccentIndex));
        RefreshThemeText();
    }

    /// <summary>当前皮肤 Id（设置页高亮选中项用）。</summary>
    public string CurrentSkinId => ThemeService.CurrentSkinId;

    /// <summary>当前强调色下标。</summary>
    public int CurrentAccentIndex => ThemeService.CurrentAccentIndex;

    #endregion

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
        _player.ApplyLyricColor();
    }

    partial void OnUserNicknameChanged(string value)
    {
        if (_suppressPersist) return;
        _settings.Update(s => s.UserNickname = value);
    }

    /// <summary>播放页封面是否旋转（透传给播放器，内部用 Preferences 持久化）。</summary>
    public bool IsCoverSpinEnabled
    {
        get => _player.IsCoverSpinEnabled;
        set
        {
            if (_player.IsCoverSpinEnabled == value) return;
            _player.IsCoverSpinEnabled = value;
            OnPropertyChanged();
        }
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

    #region 歌词配色

    /// <summary>歌词颜色方案名（下标与 UserSettings.LyricColorIndex 对应，两端共用）。</summary>
    private static readonly string[] LyricColorNames = ["跟随主题", "纯白", "纯黑", "樱花粉", "天空蓝", "薄荷绿"];

    [ObservableProperty] private int _lyricColorIndex;

    public string LyricColorText => LyricColorNames[Math.Clamp(LyricColorIndex, 0, LyricColorNames.Length - 1)];

    /// <summary>
    /// 解析出的歌词文本色，null = 跟随主题（由播放页用主题资源）。
    /// 与桌面端同一套配色，保证两端观感一致。
    /// </summary>
    public Color? LyricColor => LyricColorIndex switch
    {
        1 => Colors.White,
        2 => Colors.Black,
        3 => Color.FromArgb("#F48FB1"),
        4 => Color.FromArgb("#64B5F6"),
        5 => Color.FromArgb("#81C784"),
        _ => null,
    };

    partial void OnLyricColorIndexChanged(int value)
    {
        OnPropertyChanged(nameof(LyricColorText));
        OnPropertyChanged(nameof(LyricColor));

        if (_suppressPersist) return;
        _settings.Update(s => s.LyricColorIndex = value);
    }

    /// <summary>点击「歌词配色」：在 6 套方案间循环，播放页立即生效。</summary>
    [RelayCommand]
    private void CycleLyricColor()
        => LyricColorIndex = (LyricColorIndex + 1) % LyricColorNames.Length;

    #endregion

    #region 网络代理

    [ObservableProperty] private string _proxyUrl = string.Empty;

    partial void OnProxyUrlChanged(string value)
    {
        if (_suppressPersist) return;

        var normalized = value?.Trim() ?? string.Empty;
        _settings.Update(s => s.ProxyUrl = normalized);

        // 立即生效：HttpService 会重建共享客户端并广播 ProxyChanged，
        // 音源客户端据此重建各自带 CookieContainer 的连接。
        try
        {
            AnMusic.Services.Net.HttpService.ConfigureProxy(normalized);
        }
        catch (Exception ex)
        {
            AppPaths.LogError("应用代理设置", ex, normalized);
        }
    }

    /// <summary>清空代理（恢复跟随系统）。</summary>
    [RelayCommand]
    private void ClearProxy() => ProxyUrl = string.Empty;

    #endregion

    #region 定时关闭

    /// <summary>当前定时关闭状态文案。</summary>
    public string SleepTimerText => _player.HasSleepTimer ? _player.SleepTimerText : "未开启";

    /// <summary>在设置页直接启动 / 取消定时关闭。</summary>
    public void ApplySleepTimer(int minutes)
    {
        if (minutes <= 0) _player.CancelSleepTimer();
        else _player.StartSleepTimer(minutes);

        OnPropertyChanged(nameof(SleepTimerText));
    }

    public void StopAfterCurrentTrack()
    {
        _player.StopAfterCurrentTrack();
        OnPropertyChanged(nameof(SleepTimerText));
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

    /// <summary>插件管理已迁移到独立 <c>PluginsPage</c>（侧边栏入口），这里只暴露一个跳转提示。</summary>
    public string PluginDirectory => JsPluginLoader.PluginDir;

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

    /// <summary>与桌面端同一个仓库，Release 里同时挂着 exe 与 apk。</summary>
    private const string Repo = "PainterAnkry/AnMusic";

    /// <summary>联系作者邮箱。</summary>
    public const string AuthorEmail = "835176240@qq.com";

    /// <summary>交流 QQ 群号。</summary>
    public const string QQGroup = "348061212";

    [ObservableProperty] private string _updateCheckText = "检查更新";
    [ObservableProperty] private bool _isCheckingUpdate;

    /// <summary>查到的 APK 下载地址（下载完成后置空）。</summary>
    private string? _updateAssetUrl;

    /// <summary>
    /// 检查更新：读 GitHub 的 releases/latest，比对版本号。
    /// 有新版时直接下载 APK 并唤起系统安装器（安卓不允许静默安装，
    /// 必须先申请「安装未知来源应用」权限）。
    /// </summary>
    [RelayCommand]
    private async Task CheckUpdateAsync()
    {
        if (IsCheckingUpdate) return;

        IsCheckingUpdate = true;
        UpdateCheckText = "正在检查…";

        try
        {
            using var http = AnMusic.Services.Net.HttpService.CreateClient(timeout: TimeSpan.FromSeconds(30));
            http.DefaultRequestHeaders.Add("User-Agent", "AnMusic-Android");

            using var resp = await http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest");
            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = System.Text.Json.JsonDocument.Parse(json);

            if (AnMusic.Services.Update.UpdateFeed.ParseRelease(doc.RootElement) is not { } info)
            {
                UpdateCheckText = "解析更新信息失败";
                await AlertAsync("检查更新", "未能解析版本信息，请稍后再试。");
                return;
            }

            if (!AnMusic.Services.Update.UpdateFeed.IsNewer(info.LatestVersion, AppVersion))
            {
                UpdateCheckText = $"已是最新版本 v{AppVersion}";
                await AlertAsync("检查更新", $"当前已是最新版本 v{AppVersion}。");
                return;
            }

            // 桌面端的 PickInstallerAsset 只认 .exe，安卓这边要单独挑 .apk
            var (apkUrl, apkName) = AnMusic.Services.Update.UpdateFeed
                .PickAssetByExtension(doc.RootElement, ".apk");

            if (string.IsNullOrEmpty(apkUrl))
            {
                UpdateCheckText = $"发现新版本 v{info.LatestVersion}（无 APK）";
                await AlertAsync(
                    "发现新版本",
                    $"最新版本 v{info.LatestVersion}，但该 Release 未附带 APK 安装包。\n" +
                    "请前往项目主页手动下载。");
                return;
            }

            var confirm = await ConfirmAsync(
                "发现新版本",
                $"最新版本 v{info.LatestVersion}，当前 v{AppVersion}。\n\n是否下载并安装？",
                "下载安装", "稍后");

            if (!confirm)
            {
                UpdateCheckText = $"发现新版本 v{info.LatestVersion}";
                return;
            }

            _updateAssetUrl = apkUrl;
            await DownloadAndInstallAsync(apkUrl, apkName ?? $"AnMusic-{info.LatestVersion}.apk");
        }
        catch (Exception ex)
        {
            UpdateCheckText = "检查更新失败";
            AppPaths.LogError("检查更新", ex);
            await AlertAsync("检查更新失败", $"{ex.Message}\n\n请检查网络，或已配置的代理是否可用。");
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    private async Task DownloadAndInstallAsync(string url, string fileName)
    {
        // 放在 cache 目录，装完后由系统清理
        var target = Path.Combine(FileSystem.CacheDirectory, fileName);

        var progress = new Progress<AnMusic.Services.Update.UpdateFeed.DownloadProgress>(p =>
        {
            UpdateCheckText = p.Percent >= 0
                ? $"下载中 {p.Percent:F0}%"
                : $"下载中 {p.Read / 1024.0 / 1024.0:F1} MB";
        });

        await AnMusic.Services.Update.UpdateFeed.DownloadAsync(url, target, progress);
        UpdateCheckText = $"已下载 {fileName}";

        // 唤起系统安装器：安卓要求先授予「安装未知来源应用」权限，
        // 未授权时系统会自行弹出授权引导页。
        await InstallApkAsync(target);
    }

    private static async Task InstallApkAsync(string apkPath)
    {
        try
        {
            var context = global::Android.App.Application.Context;
            var file = new Java.IO.File(apkPath);
            var uri = global::AndroidX.Core.Content.FileProvider.GetUriForFile(
                context,
                $"{context.PackageName}.fileprovider",
                file);

            var intent = new global::Android.Content.Intent(global::Android.Content.Intent.ActionView);
            intent.SetDataAndType(uri, "application/vnd.android.package-archive");
            intent.AddFlags(global::Android.Content.ActivityFlags.GrantReadUriPermission);
            intent.AddFlags(global::Android.Content.ActivityFlags.NewTask);

            context.StartActivity(intent);
        }
        catch (Exception ex)
        {
            AppPaths.LogError("唤起安装器", ex, apkPath);
            await AlertAsync(
                "无法自动安装",
                $"安装包已下载到：\n{apkPath}\n\n请用文件管理器手动打开安装。\n\n{ex.Message}");
        }
    }

    /// <summary>用户协议文本（与桌面端保持同一份措辞）。</summary>
    public const string UserAgreement = """
        AnMusic 用户协议

        1. 本软件为开源免费音乐播放器，仅供个人学习与非商业用途使用。
        2. 用户须遵守所使用音乐源平台的用户协议与版权法规，不得用于侵权行为。
        3. 在线音源（网易云、QQ音乐、B站等）的搜索与播放能力依赖第三方公开接口，
           接口变更或服务终止可能导致相关功能不可用，本软件不承担由此造成的损失。
        4. 用户下载的音乐文件仅限个人离线欣赏，不得二次传播或用于商业用途。
        5. 本软件尊重版权，若您认为某功能侵犯了您的权益，请联系开发者移除。
        """;

    /// <summary>免责声明文本。</summary>
    public const string DisclaimerText = """
        免责声明

        本软件按"现状"提供，不提供任何明示或暗示的保证，包括但不限于适销性、
        特定用途适用性和非侵权性的保证。开发者不对因使用本软件而造成的任何直接、
        间接、附带、特殊或后果性损害承担责任。

        在线音乐源的音质与可用性受第三方平台限制，VIP/付费内容可能无法播放。
        请支持正版音乐，尊重艺术家的劳动成果。
        """;

    [RelayCommand]
    private static Task ShowAgreement() => AlertAsync("用户协议", UserAgreement);

    [RelayCommand]
    private static Task ShowDisclaimer() => AlertAsync("免责声明", DisclaimerText);

    /// <summary>复制作者邮箱到剪贴板。</summary>
    [RelayCommand]
    private static async Task CopyEmailAsync()
    {
        await Clipboard.Default.SetTextAsync(AuthorEmail);
        await AlertAsync("已复制", $"作者邮箱已复制到剪贴板：\n{AuthorEmail}");
    }

    /// <summary>复制 QQ 群号到剪贴板。</summary>
    [RelayCommand]
    private static async Task CopyQQGroupAsync()
    {
        await Clipboard.Default.SetTextAsync(QQGroup);
        await AlertAsync("已复制", $"QQ 群号已复制到剪贴板：\n{QQGroup}");
    }

    private static Task AlertAsync(string title, string message) =>
        MainThread.InvokeOnMainThreadAsync(() => Shell.Current.DisplayAlert(title, message, "好"));

    private static Task<bool> ConfirmAsync(string title, string message, string accept, string cancel) =>
        MainThread.InvokeOnMainThreadAsync(() => Shell.Current.DisplayAlert(title, message, accept, cancel));

    #endregion

    #region 预留功能

    /// <summary>尚未实现的入口统一提示，避免每个占位项各写一遍。</summary>
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
        RefreshCacheSize();
        RefreshMusicDirs();
        LoadPlayModeFromPlayer();

        OnPropertyChanged(nameof(CurrentSkinId));
        OnPropertyChanged(nameof(CurrentAccentIndex));
        OnPropertyChanged(nameof(IsCoverSpinEnabled));
        OnPropertyChanged(nameof(SleepTimerText));
        RefreshThemeText();
    }
}
