using AnMusic.Android.Services;
using AnMusic.Android.ViewModels;
using AnMusic.Models;
using AnMusic.Services;
using Microsoft.Maui.ApplicationModel.DataTransfer;

namespace AnMusic.Android;

/// <summary>
/// 主页面：问候头 + 搜索胶囊 + 快捷宫格 + 推荐横幅 + 分段曲库 + 底部迷你播放条。
/// </summary>
public partial class MainPage : ContentPage
{
    private readonly LibraryViewModel _library;
    private readonly PlayerViewModel _player;
    private readonly DownloadViewModel _downloads;

    private (LibraryTab Tab, Button Button)[] _tabs = [];
    private bool _miniBarShown;

    public MainPage(LibraryViewModel library, PlayerViewModel player, DownloadViewModel downloads)
    {
        InitializeComponent();
        _library = library;
        _player = player;
        _downloads = downloads;
        BindingContext = library;

        _tabs =
        [
            (LibraryTab.All, TabAll),
            (LibraryTab.Favorites, TabFavorites),
            (LibraryTab.Recent, TabRecent),
            (LibraryTab.Recommend, TabRecommend),
        ];
        ApplyTabVisual();

        _library.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LibraryViewModel.CurrentTab))
            {
                ApplyTabVisual();
                // 切 Tab 时列表内容轻微淡入（不动 Header，只做透明度）
                try
                {
                    TrackList.CancelAnimations();
                    TrackList.Opacity = 0.6;
                    TrackList.FadeToAsync(1, 190, Easing.SinOut);
                }
                catch { }
            }
        };

        _player.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlayerViewModel.HasTrack)
                && _player.HasTrack && !_miniBarShown)
            {
                _miniBarShown = true;
                // 迷你播放条第一次出现：自底部滑入 + 淡入
                try
                {
                    MiniBar.Opacity = 0;
                    MiniBar.TranslationY = 48;
                    MiniBar.FadeToAsync(1, 260, Easing.SinOut);
                    MiniBar.TranslateToAsync(0, 0, 280, Easing.CubicOut);
                }
                catch { }
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        ApplyTabVisual();

        try
        {
            await MaybeShowCrashReportAsync();

            if (_library.AllTracks.Count == 0 && !_library.IsScanning)
                await _library.ScanCommand.ExecuteAsync(null);
        }
        catch (Exception ex) { AppPaths.LogError("主页加载", ex); }
    }

    private static bool _crashPromptShown;

    private async Task MaybeShowCrashReportAsync()
    {
        if (_crashPromptShown) return;
        _crashPromptShown = true;

        var pending = CrashReporter.TakePending();
        if (pending is null) return;

        var head = string.Join("\n", pending.Value.Content
            .Split('\n')
            .Take(18)
            .Select(l => l.Length > 120 ? l[..120] + "…" : l));

        var copy = await DisplayAlert(
            "上次异常退出",
            "检测到应用上次意外退出。可以把日志复制出来发给我排查吗？\n\n" + head,
            "复制日志",
            "关闭");

        if (copy)
        {
            try
            {
                await Clipboard.Default.SetTextAsync(pending.Value.Content);
                await DisplayAlert("已复制", "崩溃日志已复制到剪贴板，可以粘贴发给我。", "好");
            }
            catch (Exception ex)
            {
                AppPaths.LogError("复制崩溃日志到剪贴板", ex);
            }
        }
    }

    #region 顶部操作

    private void OnOpenFlyoutClicked(object? sender, EventArgs e)
        => Shell.Current.FlyoutIsPresented = true;

    private async void OnOpenSearchClicked(object? sender, EventArgs e)
    {
        try { await Shell.Current.GoToAsync("//SearchPage"); }
        catch (Exception ex) { AppPaths.LogError("打开搜索页", ex); }
    }

    private async void OnDownloadsTapped(object? sender, TappedEventArgs e)
    {
        try { await Shell.Current.GoToAsync("//DownloadsPage"); }
        catch (Exception ex) { AppPaths.LogError("打开下载页", ex); }
    }

    #endregion

    #region 宫格 / 分段 Tab

    private void OnQuickTapped(object? sender, TappedEventArgs e)
    {
        if (TryParseTab(e.Parameter, out var tab))
            _library.CurrentTab = tab;
    }

    private void OnTabClicked(object? sender, EventArgs e)
    {
        if (sender is Button { CommandParameter: string p } && TryParseTab(p, out var tab))
            _library.CurrentTab = tab;
    }

    private static bool TryParseTab(object? value, out LibraryTab tab)
    {
        tab = LibraryTab.All;
        if (value is not string s) return false;
        if (Enum.TryParse<LibraryTab>(s, ignoreCase: true, out var parsed))
        {
            tab = parsed;
            return true;
        }
        return false;
    }

    /// <summary>分段 Tab 选中态：选中实底白字，未选透明灰字（不重建样式，只改颜色/字重）。</summary>
    private void ApplyTabVisual()
    {
        var primary = ThemeService.Get("AmPrimary");
        var secondary = ThemeService.Get("AmTextSecondary");
        var onPrimary = ThemeService.Get("AmTextOnPrimary");

        foreach (var (tab, button) in _tabs)
        {
            var active = tab == _library.CurrentTab;
            button.BackgroundColor = active ? primary : Colors.Transparent;
            button.TextColor = active ? onPrimary : secondary;
            button.FontAttributes = active ? FontAttributes.Bold : FontAttributes.None;
        }
    }

    #endregion

    #region 列表交互

    private async void OnTrackTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Element)?.BindingContext is not Track track) return;
        try
        {
            await _library.PlayTrackCommand.ExecuteAsync(track);
            _player.SetFavoriteState(_library.IsFavorite(track));
        }
        catch (Exception ex) { AppPaths.LogError("播放曲目", ex); }
    }

    private async void OnTrackMenuClicked(object? sender, EventArgs e)
    {
        if ((sender as Element)?.BindingContext is not Track track) return;

        try
        {
            var isFav = _library.IsFavorite(track);
            var isLocal = track.IsLocalTrack;

            var options = new List<string>
            {
                "立即播放",
                "下一首播放",
                isFav ? "取消喜欢" : "加入我喜欢",
                "加入歌单",
            };

            if (!isLocal) options.Add("下载到本地");

            var action = await DisplayActionSheet(track.Title, "取消", null, options.ToArray());
            if (string.IsNullOrEmpty(action) || action == "取消") return;

            switch (action)
            {
                case "立即播放":
                    await _library.PlayTrackCommand.ExecuteAsync(track);
                    _player.SetFavoriteState(_library.IsFavorite(track));
                    break;

                case "下一首播放":
                    if (!_player.HasTrack)
                    {
                        await _library.PlayTrackCommand.ExecuteAsync(track);
                        break;
                    }
                    _player.InsertNextCommand.Execute(track);
                    break;

                case "加入我喜欢":
                case "取消喜欢":
                    _library.ToggleFavoriteCommand.Execute(track);
                    _player.SetFavoriteState(_library.IsFavorite(track));
                    break;

                case "加入歌单":
                    await AddToPlaylistAsync(track);
                    break;

                case "下载到本地":
                    await _downloads.EnqueueAsync(track);
                    break;
            }
        }
        catch (Exception ex) { AppPaths.LogError("曲目菜单", ex); }
    }

    private async Task AddToPlaylistAsync(Track track)
    {
        var names = _library.Playlists.Select(p => p.Name).ToList();
        names.Add("＋ 新建歌单");

        var picked = await DisplayActionSheet("加入到…", "取消", null, names.ToArray());
        if (string.IsNullOrEmpty(picked) || picked == "取消") return;

        if (picked == "＋ 新建歌单")
        {
            var name = await DisplayPromptAsync("新建歌单", "输入歌单名称", "创建", "取消", "我的歌单");
            if (string.IsNullOrWhiteSpace(name)) return;
            _library.CreatePlaylistCommand.Execute(name);
            _library.AddToPlaylistByName(track, name);
            return;
        }

        _library.AddToPlaylistByName(track, picked);
        await DisplayAlert("已加入", $"「{track.Title}」已加入「{picked}」", "好");
    }

    #endregion

    private async void OnMiniPlayerTapped(object? sender, TappedEventArgs e)
    {
        if (!_player.HasTrack) return;
        try { await Navigation.PushModalAsync(new PlayerPage(_player)); }
        catch (Exception ex) { AppPaths.LogError("打开播放页", ex); }
    }

    private async void OnOpenQueueClicked(object? sender, EventArgs e)
    {
        if (!_player.HasTrack) return;
        try { await Navigation.PushModalAsync(new PlayerPage(_player, startInQueueMode: true)); }
        catch (Exception ex) { AppPaths.LogError("打开队列页", ex); }
    }
}
