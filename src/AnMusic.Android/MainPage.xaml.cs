using AnMusic.Android.ViewModels;
using AnMusic.Models;

namespace AnMusic.Android;

/// <summary>
/// 主页面：曲库列表 + 底部迷你播放条。
/// 采用事件回调而非纯命令绑定来处理需要"取当前项"的交互（列表点击、行内菜单），
/// 因为 MAUI 的 CollectionView 在 TapGestureRecognizer 下不直接给出 DataContext 之外的上下文。
/// </summary>
public partial class MainPage : ContentPage
{
    private readonly LibraryViewModel _library;
    private readonly PlayerViewModel _player;

    public MainPage(LibraryViewModel library, PlayerViewModel player)
    {
        InitializeComponent();
        _library = library;
        _player = player;
        BindingContext = library;

        ApplyTabVisual();
    }

    /// <summary>页面首次显示时自动扫描一次，省去用户手动点击。</summary>
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_library.AllTracks.Count == 0 && !_library.IsScanning)
            await _library.ScanCommand.ExecuteAsync(null);
    }

    #region 顶部操作

    private void OnOpenSearchClicked(object? sender, EventArgs e)
    {
        // 先把筛选浮层展开（页内快速筛选），长按/点击搜索图标也可进入完整搜索页。
        // 这里按网易云的习惯：右上角放大镜进入独立搜索页。
        _ = OpenSearchPageAsync();
    }

    private async Task OpenSearchPageAsync()
    {
        var vm = MauiProgram.Services.GetService<SearchViewModel>();
        if (vm is null)
        {
            // 搜索服务不可用时退回页内筛选，功能不至于完全丢失
            _library.IsSearchVisible = true;
            FilterEntry.Focus();
            return;
        }
        await Navigation.PushModalAsync(new SearchPage(vm, _player));
    }

    private void OnCloseFilterClicked(object? sender, EventArgs e)
    {
        _library.IsSearchVisible = false;
        _library.FilterText = string.Empty;
        FilterEntry.Unfocus();
    }

    private async void OnOpenSettingsClicked(object? sender, EventArgs e)
    {
        var vm = MauiProgram.Services.GetService<SettingsViewModel>();
        if (vm is null) return;
        await Navigation.PushModalAsync(new SettingsPage(vm));
    }

    #endregion

    #region 标签页

    private void OnTabAllClicked(object? sender, EventArgs e)
    {
        _library.CurrentTab = LibraryTab.All;
        PlaylistStrip.IsVisible = false;
        ApplyTabVisual();
    }

    private void OnTabFavoritesClicked(object? sender, EventArgs e)
    {
        _library.CurrentTab = LibraryTab.Favorites;
        PlaylistStrip.IsVisible = false;
        ApplyTabVisual();
    }

    private void OnTabPlaylistsClicked(object? sender, EventArgs e)
    {
        _library.CurrentTab = LibraryTab.Playlists;
        PlaylistStrip.IsVisible = true;
        ApplyTabVisual();
    }

    private void OnTabRecentClicked(object? sender, EventArgs e)
    {
        _library.CurrentTab = LibraryTab.Recent;
        PlaylistStrip.IsVisible = false;
        ApplyTabVisual();
    }

    /// <summary>高亮当前标签页：选中项用品牌红实底白字，其余透明灰字。</summary>
    private void ApplyTabVisual()
    {
        Style(TabAllBtn, _library.CurrentTab == LibraryTab.All);
        Style(TabFavBtn, _library.CurrentTab == LibraryTab.Favorites);
        Style(TabPlBtn, _library.CurrentTab == LibraryTab.Playlists);
        Style(TabRecentBtn, _library.CurrentTab == LibraryTab.Recent);

        static void Style(Button btn, bool active)
        {
            btn.BackgroundColor = active
                ? Color.FromArgb("#EC4141")
                : Colors.Transparent;
            btn.TextColor = active
                ? Colors.White
                : Color.FromArgb("#66666E");
            btn.FontAttributes = active ? FontAttributes.Bold : FontAttributes.None;
        }
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

    /// <summary>曲目行右侧「⋯」菜单：播放、喜欢、加入歌单、从歌单移除。</summary>
    private async void OnTrackMenuClicked(object? sender, EventArgs e)
    {
        if ((sender as Element)?.BindingContext is not Track track) return;

        var isFav = _library.IsFavorite(track);
        var options = new List<string>
        {
            "立即播放",
            isFav ? "取消喜欢" : "加入我喜欢",
            "加入歌单",
        };

        if (_library.CurrentTab == LibraryTab.Playlists && _library.SelectedPlaylist is not null)
            options.Add("从当前歌单移除");

        var action = await DisplayActionSheet(track.Title, "取消", null, options.ToArray());
        if (string.IsNullOrEmpty(action) || action == "取消") return;

        switch (action)
        {
            case "立即播放":
                await _library.PlayTrackCommand.ExecuteAsync(track);
                break;
            case "加入我喜欢":
            case "取消喜欢":
                _library.ToggleFavoriteCommand.Execute(track);
                break;
            case "加入歌单":
                await AddToPlaylistAsync(track);
                break;
            case "从当前歌单移除":
                _library.RemoveFromPlaylistCommand.Execute(track);
                break;
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
        ApplyTabVisual();
    }

    #endregion

    /// <summary>点击迷你条：打开播放详情页。</summary>
    private async void OnMiniPlayerTapped(object? sender, TappedEventArgs e)
    {
        if (!_player.HasTrack) return;
        await Navigation.PushModalAsync(new PlayerPage(_player));
    }

    /// <summary>点击迷你条右侧列表按钮：打开当前播放队列。</summary>
    private async void OnOpenQueueClicked(object? sender, EventArgs e)
    {
        if (!_player.HasTrack) return;
        await Navigation.PushModalAsync(new PlayerPage(_player, startInQueueMode: true));
    }
}
