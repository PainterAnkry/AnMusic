using AnMusic.Android.ViewModels;
using AnMusic.Services;

namespace AnMusic.Android;

/// <summary>
/// 个性电台：5 种基于本地曲库的电台模式，全部走 PlayerViewModel 的连播机制。
/// 不依赖在线数据，离线可用。
/// </summary>
public partial class DiscoverPage : ContentPage
{
    private readonly RadioViewModel _vm;

    public DiscoverPage(RadioViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;
    }

    private void OnOpenFlyoutClicked(object? sender, EventArgs e)
        => Shell.Current.FlyoutIsPresented = true;

    private async void OnBackClicked(object? sender, EventArgs e)
    {
        try { await Shell.Current.GoToAsync("//MainPage"); }
        catch { await Navigation.PopAsync(); }
    }

    private async void OnModeTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is not RadioModeOption option) return;
        try { await _vm.StartAsync(option); }
        catch (Exception ex) { AppPaths.LogError("启动电台", ex); }
    }
}