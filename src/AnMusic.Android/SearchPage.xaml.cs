using AnMusic.Android.Services;
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
    private readonly DownloadViewModel? _downloads;

    /// <summary>「试试搜索」里的常用词，点一下直接搜。</summary>
    private static readonly string[] Suggestions =
        ["周杰伦", "Taylor Swift", "钢琴曲", "ACG", "轻音乐", "经典老歌"];

    public SearchPage(SearchViewModel vm, PlayerViewModel player)
    {
        InitializeComponent();

        _vm = vm;
        _player = player;
        BindingContext = vm;

        _library = MauiProgram.Services.GetService<LibraryViewModel>();
        _downloads = MauiProgram.Services.GetService<DownloadViewModel>();

        BuildSuggestionChips();

        // 进入页面时刷新音源列表：插件是后台异步加载的，
        // 首页进来时可能还没装好，这里再拉一次以确保能选到。
        _vm.ReloadSources();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        ApplySourceVisual();
        SearchEntry.Focus();
    }

    #region 搜索框

    private void OnOpenFlyoutClicked(object? sender, EventArgs e)
        => Shell.Current.FlyoutIsPresented = true;

    private async void OnBackClicked(object? sender, EventArgs e)
    {
        try { await Shell.Current.GoToAsync("//MainPage"); }
        catch { await Navigation.PopAsync(); }
    }

    private void OnSearchCompleted(object? sender, EventArgs e)
        => _vm.SearchCommand.Execute(null);

    private void OnClearKeywordClicked(object? sender, EventArgs e)
    {
        _vm.Keyword = string.Empty;
        _vm.ClearResultsCommand.Execute(null);
        SearchEntry.Focus();
    }

    #endregion

    #region 音源

    private void OnSourceChipTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Element)?.BindingContext is not SearchSource source) return;
        if (ReferenceEquals(source, _vm.SelectedSource)) return;

        _vm.SelectedSource = source;

        // 切换音源后上一次的结果已无意义，清掉避免混淆
        _vm.ClearResultsCommand.Execute(null);
        ApplySourceVisual();
    }

    /// <summary>高亮选中的音源胶囊（换成强调色实底白字）。</summary>
    private void ApplySourceVisual()
    {
        var selected = _vm.SelectedSource;

        foreach (var child in SourceChips.Children)
        {
            if (child is not Border chip) continue;

            var isActive = selected is not null &&
                           chip.BindingContext is SearchSource s &&
                           ReferenceEquals(s, selected);

            chip.BackgroundColor = isActive ? ThemeService.Get("AmPrimary") : ThemeService.Get("AmChipBg");

            if (chip.Content is Label label)
                label.TextColor = isActive ? ThemeService.Get("AmTextOnPrimary") : ThemeService.Get("AmTextSecondary");
        }
    }

    #endregion

    #region 推荐词

    private void BuildSuggestionChips()
    {
        SuggestionChips.Clear();

        foreach (var word in Suggestions)
        {
            var chip = new Border
            {
                BackgroundColor = ThemeService.Get("AmChipBg"),
                StrokeThickness = 0,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 14 },
                Padding = new Thickness(14, 7),
                Margin = new Thickness(0, 0, 8, 8),
                Content = new Label
                {
                    Text = word,
                    FontSize = 12,
                    TextColor = ThemeService.Get("AmTextSecondary"),
                },
            };

            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => _vm.SearchWithCommand.Execute(word);
            chip.GestureRecognizers.Add(tap);

            SuggestionChips.Add(chip);
        }
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

        var isFav = _library?.IsFavorite(track) ?? false;
        var options = new List<string>
        {
            "立即播放",
            "下一首播放",
            isFav ? "取消喜欢" : "加入我喜欢",
            "加入歌单",
            "下载到本地",
        };

        var action = await DisplayActionSheet(track.Title, "取消", null, options.ToArray());
        if (string.IsNullOrEmpty(action) || action == "取消") return;

        switch (action)
        {
            case "立即播放":
                await _vm.PlayResultCommand.ExecuteAsync(track);
                if (_library is not null) _player.SetFavoriteState(_library.IsFavorite(track));
                break;

            case "下一首播放":
                _player.InsertNextCommand.Execute(track);
                break;

            case "加入我喜欢":
            case "取消喜欢":
                if (_library is null) return;
                _library.ToggleFavoriteCommand.Execute(track);
                break;

            case "加入歌单":
                await AddToPlaylistAsync(track);
                break;

            case "下载到本地":
                if (_downloads is not null) await _downloads.EnqueueAsync(track);
                break;
        }
    }

    private async Task AddToPlaylistAsync(Track track)
    {
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
    }

    private void OnHistoryTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Element)?.BindingContext is not string word) return;
        _vm.SearchWithCommand.Execute(word);
    }

    #endregion
}
