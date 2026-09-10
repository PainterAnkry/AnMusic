using System.Collections.ObjectModel;
using AnMusic.Android.Services;
using AnMusic.Models;
using AnMusic.Services;
using AnMusic.Services.Playlist;
using AnMusic.Services.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnMusic.Android.ViewModels;

/// <summary>主内容区标签页。</summary>
public enum LibraryTab
{
    /// <summary>全部本地歌曲。</summary>
    All,

    /// <summary>我喜欢的音乐。</summary>
    Favorites,

    /// <summary>用户自建歌单。</summary>
    Playlists,

    /// <summary>最近播放。</summary>
    Recent,
}

/// <summary>
/// 曲库 ViewModel：本地音乐扫描、我喜欢、歌单、最近播放的展示与增删。
/// 用户数据读写复用 Core 的 <see cref="UserDataStore"/>，与桌面端存储格式完全一致。
/// </summary>
public sealed partial class LibraryViewModel : ObservableObject
{
    private readonly LocalMusicScanner _scanner;
    private readonly UserDataStore _store;
    private readonly PlayerViewModel _player;
    private readonly IPlatformContext _platform;

    /// <summary>当前是否正在后台补封面，避免重复启动。</summary>
    private bool _enrichingCovers;

    public LibraryViewModel(
        LocalMusicScanner scanner,
        UserDataStore store,
        PlayerViewModel player,
        IPlatformContext platform)
    {
        _scanner = scanner;
        _store = store;
        _player = player;
        _platform = platform;

        LoadFromStore();

        // 当前播放曲目变化时刷新列表高亮
        _player.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlayerViewModel.CurrentTrack))
                OnPropertyChanged(nameof(NowPlayingKey));
        };
    }

    #region 状态

    [ObservableProperty] private LibraryTab _currentTab = LibraryTab.All;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _scanStatus = "尚未扫描";
    [ObservableProperty] private string _filterText = string.Empty;
    [ObservableProperty] private Playlist? _selectedPlaylist;

    /// <summary>搜索框是否展开（网易云右上角放大镜点开的浮层）。</summary>
    [ObservableProperty] private bool _isSearchVisible;

    /// <summary>全部本地曲目（未经筛选的原始列表）。</summary>
    public ObservableCollection<Track> AllTracks { get; } = [];

    /// <summary>当前界面上展示的曲目（应用筛选后的结果）。</summary>
    public ObservableCollection<Track> VisibleTracks { get; } = [];

    public ObservableCollection<Track> Favorites { get; } = [];

    public ObservableCollection<Playlist> Playlists { get; } = [];

    /// <summary>最近播放（来自 Core 的持久化数据）。</summary>
    public ObservableCollection<Track> Recent { get; } = [];

    /// <summary>页面标题。</summary>
    public string HeaderText => CurrentTab switch
    {
        LibraryTab.Favorites => "我喜欢的音乐",
        LibraryTab.Playlists => SelectedPlaylist?.Name ?? "歌单",
        LibraryTab.Recent => "最近播放",
        _ => "本地音乐",
    };

    /// <summary>当前列表的曲目数说明。</summary>
    public string CountText => $"{VisibleTracks.Count} 首";

    /// <summary>列表是否为空（用于显示空状态）。</summary>
    public bool IsEmpty => VisibleTracks.Count == 0 && !IsScanning;

    /// <summary>「播放全部」按钮上的曲目数。</summary>
    public string PlayAllText => $"播放全部({VisibleTracks.Count})";

    /// <summary>正在播放曲目的去重键，供列表行高亮与音波动画判断。</summary>
    public string NowPlayingKey => _player.CurrentTrack is { } t ? $"{t.ProviderId}:{t.Id}" : string.Empty;

    /// <summary>判断某曲目是否为当前播放曲目。</summary>
    public bool IsNowPlaying(Track track) =>
        _player.CurrentTrack is { } cur && cur.Id == track.Id && cur.ProviderId == track.ProviderId;

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    partial void OnCurrentTabChanged(LibraryTab value)
    {
        SelectedPlaylist = null;
        ApplyFilter();
        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(CountText));
    }

    partial void OnSelectedPlaylistChanged(Playlist? value)
    {
        ApplyFilter();
        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(CountText));
    }

    partial void OnIsScanningChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));

    #endregion

    #region 本地扫描

    /// <summary>扫描本机音乐并刷新列表。</summary>
    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsScanning) return;
        IsScanning = true;
        ScanStatus = "正在申请权限…";

        try
        {
            if (!await _platform.RequestMediaReadPermissionAsync())
            {
                ScanStatus = "未获得音频读取权限，请在系统设置中授权后重试";
                return;
            }

            ScanStatus = "正在扫描…";
            var dirs = _platform.LocalMusicDirectories;

            var tracks = await Task.Run(() => _scanner.Scan(dirs));

            AllTracks.Clear();
            foreach (var t in tracks) AllTracks.Add(t);

            ScanStatus = tracks.Count == 0
                ? "未找到本地音乐（请把音频放入「Music」目录）"
                : $"扫描完成，共 {tracks.Count} 首";

            ApplyFilter();
        }
        catch (Exception ex)
        {
            ScanStatus = $"扫描失败：{ex.Message}";
            AppPaths.LogError("扫描本地音乐", ex);
        }
        finally
        {
            IsScanning = false;
        }

        // 扫描结束后台补齐封面（首屏不等它）
        StartCoverEnrichment();
    }

    /// <summary>
    /// 后台为缺封面的本地曲目读取内嵌图，逐张刷新 UI。
    /// 放在扫描之后而非之中，是为了让用户先拿到列表而不是盯着进度条。
    /// </summary>
    private void StartCoverEnrichment()
    {
        if (_enrichingCovers) return;
        _enrichingCovers = true;

        _ = Task.Run(async () =>
        {
            try
            {
                await _scanner.EnrichCoversAsync(AllTracks.ToList(), track =>
                {
                    // 回调发生在后台线程；Track.CoverKey 的 setter 已发 PropertyChanged，
                    // 这里只需确保绑定在 UI 线程收到通知。
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        // 曲目可能属于「我喜欢」「歌单」等其它集合，这里统一刷新当前可见列表
                        var index = VisibleTracks.IndexOf(track);
                        if (index >= 0)
                        {
                            VisibleTracks.RemoveAt(index);
                            VisibleTracks.Insert(index, track);
                        }
                    });
                });
            }
            catch (Exception ex)
            {
                AppPaths.LogError("批量补封面", ex);
            }
            finally
            {
                _enrichingCovers = false;
            }
        });
    }

    /// <summary>按关键词筛选当前列表。</summary>
    private void ApplyFilter()
    {
        var source = CurrentTab switch
        {
            LibraryTab.Favorites => Favorites.AsEnumerable(),
            LibraryTab.Playlists => SelectedPlaylist?.Tracks ?? [],
            LibraryTab.Recent => Recent,
            _ => AllTracks,
        };

        var keyword = FilterText?.Trim() ?? string.Empty;
        var filtered = string.IsNullOrEmpty(keyword)
            ? source
            : source.Where(t =>
                t.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                t.Artist.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                t.Album.Contains(keyword, StringComparison.OrdinalIgnoreCase));

        VisibleTracks.Clear();
        foreach (var t in filtered) VisibleTracks.Add(t);

        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(PlayAllText));
    }

    #endregion

    #region 播放

    /// <summary>点击曲目：把当前可见列表作为播放队列，从该曲目开始播放。</summary>
    [RelayCommand]
    private async Task PlayTrackAsync(Track? track)
    {
        if (track is null) return;
        var index = VisibleTracks.IndexOf(track);
        await _player.PlayQueueAsync(VisibleTracks.ToList(), Math.Max(0, index));
        PushRecent(track);
    }

    /// <summary>播放全部（从头开始）。</summary>
    [RelayCommand]
    private async Task PlayAllAsync()
    {
        if (VisibleTracks.Count == 0)
        {
            ScanStatus = "列表为空，请先扫描本地音乐";
            return;
        }
        await _player.PlayQueueAsync(VisibleTracks.ToList(), 0);
    }

    /// <summary>随机播放全部。</summary>
    [RelayCommand]
    private async Task ShuffleAllAsync()
    {
        if (VisibleTracks.Count == 0) return;

        var shuffled = VisibleTracks.OrderBy(_ => Random.Shared.Next()).ToList();
        _player.PlayMode = PlayModeKind.Shuffle;
        await _player.PlayQueueAsync(shuffled, 0);
    }

    /// <summary>把曲目写入最近播放（去重后置顶，最多保留 200 首）。</summary>
    private void PushRecent(Track track)
    {
        var existing = Recent.FirstOrDefault(
            t => t.Id == track.Id && t.ProviderId == track.ProviderId);
        if (existing is not null) Recent.Remove(existing);

        Recent.Insert(0, track);
        while (Recent.Count > 200) Recent.RemoveAt(Recent.Count - 1);

        try
        {
            _store.Recent = Recent.ToList();
            _store.Save();
        }
        catch (Exception ex)
        {
            AppPaths.LogError("保存最近播放", ex);
        }
    }

    #endregion

    #region 我喜欢 / 歌单

    public bool IsFavorite(Track track) =>
        Favorites.Any(f => f.Id == track.Id && f.ProviderId == track.ProviderId);

    /// <summary>收藏 / 取消收藏。</summary>
    [RelayCommand]
    private void ToggleFavorite(Track? track)
    {
        if (track is null) return;

        var existing = Favorites.FirstOrDefault(f => f.Id == track.Id && f.ProviderId == track.ProviderId);
        if (existing is not null) Favorites.Remove(existing);
        else Favorites.Add(track);

        Persist();
        ApplyFilter();
    }

    /// <summary>新建歌单。</summary>
    [RelayCommand]
    private void CreatePlaylist(string? name)
    {
        var title = string.IsNullOrWhiteSpace(name) ? $"新建歌单 {Playlists.Count + 1}" : name.Trim();
        var playlist = new Playlist { Name = title };
        Playlists.Add(playlist);
        SaveStore();
        CurrentTab = LibraryTab.Playlists;
        SelectedPlaylist = playlist;
    }

    /// <summary>把曲目加入指定歌单（未指定时加入第一个歌单，没有则新建）。</summary>
    [RelayCommand]
    private void AddToPlaylist(Track? track)
    {
        if (track is null) return;

        var target = SelectedPlaylist;
        if (target is null)
        {
            if (Playlists.Count == 0) CreatePlaylist("我的歌单");
            target = Playlists[0];
        }

        if (target.Tracks.Any(t => t.Id == track.Id && t.ProviderId == track.ProviderId))
        {
            ScanStatus = $"「{target.Name}」中已存在该曲目";
            return;
        }

        target.Tracks.Add(track);
        SaveStore();
        ScanStatus = $"已加入「{target.Name}」";
    }

    /// <summary>把曲目加入指定名称的歌单（供搜索结果页调用）。</summary>
    public void AddToPlaylistByName(Track track, string playlistName)
    {
        var target = Playlists.FirstOrDefault(p => p.Name == playlistName);
        if (target is null)
        {
            target = new Playlist { Name = playlistName };
            Playlists.Add(target);
        }

        if (target.Tracks.Any(t => t.Id == track.Id && t.ProviderId == track.ProviderId)) return;

        target.Tracks.Add(track);
        SaveStore();
    }

    /// <summary>从当前歌单移除曲目。</summary>
    [RelayCommand]
    private void RemoveFromPlaylist(Track? track)
    {
        if (track is null || SelectedPlaylist is null) return;
        var item = SelectedPlaylist.Tracks.FirstOrDefault(
            t => t.Id == track.Id && t.ProviderId == track.ProviderId);
        if (item is null) return;

        SelectedPlaylist.Tracks.Remove(item);
        SaveStore();
        ApplyFilter();
    }

    /// <summary>删除歌单。</summary>
    [RelayCommand]
    private void DeletePlaylist(Playlist? playlist)
    {
        if (playlist is null) return;
        Playlists.Remove(playlist);
        if (ReferenceEquals(SelectedPlaylist, playlist)) SelectedPlaylist = null;
        SaveStore();
        ApplyFilter();
    }

    #endregion

    #region 持久化

    private void LoadFromStore()
    {
        foreach (var track in _store.Favorites) Favorites.Add(track);
        foreach (var playlist in _store.Playlists) Playlists.Add(playlist);
        foreach (var track in _store.Recent) Recent.Add(track);
    }

    /// <summary>把内存状态同步进存储对象并落盘。</summary>
    private void Persist()
    {
        _store.Favorites = Favorites.ToList();
        SaveStore();
    }

    private void SaveStore()
    {
        try
        {
            _store.Playlists = Playlists.ToList();
            _store.Save();
        }
        catch (Exception ex)
        {
            AppPaths.LogError("保存用户数据", ex);
        }
    }

    #endregion
}
