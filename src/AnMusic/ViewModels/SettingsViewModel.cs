using System.IO;
using System.Net.Http;
using System.Text.Json;
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

    [ObservableProperty]
    private bool _isDarkTheme;

    /// <summary>主题下拉框索引：0=深色, 1=浅色。</summary>
    public int ThemeIndex
    {
        get => IsDarkTheme ? 0 : 1;
        set => IsDarkTheme = value == 0;
    }

    [ObservableProperty]
    private string _musicDirectory = "";

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

    [ObservableProperty]
    private string _updateCheckText = "检查更新";

    /// <summary>当前版本号。</summary>
    public string CurrentVersion => "2.0.1";

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
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("AnMusic");
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

    public SettingsViewModel(
        UserSettingsService settingsService,
        LibraryViewModel library,
        PlaybackBarViewModel playbackBar,
        ProviderRegistry providerRegistry,
        JsPluginLoader pluginLoader)
    {
        _settingsService = settingsService;
        _library = library;
        _playbackBar = playbackBar;
        _providerRegistry = providerRegistry;
        _pluginLoader = pluginLoader;

        var s = settingsService.Settings;
        _isDarkTheme = s.Theme != "Light";
        _musicDirectory = s.MusicDirectory ?? "";
        _defaultVolume = s.DefaultVolume;
        _enableOnlineLyrics = s.EnableOnlineLyrics;
        _backgroundImagePath = s.BackgroundImagePath ?? "";
        _backgroundOpacity = s.BackgroundOpacity;
        _wallpaperIndex = Math.Clamp(s.WallpaperIndex, 0, 2);
        _lyricFontSize = Math.Clamp(s.LyricFontSize, 12, 24);
        _lyricColorIndex = Math.Clamp(s.LyricColorIndex, 0, 5);
        _accentColorIndex = Math.Clamp(s.AccentColorIndex, 0, 5);
        _closeBehaviorIndex = s.CloseBehavior;

        RefreshSourceLists();
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

    partial void OnIsDarkThemeChanged(bool value)
    {
        ThemeService.Apply(value);
        _settingsService.Update(s => s.Theme = value ? "Dark" : "Light");
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

    [RelayCommand]
    private void ClearAudioCache()
    {
        try
        {
            BilibiliApiClient.ClearAudioCache();
            StatusText = "B 站音频缓存已清理";
        }
        catch (Exception ex)
        {
            StatusText = $"清理失败: {ex.Message}";
        }
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
        StatusText = "强调色已更新";
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
