using AnMusic.Android.ViewModels;

namespace AnMusic.Android;

/// <summary>设置页：播放 / 本地音乐 / 音源插件 / 缓存 / 关于。</summary>
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
    }

    private async void OnBackClicked(object? sender, EventArgs e)
        => await Navigation.PopModalAsync();
}
