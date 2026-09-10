using System.IO;
using System.Reflection;
using System.Net.Http;
using System.Text.Json;
using AnMusic.Services.Lyrics;
using AnMusic.Services.Settings;
using AnMusic.Services.Audio;
using AnMusic.Services.Providers;
using AnMusic.Services.Providers.Bilibili;
using AnMusic.Services.Providers.JsPlugin;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace AnMusic.ViewModels;

/// <summary>
/// 设置页 ViewModel：主题、音乐目录、默认音量、在线歌词开关（默认关）。
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly UserSettingsService _settingsService;
    private readonly LibraryViewModel _library;
    private readonly PlaybackBarViewModel _playbackBar;

    /// <summary>皮肤下拉框索引（对应 ThemeService.Skins）。</summary>
    [ObservableProperty]
    private int _skinIndex;

    /// <summary>当前皮肤名称。</summary>
    public string SkinName => ThemeService.Find(ThemeService.CurrentSkinId)?.Name ?? "浅色";

    /// <summary>当前是否深色底（供主窗口调整背景图不透明度等逻辑复用）。</summary>
    public bool IsDarkTheme => ThemeService.IsDark;

    /// <summary>全部皮肤名称（下拉框数据源）。</summary>
    public IReadOnlyList<string> SkinNames { get; } =
        ThemeService.Skins.Select(s => s.Name).ToArray();

    /// <summary>强调色名称（下拉框数据源）。</summary>
    public IReadOnlyList<string> AccentNames { get; } = ThemeService.AccentNames;

    /// <summary>皮肤面板数据源（主页 🎨 弹层用的预览格子）。</summary>
    public System.Collections.ObjectModel.ObservableCollection<SkinOptionViewModel> Skins { get; } = [];

    partial void OnSkinIndexChanged(int value)
    {
        if (value < 0 || value >= ThemeService.Skins.Count) return;
        ApplySkin(ThemeService.Skins[value]);
    }

    /// <summary>切换皮肤：换资源字典 + 同步该皮肤默认强调色 + 落盘。</summary>
    public void ApplySkin(Skin skin)
    {
        ThemeService.ApplySkin(skin.Id);
        _settingsService.Update(s =>
        {
            s.Theme = skin.Id;
            s.AccentColorIndex = skin.AccentIndex; // 皮肤自带的配套强调色
        });
        AccentColorIndex = skin.AccentIndex;
        ThemeService.ApplyAccent(skin.AccentIndex);
        RefreshSkinSelection();
        OnPropertyChanged(nameof(SkinName));
        OnPropertyChanged(nameof(IsDarkTheme));
        StatusText = $"已切换皮肤：{skin.Name}";
    }

    /// <summary>皮肤面板点击。</summary>
    [RelayCommand]
    private void SelectSkin(SkinOptionViewModel? option)
    {
        if (option is null) return;
        ApplySkin(ThemeService.Find(option.Id) ?? ThemeService.Skins[0]);
    }

    /// <summary>刷新皮肤格子的选中态。</summary>
    public void RefreshSkinSelection()
    {
        foreach (var skin in Skins) skin.RefreshSelection();
        var idx = ThemeService.Skins.ToList().FindIndex(s => s.Id == ThemeService.CurrentSkinId);
        if (idx >= 0 && idx != SkinIndex) SkinIndex = idx;
    }

    #region 设置页分类

    /// <summary>设置页左侧分类。</summary>
    public sealed partial class SettingsCategoryItem : ObservableObject
    {
        public SettingsCategoryItem(string name) => Name = name;
        public string Name { get; }

        [ObservableProperty]
        private bool _isSelected;
    }

    /// <summary>分类列表（顺序即展示顺序）。</summary>
    public System.Collections.ObjectModel.ObservableCollection<SettingsCategoryItem> SettingsCategories { get; } = [];

    /// <summary>当前选中的设置分类。</summary>
    [ObservableProperty]
    private string _selectedSettingsCategory = "通用";

    /// <summary>切换设置分类（由设置页 code-behind 调用）。</summary>
    public void SelectCategory(string name)
    {
        SelectedSettingsCategory = name;
        foreach (var c in SettingsCategories) c.IsSelected = c.Name == name;
    }

    #endregion

    [ObservableProperty]
    private string _musicDirectory = "";

    /// <summary>下载保存目录（空 = 跟随音乐库目录）。</summary>
    [ObservableProperty]
    private string _downloadDirectory = "";

    /// <summary>网络代理地址（空 = 跟随系统）；改动立即生效并落盘。</summary>
    [ObservableProperty]
    private string _proxyUrl = "";

    partial void OnProxyUrlChanged(string value)
    {
        var url = value?.Trim() ?? "";
        _settingsService.Update(s => s.ProxyUrl = url);
        Services.Net.HttpService.ConfigureProxy(url); // 重建 HTTP 客户端，音源/更新/歌词同时生效
        StatusText = string.IsNullOrEmpty(url)
            ? "代理已关闭（跟随系统设置）"
            : $"代理已启用：{url}";
    }

    /// <summary>缓存占用概览（封面 / 在线歌词 / B站音频 / 插件音频）。</summary>
    public string CacheUsageText
    {
        get
        {
            long Sum(string dir)
            {
                try
                {
                    return Directory.Exists(dir) ? new DirectoryInfo(dir).GetFiles().Sum(f => f.Length) : 0;
                }
                catch { return 0; }
            }

            var cover = Sum(CoverCacheService.CacheDir);
            var lyrics = Sum(Services.AppPaths.LyricsDir);
            var bili = Sum(BilibiliApiClient.CacheDir);
            var plugin = Sum(JsPluginProvider.CacheDir);
            static string Mb(long bytes) => $"{bytes / 1024.0 / 1024.0:F1} MB";
            return $"当前占用：封面 {Mb(cover)}（上限 256 MB，超限自动清理最旧）· 歌词 {Mb(lyrics)} · B站音频 {Mb(bili)} · 插件音频 {Mb(plugin)}";
        }
    }

    /// <summary>下载目录展示文案（空时提示默认行为）。</summary>
    public string DownloadDirectoryLabel => string.IsNullOrWhiteSpace(DownloadDirectory)
        ? "默认：跟随音乐库目录"
        : DownloadDirectory;

    partial void OnDownloadDirectoryChanged(string value)
    {
        OnPropertyChanged(nameof(DownloadDirectoryLabel));
        _settingsService.Update(s => s.DownloadDirectory = string.IsNullOrWhiteSpace(value) ? null : value);
    }

    [ObservableProperty]
    private double _defaultVolume = 1.0;

    [ObservableProperty]
    private bool _enableOnlineLyrics;

    [ObservableProperty]
    private string _backgroundImagePath = "";

    [ObservableProperty]
    private double _backgroundOpacity = 0.35;

    /// <summary>动态壁纸索引：0=无, 1=鼠标跟随, 2=星河。</summary>
    [ObservableProperty]
    private int _wallpaperIndex;

    [ObservableProperty]
    private double _lyricFontSize = 14;

    [ObservableProperty]
    private int _lyricColorIndex;

    [ObservableProperty]
    private int _accentColorIndex;

    /// <summary>关闭行为索引：0=每次询问, 1=后台运行, 2=直接关闭。</summary>
    [ObservableProperty]
    private int _closeBehaviorIndex;

    [ObservableProperty]
    private string _statusText = "设置会自动保存";

    /// <summary>设置页搜索过滤关键词。</summary>
    [ObservableProperty]
    private string _settingsFilter = "";

    [ObservableProperty]
    private string _updateCheckText = "检查更新";

    /// <summary>当前版本号：唯一来源是 csproj 的 &lt;Version&gt;，避免与程序集版本不一致。</summary>
    public string CurrentVersion { get; } = ReadVersion();

    /// <summary>从程序集读取版本（InformationalVersion 形如 "3.0.0+abc123"，取 + 之前部分）。</summary>
    private static string ReadVersion()
    {
        var asm = typeof(SettingsViewModel).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }
        var v = asm.GetName().Version;
        return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    /// <summary>用户协议文本。</summary>
    public string UserAgreement => """
        AnMusic 用户协议

        1. 本软件为开源免费音乐播放器，仅供个人学习与非商业用途使用。
        2. 用户须遵守所使用音乐源平台的用户协议与版权法规，不得用于侵权行为。
        3. 在线音源（网易云、QQ音乐、B站等）的搜索与播放能力依赖第三方公开接口，
           接口变更或服务终止可能导致相关功能不可用，本软件不承担由此造成的损失。
        4. 用户下载的音乐文件仅限个人离线欣赏，不得二次传播或用于商业用途。
        5. 本软件尊重版权，若您认为某功能侵犯了您的权益，请联系开发者移除。
        """;

    /// <summary>免责声明文本。</summary>
    public string Disclaimer => """
        免责声明

        本软件按"现状"提供，不提供任何明示或暗示的保证，包括但不限于适销性、
        特定用途适用性和非侵权性的保证。开发者不对因使用本软件而造成的任何直接、
        间接、附带、特殊或后果性损害承担责任。

        在线音乐源的音质与可用性受第三方平台限制，VIP/付费内容可能无法播放。
        请支持正版音乐，尊重艺术家的劳动成果。
        """;

    /// <summary>检查 Github 更新（对比最新 Release 版本号）。</summary>
    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        UpdateCheckText = "正在检查更新...";
        try
        {
            var http = Services.Net.HttpService.Client; // 统一出口：含 UA / 超时 / 代理
            const string repo = "PainterAnkry/AnMusic";
            using var resp = await http.GetAsync($"https://api.github.com/repos/{repo}/releases/latest");
            if (!resp.IsSuccessStatusCode)
            {
                UpdateCheckText = "检查更新失败（网络或仓库不存在）";
                return;
            }
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
            var latestTag = doc.RootElement.TryGetProperty("tag_name", out var tag) ? tag.GetString() ?? "" : "";
            var latestVersion = latestTag.TrimStart('v');

            if (string.IsNullOrEmpty(latestVersion))
            {
                UpdateCheckText = "未获取到版本信息";
                return;
            }

            UpdateCheckText = latestVersion == CurrentVersion
                ? $"已是最新版本 v{CurrentVersion}"
                : $"发现新版本 v{latestVersion}（当前 v{CurrentVersion}），请到 Github 下载";
        }
        catch
        {
            UpdateCheckText = "检查更新失败，请检查网络连接";
        }
    }

    /// <summary>联系作者邮箱。</summary>
    public const string AuthorEmail = "835176240@qq.com";

    /// <summary>交流 QQ 群号。</summary>
    public const string QQGroup = "348061212";

    /// <summary>点击查看用户协议（弹窗显示，不再平铺全文）。</summary>
    [RelayCommand]
    private void ShowAgreement()
    {
        Views.TextDialogWindow.Show("AnMusic", "用户协议", UserAgreement);
    }

    /// <summary>点击查看免责声明（弹窗显示）。</summary>
    [RelayCommand]
    private void ShowDisclaimer()
    {
        Views.TextDialogWindow.Show("AnMusic", "免责声明", Disclaimer);
    }

    /// <summary>复制作者邮箱到剪贴板。</summary>
    [RelayCommand]
    private void CopyEmail()
    {
        try
        {
            System.Windows.Clipboard.SetText(AuthorEmail);
            StatusText = $"已复制邮箱：{AuthorEmail}";
        }
        catch (Exception ex)
        {
            StatusText = $"复制失败: {ex.Message}";
        }
    }

    /// <summary>复制交流 QQ 群号到剪贴板。</summary>
    [RelayCommand]
    private void CopyQQGroup()
    {
        try
        {
            System.Windows.Clipboard.SetText(QQGroup);
            StatusText = $"已复制 QQ 群号：{QQGroup}";
        }
        catch (Exception ex)
        {
            StatusText = $"复制失败: {ex.Message}";
        }
    }

    /// <summary>打开系统邮件客户端给作者发邮件。</summary>
    [RelayCommand]
    private void SendEmail()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = $"mailto:{AuthorEmail}",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusText = $"打开邮件客户端失败: {ex.Message}";
        }
    }

    #region 快捷键设置

    private readonly Services.Shortcuts.ShortcutService _shortcuts;

    /// <summary>快捷键总开关。</summary>
    [ObservableProperty]
    private bool _shortcutsEnabled = true;

    /// <summary>快捷键列表（动作名 + 当前按键 + 全局开关）。</summary>
    public System.Collections.ObjectModel.ObservableCollection<ShortcutItemViewModel> ShortcutItems { get; } = [];

    /// <summary>正在等待按下新按键的行（由设置页 code-behind 捕获按键后写入）。</summary>
    public ShortcutItemViewModel? CapturingItem { get; set; }

    /// <summary>进入改键状态：期间按键只用于捕获，不触发播放等动作。</summary>
    public void BeginShortcutCapture(ShortcutItemViewModel item)
    {
        _shortcuts.IsCapturing = true;
        CapturingItem = item;
    }

    /// <summary>退出改键状态。</summary>
    public void EndShortcutCapture()
    {
        _shortcuts.IsCapturing = false;
        CapturingItem = null;
    }

    /// <summary>设置页状态栏文本（供 code-behind 回显改键结果）。</summary>
    public void SetStatus(string text) => StatusText = text;

    partial void OnShortcutsEnabledChanged(bool value)
    {
        _shortcuts.SetEnabled(value);
        StatusText = value ? "快捷键已启用" : "快捷键已禁用（全局热键同步注销）";
    }

    /// <summary>重建快捷键列表（初始化 / 恢复默认后调用）。</summary>
    public void RefreshShortcutItems()
    {
        ShortcutItems.Clear();
        foreach (var action in Services.Shortcuts.ShortcutActions.All)
            ShortcutItems.Add(new ShortcutItemViewModel(_shortcuts, action));
    }

    /// <summary>刷新各行的按键与告警文本（外部改键 / 全局注册结果变化时）。</summary>
    public void RefreshShortcutWarnings()
    {
        foreach (var item in ShortcutItems) item.Refresh();
    }

    /// <summary>全部恢复默认按键。</summary>
    [RelayCommand]
    private void ResetAllShortcuts()
    {
        _shortcuts.ResetAll();
        RefreshShortcutItems();
        StatusText = "快捷键已全部恢复默认";
    }

    /// <summary>某项恢复默认（设置页行内「重置」）。</summary>
    [RelayCommand]
    private void ResetShortcut(ShortcutItemViewModel? item)
    {
        if (item is null) return;
        StatusText = item.ResetDefault();
        RefreshShortcutItems();
    }

    #endregion

    public SettingsViewModel(
        UserSettingsService settingsService,
        LibraryViewModel library,
        PlaybackBarViewModel playbackBar,
        ProviderRegistry providerRegistry,
        JsPluginLoader pluginLoader,
        Services.Shortcuts.ShortcutService shortcutService)
    {
        _settingsService = settingsService;
        _library = library;
        _playbackBar = playbackBar;
        _providerRegistry = providerRegistry;
        _pluginLoader = pluginLoader;
        _shortcuts = shortcutService;

        var s = settingsService.Settings;
        _skinIndex = Math.Max(0, ThemeService.Skins.ToList().FindIndex(x => x.Id == (ThemeService.Find(s.Theme)?.Id ?? "Light")));
        foreach (var skin in ThemeService.Skins) Skins.Add(new SkinOptionViewModel(skin));
        foreach (var name in new[] { "通用", "外观", "播放", "歌词", "快捷键", "音源", "关于" })
            SettingsCategories.Add(new SettingsCategoryItem(name));
        SelectCategory("通用");
        _musicDirectory = s.MusicDirectory ?? "";
        _downloadDirectory = s.DownloadDirectory ?? "";
        _defaultVolume = s.DefaultVolume;
        _enableOnlineLyrics = s.EnableOnlineLyrics;
        _backgroundImagePath = s.BackgroundImagePath ?? "";
        _backgroundOpacity = s.BackgroundOpacity;
        _wallpaperIndex = Math.Clamp(s.WallpaperIndex, 0, 2);
        _lyricFontSize = Math.Clamp(s.LyricFontSize, 12, 24);
        _lyricColorIndex = Math.Clamp(s.LyricColorIndex, 0, 5);
        _accentColorIndex = Math.Clamp(s.AccentColorIndex, 0, 5);
        _closeBehaviorIndex = s.CloseBehavior;
        _proxyUrl = s.ProxyUrl ?? "";
        _shortcutsEnabled = s.ShortcutsEnabled;

        RefreshSourceLists();
        RefreshShortcutItems(); // 快捷键列表随设置页初始化

        // 全局热键注册结果（成功/失败）变化时刷新行内告警
        _shortcuts.BindingsChanged += () => Dispatcher(() => RefreshShortcutWarnings());
    }

    /// <summary>把回调切到 UI 线程（全局热键注册可能由设置变更触发）。</summary>
    private static void Dispatcher(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    private void RefreshSourceLists()
    {
        RegisteredSources.Clear();
        foreach (var p in _providerRegistry.MusicProviders)
            RegisteredSources.Add(p.DisplayName);
        if (RegisteredSources.Count == 0)
            RegisteredSources.Add("（无已注册音乐源）");
        OnlineSourceCount = _providerRegistry.OnlineMusicProviders.Count;
        OnPropertyChanged(nameof(OnlineSourceCount));

        JsPlugins.Clear();
        foreach (var p in _pluginLoader.Plugins)
            JsPlugins.Add(p);
    }

    private readonly ProviderRegistry _providerRegistry;
    private readonly JsPluginLoader _pluginLoader;

    /// <summary>已注册音乐源显示名列表。</summary>
    public System.Collections.ObjectModel.ObservableCollection<string> RegisteredSources { get; } = [];

    /// <summary>已注册在线源数量（含插件源）。</summary>
    public int OnlineSourceCount { get; private set; }

    /// <summary>已加载的 .js 音源插件。</summary>
    public System.Collections.ObjectModel.ObservableCollection<JsPluginProvider> JsPlugins { get; } = [];

    /// <summary>打开插件文件夹（把 .js 音源插件放进去即可接入）。</summary>
    [RelayCommand]
    private void OpenPluginFolder()
    {
        try
        {
            Directory.CreateDirectory(JsPluginLoader.PluginDir);
            System.Diagnostics.Process.Start("explorer.exe", JsPluginLoader.PluginDir);
        }
        catch (Exception ex)
        {
            StatusText = $"打开插件目录失败: {ex.Message}";
        }
    }

    /// <summary>重新扫描并加载插件目录中的 .js 音源（异步，避免 UI 线程与下载 await 死锁）。</summary>
    [RelayCommand]
    private async Task ReloadPluginsAsync()
    {
        try
        {
            StatusText = "正在重新加载插件...";
            _providerRegistry.UnregisterJsPlugins();
            var errors = await _pluginLoader.LoadAllAsync();
            foreach (var plugin in _pluginLoader.Plugins)
                _providerRegistry.Register(plugin);
            RefreshSourceLists();
            StatusText = errors.Count > 0
                ? $"插件加载完成（{errors.Count} 个失败）：{errors[0]}"
                : $"插件加载完成，共 {_pluginLoader.Plugins.Count} 个音源";
        }
        catch (Exception ex)
        {
            StatusText = $"插件重载失败: {ex.Message}";
        }
    }

    partial void OnDefaultVolumeChanged(double value)
    {
        _playbackBar.Volume = value; // 立即生效
        _settingsService.Update(s => s.DefaultVolume = value);
    }

    partial void OnEnableOnlineLyricsChanged(bool value)
    {
        // 仅持久化开关；在线歌词 Provider 在阶段 7 提供扩展点，默认关闭
        _settingsService.Update(s => s.EnableOnlineLyrics = value);
        StatusText = value
            ? "在线歌词已启用（本地歌词缺失时自动查询 LRCLIB）"
            : "在线歌词已关闭";
    }

    [RelayCommand]
    private async Task BrowseMusicDirectoryAsync()
    {
        var dialog = new OpenFolderDialog { Title = "选择音乐文件夹" };
        if (dialog.ShowDialog() != true) return;

        MusicDirectory = dialog.FolderName;
        _settingsService.Update(s => s.MusicDirectory = MusicDirectory);
        await _library.ScanDirectoryAsync(MusicDirectory);
        StatusText = $"音乐目录：{MusicDirectory}";
    }

    /// <summary>选择下载保存目录（空 = 跟随音乐库目录）。</summary>
    [RelayCommand]
    private void BrowseDownloadDirectory()
    {
        var dialog = new OpenFolderDialog { Title = "选择下载保存目录" };
        if (dialog.ShowDialog() != true) return;

        DownloadDirectory = dialog.FolderName; // OnDownloadDirectoryChanged 自动持久化
        StatusText = $"下载目录：{DownloadDirectory}";
    }

    /// <summary>清空下载目录选择（恢复默认：跟随音乐库目录）。</summary>
    [RelayCommand]
    private void ClearDownloadDirectory()
    {
        DownloadDirectory = ""; // OnDownloadDirectoryChanged 自动持久化
        StatusText = "下载目录已恢复默认（跟随音乐库目录）";
    }

    /// <summary>一键清理全部缓存（B站音频、插件在线歌曲、封面缩略图）。
    /// 正在播放占用中的文件自动跳过，不影响已下载歌曲与歌单数据。</summary>
    [RelayCommand]
    private void ClearAllCaches()
    {
        long freed = 0;
        var skipped = 0;
        foreach (var dir in new[]
                 {
                     BilibiliApiClient.CacheDir,
                     JsPluginProvider.CacheDir,
                     CoverCacheService.CacheDir,
                     Services.AppPaths.LyricsDir   // 在线歌词缓存
                 })
        {
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.EnumerateFiles(dir))
            {
                try
                {
                    freed += new FileInfo(file).Length;
                    File.Delete(file);
                }
                catch
                {
                    skipped++; // 文件正被播放占用等，跳过
                }
            }
            try { Directory.Delete(dir, true); } catch { /* 目录残留不影响 */ }
        }

        LrclibLyricProvider.ClearCache(); // 在线歌词缓存同样清空
        OnPropertyChanged(nameof(CacheUsageText));
        StatusText = skipped > 0
            ? $"已清理缓存 {freed / 1024.0 / 1024.0:F1} MB（{skipped} 个文件正在使用已跳过）"
            : freed > 0
                ? $"已清理缓存 {freed / 1024.0 / 1024.0:F1} MB"
                : "缓存目录为空，无需清理";
    }

    partial void OnBackgroundOpacityChanged(double value)
    {
        _settingsService.Update(s => s.BackgroundOpacity = value);
    }

    partial void OnLyricFontSizeChanged(double value)
    {
        _settingsService.Update(s => s.LyricFontSize = value);
    }

    partial void OnLyricColorIndexChanged(int value)
    {
        _settingsService.Update(s => s.LyricColorIndex = value);
    }

    partial void OnAccentColorIndexChanged(int value)
    {
        ThemeService.ApplyAccent(value);
        _settingsService.Update(s => s.AccentColorIndex = value);
        StatusText = $"强调色已更新：{ThemeService.AccentNames[Math.Clamp(value, 0, ThemeService.AccentNames.Count - 1)]}";
    }

    partial void OnWallpaperIndexChanged(int value)
    {
        _settingsService.Update(s => s.WallpaperIndex = value);
        StatusText = value switch
        {
            1 => "动态壁纸：鼠标跟随",
            2 => "动态壁纸：星河",
            _ => "动态壁纸：已关闭"
        };
    }

    partial void OnCloseBehaviorIndexChanged(int value)
    {
        _settingsService.Update(s => s.CloseBehavior = value);
        StatusText = value switch
        {
            1 => "关闭行为已设为：后台运行",
            2 => "关闭行为已设为：直接关闭",
            _ => "关闭行为已设为：每次询问"
        };
    }

    [RelayCommand]
    private void BrowseBackground()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择背景图片",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.webp"
        };
        if (dialog.ShowDialog() != true) return;

        BackgroundImagePath = dialog.FileName;
        _settingsService.Update(s => s.BackgroundImagePath = BackgroundImagePath);
        StatusText = "背景图片已更新";
    }

    [RelayCommand]
    private void ClearBackground()
    {
        BackgroundImagePath = "";
        _settingsService.Update(s => s.BackgroundImagePath = "");
        StatusText = "已恢复默认背景";
    }
}
