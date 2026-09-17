using AnMusic.Android.ViewModels;
using AnMusic.Services;

namespace AnMusic.Android;

public partial class ImportPage : ContentPage
{
    private readonly ImportViewModel _vm;

    public ImportPage(ImportViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;
    }

    private async void OnBackClicked(object? sender, EventArgs e)
    {
        try { await Shell.Current.GoToAsync("//MainPage"); }
        catch { await Navigation.PopAsync(); }
    }

    private async void OnScanClicked(object? sender, EventArgs e)
    {
        try { await _vm.ScanSystemCommand.ExecuteAsync(null); }
        catch (Exception ex) { AppPaths.LogError("扫描系统音乐", ex); }
    }
}