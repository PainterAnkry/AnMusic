using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using AnMusic.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnMusic.ViewModels;

/// <summary>
/// 搜索结果分类页 + 歌手页 / 专辑页。
/// </summary>
/// <remarks>
/// 取数全部复用现有搜索（<see cref="SearchForTextAsync"/>），页面内容由搜索结果
/// 按「歌手 / 专辑」字段聚合而来 —— 不新增接口，也不编造端上没有的数据
/// （粉丝数、MV、播客、用户、歌词、声音这些没有对应能力，参考图里相应入口一律不放）。
/// </remarks>
public partial class MainViewModel
{
    // ────────── 搜索结果分类：综合 / 单曲 / 歌手 / 专辑 ──────────

    /// <summary>当前分类页签：0=综合 1=单曲 2=歌手 3=专辑。</summary>
    [ObservableProperty]
    private int _searchTabIndex;

    /// <summary>综合页签（歌手卡 + 专辑卡 + 单曲列表）。</summary>
    public bool IsSearchMixedTab => IsSearchView && SearchTabIndex == 0;

    /// <summary>单曲页签（只有曲目列表）。</summary>
    public bool IsSearchSongsTab => IsSearchView && SearchTabIndex == 1;

    /// <summary>歌手页签（歌手卡片网格）。</summary>
    public bool IsSearchArtistsTab => IsSearchView && SearchTabIndex == 2;

    /// <summary>专辑页签（专辑卡片网格）。</summary>
    public bool IsSearchAlbumsTab => IsSearchView && SearchTabIndex == 3;

    /// <summary>搜索页顶部分类条是否显示。</summary>
    public bool IsSearchTabsVisible => IsSearchView && !IsBilibiliResults;

    /// <summary>
    /// 当前搜索结果是不是 B 站（B 站没有"歌手/专辑"概念：专辑列是来源标记、
    /// 艺术家列是 UP 主），这种结果只给列表视图，不显示分类页签与卡片。
    /// </summary>
    [ObservableProperty]
    private bool _isBilibiliResults;

    /// <summary>
    /// 聚合内容面板是否显示：搜索页的「综合/歌手/专辑」页签，或歌手页的「专辑」页签。
    /// </summary>
    public bool IsSearchGroupPanelVisible =>
        (IsSearchView && SearchTabIndex is not 1) || (IsArtistView && IsArtistAlbumsTab);

    /// <summary>
    /// 聚合面板所在网格行的高度。
    /// </summary>
    /// <remarks>
    /// 综合页下面还有曲目列表，行用 Auto（配合限高）给列表留位置；
    /// 纯卡片页签（搜索页的歌手/专辑、歌手页的专辑）下面没有列表，
    /// 行占满剩余空间 —— 否则卡片多了会被限高截断（用户反馈"专辑页面显示不全"）。
    /// </remarks>
    public GridLength SearchGroupRowHeight =>
        IsSearchGroupPanelVisible && !IsSearchMixedTab
            ? new GridLength(1, GridUnitType.Star)
            : GridLength.Auto;

    /// <summary>卡片区最大高度：综合页限高给列表让位，纯卡片页签不限高（占满 + 自身滚动）。</summary>
    public double SearchGroupMaxHeight => IsSearchMixedTab ? 240 : double.PositiveInfinity;

    /// <summary>搜索分类页签名（综合/单曲/歌手/专辑）。</summary>
    public string[] SearchTabNames { get; } = ["综合", "单曲", "歌手", "专辑"];

    /// <summary>歌手页页签名（歌曲/专辑）。</summary>
    public string[] ArtistTabNames { get; } = ["歌曲", "专辑"];

    /// <summary>搜索结果里的歌手卡片。</summary>
    public ObservableCollection<HomeCard> SearchArtists { get; } = [];

    /// <summary>综合页签顶部只放最匹配的一位歌手（其余在「歌手」页签里看）。</summary>
    public ObservableCollection<HomeCard> SearchTopArtists { get; } = [];

