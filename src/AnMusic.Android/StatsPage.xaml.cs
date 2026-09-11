using AnMusic.Android.ViewModels;

namespace AnMusic.Android;

/// <summary>听歌排行页：用户等级 + 按累计收听时长排序的曲目榜。</summary>
public partial class StatsPage : ContentPage
{
    private readonly StatsViewModel _vm;

    public StatsPage(StatsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _vm.Refresh();
    }

    private void OnOpenFlyoutClicked(object? sender, EventArgs e)
        => Shell.Current.FlyoutIsPresented = true;

    private async void OnBackClicked(object? sender, EventArgs e)
    {
        try { await Shell.Current.GoToAsync("//MainPage"); }
        catch { await Navigation.PopAsync(); }
    }

    private async void OnItemTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Element)?.BindingContext is not RankedTrack item) return;
        await _vm.PlayCommand.ExecuteAsync(item);
    }

    private async void OnClearClicked(object? sender, EventArgs e)
        => await _vm.ClearCommand.ExecuteAsync(null);
}
