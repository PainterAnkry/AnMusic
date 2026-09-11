using System.Collections.ObjectModel;
using AnMusic.Models;
using AnMusic.Services;
using AnMusic.Services.Playlist;
using AnMusic.Services.Providers;
using AnMusic.Services.Providers.JsPlugin;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnMusic.Android.ViewModels;

/// <summary>可搜索的音乐来源（本地 + 在线插件）。</summary>
public sealed class SearchSource
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public bool IsOnline { get; init; }

    /// <summary>供界面直接绑定的显示名（本地源加个图标前缀）。</summary>
    public string Label => IsOnline ? DisplayName : $"📁 {DisplayName}";
}

/// <summary>
/// 搜索 ViewModel：跨音源检索。
/// 音源来自 Core 的 <see cref="ProviderRegistry"/>，插件源由 <see cref="JsPluginLoader"/> 装载。
/// </summary>
public sealed partial class SearchViewModel : ObservableObject
{
    private readonly ProviderRegistry _registry;
    private readonly JsPluginLoader _loader;
    private readonly PlayerViewModel _player;
    private readonly UserDataStore _store;

    /// <summary>当前搜索的取消源，切换关键词时取消上一次。</summary>
    private CancellationTokenSource? _cts;

    public SearchViewModel(
        ProviderRegistry registry,
        JsPluginLoader loader,
        PlayerViewModel player,
        UserDataStore store)
    {
        _registry = registry;
        _loader = loader;
        _player = player;
        _store = store;

        // 搜索历史是跨端持久化数据（Core 的 userdata.json），与桌面端共用同一份
        foreach (var word in _store.SearchHistory) History.Add(word);

        ReloadSources();
    }

    #region 状态

    [ObservableProperty] private string _keyword = string.Empty;
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private string _statusText = "输入关键词开始搜索";
    [ObservableProperty] private SearchSource? _selectedSource;
    [ObservableProperty] private bool _hasSearched;

    /// <summary>可选的搜索来源。</summary>
    public ObservableCollection<SearchSource> Sources { get; } = [];

    /// <summary>搜索结果。</summary>
    public ObservableCollection<Track> Results { get; } = [];

    /// <summary>搜索历史（来自 Core 持久化）。</summary>
    public ObservableCollection<string> History { get; } = [];

    /// <summary>结果是否为空且已搜索过（显示空状态）。</summary>
    public bool ShowEmpty => HasSearched && Results.Count == 0 && !IsSearching;

    /// <summary>已有结果说明文案。</summary>
    public string ResultCountText => HasSearched && Results.Count > 0
        ? $"共 {Results.Count} 条结果"
        : string.Empty;

    partial void OnIsSearchingChanged(bool value) => OnPropertyChanged(nameof(ShowEmpty));

    /// <summary>重新从注册中心拉取音源列表（插件加载完成后调用）。</summary>
    public void ReloadSources()
    {
        var previousId = SelectedSource?.Id;

        Sources.Clear();
        Sources.Add(new SearchSource { Id = "local-file", DisplayName = "本地音乐", IsOnline = false });

        foreach (var provider in _registry.OnlineMusicProviders)
        {
            Sources.Add(new SearchSource
            {
                Id = provider.Id,
                DisplayName = provider.DisplayName,
                IsOnline = true,
            });
        }

        SelectedSource = Sources.FirstOrDefault(s => s.Id == previousId) ?? Sources.FirstOrDefault();

        if (Sources.Count <= 1)
            StatusText = "未发现在线音源：把音源 .js 插件放入插件目录后重启应用即可出现在这里";
    }

    /// <summary>当前已加载的插件数（设置页与调试用）。</summary>
    public int LoadedPluginCount => _loader.Plugins.Count;

    #endregion

    #region 搜索

    /// <summary>当前已拉取到的页码（插件源从 1 开始）。</summary>
    private int _currentPage;

    /// <summary>当前搜索是否还有下一页可拉（本地源与不支持分页的源恒为 false）。</summary>
    [ObservableProperty] private bool _hasMore;

    [ObservableProperty] private bool _isLoadingMore;

    /// <summary>「加载更多」按钮文案。</summary>
    public string LoadMoreText => IsLoadingMore ? "正在加载…" : "加载更多";

    partial void OnIsLoadingMoreChanged(bool value) => OnPropertyChanged(nameof(LoadMoreText));

    /// <summary>当前可用插件源是否支持分页（只有插件源能翻页）。</summary>
    private static bool SupportsPaging(IMusicProvider provider) => provider is JsPluginProvider;

    /// <summary>执行搜索（重置到第一页）。</summary>
    [RelayCommand]
    private async Task SearchAsync()
    {
        var word = Keyword?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(word))
        {
            StatusText = "请输入关键词";
            return;
        }

        if (SelectedSource is null)
        {
            StatusText = "请先选择搜索来源";
            return;
        }

        // 取消上一次未完成的搜索
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        IsSearching = true;
        Results.Clear();
        _currentPage = 0;
        HasMore = false;
        StatusText = "搜索中…";
        OnPropertyChanged(nameof(ResultCountText));

