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

    /// <summary>主页「最近在听」：最近播放过的曲目，点一下直接播。</summary>
    public ObservableCollection<HomeCard> HomeRecentTracks { get; } = [];

    /// <summary>最近播放是否为空（显示提示文案）。</summary>
    [ObservableProperty]
    private bool _hasNoRecent;

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
        BuildRecentTracks();

        var local = Library.Tracks.Count;
        var favorites = Favorites.Count;
        HomeSummary = $"本地音乐 {local} 首 · 我喜欢 {favorites} 首 · 我的歌单 {UserPlaylists.Count} 个";
    }

    /// <summary>横幅：既有视图入口；没有曲目封面时回落到内置设计图（Assets/home）。</summary>
    private void BuildBanners()
    {
        HomeBanners.Clear();

        HomeBanners.Add(new HomeCard
        {
            Icon = "🎵",
            Title = "本地歌曲",
            Description = Library.Tracks.Count > 0
                ? $"音乐库里的 {Library.Tracks.Count} 首"
                : "还没有本地音乐，点「打开文件夹」扫描",
            CoverTrack = Library.Tracks.FirstOrDefault(),
            DefaultCover = DefaultCover("local"),
            Command = ShowAllTracksCommand,
        });

        HomeBanners.Add(new HomeCard
        {
            Icon = "📻",
            Title = "个性电台",
            Description = Favorites.Count > 0
                ? $"根据「我喜欢」的 {Favorites.Count} 首自动推荐"
                : "先收藏几首歌，再回来听推荐",
            CoverTrack = Favorites.FirstOrDefault(),
            DefaultCover = DefaultCover("radio"),
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
            DefaultCover = DefaultCover("ranking"),
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
            DefaultCover = DefaultCover("stats"),
            Command = ShowListeningStatsCommand,
        });

        HomeBanners.Add(new HomeCard
        {
            Icon = "🕘",
            Title = "最近播放",
            Description = Recent.Count > 0 ? $"最近听过的 {Recent.Count} 首" : "还没有播放记录",
            CoverTrack = Recent.FirstOrDefault(),
            DefaultCover = DefaultCover("recent"),
            Command = ShowRecentCommand,
        });

        HomeBanners.Add(new HomeCard
        {
            Icon = "❤",
            Title = "我喜欢",
            Description = Favorites.Count > 0 ? $"收藏的 {Favorites.Count} 首" : "还没有收藏歌曲",
            CoverTrack = Favorites.FirstOrDefault(),
            DefaultCover = DefaultCover("favorite"),
            Command = ShowFavoritesCommand,
        });
    }

    /// <summary>内置默认封面的打包地址（无自有封面时用它，避免出现空白卡片）。</summary>
    private static string DefaultCover(string name)
        => $"pack://application:,,,/AnMusic;component/Assets/home/{name}.png";

    /// <summary>歌单区：用户歌单（自定义封面 → 最近添加的歌曲封面）+ 新建歌单占位卡。</summary>
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
                Playlist = playlist,
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

    /// <summary>
    /// 「最近在听」：最近播放过的曲目，点一下就以"最近播放"为队列直接开播。
    /// </summary>
    /// <remarks>
    /// 用最近播放的既有数据，不是新功能；比原来的"快捷入口"更像个首页该有的内容区
    /// （快捷入口里的下载管理/一起听/均衡器/桌面歌词/音源插件在侧边栏与播放栏都能进）。
    /// </remarks>
    private void BuildRecentTracks()
    {
        HomeRecentTracks.Clear();
        foreach (var track in Recent.Take(6))
        {
            HomeRecentTracks.Add(new HomeCard
            {
                Icon = "🎵",
                Title = track.Title,
                Description = track.Artist,
                CoverTrack = track,
                Command = PlayHomeTrackCommand,
                CommandParameter = track,
            });
        }
        HasNoRecent = HomeRecentTracks.Count == 0;
    }

    /// <summary>主页点歌：以「最近播放」为队列从该曲开始播。</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private async Task PlayHomeTrackAsync(Track? track)
    {
        if (track is null) return;
        await PlayTrackAsync(track, [.. Recent]);
    }

    #region 歌单封面

    /// <summary>自定义封面存放目录（选图后会复制进来，原图移动/删除都不影响）。</summary>
    private static string PlaylistCoverDir => System.IO.Path.Combine(Services.AppPaths.DataRoot, "playlist-covers");

    /// <summary>
    /// 给歌单设置自定义封面：选一张图片 → 复制到应用数据目录 → 落盘。
    /// </summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ChoosePlaylistCover(Playlist? playlist)
    {
        if (playlist is null) return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"选择「{playlist.Name}」的封面",
            Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.gif|所有文件|*.*",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            System.IO.Directory.CreateDirectory(PlaylistCoverDir);
            var ext = System.IO.Path.GetExtension(dialog.FileName);
            if (string.IsNullOrEmpty(ext)) ext = ".png";
            // 文件名带歌单 Id：换封面直接覆盖旧的，不留垃圾
            var target = System.IO.Path.Combine(PlaylistCoverDir, $"{playlist.Id}{ext}");
            System.IO.File.Copy(dialog.FileName, target, overwrite: true);

            playlist.CoverPath = target;
            SaveUserData();
            SearchStatus = $"已设置「{playlist.Name}」的封面";
            RefreshHome(); // 主页卡片跟着刷新
        }
        catch (Exception ex)
        {
            Services.AppPaths.LogError("设置歌单封面", ex, dialog.FileName);
            Views.UiDialog.Error("设置封面失败", ex);
        }
    }

    /// <summary>恢复歌单默认封面（= 最近添加的那首歌的封面）。</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ResetPlaylistCover(Playlist? playlist)
    {
        if (playlist is null || playlist.CoverPath.Length == 0) return;

        try
        {
            var old = playlist.CoverPath;
            playlist.CoverPath = "";
            if (System.IO.File.Exists(old)) System.IO.File.Delete(old);
        }
        catch (Exception ex)
        {
            // 删不掉旧图不影响恢复默认封面
            Services.AppPaths.LogError("删除歌单封面", ex, playlist.CoverPath);
        }

        SaveUserData();
        SearchStatus = $"「{playlist.Name}」已恢复默认封面";
        RefreshHome();
    }

    #endregion
}
