using AnMusic.Android.ViewModels;
using AnMusic.Models;

namespace AnMusic.Android;

/// <summary>
/// 搜索页：跨音源检索。音源来自 ProviderRegistry（本地 + 已加载的 .js 插件）。
/// </summary>
public partial class SearchPage : ContentPage
{
    private readonly SearchViewModel _vm;
    private readonly PlayerViewModel _player;
    private readonly LibraryViewModel? _library;

    public SearchPage(SearchViewModel vm, PlayerViewModel player)
    {
        InitializeComponent();

        _vm = vm;
        _player = player;
        BindingContext = vm;

        _library = MauiProgram.Services.GetService<LibraryViewModel>();

        // 进入页面时刷新音源列表：插件是后台异步加载的，
        // 首页进来时可能还没装好，这里再拉一次以确保能选到。
        _vm.ReloadSources();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        SearchEntry.Focus();
    }

    #region 搜索框

    private void OnSearchCompleted(object? sender, EventArgs e)
        => _vm.SearchCommand.Execute(null);

    private void OnClearKeywordClicked(object? sender, EventArgs e)
    {
        _vm.Keyword = string.Empty;
        _vm.ClearResultsCommand.Execute(null);
        SearchEntry.Focus();
    }

    private async void OnBackClicked(object? sender, EventArgs e)
        => await Navigation.PopModalAsync();

    #endregion

    #region 音源

    /// <summary>切换音源时清空上一次结果，避免不同来源的结果混在一起。</summary>
    private void OnSourceSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // 只有用户主动切换（存在新增项）时才清空；初始化赋值不清空
        if (e.PreviousSelection.Count == 0 && e.CurrentSelection.Count == 1) return;

        _vm.ClearResultsCommand.Execute(null);
    }

    /// <summary>高亮选中的音源胶囊。</summary>
    private void ApplySourceVisual()
    {
        // CollectionView 的选中态没有内置样式，这里按索引取容器调整外观
        // （音源数量少，直接遍历可见项即可）
    }

    #endregion

    #region 结果交互

    private async void OnResultTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Element)?.BindingContext is not Track track) return;

        await _vm.PlayResultCommand.ExecuteAsync(track);
        if (_library is not null)
            _player.SetFavoriteState(_library.IsFavorite(track));

        await Navigation.PushModalAsync(new PlayerPage(_player));
    }

    private async void OnResultMenuClicked(object? sender, EventArgs e)
    {
        if ((sender as Element)?.BindingContext is not Track track) return;

        var options = new List<string> { "立即播放", "加入我喜欢", "加入歌单" };
        var action = await DisplayActionSheet(track.Title, "取消", null, options.ToArray());
        if (string.IsNullOrEmpty(action) || action == "取消") return;

        switch (action)
        {
            case "立即播放":
                await _vm.PlayResultCommand.ExecuteAsync(track);
                break;

            case "加入我喜欢":
                if (_library is null) return;
                _library.ToggleFavoriteCommand.Execute(track);
                await DisplayAlert("已收藏", $"「{track.Title}」已加入我喜欢", "好");
                break;

            case "加入歌单":
                if (_library is null) return;
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
                }
                else
                {
                    _library.AddToPlaylistByName(track, picked);
                }

                await DisplayAlert("已加入", $"「{track.Title}」已加入「{picked}」", "好");
                break;
        }
    }

    private void OnHistoryTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Element)?.BindingContext is not string word) return;
        _vm.SearchWithCommand.Execute(word);
    }

    #endregion
}
