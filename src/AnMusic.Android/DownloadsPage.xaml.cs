using AnMusic.Android.Services;
using AnMusic.Android.ViewModels;

namespace AnMusic.Android;

/// <summary>
/// 下载管理页：上半部分是「下载任务」（把在线曲目保存进设备音乐库），
/// 下半部分是「缓冲缓存」（各音源播放产生的临时文件）。
/// </summary>
public partial class DownloadsPage : ContentPage
{
    private readonly DownloadViewModel _vm;

    public DownloadsPage(DownloadViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;
        ApplyTabVisual(tasksActive: true);
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

    private void OnRefreshClicked(object? sender, EventArgs e) => _vm.Refresh();

    #region 分段

    private void OnTabTasksClicked(object? sender, EventArgs e) => SwitchPanel(tasks: true);

    private void OnTabCacheClicked(object? sender, EventArgs e) => SwitchPanel(tasks: false);

    private void SwitchPanel(bool tasks)
    {
        TaskPanel.IsVisible = tasks;
        CachePanel.IsVisible = !tasks;
        ApplyTabVisual(tasks);
        if (!tasks) _vm.RefreshCacheCommand.Execute(null);
    }

    private void ApplyTabVisual(bool tasksActive)
    {
        Style(TabTasksBtn, tasksActive);
        Style(TabCacheBtn, !tasksActive);

        static void Style(Button btn, bool active)
        {
            btn.BackgroundColor = active ? ThemeService.Get("AmPrimary") : Colors.Transparent;
            btn.TextColor = active ? ThemeService.Get("AmTextOnPrimary") : ThemeService.Get("AmTextSecondary");
            btn.FontAttributes = active ? FontAttributes.Bold : FontAttributes.None;
        }
    }

    #endregion

    #region 下载任务

    private void OnRetryTaskClicked(object? sender, EventArgs e)
    {
        if ((sender as Element)?.BindingContext is DownloadTaskItem item)
            _vm.RetryTaskCommand.Execute(item);
    }

    private void OnRemoveTaskClicked(object? sender, EventArgs e)
    {
        if ((sender as Element)?.BindingContext is DownloadTaskItem item)
            _vm.RemoveTaskCommand.Execute(item);
    }

    private void OnClearFinishedClicked(object? sender, EventArgs e)
        => _vm.ClearFinishedTasksCommand.Execute(null);

    #endregion

    #region 缓冲缓存

    private void OnDeleteCacheFileClicked(object? sender, EventArgs e)
    {
        if ((sender as Element)?.BindingContext is CacheFileItem item)
            _vm.DeleteCacheFileCommand.Execute(item);
    }

    private async void OnClearAllCacheClicked(object? sender, EventArgs e)
        => await _vm.ClearAllCacheCommand.ExecuteAsync(null);

    #endregion
}