        try
        {
            var provider = _registry.Find(SelectedSource.Id);
            if (provider is null)
            {
                StatusText = $"音源「{SelectedSource.DisplayName}」不可用";
                return;
            }

            var tracks = await FetchPageAsync(provider, word, 1, ct);
            if (ct.IsCancellationRequested) return;

            _currentPage = 1;
            AppendDeduped(tracks);

            // 插件源按页拉，返回满页就认为还有下一页；本地源一次给全，没有"更多"
            HasMore = SupportsPaging(provider) && tracks.Count > 0;

            UpdateResultStatus(word);
            PushHistory(word);
        }
        catch (OperationCanceledException)
        {
            // 主动取消，不算错误
        }
        catch (Exception ex)
        {
            StatusText = $"搜索失败：{ex.Message}";
            AppPaths.LogError("搜索音乐", ex, $"{SelectedSource.Id}:{word}");
        }
        finally
        {
            if (!ct.IsCancellationRequested) IsSearching = false;
            HasSearched = true;
            OnPropertyChanged(nameof(ShowEmpty));
            OnPropertyChanged(nameof(ResultCountText));
        }
    }

    /// <summary>
    /// 「加载更多」：继续按当前音源拉下一页并追加。
    /// 跨页按 <c>ProviderId:Id</c> 去重 —— 插件源分页之间常有重叠条目，
    /// 不去重会在列表里出现同一首歌多次。
    /// </summary>
    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (IsLoadingMore || IsSearching || !HasMore) return;

        var word = Keyword?.Trim() ?? string.Empty;
        var source = SelectedSource;
        if (string.IsNullOrEmpty(word) || source is null) return;

        var provider = _registry.Find(source.Id);
        if (provider is null || !SupportsPaging(provider))
        {
            HasMore = false;
            return;
        }

        _cts ??= new CancellationTokenSource();
        var ct = _cts.Token;

        IsLoadingMore = true;

        try
        {
            var nextPage = _currentPage + 1;
            var tracks = await FetchPageAsync(provider, word, nextPage, ct);
            if (ct.IsCancellationRequested) return;

            var added = AppendDeduped(tracks);
            _currentPage = nextPage;

            // 空页或整页都是重复的，说明已经到底了
            if (tracks.Count == 0 || added == 0) HasMore = false;

            UpdateResultStatus(word);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // 保留已加载的结果，只提示这次失败，用户可以再点一次重试
            StatusText = $"加载更多失败：{ex.Message}";
            AppPaths.LogError("加载更多搜索结果", ex, $"{source.Id}:{word}:p{_currentPage + 1}");
        }
        finally
        {
            IsLoadingMore = false;
            OnPropertyChanged(nameof(ResultCountText));
        }
    }

    /// <summary>按来源类型选择合适的取数方式（插件源走分页接口）。</summary>
    private static Task<IReadOnlyList<Track>> FetchPageAsync(
        IMusicProvider provider, string word, int page, CancellationToken ct)
        => provider is JsPluginProvider jsPlugin
            ? jsPlugin.SearchPageAsync(word, page, ct)
            : provider.SearchAsync(word, ct);

    /// <summary>追加去重，返回实际新增条数。</summary>
    private int AppendDeduped(IReadOnlyList<Track> tracks)
    {
        var seen = new HashSet<string>(
            Results.Select(t => $"{t.ProviderId}:{t.Id}"),
            StringComparer.Ordinal);

        var added = 0;
        foreach (var t in tracks)
        {
            if (!seen.Add($"{t.ProviderId}:{t.Id}")) continue;
            Results.Add(t);
            added++;
        }

        return added;
    }

    private void UpdateResultStatus(string word)
    {
        StatusText = Results.Count == 0
            ? $"「{word}」没有找到结果"
            : HasMore
                ? $"已找到 {Results.Count} 首"
                : $"已找到 {Results.Count} 首（已到底）";
    }

    /// <summary>点历史词直接搜。</summary>
    [RelayCommand]
    private async Task SearchWithAsync(string? word)
    {
        if (string.IsNullOrWhiteSpace(word)) return;
        Keyword = word;
        await SearchAsync();
    }

    /// <summary>清空搜索历史（同时落盘 —— 历史是跨端共用数据，只在内存里清会"复活"）。</summary>
    [RelayCommand]
    private void ClearHistory()
    {
        History.Clear();

        try
        {
            _store.SearchHistory = [];
            _store.Save();
        }
        catch (Exception ex)
        {
            AppPaths.LogError("清空搜索历史", ex);
        }
    }

    /// <summary>清空结果（返回初始态）。</summary>
    [RelayCommand]
    private void ClearResults()
    {
        Results.Clear();
        HasSearched = false;
        HasMore = false;
        _currentPage = 0;
        StatusText = "输入关键词开始搜索";
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ResultCountText));
    }

    private void PushHistory(string word)
    {
        var existing = History.FirstOrDefault(h => string.Equals(h, word, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) History.Remove(existing);
        History.Insert(0, word);
        while (History.Count > 20) History.RemoveAt(History.Count - 1);

        try
        {
            _store.SearchHistory = History.ToList();
            _store.Save();
        }
        catch (Exception ex)
        {
            AppPaths.LogError("保存搜索历史", ex);
        }
    }

    #endregion

    #region 播放

    /// <summary>播放某条结果：以整个结果列表为队列，从该项开始。</summary>
    [RelayCommand]
    private async Task PlayResultAsync(Track? track)
    {
        if (track is null) return;
        var index = Results.IndexOf(track);
        await _player.PlayQueueAsync(Results.ToList(), Math.Max(0, index));
    }

    /// <summary>播放全部结果。</summary>
    [RelayCommand]
    private async Task PlayAllResultsAsync()
    {
        if (Results.Count == 0) return;
        await _player.PlayQueueAsync(Results.ToList(), 0);
    }

    #endregion
}
