using AnMusic.Android.ViewModels;
using AnMusic.Services;

namespace AnMusic.Android;

public partial class PluginsPage : ContentPage
{
    private readonly PluginsViewModel _vm;

    public PluginsPage(PluginsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        // 每次进入页面自动拉一次新：插件可能已经被外部脚本下载到目录里
        try { await _vm.LoadAsync(); }
        catch (Exception ex) { AppPaths.LogError("插件页加载", ex); }
    }

    private async void OnBackClicked(object? sender, EventArgs e)
    {
        try { await Shell.Current.GoToAsync("//MainPage"); }
        catch { /* 导航过程中状态异常，直接关 */ await Navigation.PopAsync(); }
    }

    private async void OnReloadClicked(object? sender, EventArgs e)
    {
        try { await _vm.LoadAsync(); }
        catch (Exception ex) { AppPaths.LogError("插件页刷新", ex); }
    }

    private async void OnUrlCompleted(object? sender, EventArgs e)
    {
        try
        {
            if (_vm.InstallUrlCommand.CanExecute(null))
                await _vm.InstallUrlCommand.ExecuteAsync(null);
        }
        catch (Exception ex) { AppPaths.LogError("插件订阅安装", ex); }
    }
}
