using AnMusic.Services.Settings;
using AnMusic.Services.Audio;
using AnMusic.Services.Providers;
using AnMusic.Services.Providers.Bilibili;
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

    [ObservableProperty]
    private double _lyricFontSize = 14;

    [ObservableProperty]
    private int _lyricColorIndex;

    [ObservableProperty]
    private string _statusText = "设置会自动保存";

    public SettingsViewModel(
        UserSettingsService settingsService,
        LibraryViewModel library,
        PlaybackBarViewModel playbackBar,
        ProviderRegistry providerRegistry)
    {
        _settingsService = settingsService;
        _library = library;
        _playbackBar = playbackBar;

        var s = settingsService.Settings;
        _isDarkTheme = s.Theme != "Light";
        _musicDirectory = s.MusicDirectory ?? "";
        _defaultVolume = s.DefaultVolume;
        _enableOnlineLyrics = s.EnableOnlineLyrics;
        _backgroundImagePath = s.BackgroundImagePath ?? "";
        _backgroundOpacity = s.BackgroundOpacity;
        _lyricFontSize = Math.Clamp(s.LyricFontSize, 12, 24);
        _lyricColorIndex = Math.Clamp(s.LyricColorIndex, 0, 5);

        // 已注册的音乐源列表（本地 + 在线扩展点）
        foreach (var p in providerRegistry.MusicProviders)
            RegisteredSources.Add(p.DisplayName);
        if (RegisteredSources.Count == 0)
            RegisteredSources.Add("（无已注册音乐源）");
        OnlineSourceCount = providerRegistry.OnlineMusicProviders.Count;
    }

    /// <summary>已注册音乐源显示名列表。</summary>
    public System.Collections.ObjectModel.ObservableCollection<string> RegisteredSources { get; } = [];

    /// <summary>已注册在线源数量（当前为 0，留作扩展点）。</summary>
    public int OnlineSourceCount { get; }

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
