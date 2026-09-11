using AnMusic.Android.Services;
using AnMusic.Android.ViewModels;

namespace AnMusic.Android;

/// <summary>设置页：外观皮肤 / 播放 / 本地音乐 / 音源插件 / 缓存 / 更多功能 / 关于。</summary>
public partial class SettingsPage : ContentPage
{
    private readonly SettingsViewModel _vm;

    public SettingsPage(SettingsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        // 每次进入都重新统计缓存与插件，避免显示上一次的陈旧数据
        _vm.Refresh();
        ApplyThemeVisual();
    }

    private void OnOpenFlyoutClicked(object? sender, EventArgs e)
        => Shell.Current.FlyoutIsPresented = true;

    private async void OnBackClicked(object? sender, EventArgs e)
    {
        try { await Shell.Current.GoToAsync("//MainPage"); }
        catch { await Navigation.PopAsync(); }
    }

    #region 外观

    private void OnSkinTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Element)?.BindingContext is not SkinOption option) return;

        _vm.SelectSkin(option.Id);
        ApplyThemeVisual();
    }

    private void OnAccentTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Element)?.BindingContext is not AccentOption option) return;

        _vm.SelectAccent(option.Index);
        ApplyThemeVisual();
    }

    /// <summary>把当前皮肤/强调色对应的胶囊刷成选中态。</summary>
    private void ApplyThemeVisual()
    {
        var activeBg = ThemeService.Get("AmPrimary");
        var activeText = ThemeService.Get("AmTextOnPrimary");
        var normalBg = ThemeService.Get("AmChipBg");
        var normalText = ThemeService.Get("AmTextSecondary");

        foreach (var child in SkinChips.Children)
        {
            if (child is not Border chip) continue;

            var isActive = chip.BindingContext is SkinOption option &&
                           string.Equals(option.Id, _vm.CurrentSkinId, StringComparison.OrdinalIgnoreCase);

            Paint(chip, isActive, activeBg, activeText, normalBg, normalText);
        }

        foreach (var child in AccentChips.Children)
        {
            if (child is not Border chip) continue;

            var isActive = chip.BindingContext is AccentOption option &&
                           option.Index == _vm.CurrentAccentIndex;

            Paint(chip, isActive, activeBg, activeText, normalBg, normalText);
        }

        static void Paint(Border chip, bool active, Color activeBg, Color activeText, Color normalBg, Color normalText)
        {
            chip.BackgroundColor = active ? activeBg : normalBg;

            // 胶囊内容固定是「色块 + 名称」的横向布局，取第二个子项即名称
            if (chip.Content is HorizontalStackLayout row && row.Children.Count > 1 &&
                row.Children[1] is Label label)
            {
                label.TextColor = active ? activeText : normalText;
            }
        }
    }

    #endregion

    #region 跳转

    private async void OnOpenUserPageTapped(object? sender, TappedEventArgs e)
        => await Shell.Current.GoToAsync("//UserPage");

    private async void OnOpenEqualizerTapped(object? sender, TappedEventArgs e)
        => await Navigation.PushModalAsync(new EqualizerPage(MauiProgram.Services.GetService<EqualizerService>()!));

    private async void OnOpenDownloadsTapped(object? sender, TappedEventArgs e)
        => await Shell.Current.GoToAsync("//DownloadsPage");

    private async void OnOpenPluginsClicked(object? sender, TappedEventArgs e)
        => await Shell.Current.GoToAsync("//PluginsPage");

    #endregion

    #region 定时关闭

    private async void OnSleepTimerTapped(object? sender, EventArgs e)
    {
        string[] options = ["15 分钟", "30 分钟", "45 分钟", "60 分钟", "播完当前曲目后停止", "取消定时"];
        var action = await DisplayActionSheet("定时关闭", "返回", null, options);

        switch (action)
        {
            case null or "返回":
                return;

            case "播完当前曲目后停止":
                _vm.StopAfterCurrentTrack();
                break;

            case "取消定时":
                _vm.ApplySleepTimer(0);
                break;

            default:
                _vm.ApplySleepTimer(int.Parse(action.Split(' ')[0]));
                break;
        }
    }

    #endregion
}
