using System.Collections.ObjectModel;
using AnMusic.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AnMusic.ViewModels;

/// <summary>
/// MainViewModel 的主页部分（partial 拆分）：把现有功能按"首页"的方式摆出来。
/// </summary>
/// <remarks>
/// 这里**只做聚合与入口**，不引入新功能：横幅是既有视图（个性电台/排行榜/听歌排行/最近播放/我喜欢），
/// 歌单区是用户自己的歌单，快捷入口是既有面板（下载管理/一起听/均衡器/桌面歌词/音源插件）。
/// 所有卡片的数据都来自现有集合，不额外请求网络。
/// </remarks>
public partial class MainViewModel
{
    /// <summary>主页横幅：既有发现类视图的入口。</summary>
    public ObservableCollection<HomeCard> HomeBanners { get; } = [];

    /// <summary>主页歌单区：用户歌单 + "新建歌单"占位卡。</summary>
    public ObservableCollection<HomeCard> HomePlaylists { get; } = [];

    /// <summary>主页快捷入口：既有面板/窗口。</summary>
    public ObservableCollection<HomeCard> HomeShortcuts { get; } = [];

    /// <summary>主页顶部的一句汇总（本地曲库 / 收藏 / 歌单）。</summary>
    [ObservableProperty]
    private string _homeSummary = "";

    /// <summary>是否还没有任何歌单（显示空状态提示）。</summary>
    [ObservableProperty]
    private bool _hasNoPlaylist;

    /// <summary>重建主页卡片（进入主页时、以及相关集合变化时调用）。</summary>
    public void RefreshHome()
    {
        BuildBanners();
        BuildPlaylists();
        BuildShortcuts();

        var local = Library.Tracks.Count;
        var favorites = Favorites.Count;
        HomeSummary = $"本地音乐 {local} 首 · 我喜欢 {favorites} 首 · 我的歌单 {UserPlaylists.Count} 个";
    }

    /// <summary>横幅：五个现有视图，说明文字用现有数据算，不编造指标。</summary>
    private void BuildBanners()
    {
        HomeBanners.Clear();

        HomeBanners.Add(new HomeCard
        {
            Icon = "📻",
            Title = "个性电台",
            Description = Favorites.Count > 0
                ? $"根据「我喜欢」的 {Favorites.Count} 首自动推荐"
                : "先收藏几首歌，再回来听推荐",
            CoverTrack = Favorites.FirstOrDefault(),
            Command = ShowRadioCommand,
        });

        HomeBanners.Add(new HomeCard
        {
            Icon = "🏆",
            Title = "排行榜",
            Description = SelectedRankingBoard.Length > 0
                ? $"各音源热歌/新歌榜单 · 当前 {SelectedRankingBoard}"
                : "各音源热歌 / 新歌榜单",
            CoverTrack = RankingTracks.FirstOrDefault(),
            Command = ShowRankingCommand,
        });

        HomeBanners.Add(new HomeCard
        {
            Icon = "📊",
            Title = "听歌排行",
            Description = ListeningStatsTracks.Count > 0
                ? $"你听得最多的 {ListeningStatsTracks.Count} 首"
                : "统计每首歌的播放次数与累计时长",
            CoverTrack = ListeningStatsTracks.FirstOrDefault(),
            Command = ShowListeningStatsCommand,
        });

        HomeBanners.Add(new HomeCard
        {
            Icon = "🕘",
            Title = "最近播放",
            Description = Recent.Count > 0 ? $"最近听过的 {Recent.Count} 首" : "还没有播放记录",
            CoverTrack = Recent.FirstOrDefault(),
            Command = ShowRecentCommand,
        });

        HomeBanners.Add(new HomeCard
        {
            Icon = "❤",
            Title = "我喜欢",
            Description = Favorites.Count > 0 ? $"收藏的 {Favorites.Count} 首" : "还没有收藏歌曲",
            CoverTrack = Favorites.LastOrDefault(),
            Command = ShowFavoritesCommand,
        });
    }

    /// <summary>歌单区：用户歌单（封面取歌单第一首的封面）+ 新建歌单占位卡。</summary>
    private void BuildPlaylists()
    {
        HomePlaylists.Clear();
        foreach (var playlist in UserPlaylists)
        {
            HomePlaylists.Add(new HomeCard
            {
                Icon = "🎵",
                Title = playlist.Name,
                Description = $"{playlist.Tracks.Count} 首",
                CoverTrack = playlist.Tracks.FirstOrDefault(),
                Command = SelectPlaylistCommand,
                CommandParameter = playlist,
            });
        }

        HasNoPlaylist = UserPlaylists.Count == 0;

        // 只展示前 5 个，第 6 格固定是"新建歌单"（与参考图的一行 6 格一致）
        while (HomePlaylists.Count > 5) HomePlaylists.RemoveAt(HomePlaylists.Count - 1);
        HomePlaylists.Add(new HomeCard
        {
            Icon = "＋",
            Title = "新建歌单",
            Description = UserPlaylists.Count > 5 ? $"还有 {UserPlaylists.Count - 5} 个，见左侧列表" : "把喜欢的歌归到一起",
            Command = CreatePlaylistCommand,
            IsPlaceholder = true,
        });
    }

    /// <summary>快捷入口：都是既有面板/窗口，不新增功能。</summary>
    private void BuildShortcuts()
    {
        if (HomeShortcuts.Count > 0) return; // 静态入口，建一次即可

        HomeShortcuts.Add(new HomeCard
        {
            Icon = "📥",
            Title = "下载管理",
            Description = "在线歌曲的下载队列与缓存清理",
            Command = ShowDownloadsCommand,
        });
        HomeShortcuts.Add(new HomeCard
        {
            Icon = "🎧",
            Title = "一起听",
            Description = "建个房间，和朋友同步听同一首",
            ActionKey = "together",
        });
        HomeShortcuts.Add(new HomeCard
        {
            Icon = "🎛",
            Title = "均衡器",
            Description = "10 段图形均衡器与预设",
            ActionKey = "eq",
        });
        HomeShortcuts.Add(new HomeCard
        {
            Icon = "🖥",
            Title = "桌面歌词",
            Description = "独立置顶的歌词悬浮窗",
            Command = ToggleDesktopLyricsCommand,
        });
        HomeShortcuts.Add(new HomeCard
        {
            Icon = "🔌",
            Title = "音源插件",
            Description = "管理 MusicFree 兼容的 .js 音源",
            Command = OpenPluginSettingsCommand,
        });
    }

    /// <summary>打开设置页的「音源」分类（快捷入口用）。</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void OpenPluginSettings()
    {
        Settings.SelectCategory("音源");
        ShowSettings();
    }
}
