using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using AnMusic.Models;
using AnMusic.Services.Playlist;
using AnMusic.Services.Providers;
using AnMusic.Services.Providers.Bilibili;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace AnMusic.ViewModels;

/// <summary>搜索来源。</summary>
public enum SearchSource { All, Local, Bilibili }

/// <summary>主内容区显示的视图。</summary>
public enum ViewMode { AllTracks, SearchResults, Playlist, Favorites, Recent }

/// <summary>
/// 主 ViewModel：装配各子 ViewModel，管理歌单、我喜欢、最近播放、搜索与导航。
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly IPlaylistQueue _queue;
    private readonly PlaybackBarViewModel _playbackBar;
    private readonly ProviderRegistry _registry;
    private readonly UserDataStore _store;
    private readonly LocalFileProvider _localFiles;

    /// <summary>右键菜单当前曲目（"添加到歌单"子菜单使用）。</summary>
    public Track? PendingMenuTrack { get; set; }

    public PlaybackBarViewModel PlaybackBar => _playbackBar;
    public LibraryViewModel Library { get; }
    public LyricViewModel Lyrics { get; }
    public SettingsViewModel Settings { get; }

    public ObservableCollection<Playlist> UserPlaylists { get; } = [];
    public ObservableCollection<Track> Favorites { get; } = [];
    public ObservableCollection<Track> Recent { get; } = [];
    public ObservableCollection<string> SearchHistory { get; } = [];

    [ObservableProperty]
    private Playlist? _selectedPlaylist;

    /// <summary>是否显示设置页。</summary>
    [ObservableProperty]
    private bool _isShowingSettings;

    /// <summary>是否显示搜索结果页（与 ViewMode 联动，供既有绑定使用）。</summary>
    [ObservableProperty]
    private bool _isShowingSearch;

    /// <summary>当前视图模式。</summary>
    [ObservableProperty]
    private ViewMode _viewMode = ViewMode.AllTracks;

    /// <summary>搜索关键词。</summary>
    [ObservableProperty]
    private string _searchText = "";

    /// <summary>是否正在搜索。</summary>
    [ObservableProperty]
    private bool _isSearching;

    /// <summary>搜索状态提示（错误/统计）。</summary>
    [ObservableProperty]
    private string _searchStatus = "";

    /// <summary>搜索来源：All=全部, Local=仅本地, Bilibili=仅B站。</summary>
    [ObservableProperty]
    private SearchSource _searchSource = SearchSource.All;

    /// <summary>歌词面板是否展开（点击曲目封面切换）。</summary>
    [ObservableProperty]
    private bool _isLyricsOpen;

    /// <summary>内容区（标题行/状态条/列表）是否可见：设置页或歌词页打开时整体隐藏，避免下层内容透过半透明页面显示。</summary>
    public bool IsContentAreaVisible => !IsShowingSettings && !IsLyricsOpen;

    partial void OnIsShowingSettingsChanged(bool value) => OnPropertyChanged(nameof(IsContentAreaVisible));

    partial void OnIsLyricsOpenChanged(bool value) => OnPropertyChanged(nameof(IsContentAreaVisible));

    /// <summary>搜索结果（B 站 + 本地匹配）。</summary>
    public ObservableCollection<Track> SearchResults { get; } = [];

    /// <summary>内容区标题（跟随视图切换）。</summary>
    public string CurrentViewTitle => ViewMode switch
    {
        ViewMode.SearchResults => "搜索结果",
        ViewMode.Playlist => SelectedPlaylist?.Name ?? "歌单",
        ViewMode.Favorites => "我喜欢",
        ViewMode.Recent => "最近播放",
        _ => "全部音乐"
    };

    /// <summary>侧边栏高亮状态（供 DataTrigger 使用）。</summary>
    public bool IsAllTracksView => ViewMode == ViewMode.AllTracks;
    public bool IsSearchView => ViewMode == ViewMode.SearchResults;
    public bool IsPlaylistView => ViewMode == ViewMode.Playlist;
    public bool IsFavoritesView => ViewMode == ViewMode.Favorites;
    public bool IsRecentView => ViewMode == ViewMode.Recent;

    /// <summary>当前显示的曲目列表。</summary>
    public System.Collections.IList CurrentTracks => ViewMode switch
    {
        ViewMode.SearchResults => (System.Collections.IList)SearchResults,
        ViewMode.Playlist => (System.Collections.IList?)SelectedPlaylist?.Tracks ?? Library.Tracks,
        ViewMode.Favorites => (System.Collections.IList)Favorites,
        ViewMode.Recent => (System.Collections.IList)Recent,
        _ => Library.Tracks
    };

    public MainViewModel(PlaybackBarViewModel playbackBar, LibraryViewModel library, IPlaylistQueue queue,
        LyricViewModel lyrics, SettingsViewModel settings, ProviderRegistry registry, UserDataStore store,
        LocalFileProvider localFiles)
    {
        _playbackBar = playbackBar;
        _queue = queue;
        _registry = registry;
        Library = library;
        Lyrics = lyrics;
        Settings = settings;
        _store = store;
        _localFiles = localFiles;
        LoadUserData();
    }

    private void LoadUserData()
    {
        foreach (var p in _store.Playlists) UserPlaylists.Add(p);
        foreach (var t in _store.Favorites) Favorites.Add(t);
        foreach (var t in _store.Recent) Recent.Add(t);
        foreach (var h in _store.SearchHistory) SearchHistory.Add(h);
    }

    private void SaveUserData()
    {
        _store.Playlists = [.. UserPlaylists];
        _store.Favorites = [.. Favorites];
        _store.Recent = [.. Recent];
        _store.SearchHistory = [.. SearchHistory];
        _store.Save();
    }

    /// <summary>从当前可见列表播放指定曲目（设队列+播放+加载歌词），并记录最近播放。</summary>
    public async Task PlayTrackAsync(Track track)
    {
        var list = CurrentTracks;
        var index = list.IndexOf(track);
        var tracks = list.OfType<Track>().ToList();
        _queue.SetItems(tracks, Math.Max(0, index));
        await _playbackBar.LoadAndPlayAsync(track);
        await Lyrics.LoadLyricsAsync(track);
        RecordRecent(track);
    }

    /// <summary>播放当前列表全部曲目（从第一首开始）。</summary>
    public async Task PlayAllAsync()
    {
        var tracks = CurrentTracks.OfType<Track>().ToList();
        if (tracks.Count == 0) return;
        _queue.SetItems(tracks, 0);
        await _playbackBar.LoadAndPlayAsync(tracks[0]);
        await Lyrics.LoadLyricsAsync(tracks[0]);
        RecordRecent(tracks[0]);
    }

    private void RecordRecent(Track track)
    {
        var existing = Recent.FirstOrDefault(t => t.Id == track.Id && t.ProviderId == track.ProviderId);
        if (existing is not null) Recent.Remove(existing);
        Recent.Insert(0, track);
        while (Recent.Count > 100) Recent.RemoveAt(Recent.Count - 1);
        SaveUserData();
    }

    partial void OnViewModeChanged(ViewMode value)
    {
        OnPropertyChanged(nameof(CurrentTracks));
        OnPropertyChanged(nameof(CurrentViewTitle));
        OnPropertyChanged(nameof(IsAllTracksView));
        OnPropertyChanged(nameof(IsSearchView));
        OnPropertyChanged(nameof(IsPlaylistView));
        OnPropertyChanged(nameof(IsFavoritesView));
        OnPropertyChanged(nameof(IsRecentView));
    }

    partial void OnSelectedPlaylistChanged(Playlist? value)
    {
        OnPropertyChanged(nameof(CurrentTracks));
        OnPropertyChanged(nameof(CurrentViewTitle));
    }

    private void SetViewMode(ViewMode mode)
    {
        ViewMode = mode;
        IsShowingSearch = mode == ViewMode.SearchResults;
        IsShowingSettings = false;
    }

    [RelayCommand]
    private void ShowAllTracks() => SetViewMode(ViewMode.AllTracks);

    [RelayCommand]
    private void ShowFavorites() => SetViewMode(ViewMode.Favorites);

    [RelayCommand]
    private void ShowRecent() => SetViewMode(ViewMode.Recent);

    [RelayCommand]
    private void ShowSettings()
    {
        // 歌词遮罩盖在内容区之上，先关闭歌词再打开设置
        if (IsLyricsOpen) IsLyricsOpen = false;
        IsShowingSettings = true;
    }

    [RelayCommand]
    private void ShowSearch() => SetViewMode(ViewMode.SearchResults);

    [RelayCommand]
    private void ToggleLyrics() => IsLyricsOpen = !IsLyricsOpen;

    [RelayCommand]
    private async Task PlayAllCurrentAsync() => await PlayAllAsync();

    /// <summary>从历史记录重新搜索。</summary>
    [RelayCommand]
    private async Task SearchFromHistory(string? keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return;
        SearchText = keyword;
        await SearchAsync();
    }

    [RelayCommand]
    private void ClearSearchHistory()
    {
        SearchHistory.Clear();
        SaveUserData();
    }

    /// <summary>切换"我喜欢"收藏状态（本地与 B 站曲目均支持）。</summary>
    [RelayCommand]
    private void ToggleFavorite(Track? track)
    {
        if (track is null) return;
        var existing = Favorites.FirstOrDefault(t => t.Id == track.Id && t.ProviderId == track.ProviderId);
        if (existing is not null)
        {
            Favorites.Remove(existing);
            SearchStatus = $"已移出我喜欢: {track.Title}";
        }
        else
        {
            Favorites.Insert(0, track);
            SearchStatus = $"已加入我喜欢: {track.Title}";
        }
        SaveUserData();
    }

    [RelayCommand]
    private void CreatePlaylist()
    {
        var playlist = new Playlist { Name = $"歌单 {UserPlaylists.Count + 1}" };
        UserPlaylists.Add(playlist);
        SaveUserData();
        SelectPlaylist(playlist);
    }

    [RelayCommand]
    private void DeletePlaylist(Playlist playlist)
    {
        UserPlaylists.Remove(playlist);
        SaveUserData();
        if (SelectedPlaylist == playlist)
            SetViewMode(ViewMode.AllTracks);
    }

    [RelayCommand]
    private void SelectPlaylist(Playlist playlist)
    {
        SelectedPlaylist = playlist;
        SetViewMode(ViewMode.Playlist);
    }

    /// <summary>重命名歌单。</summary>
    public void RenamePlaylist(Playlist playlist, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return;
        playlist.Name = newName.Trim();
        SaveUserData();
        OnPropertyChanged(nameof(CurrentViewTitle));
    }

    /// <summary>批量导入本地音频文件到歌单。</summary>
    [RelayCommand]
    private void ImportTracksToPlaylist(Playlist? playlist)
    {
        if (playlist is null) return;
        var dialog = new OpenFileDialog
        {
            Title = "批量导入歌曲",
            Filter = "音频文件|*.mp3;*.flac;*.wav;*.m4a;*.aac;*.wma;*.ogg",
            Multiselect = true
        };
        if (dialog.ShowDialog() != true) return;

        var added = 0;
        foreach (var file in dialog.FileNames)
        {
            if (playlist.Tracks.Any(t => string.Equals(t.FilePath, file, StringComparison.OrdinalIgnoreCase)))
                continue; // 已在歌单中，跳过
            playlist.Tracks.Add(_localFiles.CreateTrackFromFile(file));
            added++;
        }
        SaveUserData();
        SearchStatus = $"已导入 {added} 首歌曲到「{playlist.Name}」";
    }

    /// <summary>把右键菜单锁定的曲目加入指定歌单。</summary>
    [RelayCommand]
    private void AddTrackToPlaylist(Playlist? playlist)
    {
        if (playlist is null || PendingMenuTrack is null) return;
        var track = PendingMenuTrack;
        if (!playlist.Tracks.Any(t => t.Id == track.Id && t.ProviderId == track.ProviderId))
            playlist.Tracks.Add(track);
        SaveUserData();
        PendingMenuTrack = null;
    }

    #region 在线曲目下载（B 站）

    /// <summary>右键播放指定曲目（设队列+播放+歌词+最近播放）。</summary>
    [RelayCommand]
    private async Task PlayTrack(Track? track)
    {
        if (track is null) return;
        await PlayTrackAsync(track);
    }

    /// <summary>下一首播放：把曲目插到当前播放曲目之后。</summary>
    [RelayCommand]
    private void PlayNext(Track? track)
    {
        if (track is null) return;
        _queue.InsertNext(track);
        SearchStatus = $"已设为下一首播放: {track.Title}";
    }

    /// <summary>从当前打开的歌单中移除曲目。</summary>
    [RelayCommand]
    private void RemoveTrackFromPlaylist(Track? track)
    {
        if (track is null || ViewMode != ViewMode.Playlist || SelectedPlaylist is null) return;
        SelectedPlaylist.Tracks.Remove(track);
        SaveUserData();
    }

    /// <summary>下载在线曲目（当前支持 B 站）为本地 .m4a 文件，方便离线播放。</summary>
    [RelayCommand]
    private async Task DownloadTrackAsync(Track? track)
    {
        if (track is null) return;

        if (!string.IsNullOrEmpty(track.FilePath) && File.Exists(track.FilePath))
        {
            MessageBox.Show("该曲目已是本地文件，无需下载", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_registry.Find(track.ProviderId) is not IOnlineMusicProvider online)
        {
            MessageBox.Show("该曲目没有对应的在线源，无法下载", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SearchStatus = $"⬇ 正在下载: {track.Title}";
        try
        {
            // 先缓冲到音频缓存，再复制为正规文件名保存到音乐目录
            var cachedPath = await online.ResolveToLocalAsync(track);

            var dir = GetDownloadDirectory();
            Directory.CreateDirectory(dir);
            var targetPath = UniquePath(Path.Combine(dir, SanitizeFileName($"{track.Artist} - {track.Title}") + ".m4a"));
            File.Copy(cachedPath, targetPath);

            // 直接加入本地音乐库，无需整库重扫
            if (Library.Tracks.OfType<Track>().All(t => !string.Equals(t.FilePath, targetPath, StringComparison.OrdinalIgnoreCase)))
            {
                Library.Tracks.Add(new Track
                {
                    Id = targetPath,
                    FilePath = targetPath,
                    Title = track.Title,
                    Artist = track.Artist,
                    Album = track.Album,
                    Duration = track.Duration,
                    ProviderId = "local-file"
                });
            }

            SearchStatus = $"✔ 已下载: {Path.GetFileName(targetPath)}（{dir}）";
        }
        catch (Exception ex)
        {
            SearchStatus = $"下载失败: {ex.Message}";
            MessageBox.Show($"下载失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>下载保存目录：优先用户设置的音乐目录，否则「音乐\AnMusic」。</summary>
    private string GetDownloadDirectory()
    {
        var musicDir = Settings.MusicDirectory;
        return !string.IsNullOrWhiteSpace(musicDir) && Path.IsPathRooted(musicDir)
            ? musicDir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "AnMusic");
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Trim();
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    #endregion

    /// <summary>执行搜索：B 站在线搜索 + 本地库过滤，结果合并展示。</summary>
    [RelayCommand]
    private async Task SearchAsync()
    {
        var keyword = SearchText?.Trim();
        if (string.IsNullOrEmpty(keyword))
            return;

        IsSearching = true;
        SearchStatus = "";
        SearchResults.Clear();
        SetViewMode(ViewMode.SearchResults);

        // 记录搜索历史（去重置顶，最多 20 条）
        var existing = SearchHistory.FirstOrDefault(h => h == keyword);
        if (existing is not null) SearchHistory.Remove(existing);
        SearchHistory.Insert(0, keyword);
        while (SearchHistory.Count > 20) SearchHistory.RemoveAt(SearchHistory.Count - 1);
        SaveUserData();

        try
        {
            var localCount = 0;
            var biliCount = 0;

            // 本地库匹配（All 或 Local 时执行）
            if (SearchSource is SearchSource.All or SearchSource.Local)
            {
                var localMatches = Library.Tracks.OfType<Track>()
                    .Where(t => t.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                             || t.Artist.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                foreach (var t in localMatches)
                    SearchResults.Add(t);
                localCount = localMatches.Count;
            }

            // B 站在线搜索（All 或 Bilibili 时执行）
            if (SearchSource is SearchSource.All or SearchSource.Bilibili)
            {
                var biliProvider = _registry.Find("bilibili") as IOnlineMusicProvider;
                if (biliProvider is not null)
                {
                    try
                    {
                        var online = await biliProvider.SearchAsync(keyword);
                        foreach (var t in online)
                            SearchResults.Add(t);
                        biliCount = online.Count;
                    }
                    catch (BilibiliApiException ex)
                    {
                        SearchStatus = ex.Message;
                    }
                }
            }

            // 已有错误信息时保留，否则显示统计
            if (string.IsNullOrEmpty(SearchStatus))
            {
                SearchStatus = SearchResults.Count == 0
                    ? "未找到匹配结果"
                    : $"共 {SearchResults.Count} 条（本地 {localCount} + B站 {biliCount}）";
            }
        }
        catch (Exception ex)
        {
            SearchStatus = $"搜索失败: {ex.Message}";
        }
        finally
        {
            IsSearching = false;
        }
    }
}