    /// <summary>综合页签的专辑卡只放一行（6 张），给下方曲目列表留出空间。</summary>
    public ObservableCollection<HomeCard> SearchTopAlbums { get; } = [];

    /// <summary>搜索结果里的专辑卡片。</summary>
    public ObservableCollection<HomeCard> SearchAlbums { get; } = [];

    [RelayCommand]
    private void SelectSearchTab(int index) => SearchTabIndex = index;

    /// <summary>
    /// 页签索引变化 → 刷新派生可见性。
    /// </summary>
    /// <remarks>
    /// 关键：界面上页签是 ListBox 的 SelectedIndex 双向绑定，用户点击时只改属性、
    /// 不会走命令，所以必须在这里发通知，否则点了页签内容不切换。
    /// </remarks>
    partial void OnSearchTabIndexChanged(int value) => NotifySearchTabs();

    private void NotifySearchTabs()
    {
        OnPropertyChanged(nameof(IsSearchMixedTab));
        OnPropertyChanged(nameof(IsSearchSongsTab));
        OnPropertyChanged(nameof(IsSearchArtistsTab));
        OnPropertyChanged(nameof(IsSearchAlbumsTab));
        OnPropertyChanged(nameof(IsSearchTabsVisible));
        OnPropertyChanged(nameof(IsSearchGroupPanelVisible));
        OnPropertyChanged(nameof(IsTrackListVisible));
        OnPropertyChanged(nameof(SearchGroupRowHeight));
        OnPropertyChanged(nameof(SearchGroupMaxHeight));
    }

    partial void OnIsBilibiliResultsChanged(bool value) => NotifySearchTabs();

    /// <summary>按「歌手 / 专辑」聚合搜索结果，填充分类页签用的卡片。</summary>
    public void RefreshSearchGroups()
    {
        var tracks = SearchResults.OfType<Track>().ToList();

        // B 站结果不分歌手/专辑：强制回到「单曲」列表视图（与网易云/QQ 的排版一致）
        IsBilibiliResults = tracks.Count > 0 && tracks.All(t => t.ProviderId == "bilibili");
        if (IsBilibiliResults && SearchTabIndex != 1) SearchTabIndex = 1;

        SearchArtists.Clear();
        foreach (var group in tracks
                     .Where(t => t.Artist.Length > 0)
                     .GroupBy(t => t.Artist)
                     .OrderByDescending(g => g.Count())
                     .Take(12))
        {
            SearchArtists.Add(new HomeCard
            {
                Icon = "🎤",
                Title = group.Key,
                Description = $"单曲 {group.Count()} 首",
                CoverTrack = PickCover(group),
                Command = ShowArtistCommand,
                CommandParameter = group.Key,
            });
        }

        SearchAlbums.Clear();
        foreach (var group in tracks
                     .Where(t => t.Album.Length > 0)
                     .GroupBy(t => $"{t.Artist}\u0001{t.Album}")
                     .OrderByDescending(g => g.Count())
                     .Take(12))
        {
            var first = group.First();
            SearchAlbums.Add(new HomeCard
            {
                Icon = "💿",
                Title = first.Album,
                Description = $"{first.Artist} · {group.Count()} 首",
                CoverTrack = PickCover(group),
                Command = ShowAlbumCommand,
                CommandParameter = first.Album,
            });
        }

        // 综合页顶部：只取最匹配的一位歌手
        SearchTopArtists.Clear();
        if (SearchArtists.Count > 0)
        {
            var top = SearchArtists[0];
            SearchTopArtists.Add(new HomeCard
            {
                Icon = top.Icon,
                Title = top.Title,
                Description = top.Description,
                CoverTrack = top.CoverTrack,
                Command = top.Command,
                CommandParameter = top.CommandParameter,
            });
        }

        // 综合页的专辑卡只保留一行（6 张）：卡片区太高会把下面的曲目列表挤没
        SearchTopAlbums.Clear();
        foreach (var album in SearchAlbums.Take(6))
        {
            SearchTopAlbums.Add(new HomeCard
            {
                Icon = album.Icon,
                Title = album.Title,
                Description = album.Description,
                CoverTrack = album.CoverTrack,
                Command = album.Command,
                CommandParameter = album.CommandParameter,
            });
        }
    }

