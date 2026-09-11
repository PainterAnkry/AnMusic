using AnMusic.Android.ViewModels;

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
        await _vm.LoadAsync();
    }

    private async void OnBackClicked(object? sender, EventArgs e)
    {
        try { await Shell.Current.GoToAsync("//MainPage"); }
        catch { /* 导航过程中状态异常，直接关 */ await Navigation.PopAsync(); }
    }

    private async void OnReloadClicked(object? sender, EventArgs e) => await _vm.LoadAsync();

    private async void OnUrlCompleted(object? sender, EventArgs e)
    {
        if (_vm.InstallUrlCommand.CanExecute(null))
            await _vm.InstallUrlCommand.ExecuteAsync(null);
    }

    private void OnPresetTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is PluginPreset preset)
            _vm.UsePresetCommand.Execute(preset);
    }
}