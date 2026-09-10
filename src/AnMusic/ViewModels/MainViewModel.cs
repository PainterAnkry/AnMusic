using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using AnMusic.Models;
using AnMusic.Services.Playlist;
using AnMusic.Services.Providers;
using AnMusic.Services.Providers.Bilibili;
using AnMusic.Services.Providers.JsPlugin;
using AnMusic.Services.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace AnMusic.ViewModels;

/// <summary>搜索来源。</summary>
public enum SearchSource { Local, NetEase, QQMusic, Bilibili }

/// <summary>主内容区显示的视图。</summary>
public enum ViewMode { AllTracks, SearchResults, Playlist, Favorites, Recent, Ranking, ListeningStats, Radio }

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
    private readonly UserSettingsService _settingsService;
    private readonly Func<Views.DesktopLyricsWindow> _desktopLyricsWindowFactory;
    private readonly Func<Views.MiniPlayerWindow> _miniPlayerWindowFactory;

    /// <summary>右键菜单当前曲目（"添加到歌单"子菜单使用）。</summary>
    public Track? PendingMenuTrack { get; set; }

    #region 全局导航（前进/返回）

    /// <summary>导航历史条目：记录视图模式与选中歌单，支持前进/返回。</summary>
    private sealed class NavEntry
    {
        public ViewMode Mode { get; init; }
        public Playlist? Playlist { get; init; }
    }

    private readonly Stack<NavEntry> _backStack = new();
    private readonly Stack<NavEntry> _forwardStack = new();
    private bool _suppressNavRecord;

    public bool CanGoBack => _backStack.Count > 0;
    public bool CanGoForward => _forwardStack.Count > 0;

    #endregion

    public PlaybackBarViewModel PlaybackBar => _playbackBar;
    public LibraryViewModel Library { get; }
    public LyricViewModel Lyrics { get; }
    public SettingsViewModel Settings { get; }
    public ListenTogetherViewModel ListenTogether { get; }

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

    /// <summary>当前列表过滤关键词（仅过滤显示，不影响数据与播放队列；在标题栏过滤框输入）。</summary>
    [ObservableProperty]
    private string _listFilterText = "";

    /// <summary>是否正在搜索。</summary>
    [ObservableProperty]
    private bool _isSearching;

    /// <summary>搜索状态提示（错误/统计）。</summary>
    [ObservableProperty]
    private string _searchStatus = "";

    /// <summary>搜索来源：Local=仅本地, NetEase=网易云, QQMusic=QQ音乐, Bilibili=仅B站。</summary>
    [ObservableProperty]
    private SearchSource _searchSource = SearchSource.Local;

    /// <summary>歌词面板是否展开（点击曲目封面切换）。</summary>
    [ObservableProperty]
    private bool _isLyricsOpen;

    /// <summary>桌面歌词窗口是否打开（按钮态联动）。</summary>
    [ObservableProperty]
    private bool _isDesktopLyricsOpen;

    /// <summary>内容区（标题行/状态条/列表）是否可见：设置页或歌词页打开时整体隐藏，避免下层内容透过半透明页面显示。</summary>
    public bool IsContentAreaVisible => !IsShowingSettings && !IsLyricsOpen;

    /// <summary>当前播放曲目是否已收藏（我喜欢）。</summary>
    public bool IsCurrentFavorited =>
        _playbackBar.CurrentTrack is { } t &&
        Favorites.Any(f => f.Id == t.Id && f.ProviderId == t.ProviderId);

    #region 用户系统

    /// <summary>用户昵称（下拉资料面板内编辑，修改即保存）。</summary>
    public string UserNickname
    {
        get => _settingsService.Settings.UserNickname;
        set
        {
            _settingsService.Settings.UserNickname = value;
            _settingsService.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>用户头像路径。</summary>
    public string? UserAvatarPath
    {
        get => _settingsService.Settings.UserAvatarPath;
        set
        {
            _settingsService.Settings.UserAvatarPath = value;
            try { _settingsService.Save(); } catch { /* 保存失败不阻断 UI 刷新 */ }
            OnPropertyChanged();
        }
    }

    /// <summary>按累计听歌小时数计算等级信息（等级、本级起点、下一级所需）。</summary>
    private static (int Level, double Cur, double Next) GetLevelInfo(double hours) => hours switch
    {
        < 0.5 => (1, 0.0, 0.5),
        < 2 => (2, 0.5, 2.0),
        < 6 => (3, 2.0, 6.0),
        < 15 => (4, 6.0, 15.0),
        < 40 => (5, 15.0, 40.0),
        _ => (6, 40.0, double.PositiveInfinity)
    };

    /// <summary>用户等级（1-6），按累计听歌时长计算。</summary>
    public int UserLevel => GetLevelInfo(_store.TotalListeningSeconds / 3600.0).Level;

    /// <summary>下一级所需听歌时长（小时）。</summary>
    public string UserLevelProgress
    {
        get
        {
            var hours = _store.TotalListeningSeconds / 3600.0;
            var (_, cur, next) = GetLevelInfo(hours);
            if (double.IsPositiveInfinity(next)) return $"累计 {hours:F1}h · 已满级";
            return $"{hours:F1}h / {next}h";
        }
    }

    /// <summary>经验进度条值（0-100），升到下一级的百分比。</summary>
    public double UserLevelProgressValue
    {
        get
        {
            var hours = _store.TotalListeningSeconds / 3600.0;
            var (_, cur, next) = GetLevelInfo(hours);
            if (double.IsPositiveInfinity(next)) return 100;
            return Math.Clamp((hours - cur) / (next - cur) * 100, 0, 100);
        }
    }

    /// <summary>累计听歌时长文本。</summary>
    public string TotalListeningText
    {
        get
        {
            var ts = TimeSpan.FromSeconds(_store.TotalListeningSeconds);
            return ts.TotalHours >= 1 ? $"{ts.TotalHours:F1} 小时" : $"{ts.TotalMinutes:F0} 分钟";
        }
    }

    /// <summary>选择头像并打开自由裁剪窗口。</summary>
    [RelayCommand]
    private void ChangeAvatar()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择头像",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.webp"
        };
        if (dialog.ShowDialog() != true) return;

        var crop = new Views.AvatarCropWindow(dialog.FileName)
        {
            Owner = Application.Current.MainWindow
        };
        if (crop.ShowDialog() != true || string.IsNullOrEmpty(crop.CroppedImagePath)) return;

        UserAvatarPath = crop.CroppedImagePath;
        OnPropertyChanged(nameof(UserAvatarPath));
    }

    #endregion

    partial void OnIsShowingSettingsChanged(bool value)
    {
        // 两个页面互斥：打开设置时收起歌词遮罩
        if (value && IsLyricsOpen) IsLyricsOpen = false;
        OnPropertyChanged(nameof(IsContentAreaVisible));
        RefreshEmptyState();
    }

    partial void OnIsLyricsOpenChanged(bool value)
    {
        // 歌词遮罩是半透明的，若设置页仍显示会透过遮罩露出：打开歌词时同步收起设置页
        if (value && IsShowingSettings) IsShowingSettings = false;
        OnPropertyChanged(nameof(IsContentAreaVisible));
    }

    /// <summary>搜索结果（在线源分页时仅展示已“放行”的部分，池见 _searchPool）。</summary>
    public ObservableCollection<Track> SearchResults { get; } = [];

    /// <summary>搜索分页：每批展示条数（首次搜索显示 50 条，之后每次“加载更多”再放行 50 条）。</summary>
    private const int SearchBatchSize = 50;

    /// <summary>搜索分页：单批拉取的最大页数（页大小由各音源决定，防止页过小时请求过多）。</summary>
    private const int SearchMaxPagesPerBatch = 12;

    /// <summary>在线搜索结果全量池（按拉取顺序、去重后的全部结果）。</summary>
    private readonly List<Track> _searchPool = [];

    /// <summary>在线搜索结果去重键（ProviderId:Id）。</summary>
    private readonly HashSet<string> _seenSearchIds = [];

    /// <summary>当前已放行展示的池内条数（SearchResults 恒等于 _searchPool 前 N 条）。</summary>
    private int _searchShownCount;

    /// <summary>下一个待请求的页号（从 1 开始）。</summary>
    private int _searchNextPage = 1;

    /// <summary>当前关键词结果是否已取尽（无更多页可拉）。</summary>
    private bool _searchEnded = true;

    /// <summary>分页搜索中的在线音源（网易云/QQ 插件或 B 站原生源）。</summary>
    private IOnlineMusicProvider? _searchProvider;

    /// <summary>分页搜索发起时的来源快照（切换标签不影响进行中的分页）。</summary>
    private SearchSource _searchActiveSource;

    /// <summary>状态文案中的音源名（网易云/QQ音乐/B站）。</summary>
    private string _searchLabel = "";

    /// <summary>分页用关键词（加载更多时继续使用）。</summary>
    private string _searchKeyword = "";

    /// <summary>搜索会话号：新搜索/合集加载使旧的进行中拉取失效。</summary>
    private int _searchSession;

    /// <summary>是否显示“加载更多”按钮。</summary>
    [ObservableProperty]
    private bool _isSearchMoreVisible;

    /// <summary>“加载更多”按钮文案。</summary>
    [ObservableProperty]
    private string _searchMoreText = "加载更多";

    /// <summary>排行榜曲目（网易云榜单）。</summary>
    public ObservableCollection<Track> RankingTracks { get; } = [];

    /// <summary>听歌排行曲目（按播放时长降序）。</summary>
    public ObservableCollection<Track> ListeningStatsTracks { get; } = [];

    /// <summary>个性电台曲目（基于我喜欢推荐）。</summary>
    public ObservableCollection<Track> RadioTracks { get; } = [];

    /// <summary>内容区标题（跟随视图切换）。</summary>
    public string CurrentViewTitle => ViewMode switch
    {
        ViewMode.SearchResults => "搜索结果",
        ViewMode.Playlist => SelectedPlaylist?.Name ?? "歌单",
        ViewMode.Favorites => "我喜欢",
        ViewMode.Recent => "最近播放",
        ViewMode.Ranking => "排行榜",
        ViewMode.ListeningStats => "听歌排行",
        ViewMode.Radio => "个性电台",
        _ => "全部音乐"
    };

    /// <summary>侧边栏高亮状态（供 DataTrigger 使用）。</summary>
    public bool IsAllTracksView => ViewMode == ViewMode.AllTracks;
    public bool IsSearchView => ViewMode == ViewMode.SearchResults;
    public bool IsPlaylistView => ViewMode == ViewMode.Playlist;
    public bool IsFavoritesView => ViewMode == ViewMode.Favorites;
    public bool IsRecentView => ViewMode == ViewMode.Recent;
    public bool IsRankingView => ViewMode == ViewMode.Ranking;
    public bool IsListeningStatsView => ViewMode == ViewMode.ListeningStats;
    public bool IsRadioView => ViewMode == ViewMode.Radio;

    /// <summary>按时段的小问候（早安 / 中午好 / 晚安），显示在内容区标题右侧。</summary>
    [ObservableProperty]
    private string _greeting = "";

    /// <summary>按当前时间刷新问候语。</summary>
    public void RefreshGreeting()
    {
        Greeting = DateTime.Now.Hour switch
        {
            >= 5 and < 11 => "早安 ☀️ 新的一天，从一首歌开始",
            >= 11 and < 18 => "中午好 🌤 来点轻快的音乐吧",
            _ => "晚安 🌙 让音乐陪你放松一下"
        };
    }

    /// <summary>空列表状态引导文案（根据视图模式动态切换）。</summary>
    [ObservableProperty]
    private string _emptyStateText = "音乐库还是空的\n点击右上角「📂 打开文件夹」选择你的音乐目录";

    /// <summary>是否显示空状态引导（列表为空且未在加载时显示）。</summary>
    [ObservableProperty]
    private bool _isEmptyStateVisible;

    partial void OnIsSearchingChanged(bool value) => RefreshEmptyState();

    /// <summary>根据当前视图与数据状态刷新空列表引导文案/可见性。</summary>
    public void RefreshEmptyState()
    {
        // 集合可能被后台线程填充（插件封面/异步搜索），统一切回 UI 线程再更新绑定
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(RefreshEmptyState);
            return;
        }

        var empty = (CurrentTracks?.Count ?? 0) == 0;
        var loading = ViewMode == ViewMode.AllTracks && Library.IsLoading;

        EmptyStateText = ViewMode switch
        {
            ViewMode.AllTracks => loading
                ? "正在扫描音乐文件夹…"
                : "音乐库还是空的\n点击右上角「📂 打开文件夹」选择你的音乐目录",
            ViewMode.SearchResults => IsSearching
                ? "正在搜索…"
                : "输入关键词开始搜索\n或从左侧选择不同音源",
            ViewMode.Favorites => "还没有收藏歌曲\n播放时点 ♡ 即可加入喜欢",
            ViewMode.Recent => "还没有播放记录\n播放歌曲后将自动记录在此",
            ViewMode.Playlist => "这个歌单是空的\n右键歌曲选择「加入歌单」或批量导入",
            ViewMode.Ranking => "正在加载排行榜…",
            ViewMode.ListeningStats => "暂无听歌统计\n多听几首歌后这里会展示时长排行",
            ViewMode.Radio => "正在生成个性电台…",
            _ => ""
        };

        IsEmptyStateVisible = empty && !IsShowingSettings && !string.IsNullOrEmpty(EmptyStateText);
    }


    /// <summary>当前显示的曲目列表。</summary>
    public System.Collections.IList CurrentTracks => ViewMode switch
    {
        ViewMode.SearchResults => (System.Collections.IList)SearchResults,
        ViewMode.Playlist => (System.Collections.IList?)SelectedPlaylist?.Tracks ?? Library.Tracks,
        ViewMode.Favorites => (System.Collections.IList)Favorites,
        ViewMode.Recent => (System.Collections.IList)Recent,
        ViewMode.Ranking => (System.Collections.IList)RankingTracks,
        ViewMode.ListeningStats => (System.Collections.IList)ListeningStatsTracks,
        ViewMode.Radio => (System.Collections.IList)RadioTracks,
        _ => Library.Tracks
    };

    public MainViewModel(PlaybackBarViewModel playbackBar, LibraryViewModel library, IPlaylistQueue queue,
        LyricViewModel lyrics, SettingsViewModel settings, ProviderRegistry registry, UserDataStore store,
        LocalFileProvider localFiles, UserSettingsService settingsService,
        Func<Views.DesktopLyricsWindow> desktopLyricsWindowFactory,
        Func<Views.MiniPlayerWindow> miniPlayerWindowFactory,
        ListenTogetherViewModel listenTogether)
    {
        _playbackBar = playbackBar;
        _queue = queue;
        _registry = registry;
        Library = library;
        Lyrics = lyrics;
        Settings = settings;
        ListenTogether = listenTogether;
        _store = store;
        _localFiles = localFiles;
        _settingsService = settingsService;
        _desktopLyricsWindowFactory = desktopLyricsWindowFactory;
        _miniPlayerWindowFactory = miniPlayerWindowFactory;
        LoadUserData();

        // 空状态响应各数据集合变化
        foreach (var col in new System.Collections.IList[] { Library.Tracks, Favorites, Recent, RankingTracks, ListeningStatsTracks, RadioTracks })
        {
            if (col is System.Collections.Specialized.INotifyCollectionChanged ncc)
                ncc.CollectionChanged += (_, _) => RefreshEmptyState();
        }
        // 音乐库扫描状态变化时刷新空引导
        Library.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(LibraryViewModel.IsLoading)) RefreshEmptyState(); };
        // ViewMode 切换 / 搜索状态变化由 partial methods 联动 RefreshEmptyState

        // 当前曲目变化或收藏列表变化时，刷新爱心按钮状态
        _playbackBar.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlaybackBarViewModel.CurrentTrack))
                OnPropertyChanged(nameof(IsCurrentFavorited));
            else if (e.PropertyName == nameof(PlaybackBarViewModel.PositionSeconds))
                RecordPlayTime();
        };
        Favorites.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsCurrentFavorited));

        // 初始视图可能是空音乐库，构造完成后立即算一次空状态
        RefreshEmptyState();

        // 按时段问候：启动即算一次，之后每小时边界附近自动刷新（跨时段不用重启）
        RefreshGreeting();
        _greetingTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(5)
        };
        _greetingTimer.Tick += (_, _) => RefreshGreeting();
        _greetingTimer.Start();
    }

    private readonly System.Windows.Threading.DispatcherTimer _greetingTimer;

    private double _lastRecordedPosition;

    /// <summary>记录当前曲目的播放时长（每次 PositionSeconds 变化时累加差值）。</summary>
    private void RecordPlayTime()
    {
        if (!_playbackBar.IsPlaying || _playbackBar.CurrentTrack is not { } track) return;
        var delta = _playbackBar.PositionSeconds - _lastRecordedPosition;
        if (delta is > 0 and < 5) // 过滤异常跳转（如拖动进度条）
        {
            var prevLevel = UserLevel;
            var key = $"{track.ProviderId}:{track.Id}";
            _store.PlayStats.TryGetValue(key, out var total);
            _store.PlayStats[key] = total + delta;
            _store.TotalListeningSeconds += delta;
            // 等级变化时通知 UI 更新（资料面板进度条每次播放都刷新）
            if (UserLevel != prevLevel)
            {
                OnPropertyChanged(nameof(UserLevel));
                OnPropertyChanged(nameof(UserLevelProgress));
            }
            OnPropertyChanged(nameof(UserLevelProgressValue));
            OnPropertyChanged(nameof(TotalListeningText));
            // 每 30 秒保存一次，避免频繁写盘
            if ((int)(_store.TotalListeningSeconds / 30) != (int)((_store.TotalListeningSeconds - delta) / 30))
                _store.Save();
        }
        _lastRecordedPosition = _playbackBar.PositionSeconds;
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

    /// <summary>从当前可见列表播放指定曲目（设队列+播放+加载歌词），并记录最近播放。
    /// orderedSource：双击行时传入当前视图（含排序/过滤）顺序，保证播放队列与所见一致；为空则用 CurrentTracks。</summary>
    public async Task PlayTrackAsync(Track track, IReadOnlyList<Track>? orderedSource = null)
    {
        var list = orderedSource ?? CurrentTracks.OfType<Track>().ToList();
        var index = -1;
        for (var i = 0; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i], track) ||
                (list[i].Id == track.Id && list[i].ProviderId == track.ProviderId))
            {
                index = i;
                break;
            }
        }
        _queue.SetItems(list, Math.Max(0, index));
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
        // 电台视图启用队列自动续播，离开电台视图时取消
        _playbackBar.AutoRefillHandler = value == ViewMode.Radio ? RadioRefillAsync : null;

        UpdateSearchMoreState();
        OnPropertyChanged(nameof(CurrentTracks));
        OnPropertyChanged(nameof(CurrentViewTitle));
        OnPropertyChanged(nameof(IsAllTracksView));
        OnPropertyChanged(nameof(IsSearchView));
        OnPropertyChanged(nameof(IsPlaylistView));
        OnPropertyChanged(nameof(IsFavoritesView));
        OnPropertyChanged(nameof(IsRecentView));
        OnPropertyChanged(nameof(IsRankingView));
        OnPropertyChanged(nameof(IsListeningStatsView));
        OnPropertyChanged(nameof(IsRadioView));
        RefreshEmptyState();
    }

    partial void OnSelectedPlaylistChanged(Playlist? value)
    {
        OnPropertyChanged(nameof(CurrentTracks));
        OnPropertyChanged(nameof(CurrentViewTitle));
        RefreshEmptyState();
    }

    /// <summary>切换视图模式，并记录导航历史（前进/返回）。</summary>
    private void SetViewMode(ViewMode mode, Playlist? playlist = null)
    {
        // 记录当前状态到返回栈（前进/返回导航时不重复记录）
        if (!_suppressNavRecord)
        {
            _backStack.Push(new NavEntry { Mode = ViewMode, Playlist = SelectedPlaylist });
            _forwardStack.Clear();
            OnPropertyChanged(nameof(CanGoBack));
            OnPropertyChanged(nameof(CanGoForward));
        }

        ViewMode = mode;
        if (mode == ViewMode.Playlist)
            SelectedPlaylist = playlist;
        IsShowingSearch = mode == ViewMode.SearchResults;
        IsShowingSettings = false;
        // 切换视图时清掉上一视图遗留的状态文案（如“飙升榜共 99 首(内置榜单)”串到歌单页）；
        // 各视图自身的加载结果文案在其切换完成后另行写入
        SearchStatus = "";
    }

    [RelayCommand]
    private void ShowAllTracks() => SetViewMode(ViewMode.AllTracks);

    [RelayCommand]
    private void ShowFavorites() => SetViewMode(ViewMode.Favorites);

    [RelayCommand]
    private void ShowRecent() => SetViewMode(ViewMode.Recent);

    /// <summary>按关键词（匹配 Id 或 DisplayName）查找已加载的插件音源。</summary>
    private JsPluginProvider? FindPluginSource(params string[] keywords) =>
        _registry.OnlineMusicProviders.OfType<JsPluginProvider>()
            .FirstOrDefault(p => keywords.Any(k =>
                p.Id.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                p.DisplayName.Contains(k, StringComparison.OrdinalIgnoreCase)));

    /// <summary>榜单子列表名（由插件源 getTopLists 动态加载，替代内置网易云 API）。</summary>
    public ObservableCollection<string> RankingBoardNames { get; } = [];

    /// <summary>榜单 ID 列表（与 RankingBoardNames 顺序对应）。</summary>
    private List<(string Id, string Title)> _rankingBoards = [];

    /// <summary>当前选中的子榜单名。</summary>
    [ObservableProperty]
    private string _selectedRankingBoard = "";

    private readonly Dictionary<string, List<Track>> _rankingCache = new();

    /// <summary>内置网易云官方榜单（插件榜单为 HTML 抓取型受限时的兜底；播放仍走网易插件解析）。</summary>
    private static readonly IReadOnlyList<(string Id, string Title)> FallbackBoards =
    [
        ("19723756", "飙升榜"),
        ("3779629", "新歌榜"),
        ("3778678", "热歌榜"),
        ("2884035", "原创榜")
    ];

    /// <summary>排行榜：从插件源动态加载榜单列表（优先网易云插件，其次 QQ 插件，受限时回退内置榜单）。</summary>
    [RelayCommand]
    private async Task ShowRankingAsync()
    {
        SetViewMode(ViewMode.Ranking);

        // 榜单列表已加载过：仅补载当前榜单内容
        if (_rankingBoards.Count > 0)
        {
            if (RankingTracks.Count == 0) await LoadRankingBoardAsync(SelectedRankingBoard);
            return;
        }

        var plugin = FindPluginSource("netease", "wy", "网易")
                     ?? FindPluginSource("qqmusic", "qq", "酷");
        if (plugin is null)
        {
            UseFallbackBoards("排行榜暂不可用（插件源未加载），已切换内置榜单");
            return;
        }

        SearchStatus = "正在加载榜单列表...";
        try
        {
            var boards = await plugin.GetTopListsAsync();
            if (boards.Count == 0) throw new InvalidOperationException("插件源未提供排行榜");

            _rankingBoards = boards.Select(b => (b.Id, b.Title)).ToList();
            RankingBoardNames.Clear();
            foreach (var b in _rankingBoards)
                RankingBoardNames.Add(b.Title);

            SelectedRankingBoard = _rankingBoards[0].Title; // 触发首个榜单加载
        }
        catch (Exception ex)
        {
            UseFallbackBoards($"插件榜单受限（{ex.Message}），已切换内置榜单");
        }
    }

    /// <summary>启用内置网易云榜单兜底。</summary>
    private void UseFallbackBoards(string status)
    {
        _rankingBoards = FallbackBoards.Select(b => (b.Id, b.Title)).ToList();
        RankingBoardNames.Clear();
        foreach (var b in _rankingBoards)
            RankingBoardNames.Add(b.Title);
        SearchStatus = status;

        var first = _rankingBoards[0].Title;
        if (SelectedRankingBoard == first)
            _ = LoadRankingBoardAsync(first); // 同名不会触发属性变更，手动加载
        else
            SelectedRankingBoard = first;
    }

    partial void OnSelectedRankingBoardChanged(string value)
    {
        if (!string.IsNullOrEmpty(value)) _ = LoadRankingBoardAsync(value);
    }

    /// <summary>加载指定子榜单（插件 getTopListDetail，带缓存）。</summary>
    private async Task LoadRankingBoardAsync(string boardName)
    {
        if (string.IsNullOrEmpty(boardName)) return;

        if (_rankingCache.TryGetValue(boardName, out var cached))
        {
            RankingTracks.Clear();
            foreach (var t in cached) RankingTracks.Add(t);
            SearchStatus = $"{boardName} 共 {cached.Count} 首";
            return;
        }

        var board = _rankingBoards.FirstOrDefault(b => b.Title == boardName);
        if (board.Id is null) return;

        var plugin = FindPluginSource("netease", "wy", "网易")
                     ?? FindPluginSource("qqmusic", "qq", "酷");
        if (plugin is null)
        {
            SearchStatus = "排行榜暂不可用（插件源未加载）";
            return;
        }

        SearchStatus = $"正在加载 {boardName}...";
        try
        {
            var tracks = await plugin.GetTopListDetailAsync(board.Id);
            if (tracks.Count == 0) throw new InvalidOperationException("插件未返回榜单内容");

            _rankingCache[boardName] = [.. tracks];
            RankingTracks.Clear();
            foreach (var t in tracks) RankingTracks.Add(t);
            SearchStatus = $"{boardName} 共 {tracks.Count} 首";
        }
        catch (Exception)
        {
            // 插件榜单详情受限（HTML 抓取型）：回退内置网易云榜单 API，播放仍走网易插件解析
            try
            {
                var tracks = await LoadFallbackBoardDetailAsync(board.Id, plugin.Id);
                _rankingCache[boardName] = [.. tracks];
                RankingTracks.Clear();
                foreach (var t in tracks) RankingTracks.Add(t);
                SearchStatus = $"{boardName} 共 {tracks.Count} 首（内置榜单）";
            }
            catch (Exception ex2)
            {
                SearchStatus = $"加载{boardName}失败: {ex2.Message}";
            }
        }
    }

    /// <summary>内置网易云榜单详情 API（/api/v6/playlist/detail），曲目 ProviderId 指向网易插件以便播放解析。</summary>
    private static readonly System.Net.Http.HttpClient _boardHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    private async Task<List<Track>> LoadFallbackBoardDetailAsync(string boardId, string neteasePluginId)
    {
        using var resp = await _boardHttp.GetAsync($"https://music.163.com/api/v6/playlist/detail?id={boardId}&n=100");
        resp.EnsureSuccessStatusCode();
        using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());

        if (!doc.RootElement.TryGetProperty("playlist", out var pl) ||
            !pl.TryGetProperty("tracks", out var tracks) ||
            tracks.ValueKind != System.Text.Json.JsonValueKind.Array)
            throw new InvalidOperationException("内置榜单接口返回异常");

        var list = new List<Track>();
        foreach (var t in tracks.EnumerateArray())
        {
            string Str(string n) => t.TryGetProperty(n, out var e) && e.ValueKind == System.Text.Json.JsonValueKind.String
                ? e.GetString() ?? "" : "";
            var id = t.TryGetProperty("id", out var idEl) ? idEl.ToString() : "";
            if (string.IsNullOrEmpty(id) || id == "null") continue;

            var artist = "";
            if (t.TryGetProperty("ar", out var ar) && ar.ValueKind == System.Text.Json.JsonValueKind.Array)
                artist = string.Join("/", ar.EnumerateArray()
                    .Select(a => a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "")
                    .Where(n => n.Length > 0));

            string album = "", cover = "";
            if (t.TryGetProperty("al", out var al) && al.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                album = al.TryGetProperty("name", out var an) ? an.GetString() ?? "" : "";
                cover = al.TryGetProperty("picUrl", out var pc) ? pc.GetString() ?? "" : "";
            }

            var duration = t.TryGetProperty("dt", out var dt) && dt.ValueKind == System.Text.Json.JsonValueKind.Number
                ? dt.GetDouble() / 1000.0 : 0;

            list.Add(new Track
            {
                Id = id,
                Title = Str("name") is { Length: > 0 } n2 ? n2 : "未知标题",
                Artist = artist is { Length: > 0 } ? artist : "未知艺术家",
                Album = album,
                Duration = TimeSpan.FromSeconds(duration),
                FilePath = "",
                ProviderId = neteasePluginId,
                CoverUrl = cover
            });
        }
        LoadPluginCovers(list);
        return list;
    }

    /// <summary>按 ProviderId 找到对应插件并后台下载封面缓存。</summary>
    private void LoadPluginCovers(List<Track> tracks)
    {
        if (tracks.Count == 0) return;
        var provider = _registry.MusicProviders.FirstOrDefault(p => p.Id == tracks[0].ProviderId);
        if (provider is Services.Providers.JsPlugin.JsPluginProvider jp)
            jp.PreloadCovers(tracks);
    }

    /// <summary>听歌排行：按播放时长统计本地及在线曲目排名。</summary>
    [RelayCommand]
    private void ShowListeningStats()
    {
        SetViewMode(ViewMode.ListeningStats);
        ListeningStatsTracks.Clear();

        // 从所有已知曲目中匹配播放统计，按时长降序
        var allTracks = Library.Tracks.Concat(Favorites).Concat(Recent)
            .GroupBy(t => $"{t.ProviderId}:{t.Id}")
            .Select(g => g.First())
            .ToList();

        var ranked = allTracks
            .Select(t => new
            {
                Track = t,
                Seconds = _store.PlayStats.TryGetValue($"{t.ProviderId}:{t.Id}", out var s) ? s : 0
            })
            .Where(x => x.Seconds > 0)
            .OrderByDescending(x => x.Seconds)
            .Take(100)
            .ToList();

        foreach (var x in ranked)
            ListeningStatsTracks.Add(x.Track);

        SearchStatus = ranked.Count > 0
            ? $"共 {ranked.Count} 首，累计 {TimeSpan.FromSeconds(_store.TotalListeningSeconds):hh\\:mm\\:ss}"
            : "暂无听歌统计";
    }

    /// <summary>个性电台：基于我喜欢随机生成推荐（不限数量），点击进入自动持续播放。</summary>
    [RelayCommand]
    private async Task ShowRadioAsync()
    {
        SetViewMode(ViewMode.Radio);

        var tracks = await GenerateRadioTracksAsync();
        RadioTracks.Clear();
        foreach (var t in tracks) RadioTracks.Add(t);
        SearchStatus = $"个性电台已生成 {RadioTracks.Count} 首，自动播放中";

        // 点击进入即自动播放
        if (RadioTracks.Count > 0)
        {
            _queue.SetItems([.. RadioTracks], 0);
            await _playbackBar.LoadAndPlayAsync(RadioTracks[0]);
            await Lyrics.LoadLyricsAsync(RadioTracks[0]);
            RecordRecent(RadioTracks[0]);
        }
    }

    /// <summary>
    /// 生成电台推荐（个性化算法）：以"我喜欢 + 最近播放"为种子统计艺术家偏好，
    /// 按权重通过插件源搜索相似歌曲、本地曲库同艺术家补充；无种子或在线失败时回退随机榜单。
    /// </summary>
    private async Task<List<Track>> GenerateRadioTracksAsync()
    {
        var rng = new Random();
        var result = new List<Track>();
        var recentKeys = Recent.Take(15).Select(t => $"{t.ProviderId}:{t.Id}").ToHashSet();

        void AddRange(IEnumerable<Track> source)
        {
            foreach (var t in source)
            {
                var key = $"{t.ProviderId}:{t.Id}";
                // 去重 + 避免刚听过的歌曲重复推送
                if (!recentKeys.Contains(key) && result.All(x => x.Id != t.Id || x.ProviderId != t.ProviderId))
                    result.Add(t);
            }
        }

        // ---- 1. 我喜欢优先入列 ----
        AddRange(Favorites.OrderBy(_ => rng.Next()));

        // ---- 2. 种子曲目（我喜欢 + 最近播放）统计艺术家偏好权重 ----
        var seeds = Favorites.Concat(Recent).ToList();
        var artistWeights = seeds
            .Select(t => SplitFirstArtist(t.Artist))
            .Where(a => a.Length >= 2)
            .GroupBy(a => a, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Take(8)
            .Select(g => (Artist: g.Key, Weight: g.Count()))
            .ToList();

        // ---- 3. 本地曲库：偏好艺术家曲目加权在前，其余洗牌少量补充 ----
        var lib = Library.Tracks.OfType<Track>().ToList();
        var matched = lib.Where(t =>
                artistWeights.Any(w => t.Artist?.Contains(w.Artist, StringComparison.OrdinalIgnoreCase) == true))
            .OrderByDescending(t => artistWeights
                .Where(w => t.Artist?.Contains(w.Artist, StringComparison.OrdinalIgnoreCase) == true)
                .Sum(w => w.Weight))
            .ThenBy(_ => rng.Next());
        AddRange(matched);
        AddRange(lib.Where(t => !result.Any(r => r.Id == t.Id && r.ProviderId == t.ProviderId))
            .OrderBy(_ => rng.Next()).Take(10));

        // ---- 4. 在线相似推荐：按艺术家权重并行搜索（网易插件曲库最全） ----
        try
        {
            var plugin = FindPluginSource("netease", "wy", "网易")
                         ?? FindPluginSource("qqmusic", "qq", "酷");
            if (plugin is not null && artistWeights.Count > 0)
            {
                var weights = artistWeights;
                var lists = await Task.WhenAll(weights.Select(async w =>
                {
                    try
                    {
                        var res = await plugin.SearchAsync(w.Artist);
                        return (Weight: w.Weight, Tracks: res.Take(12).ToList());
                    }
                    catch
                    {
                        return (Weight: w.Weight, Tracks: new List<Track>());
                    }
                }));

                // 加权随机排序：权重越高（听得越多）越靠前，同时保持随机性
                var pooled = lists.SelectMany(x => x.Tracks, (x, t) => (Track: t, x.Weight))
                    .OrderByDescending(x => x.Weight * rng.NextDouble())
                    .Select(x => x.Track);
                AddRange(pooled);
            }
            else
            {
                await AddOnlineBoardTracksAsync(rng, AddRange);
            }
        }
        catch
        {
            // 在线推荐失败不影响本地电台内容
        }

        return result;
    }

    /// <summary>取首个主艺术家（"A/B" 形式取 A；未知艺术家返回空）。</summary>
    private static string SplitFirstArtist(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist) || artist.Contains("未知")) return "";
        return artist.Split('/', '、', ',', '，')[0].Trim();
    }

    /// <summary>兜底在线推荐：随机拉取一个插件源榜单取 50 首。</summary>
    private async Task AddOnlineBoardTracksAsync(Random rng, Action<IEnumerable<Track>> addRange)
    {
        try
        {
            var plugin = FindPluginSource("netease", "wy", "网易")
                         ?? FindPluginSource("qqmusic", "qq", "酷");
            if (plugin is not null)
            {
                var boards = await plugin.GetTopListsAsync();
                if (boards.Count > 0)
                {
                    var board = boards[rng.Next(boards.Count)];
                    var online = await plugin.GetTopListDetailAsync(board.Id);
                    addRange(online.OrderBy(_ => rng.Next()).Take(50));
                }
            }
        }
        catch
        {
            // 在线推荐失败不影响本地电台内容
        }
    }

    /// <summary>电台队列播完后的自动续播：把剩余推荐追加入队（我的喜欢循环洗牌），返回是否成功。</summary>
    private Task<bool> RadioRefillAsync()
    {
        if (ViewMode != ViewMode.Radio) return Task.FromResult(false);

        // 从我喜欢重新洗牌取一批没在当前队列里的曲目；全部听过则重新洗牌全部
        var rng = new Random();
        var inQueue = _queue.Queue.Select(t => $"{t.ProviderId}:{t.Id}").ToHashSet();
        var candidates = Favorites
            .Where(t => !inQueue.Contains($"{t.ProviderId}:{t.Id}"))
            .OrderBy(_ => rng.Next())
            .Take(30)
            .ToList();
        if (candidates.Count == 0 && Favorites.Count > 0)
            candidates = Favorites.OrderBy(_ => rng.Next()).Take(30).ToList();

        foreach (var t in candidates)
        {
            RadioTracks.Add(t);
        }
        _queue.Append(candidates);
        return Task.FromResult(candidates.Count > 0);
    }

    [RelayCommand]
    private void ShowSettings()
    {
        // 歌词遮罩盖在内容区之上，先关闭歌词再打开设置
        if (IsLyricsOpen) IsLyricsOpen = false;
        SearchStatus = ""; // 设置遮罩下的内容状态不再保留
        IsShowingSettings = true;
    }

    [RelayCommand]
    private void ShowSearch() => SetViewMode(ViewMode.SearchResults);

    /// <summary>全局返回：回到上一个视图。</summary>
    [RelayCommand]
    private void GoBack()
    {
        if (_backStack.Count == 0) return;
        _forwardStack.Push(new NavEntry { Mode = ViewMode, Playlist = SelectedPlaylist });
        var prev = _backStack.Pop();
        _suppressNavRecord = true;
        try
        {
            ViewMode = prev.Mode;
            SelectedPlaylist = prev.Playlist;
            IsShowingSearch = prev.Mode == ViewMode.SearchResults;
            IsShowingSettings = false;
            SearchStatus = ""; // 导航后不残留上一视图的状态文案
        }
        finally
        {
            _suppressNavRecord = false;
        }
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }

    /// <summary>全局前进：前往下一个视图。</summary>
    [RelayCommand]
    private void GoForward()
    {
        if (_forwardStack.Count == 0) return;
        _backStack.Push(new NavEntry { Mode = ViewMode, Playlist = SelectedPlaylist });
        var next = _forwardStack.Pop();
        _suppressNavRecord = true;
        try
        {
            ViewMode = next.Mode;
            SelectedPlaylist = next.Playlist;
            IsShowingSearch = next.Mode == ViewMode.SearchResults;
            IsShowingSettings = false;
            SearchStatus = ""; // 导航后不残留上一视图的状态文案
        }
        finally
        {
            _suppressNavRecord = false;
        }
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }

    [RelayCommand]
    private void ToggleLyrics() => IsLyricsOpen = !IsLyricsOpen;

    /// <summary>桌面歌词窗口实例（运行时按需创建，关闭后置空）。</summary>
    private Views.DesktopLyricsWindow? _desktopLyricsWindow;

    /// <summary>切换桌面歌词窗口：未开 → 创建并显示；已开 → 关闭。</summary>
    [RelayCommand]
    private void ToggleDesktopLyrics()
    {
        if (_desktopLyricsWindow is { } w && w.IsLoaded)
        {
            w.Close();
            _desktopLyricsWindow = null;
            IsDesktopLyricsOpen = false;
            return;
        }

        _desktopLyricsWindow = _desktopLyricsWindowFactory();
        _desktopLyricsWindow.Closed += (_, _) =>
        {
            _desktopLyricsWindow = null;
            IsDesktopLyricsOpen = false;
        };
        _desktopLyricsWindow.Show();
        IsDesktopLyricsOpen = true;
    }

    /// <summary>桌面歌词窗口被外部关闭（如双击）时由窗口回调，同步按钮状态。</summary>
    public void NotifyDesktopLyricsClosed()
    {
        _desktopLyricsWindow = null;
        IsDesktopLyricsOpen = false;
    }

    /// <summary>迷你悬浮卡片播放器窗口实例（运行时按需创建，关闭后置空；收起时仅隐藏，实例保留）。</summary>
    private Views.MiniPlayerWindow? _miniPlayerWindow;

    /// <summary>迷你卡片是否正在显示（供菜单项勾选状态）。</summary>
    [ObservableProperty]
    private bool _isMiniPlayerOpen;

    /// <summary>主窗口是否因开启迷你卡片而被自动最小化（仅此种情况才在关闭卡片时复原，避免打扰用户原本的最小化）。</summary>
    private bool _mainWindowMinimizedForCard;

    /// <summary>切换迷你悬浮卡片：已显示 → 收起；已收起 → 复原；从未打开 → 创建并显示。
    /// 卡片显示期间默认把主窗口最小化，卡片收起/关闭时再复原。</summary>
    [RelayCommand]
    private void ToggleMiniPlayer()
    {
        switch (_miniPlayerWindow)
        {
            case { IsVisible: true } visible:
                visible.Hide();
                IsMiniPlayerOpen = false;
                RestoreMainWindowForCard();
                return;
            case { } hidden: // 收起过：直接复原，位置状态仍在
                hidden.Show();
                hidden.Activate();
                IsMiniPlayerOpen = true;
                MinimizeMainWindowForCard();
                return;
        }

        _miniPlayerWindow = _miniPlayerWindowFactory();
        _miniPlayerWindow.Closed += (_, _) =>
        {
            _miniPlayerWindow = null;
            IsMiniPlayerOpen = false;
            RestoreMainWindowForCard();
        };
        _miniPlayerWindow.Show();
        IsMiniPlayerOpen = true;
        MinimizeMainWindowForCard();
    }

    /// <summary>开启卡片时最小化主窗口（记忆是否由本功能触发）。</summary>
    private void MinimizeMainWindowForCard()
    {
        if (Application.Current?.MainWindow is not { } main) return;
        if (!main.IsVisible) return;                            // 已收进托盘：保持原样
        if (main.WindowState == WindowState.Minimized) return;  // 本来就是最小化：退出卡片时不应擅自弹出
        main.WindowState = WindowState.Minimized;
        _mainWindowMinimizedForCard = true;
    }

    /// <summary>卡片收起/关闭时复原主窗口（仅复原由卡片触发的那次最小化）。</summary>
    private void RestoreMainWindowForCard()
    {
        if (!_mainWindowMinimizedForCard) return;
        _mainWindowMinimizedForCard = false;
        if (Application.Current?.MainWindow is not { } main) return;
        if (!main.IsVisible) return;                            // 期间被收进托盘：不要擅自弹出
        if (main.WindowState != WindowState.Minimized) return;
        main.WindowState = WindowState.Normal;
        main.Activate();
    }

    /// <summary>迷你卡片“收起”按钮回调：同步菜单勾选状态并复原主窗口。</summary>
    public void NotifyMiniPlayerHidden()
    {
        IsMiniPlayerOpen = false;
        RestoreMainWindowForCard();
    }

    /// <summary>迷你卡片被关闭（✕）时回调：置空实例并复原主窗口。</summary>
    public void NotifyMiniPlayerClosed()
    {
        _miniPlayerWindow = null;
        IsMiniPlayerOpen = false;
        RestoreMainWindowForCard();
    }

    // 分享当前歌曲相关实现已拆分到 MainViewModel.Share.cs（partial class，成员可直接访问）

    // 快捷键动作分发相关实现已拆分到 MainViewModel.Shortcuts.cs（partial class，成员可直接访问）

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

    /// <summary>以指定关键词搜索（供歌词页点击歌手/专辑跳转使用）。</summary>
    public async Task SearchForTextAsync(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        SearchText = text;
        await SearchAsync();
    }

    /// <summary>打开 B 站合集（专辑），展示合集内所有视频。</summary>
    [RelayCommand]
    private async Task OpenBilibiliCollectionAsync(string? seasonId)
    {
        if (string.IsNullOrWhiteSpace(seasonId)) return;
        if (_registry.Find("bilibili") is not BilibiliMusicProvider bili) return;

        // 合集为一次性全量视图：作废旧搜索分页会话，且不提供“加载更多”
        _searchSession++;
        var session = _searchSession;
        _searchProvider = null;
        _searchEnded = true;
        _searchPool.Clear();
        _seenSearchIds.Clear();
        _searchShownCount = 0;
        IsSearchMoreVisible = false;
        SearchResults.Clear();
        SetViewMode(ViewMode.SearchResults);

        IsSearching = true;
        SearchStatus = $"正在加载 B 站合集 {seasonId}...";

        try
        {
            var tracks = await bili.GetCollectionVideosAsync(seasonId);
            if (session != _searchSession) return;
            foreach (var t in tracks)
            {
                _searchPool.Add(t);
                _seenSearchIds.Add($"{t.ProviderId}:{t.Id}");
            }
            SyncSearchShown(tracks.Count);
            SearchStatus = tracks.Count == 0
                ? "合集为空或加载失败"
                : $"合集共 {tracks.Count} 个视频";
        }
        catch (Exception ex)
        {
            if (session == _searchSession) SearchStatus = $"加载合集失败: {ex.Message}";
        }
        finally
        {
            if (session == _searchSession) IsSearching = false;
        }
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

    /// <summary>底部播放栏爱心按钮：收藏/取消收藏当前播放曲目。</summary>
    [RelayCommand]
    private void ToggleCurrentFavorite()
    {
        if (_playbackBar.CurrentTrack is { } track)
            ToggleFavorite(track);
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
        SetViewMode(ViewMode.Playlist, playlist);
    }

    // 播放队列管理相关实现已拆分到 MainViewModel.Queue.cs（partial class，成员可直接访问）

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

    /// <summary>从网易云 / QQ音乐歌单分享链接导入：粘贴链接 → 由对应音源插件解析 → 新建歌单并展示。</summary>
    [RelayCommand]
    private async Task ImportPlaylistFromLinkAsync()
    {
        var url = PromptForPlaylistLink();
        if (string.IsNullOrWhiteSpace(url)) return;
        url = url.Trim();

        // 按链接来源优先匹配插件（网易云链接优先元力WY/网易，QQ 链接优先元力QQ/酷狗），
        // 失败后依次尝试其它已加载插件（依赖其 importMusicSheet 实现）
        var preferred = url.Contains("163.com") || url.Contains("music.163")
            ? FindPluginSource("netease", "wy", "网易")
            : url.Contains("y.qq.com") || url.Contains("qq.com")
                ? FindPluginSource("qqmusic", "qq", "酷gou", "酷")
                : null;

        var candidates = new List<JsPluginProvider>();
        if (preferred is not null) candidates.Add(preferred);
        foreach (var p in _registry.OnlineMusicProviders.OfType<JsPluginProvider>())
            if (!candidates.Contains(p)) candidates.Add(p);

        if (candidates.Count == 0)
        {
            Views.UiDialog.Info("没有已加载的在线音源插件，无法导入歌单。\n请先在 设置 → 音源插件 中加载网易云/QQ音乐插件。", "导入歌单");
            return;
        }

        SearchStatus = "正在从链接导入歌单…";
        (string Name, IReadOnlyList<Track> Tracks)? result = null;
        string? lastError = null;
        foreach (var p in candidates)
        {
            try
            {
                result = await p.ImportMusicSheetAsync(url);
                if (result is not null) break;
            }
            catch (Exception ex)
            {
                lastError = ex.Message; // 该插件解析失败，换下一个尝试
            }
        }

        if (result is not { Tracks.Count: > 0 })
        {
            SearchStatus = "导入歌单失败";
            Views.UiDialog.Warn(lastError is null
                    ? "未从该链接解析到歌单（当前插件不支持该平台歌单或链接格式不正确）。\n支持形如：\nhttps://music.163.com/playlist?id=3778678\nhttps://y.qq.com/n/ryqq/playlist/8655958142"
                    : $"导入歌单失败：{lastError}", "导入歌单");
            return;
        }

        // 歌单重名时自动追加序号
        var name = string.IsNullOrWhiteSpace(result.Value.Name) ? "导入的歌单" : result.Value.Name.Trim();
        var uniqueName = name;
        for (var i = 2; UserPlaylists.Any(p => p.Name == uniqueName); i++)
            uniqueName = $"{name} {i}";

        var playlist = new Playlist { Name = uniqueName };
        foreach (var t in result.Value.Tracks)
            playlist.Tracks.Add(t);
        UserPlaylists.Insert(0, playlist);
        SaveUserData();
        SelectPlaylist(playlist);
        SearchStatus = $"✔ 已导入 {playlist.Tracks.Count} 首到「{playlist.Name}」";
    }

    /// <summary>弹出歌单分享链接输入框，返回输入内容（取消返回 null）。</summary>
    private static string? PromptForPlaylistLink()
    {
        var window = new Window
        {
            Title = "导入歌单",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Owner = Application.Current.MainWindow,
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("BgPanel")
        };

        var tip = new System.Windows.Controls.TextBlock
        {
            Text = "粘贴网易云 / QQ音乐 歌单分享链接，例如：\nhttps://music.163.com/playlist?id=3778678\nhttps://y.qq.com/n/ryqq/playlist/8655958142",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("FgMuted"),
            FontSize = 12,
            Margin = new Thickness(6, 0, 6, 10)
        };
        var input = new System.Windows.Controls.TextBox
        {
            Margin = new Thickness(6, 0, 6, 14),
            Padding = new Thickness(8, 6, 8, 6)
        };
        var ok = new System.Windows.Controls.Button { Content = "导入", IsDefault = true, Width = 90, Margin = new Thickness(0, 0, 8, 0), Cursor = System.Windows.Input.Cursors.Hand };
        var cancel = new System.Windows.Controls.Button { Content = "取消", IsCancel = true, Width = 80, Cursor = System.Windows.Input.Cursors.Hand };
        if (Application.Current.MainWindow?.TryFindResource("BtnStyle") is Style btnStyle)
        {
            ok.Style = btnStyle;
            cancel.Style = btnStyle;
        }

        var bar = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        bar.Children.Add(ok);
        bar.Children.Add(cancel);

        var root = new System.Windows.Controls.StackPanel { Margin = new Thickness(18, 14, 18, 14) };
        root.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "从链接导入歌单",
            FontWeight = FontWeights.SemiBold,
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("FgPrimary"),
            Margin = new Thickness(6, 0, 6, 10)
        });
        root.Children.Add(tip);
        root.Children.Add(input);
        root.Children.Add(bar);
        window.Content = root;

        string? result = null;
        ok.Click += (_, _) =>
        {
            result = input.Text;
            window.DialogResult = true;
        };
        input.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter) ok.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        };
        return window.ShowDialog() == true && !string.IsNullOrWhiteSpace(result) ? result : null;
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

    // 在线曲目下载相关实现已拆分到 MainViewModel.Download.cs（partial class，成员可直接访问）

    /// <summary>执行搜索：本地直接匹配；网易云/QQ音乐/B站 在线源自动翻页取足一批（默认 50 条），
    /// 列表底部提供“加载更多”继续分页追加。</summary>
    [RelayCommand]
    private async Task SearchAsync()
    {
        var keyword = SearchText?.Trim();
        if (string.IsNullOrEmpty(keyword))
            return;

        // 记录搜索历史（去重置顶，最多 20 条）
        var existing = SearchHistory.FirstOrDefault(h => h == keyword);
        if (existing is not null) SearchHistory.Remove(existing);
        SearchHistory.Insert(0, keyword);
        while (SearchHistory.Count > 20) SearchHistory.RemoveAt(SearchHistory.Count - 1);
        SaveUserData();

        // 新搜索：重置分页状态（会话号递增，使旧的进行中拉取自动失效）
        _searchKeyword = keyword;
        _searchSession++;
        var session = _searchSession;
        _searchNextPage = 1;
        _searchEnded = false;
        _searchProvider = null;
        _searchPool.Clear();
        _seenSearchIds.Clear();
        _searchShownCount = 0;
        SearchResults.Clear();
        IsSearchMoreVisible = false;
        SearchStatus = "";
        SetViewMode(ViewMode.SearchResults);
        IsSearching = true;

        try
        {
            switch (SearchSource)
            {
                case SearchSource.Local:
                {
                    _searchLabel = "本地库";
                    _searchEnded = true;
                    var localMatches = Library.Tracks.OfType<Track>()
                        .Where(t => t.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                                 || t.Artist.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    foreach (var t in localMatches)
                    {
                        _searchPool.Add(t);
                        _seenSearchIds.Add($"{t.ProviderId}:{t.Id}");
                    }
                    SyncSearchShown(localMatches.Count);
                    SearchStatus = localMatches.Count == 0 ? "本地库未找到匹配结果" : $"本地库共 {localMatches.Count} 条";
                    break;
                }
                case SearchSource.NetEase:
                {
                    var wy = FindPluginSource("netease", "wy", "网易");
                    if (wy is null)
                    {
                        _searchEnded = true;
                        SearchStatus = "网易云插件源未加载（设置→插件管理→重新加载）";
                        break;
                    }
                    _searchLabel = "网易云";
                    _searchProvider = wy;
                    _searchActiveSource = SearchSource.NetEase;
                    await LoadSearchPagesAsync(session, SearchBatchSize);
                    if (session != _searchSession) return;
                    SyncSearchShown(SearchBatchSize);
                    UpdateSearchStatusText();
                    break;
                }
                case SearchSource.QQMusic:
                {
                    var qq = FindPluginSource("qqmusic", "qq", "酷gou");
                    if (qq is null)
                    {
                        _searchEnded = true;
                        SearchStatus = "QQ音乐插件源未加载（设置→插件管理→重新加载）";
                        break;
                    }
                    _searchLabel = "QQ音乐";
                    _searchProvider = qq;
                    _searchActiveSource = SearchSource.QQMusic;
                    await LoadSearchPagesAsync(session, SearchBatchSize);
                    if (session != _searchSession) return;
                    SyncSearchShown(SearchBatchSize);
                    UpdateSearchStatusText();
                    break;
                }
                case SearchSource.Bilibili:
                {
                    if (_registry.Find("bilibili") is not BilibiliMusicProvider bili)
                    {
                        _searchEnded = true;
                        SearchStatus = "B站源未加载";
                        break;
                    }
                    _searchLabel = "B站";
                    _searchProvider = bili;
                    _searchActiveSource = SearchSource.Bilibili;
                    await LoadSearchPagesAsync(session, SearchBatchSize);
                    if (session != _searchSession) return;
                    SyncSearchShown(SearchBatchSize);
                    UpdateSearchStatusText();
                    break;
                }
            }
            UpdateSearchMoreState();
        }
        catch (Exception ex)
        {
            // 已有部分结果时保留并全部上屏，不判定取尽：保留“加载更多”入口以便重试失败页
            if (_searchPool.Count == 0)
            {
                _searchEnded = true;
                SearchStatus = $"搜索失败: {ex.Message}";
            }
            else
            {
                SyncSearchShown(int.MaxValue);
                SearchStatus = $"{_searchLabel}加载中断: {ex.Message}（已显示 {_searchShownCount} 条，可点击「加载更多」重试）";
            }
            UpdateSearchMoreState();
        }
        finally
        {
            if (session == _searchSession) IsSearching = false;
        }
    }

    /// <summary>“加载更多”：继续按当前音源分页拉取并放行一批（默认再 +50 条）。</summary>
    [RelayCommand]
    private async Task LoadMoreSearchResultsAsync()
    {
        if (IsSearching || _searchEnded || _searchProvider is null) return;

        var session = _searchSession;
        IsSearching = true;
        SearchMoreText = "正在加载…";
        SearchStatus = $"{_searchLabel}正在加载更多…";
        try
        {
            await LoadSearchPagesAsync(session, _searchShownCount + SearchBatchSize);
            if (session != _searchSession) return;
            SyncSearchShown(_searchShownCount + SearchBatchSize);
            UpdateSearchStatusText();
        }
        catch (Exception ex)
        {
            if (session == _searchSession)
            {
                if (_searchPool.Count == 0)
                    SearchStatus = $"加载失败: {ex.Message}";
                else
                {
                    SyncSearchShown(int.MaxValue);
                    SearchStatus = $"{_searchLabel}加载中断: {ex.Message}（已显示 {_searchShownCount} 条，可再次点击「加载更多」重试）";
                }
            }
        }
        finally
        {
            if (session == _searchSession)
            {
                SearchMoreText = "加载更多";
                UpdateSearchMoreState();
                IsSearching = false;
            }
        }
    }

    /// <summary>逐页拉取在线搜索结果直到池中达到 targetShown 条（取尽或达到单批页数上限时停止）。
    /// 任一页失败向上抛出，由调用方决定展示策略（失败页号未前进，可重试）。</summary>
    private async Task LoadSearchPagesAsync(int session, int targetShown)
    {
        var source = _searchActiveSource;
        var pages = 0;
        while (!_searchEnded && session == _searchSession && _searchPool.Count < targetShown &&
               pages++ < SearchMaxPagesPerBatch)
        {
            var batch = await FetchSearchPageAsync(source, _searchNextPage);

            if (session != _searchSession) return;
            _searchNextPage++;
            if (batch.Count == 0)
            {
                _searchEnded = true;
                break;
            }

            var added = 0;
            foreach (var t in batch)
            {
                var key = $"{t.ProviderId}:{t.Id}";
                if (!_seenSearchIds.Add(key)) continue;
                added++;
                _searchPool.Add(t);
            }
            // 整页均为重复（如源的分页不稳定或已到尾部）：继续翻页只会重复请求，判定取尽
            if (added == 0)
            {
                _searchEnded = true;
                break;
            }
        }
    }

    /// <summary>按当前音源拉取指定页（第 1 页起）。</summary>
    private Task<IReadOnlyList<Track>> FetchSearchPageAsync(SearchSource source, int page) => source switch
    {
        SearchSource.NetEase or SearchSource.QQMusic =>
            ((JsPluginProvider)_searchProvider!).SearchPageAsync(_searchKeyword, page),
        SearchSource.Bilibili =>
            ((BilibiliMusicProvider)_searchProvider!).SearchPageAsync(_searchKeyword, page),
        _ => Task.FromResult<IReadOnlyList<Track>>([])
    };

    /// <summary>让 SearchResults 与池前 N 条对齐（放行/回收）。</summary>
    private void SyncSearchShown(int targetShown)
    {
        var want = Math.Min(targetShown, _searchPool.Count);
        while (SearchResults.Count < want)
            SearchResults.Add(_searchPool[SearchResults.Count]);
        while (SearchResults.Count > want)
            SearchResults.RemoveAt(SearchResults.Count - 1);
        _searchShownCount = want;
    }

    /// <summary>刷新“加载更多”按钮可见性（仅在线分页进行中/未取尽时显示）。</summary>
    private void UpdateSearchMoreState() =>
        IsSearchMoreVisible = ViewMode == ViewMode.SearchResults && !_searchEnded &&
                              SearchResults.Count > 0 && _searchProvider is not null;

    /// <summary>按当前展示条数刷新状态栏文案。</summary>
    private void UpdateSearchStatusText()
    {
        var n = SearchResults.Count;
        if (n == 0)
        {
            if (_searchEnded) SearchStatus = $"{_searchLabel}未找到匹配结果";
            return;
        }
        SearchStatus = _searchEnded
            ? $"{_searchLabel}共 {n} 条"
            : $"{_searchLabel}已显示 {n} 条，可点击下方「加载更多」继续";
    }
}