    // ────────── 歌手页 ──────────

    /// <summary>歌手名（页面标题）。</summary>
    [ObservableProperty]
    private string _artistName = "";

    /// <summary>歌手页页签：0=歌曲 1=专辑。</summary>
    [ObservableProperty]
    private int _artistTabIndex;

    [ObservableProperty]
    private bool _isArtistLoading;

    /// <summary>歌手页头部信息（单曲数 / 专辑组数，全部来自真实数据）。</summary>
    [ObservableProperty]
    private string _artistMeta = "";

    /// <summary>歌手头像：该歌手第一首有封面的歌（没有则显示占位）。</summary>
    [ObservableProperty]
    private string _artistCoverKey = "";

    public ObservableCollection<Track> ArtistSongs { get; } = [];

    public ObservableCollection<HomeCard> ArtistAlbums { get; } = [];

    /// <summary>歌手页「歌曲」页签（必须同时是歌手页：否则去过歌手页后搜索页也会渲染这一块）。</summary>
    public bool IsArtistSongsTab => IsArtistView && ArtistTabIndex == 0;

    /// <summary>歌手页「专辑」页签（同样必须限定在歌手页内）。</summary>
    public bool IsArtistAlbumsTab => IsArtistView && ArtistTabIndex == 1;

    [RelayCommand]
    private void SelectArtistTab(int index) => ArtistTabIndex = index;

    /// <summary>歌手页页签索引变化 → 刷新派生可见性（同搜索页：点击走双向绑定，不走命令）。</summary>
    partial void OnArtistTabIndexChanged(int value) => NotifyArtistTabs();

    private void NotifyArtistTabs()
    {
        OnPropertyChanged(nameof(IsArtistSongsTab));
        OnPropertyChanged(nameof(IsArtistAlbumsTab));
        OnPropertyChanged(nameof(IsSearchGroupPanelVisible));
        OnPropertyChanged(nameof(IsTrackListVisible));
        OnPropertyChanged(nameof(SearchGroupRowHeight));
        OnPropertyChanged(nameof(SearchGroupMaxHeight));
    }

    /// <summary>打开歌手页（卡片点击 / 列表点歌手名 / 歌词页点歌手名都走这里）。</summary>
    [RelayCommand]
    private Task ShowArtistAsync(string? artist) => ShowArtistPageAsync(artist);

    public Task ShowArtistPageAsync(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return Task.CompletedTask;
        return ShowArtistPageCoreAsync(artist.Trim());
    }

    private async Task ShowArtistPageCoreAsync(string artist)
    {
        ArtistName = artist;
        ArtistTabIndex = 0;
        OnPropertyChanged(nameof(IsArtistSongsTab));
        OnPropertyChanged(nameof(IsArtistAlbumsTab));
        ArtistSongs.Clear();
        ArtistAlbums.Clear();
        ArtistMeta = "";
        SetViewMode(ViewMode.Artist); // 标题/列表先切过去，取数期间显示加载态
        IsArtistLoading = true;
        try
        {
            await SearchForTextAsync(artist, ViewMode.Artist);
        }
        finally
        {
            IsArtistLoading = false;
        }
    }

