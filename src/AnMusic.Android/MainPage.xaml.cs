using AnMusic.Android.Services;
using AnMusic.Android.ViewModels;
using AnMusic.Models;
using AnMusic.Services;
using Microsoft.Maui.ApplicationModel.DataTransfer;

namespace AnMusic.Android;

/// <summary>
/// 主页面：曲库列表 + 底部迷你播放条。
/// 需要「取当前项」的交互（列表点击、行内菜单）用事件回调而非纯命令绑定，
/// 因为 MAUI 的 CollectionView 在 TapGestureRecognizer 下不直接给出 DataContext 之外的上下文。
/// </summary>
public partial class MainPage : ContentPage
{
    private readonly LibraryViewModel _library;
    private readonly PlayerViewModel _player;
    private readonly DownloadViewModel _downloads;

    public MainPage(LibraryViewModel library, PlayerViewModel player, DownloadViewModel downloads)
    {
        InitializeComponent();
        _library = library;
        _player = player;
        _downloads = downloads;
        BindingContext = library;

        // 歌单增删改后刷新胶囊条（增删会改变集合，选中态也要重算）
        _library.PlaylistsChanged += (_, _) => ApplyPlaylistChipVisual();

        ApplyTabVisual();
    }

    /// <summary>页面首次显示时自动扫描一次，省去用户手动点击。</summary>
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        ApplyTabVisual();
        ApplyPlaylistChipVisual();

        // 启动后第一件事：看一眼有没有上次的崩溃栈要呈给用户
        await MaybeShowCrashReportAsync();

