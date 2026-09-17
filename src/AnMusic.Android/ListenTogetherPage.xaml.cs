using AnMusic.Android.ViewModels;
using AnMusic.Services;

namespace AnMusic.Android;

/// <summary>一起听页：创建/加入房间、展示成员、同步播放状态。</summary>
public partial class ListenTogetherPage : ContentPage
{
    private readonly ListenTogetherViewModel _vm;

    public ListenTogetherPage(ListenTogetherViewModel vm)
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

    protected override async void OnDisappearing()
    {
        base.OnDisappearing();
        // 离开页面即断开：房间常驻会一直占用端口和长轮询，用户很难察觉
        try { await _vm.CleanupAsync(); }
        catch (Exception ex) { AppPaths.LogError("一起听清理", ex); }
    }
}