    /// <summary>用搜索结果重建歌手页内容（歌曲列表 + 专辑卡片）。</summary>
    public void RefreshArtistPage()
    {
        var all = SearchResults.OfType<Track>().ToList();
        var songs = all.Where(t => t.Artist.Contains(ArtistName, StringComparison.OrdinalIgnoreCase)).ToList();
        // 音源给的是宽匹配（歌手名只命中一部分）时退回全部结果，别让页面空着
        if (songs.Count == 0) songs = all;

        ArtistSongs.Clear();
        foreach (var t in songs) ArtistSongs.Add(t);

        ArtistAlbums.Clear();
        foreach (var group in songs
                     .Where(t => t.Album.Length > 0)
                     .GroupBy(t => t.Album)
                     .OrderByDescending(g => g.Count())
                     .Take(12))
        {
            ArtistAlbums.Add(new HomeCard
            {
                Icon = "💿",
                Title = group.Key,
                Description = $"{group.Count()} 首",
                CoverTrack = PickCover(group),
                Command = ShowAlbumCommand,
                CommandParameter = group.Key,
            });
        }

        ArtistMeta = $"单曲 {ArtistSongs.Count} 首 · 专辑 {ArtistAlbums.Count} 组";
        ArtistCoverKey = FirstCoverKey(ArtistSongs);
        // 封面晚点到也没关系：到了就补上头像（卡片封面绑的是曲目对象，会自动刷新）
        WatchCoverArrival(ArtistSongs, () =>
        {
            if (ArtistCoverKey.Length == 0) ArtistCoverKey = FirstCoverKey(ArtistSongs);
        });
        OnPropertyChanged(nameof(CurrentTracks));
        RefreshEmptyState();
    }

    // ────────── 专辑页 ──────────

    /// <summary>专辑名（页面标题）。</summary>
    [ObservableProperty]
    private string _albumName = "";

    [ObservableProperty]
    private string _albumArtist = "";

    /// <summary>专辑页头部信息（曲目数，来自真实数据）。</summary>
    [ObservableProperty]
    private string _albumMeta = "";

    /// <summary>专辑封面：专辑内第一首有封面的歌。</summary>
    [ObservableProperty]
    private string _albumCoverKey = "";

    [ObservableProperty]
    private bool _isAlbumLoading;

    public ObservableCollection<Track> AlbumTracks { get; } = [];

    /// <summary>打开专辑页（卡片点击 / 列表点专辑名 / 歌词页点专辑名都走这里）。</summary>
    [RelayCommand]
    private Task ShowAlbumAsync(string? album) => ShowAlbumPageAsync(album);

    public Task ShowAlbumPageAsync(string? album)
    {
        if (string.IsNullOrWhiteSpace(album)) return Task.CompletedTask;
        return ShowAlbumPageCoreAsync(album.Trim());
    }

    private async Task ShowAlbumPageCoreAsync(string album)
    {
        AlbumName = album;
        AlbumTracks.Clear();
        AlbumArtist = "";
        SetViewMode(ViewMode.Album);
        IsAlbumLoading = true;
        try
        {
            await SearchForTextAsync(album, ViewMode.Album);
        }
        finally
        {
            IsAlbumLoading = false;
        }
    }