        if (_library.AllTracks.Count == 0 && !_library.IsScanning)
            await _library.ScanCommand.ExecuteAsync(null);
    }

    /// <summary>每次进程只在第一次 OnAppearing 弹崩溃提示。</summary>
    private static bool _crashPromptShown;

    private async Task MaybeShowCrashReportAsync()
    {
        if (_crashPromptShown) return;
        _crashPromptShown = true;

        var pending = CrashReporter.TakePending();
        if (pending is null) return;

        // 弹窗里只显示前面几行，避免超长；完整内容用"复制日志"按钮提供
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

    /// <summary>右上角放大镜：进入独立搜索页（跨音源检索）。</summary>
    private async void OnOpenSearchClicked(object? sender, EventArgs e)
    {
        try
        {
            await Shell.Current.GoToAsync("//SearchPage");
        }
        catch (Exception ex)
        {
            AppPaths.LogError("打开搜索页", ex);
        }
    }

    #endregion

    #region 标签页

    private void OnTabAllClicked(object? sender, EventArgs e) => SwitchTab(LibraryTab.All);

    private void OnTabFavoritesClicked(object? sender, EventArgs e) => SwitchTab(LibraryTab.Favorites);

    private void OnTabPlaylistsClicked(object? sender, EventArgs e) => SwitchTab(LibraryTab.Playlists);

    private void OnTabRecentClicked(object? sender, EventArgs e) => SwitchTab(LibraryTab.Recent);

    private void SwitchTab(LibraryTab tab)
    {
        _library.CurrentTab = tab;

        // 切歌单标签时自动选中第一个歌单，避免右侧列表空着让人困惑
        if (tab == LibraryTab.Playlists && _library.SelectedPlaylist is null && _library.Playlists.Count > 0)
            _library.SelectedPlaylist = _library.Playlists[0];

        PlaylistStrip.IsVisible = tab == LibraryTab.Playlists;
        ApplyTabVisual();
        ApplyPlaylistChipVisual();
    }

    /// <summary>高亮当前标签：选中项用强调色实底白字，其余透明灰字。</summary>
    private void ApplyTabVisual()
    {
        Style(TabAllBtn, _library.CurrentTab == LibraryTab.All);
        Style(TabFavBtn, _library.CurrentTab == LibraryTab.Favorites);
        Style(TabPlBtn, _library.CurrentTab == LibraryTab.Playlists);
        Style(TabRecentBtn, _library.CurrentTab == LibraryTab.Recent);

        static void Style(Button btn, bool active)
        {
            btn.BackgroundColor = active ? ThemeService.Get("AmPrimary") : Colors.Transparent;
            btn.TextColor = active ? ThemeService.Get("AmTextOnPrimary") : ThemeService.Get("AmTextSecondary");
            btn.FontAttributes = active ? FontAttributes.Bold : FontAttributes.None;
        }
    }

    /// <summary>高亮当前选中的歌单胶囊。</summary>
    private void ApplyPlaylistChipVisual()
    {
        var selected = _library.SelectedPlaylist;

        foreach (var child in PlaylistChips.Children)
        {
            if (child is not Border chip) continue;

            var isActive = selected is not null &&
                           chip.BindingContext is Playlist p &&
                           ReferenceEquals(p, selected);

            chip.BackgroundColor = isActive ? ThemeService.Get("AmPrimary") : ThemeService.Get("AmChipBg");

            if (chip.Content is Label label)
                label.TextColor = isActive ? ThemeService.Get("AmTextOnPrimary") : ThemeService.Get("AmTextSecondary");
        }
    }

    private void OnPlaylistChipTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Element)?.BindingContext is not Playlist playlist) return;

        _library.SelectedPlaylist = playlist;
        ApplyPlaylistChipVisual();
    }

    #endregion

    #region 列表交互

    private async void OnTrackTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Element)?.BindingContext is Track track)
        {
            await _library.PlayTrackCommand.ExecuteAsync(track);
            _player.SetFavoriteState(_library.IsFavorite(track));
        }
    }

    /// <summary>曲目行右侧「⋯」菜单：播放、下一首、喜欢、加入歌单、下载、从歌单移除。</summary>
    private async void OnTrackMenuClicked(object? sender, EventArgs e)
    {
        if ((sender as Element)?.BindingContext is not Track track) return;

        var isFav = _library.IsFavorite(track);
        // 只按来源判断：在线曲目播过一次后 FilePath 上会有播放缓冲，不能当成"本地已有文件"
        var isLocal = track.IsLocalTrack;

        var options = new List<string>
        {
            "立即播放",
            "下一首播放",
            isFav ? "取消喜欢" : "加入我喜欢",
            "加入歌单",
        };

        if (!isLocal) options.Add("下载到本地");

        if (_library.CurrentTab == LibraryTab.Playlists && _library.SelectedPlaylist is not null)
        {
            options.Add("从当前歌单移除");
            options.Add("重命名歌单");
            options.Add("删除歌单");
        }

        var action = await DisplayActionSheet(track.Title, "取消", null, options.ToArray());
        if (string.IsNullOrEmpty(action) || action == "取消") return;

        switch (action)
        {
            case "立即播放":
                await _library.PlayTrackCommand.ExecuteAsync(track);
                _player.SetFavoriteState(_library.IsFavorite(track));
                break;

            case "下一首播放":
                // 队列为空时先把它播起来，否则"下一首"没有落点
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
                ApplyTabVisual();
                break;

            case "加入歌单":
                await AddToPlaylistAsync(track);
                break;

            case "下载到本地":
                await _downloads.EnqueueAsync(track);
                break;

            case "从当前歌单移除":
                _library.RemoveFromPlaylistCommand.Execute(track);
                ApplyPlaylistChipVisual();
                break;

            case "重命名歌单":
                await _library.RenamePlaylistCommand.ExecuteAsync(_library.SelectedPlaylist);
                ApplyPlaylistChipVisual();
                break;

            case "删除歌单":
            {
                var target = _library.SelectedPlaylist;
                if (target is null) break;
                var confirm = await DisplayAlert("删除歌单", $"确定删除「{target.Name}」吗？此操作不可撤销。", "删除", "取消");
                if (!confirm) break;
                _library.DeletePlaylistCommand.Execute(target);
                PlaylistStrip.IsVisible = _library.CurrentTab == LibraryTab.Playlists;
                ApplyPlaylistChipVisual();
                break;
            }
        }
    }

    /// <summary>选择目标歌单（无歌单时提供新建入口）。</summary>
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

    private async void OnCreatePlaylistClicked(object? sender, EventArgs e)
    {
        var name = await DisplayPromptAsync("新建歌单", "输入歌单名称", "创建", "取消", "我的歌单");
        if (string.IsNullOrWhiteSpace(name)) return;

        _library.CreatePlaylistCommand.Execute(name);
        ApplyPlaylistChipVisual();
    }

    #endregion

    /// <summary>点击迷你条左侧区域：打开播放详情页。</summary>
    private async void OnMiniPlayerTapped(object? sender, TappedEventArgs e)
    {
        if (!_player.HasTrack) return;
        await Navigation.PushModalAsync(new PlayerPage(_player));
    }

    /// <summary>点击迷你条右侧列表按钮：打开播放详情页并直接展开播放队列。</summary>
    private async void OnOpenQueueClicked(object? sender, EventArgs e)
    {
        if (!_player.HasTrack) return;
        await Navigation.PushModalAsync(new PlayerPage(_player, startInQueueMode: true));
    }
}
