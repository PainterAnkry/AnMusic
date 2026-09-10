using System.Collections.ObjectModel;
using AnMusic.Models;
using AnMusic.Services;
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

    /// <summary>当前搜索的取消源，切换关键词时取消上一次。</summary>
    private CancellationTokenSource? _cts;

    public SearchViewModel(
        ProviderRegistry registry,
        JsPluginLoader loader,
        PlayerViewModel player)
    {
        _registry = registry;
        _loader = loader;
        _player = player;

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

    /// <summary>执行搜索。</summary>
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

            // 插件源支持分页接口时优先用，能拿到更多结果
            IReadOnlyList<Track> tracks;
            if (provider is JsPluginProvider jsPlugin)
                tracks = await jsPlugin.SearchPageAsync(word, page: 1, ct);
            else
                tracks = await provider.SearchAsync(word, ct);

            if (ct.IsCancellationRequested) return;

            foreach (var t in tracks) Results.Add(t);

            StatusText = Results.Count == 0
                ? $"「{word}」没有找到结果"
                : $"已找到 {Results.Count} 首";

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

    /// <summary>点历史词直接搜。</summary>
    [RelayCommand]
    private async Task SearchWithAsync(string? word)
    {
        if (string.IsNullOrWhiteSpace(word)) return;
        Keyword = word;
        await SearchAsync();
    }

    /// <summary>清空搜索历史。</summary>
    [RelayCommand]
    private void ClearHistory() => History.Clear();

    /// <summary>清空结果（返回初始态）。</summary>
    [RelayCommand]
    private void ClearResults()
    {
        Results.Clear();
        HasSearched = false;
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