    /// <summary>用搜索结果重建专辑页内容。</summary>
    public void RefreshAlbumPage()
    {
        var all = SearchResults.OfType<Track>().ToList();
        var tracks = all.Where(t => t.Album.Equals(AlbumName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (tracks.Count == 0)
            tracks = all.Where(t => t.Album.Contains(AlbumName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (tracks.Count == 0) tracks = all; // 宽匹配兜底，避免空页

        AlbumTracks.Clear();
        foreach (var t in tracks) AlbumTracks.Add(t);
        AlbumArtist = tracks.FirstOrDefault(t => t.Artist.Length > 0)?.Artist ?? "";
        AlbumMeta = $"{AlbumArtist} · {AlbumTracks.Count} 首";
        AlbumCoverKey = FirstCoverKey(AlbumTracks);
        // 封面晚点到也没关系：到了就补上专辑封面
        WatchCoverArrival(AlbumTracks, () =>
        {
            if (AlbumCoverKey.Length == 0) AlbumCoverKey = FirstCoverKey(AlbumTracks);
        });

        OnPropertyChanged(nameof(CurrentTracks));
        RefreshEmptyState();
    }

    // ────────── 本地歌曲：打开本地文件 ──────────

    /// <summary>
    /// 「打开本地文件」：选本地音频文件加入本地歌曲（路径记住，重扫目录后仍在）。
    /// </summary>
    [RelayCommand]
    private void OpenLocalFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "打开本地文件",
            Filter = "音频文件|*.mp3;*.flac;*.wav;*.m4a;*.aac;*.wma;*.ogg|所有文件|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog() != true) return;

        var added = AddLocalFiles(dialog.FileNames);
        SearchStatus = added == 0
            ? "选中的文件已经在本地歌曲里了"
            : $"已添加 {added} 首到本地歌曲";
    }

    /// <summary>把文件加进本地歌曲并记住路径，返回真正新增的数量。</summary>
    public int AddLocalFiles(IEnumerable<string> files)
    {
        var settings = _settingsService.Settings;
        settings.ExtraLocalFiles ??= [];
        var known = Library.Tracks
            .Select(t => t.FilePath)
            .Where(p => !string.IsNullOrEmpty(p))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = 0;
        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file) || !known.Add(file)) continue;

            Library.Tracks.Add(_localFiles.CreateTrackFromFile(file));
            if (!settings.ExtraLocalFiles.Contains(file, StringComparer.OrdinalIgnoreCase))
                settings.ExtraLocalFiles.Add(file);
            added++;
        }

        if (added > 0)
        {
            _settingsService.Save();
            RefreshEmptyState();
            if (IsHomeView) RefreshHome();
        }
        return added;
    }

    // ────────── 公共小工具 ──────────

    private PropertyChangedEventHandler? _coverWatchHandler;
    private readonly List<Track> _coverWatched = [];

    /// <summary>
    /// 盯住这一批曲目的封面：后台下载完成时回调一次。
    /// </summary>
    /// <remarks>
    /// 封面是后台下载的（在线音源尤其慢），页面重建时往往还没有；
    /// 只在重建那一刻算一次的话，歌手头像 / 专辑封面会一直是空的
    /// （用户反馈的"歌手封面大概率不展示"）。回调要切回 UI 线程：
    /// 封面是在后台线程写进 Track 的。
    /// </remarks>
    private void WatchCoverArrival(IEnumerable<Track> tracks, Action onCoverArrived)
    {
        if (_coverWatchHandler is not null)
            foreach (var t in _coverWatched) t.PropertyChanged -= _coverWatchHandler;
        _coverWatched.Clear();

        _coverWatchHandler = (_, e) =>
        {
            if (e.PropertyName != nameof(Track.CoverKey)) return;

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                onCoverArrived();
                return;
            }
            dispatcher.BeginInvoke(onCoverArrived);
        };

        foreach (var t in tracks)
        {
            t.PropertyChanged += _coverWatchHandler;
            _coverWatched.Add(t);
        }
    }

    private static string FirstCoverKey(IEnumerable<Track> tracks)
        => tracks.FirstOrDefault(t => !string.IsNullOrEmpty(t.CoverKey))?.CoverKey ?? "";

    /// <summary>
    /// 「加载更多」拿到新一页后重建当前页面。
    /// </summary>
    /// <remarks>
    /// 歌手页 / 专辑页也是从搜索结果里筛出来的（只显示前 N 条的窗口），
    /// 所以翻页后必须重建，否则列表永远停在第一页。
    /// </remarks>
    public void RefreshDetailPagesAfterLoadMore()
    {
        RefreshSearchGroups();
        if (IsArtistView) RefreshArtistPage();
        else if (IsAlbumView) RefreshAlbumPage();
    }

    /// <summary>取一组曲目里第一个有封面的，做卡片封面（CoverKey 可能为 null，插件源不保证有）。</summary>
    private static Track? PickCover(IEnumerable<Track> group)
        => group.FirstOrDefault(t => !string.IsNullOrEmpty(t.CoverKey)) ?? group.FirstOrDefault();
}
