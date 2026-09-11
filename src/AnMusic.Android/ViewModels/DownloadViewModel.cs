using System.Collections.ObjectModel;
using AnMusic.Android.Services;
using AnMusic.Models;
using AnMusic.Services;
using AnMusic.Services.Metadata;
using AnMusic.Services.Providers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnMusic.Android.ViewModels;

/// <summary>下载任务状态。</summary>
public enum DownloadTaskState
{
    Queued,
    Downloading,
    Done,
    Failed,
}

/// <summary>
/// 下载队列中的单个任务。对应桌面端的 DownloadTaskItem，
/// 但安卓端把结果路径固定为「系统媒体库的 Music/AnMusic」。
/// </summary>
public sealed partial class DownloadTaskItem : ObservableObject
{
    public required Track Track { get; init; }

    /// <summary>请求的音质（不支持多音质的源忽略此项）。</summary>
    public AudioQuality Quality { get; init; } = AudioQuality.ExHigh;

    [ObservableProperty] private DownloadTaskState _state = DownloadTaskState.Queued;
    [ObservableProperty] private string _message = string.Empty;
    [ObservableProperty] private string? _resultPath;

    public string Title => Track.Title;
    public string Artist => Track.Artist;

    public string StateText => State switch
    {
        DownloadTaskState.Queued => "排队中",
        DownloadTaskState.Downloading => "下载中…",
        DownloadTaskState.Done => "已完成",
        DownloadTaskState.Failed => $"失败：{Message}",
        _ => string.Empty,
    };

    public bool IsRunning => State is DownloadTaskState.Queued or DownloadTaskState.Downloading;
    public bool CanRetry => State == DownloadTaskState.Failed;
    public bool IsDone => State == DownloadTaskState.Done;

    /// <summary>列表右侧的状态点颜色（用资源键，由 XAML 绑到 DynamicResource 不可行，故给字符串键）。</summary>
    public string StateColorKey => State switch
    {
        DownloadTaskState.Done => "AmSuccess",
        DownloadTaskState.Failed => "AmDanger",
        DownloadTaskState.Downloading => "AmPrimary",
        _ => "AmTextTertiary",
    };

    partial void OnStateChanged(DownloadTaskState value) => RefreshDerived();
    partial void OnMessageChanged(string value) => RefreshDerived();
    partial void OnResultPathChanged(string? value) => RefreshDerived();

    private void RefreshDerived()
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(StateColorKey));
    }
}

/// <summary>缓存目录里的单个文件。</summary>
public sealed class CacheFileItem
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required string SourceText { get; init; }
    public required string SizeText { get; init; }
}

/// <summary>
/// 下载管理 ViewModel。两块内容：
/// <list type="bullet">
/// <item><b>下载任务</b>：把在线曲目按指定音质取回并保存进系统音乐库（对应桌面端的下载队列）。</item>
/// <item><b>缓冲缓存</b>：各音源播放时产生的临时文件，可清理释放空间。</item>
/// </list>
/// </summary>
public sealed partial class DownloadViewModel : ObservableObject
{
    private readonly ProviderRegistry _registry;
    private readonly IMetadataReader _metadata;
    private readonly LibraryViewModel _library;

    private bool _pumpRunning;

    public DownloadViewModel(
        ProviderRegistry registry,
        IMetadataReader metadata,
        LibraryViewModel library)
    {
        _registry = registry;
        _metadata = metadata;
        _library = library;
        RefreshCache();
    }

    #region 状态

    /// <summary>下载任务（新的在前）。</summary>
    public ObservableCollection<DownloadTaskItem> Tasks { get; } = [];

    /// <summary>缓存文件列表。</summary>
    public ObservableCollection<CacheFileItem> CacheFiles { get; } = [];

    [ObservableProperty] private string _taskSummary = "还没有下载任务";
    [ObservableProperty] private string _cacheSummary = "计算中…";
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isBusy;

    public bool HasTasks => Tasks.Count > 0;
    public bool HasCache => CacheFiles.Count > 0;

    /// <summary>下载保存位置说明（界面提示用）。</summary>
    public string SaveLocationText => $"保存到 {DownloadStorage.DisplayFolder}，保存后自动进入本地音乐";

    private void RefreshTaskSummary()
    {
        var running = Tasks.Count(t => t.IsRunning);
        var done = Tasks.Count(t => t.IsDone);
        var failed = Tasks.Count(t => t.CanRetry);

        TaskSummary = Tasks.Count == 0
            ? "还没有下载任务"
            : $"{Tasks.Count} 个任务" +
              (running > 0 ? $" · 进行中 {running}" : "") +
              (done > 0 ? $" · 完成 {done}" : "") +
              (failed > 0 ? $" · 失败 {failed}" : "");

        OnPropertyChanged(nameof(HasTasks));
    }

    #endregion

    #region 下载队列

    /// <summary>
    /// 加入下载队列。支持多音质的源会先弹音质选择（与桌面端一致）。
    /// </summary>
    public async Task EnqueueAsync(Track track)
    {
        if (track is null) return;

        // 只有本地曲目才谈得上"已是本地文件"：在线曲目听过一次后挂的是播放缓冲，
        // 拿 File.Exists(FilePath) 判断会把缓存误当成已下载，导致下载被拦住
        if (track.IsAlreadyLocalFile)
        {
            await AlertAsync("提示", "该曲目已是本地文件，无需下载。");
            return;
        }

        if (_registry.Find(track.ProviderId) is not IOnlineMusicProvider online)
        {
            await AlertAsync("提示", "该曲目没有对应的在线音源，无法下载。");
            return;
        }

        var existing = Tasks.FirstOrDefault(t => SameTrack(t.Track, track));
        if (existing is not null)
        {
            StatusMessage = existing.State switch
            {
                DownloadTaskState.Failed => $"「{track.Title}」下载失败，可在此重试",
                DownloadTaskState.Done => $"「{track.Title}」已下载完成",
                _ => $"「{track.Title}」已在下载队列中",
            };
            return;
        }

        var quality = AudioQuality.ExHigh;
        try
        {
            var qualities = await online.GetAvailableQualitiesAsync(track);
            if (qualities.Count > 1)
            {
                var picked = await PickQualityAsync(qualities);
                if (picked is null) return; // 用户取消
                quality = picked.Value;
            }
            else if (qualities.Count == 1)
            {
                quality = qualities[0];
            }
        }
        catch (Exception ex)
        {
            AppPaths.LogError("查询可选音质", ex, track.Title);
        }

        Tasks.Insert(0, new DownloadTaskItem { Track = track, Quality = quality });
        RefreshTaskSummary();
        StatusMessage = $"已加入下载队列：{track.Title}";
        _ = PumpAsync();
    }

    /// <summary>下载队列泵：逐个执行排队中的任务。</summary>
    private async Task PumpAsync()
    {
        if (_pumpRunning) return;
        _pumpRunning = true;
        IsBusy = true;
        try
        {
            while (Tasks.FirstOrDefault(t => t.State == DownloadTaskState.Queued) is { } item)
                await ExecuteAsync(item);
        }
        finally
        {
            _pumpRunning = false;
            IsBusy = false;
            RefreshTaskSummary();
        }
    }

    private async Task ExecuteAsync(DownloadTaskItem item)
    {
        var track = item.Track;
        item.State = DownloadTaskState.Downloading;
        item.Message = string.Empty;
        StatusMessage = $"正在下载：{track.Title}";

        try
        {
            if (_registry.Find(track.ProviderId) is not IOnlineMusicProvider online)
                throw new InvalidOperationException("该曲目没有对应的在线音源");

            // 网易云 / QQ 音乐支持指定音质；其余源（插件、B 站）只做缓冲
            var cachedPath = track.ProviderId is "netease" or "qqmusic"
                ? await online.DownloadAsync(track, item.Quality)
                : await online.ResolveToLocalAsync(track);

            var displayName = SanitizeName($"{track.Artist} - {track.Title}");
            var savedPath = await DownloadStorage.PublishAsync(cachedPath, displayName);

            if (string.IsNullOrEmpty(savedPath))
                throw new IOException("写入音乐目录失败（可能是存储权限或空间不足）");

            item.ResultPath = savedPath;
            item.State = DownloadTaskState.Done;
            StatusMessage = $"已下载：{Path.GetFileName(savedPath)}";

            RegisterIntoLibrary(track, savedPath);
        }
        catch (Exception ex)
        {
            item.State = DownloadTaskState.Failed;
            item.Message = ex.Message;
            StatusMessage = $"下载失败：{track.Title}";
            AppPaths.LogError("下载歌曲", ex, track.Title);
        }
    }

    /// <summary>
    /// 把下载好的文件补进当前曲库内存列表，用户回到「我的音乐」就能立刻看到，
    /// 不必等下次重新扫描。
    /// </summary>
    private void RegisterIntoLibrary(Track source, string path)
    {
        try
        {
            if (_library.AllTracks.Any(t => string.Equals(t.FilePath, path, StringComparison.Ordinal)))
                return;

            // 文件已是本地可读的，优先用真实标签（在线源的标题常带后缀/伴奏标记）
            var meta = _metadata.Read(path);
            var track = new Track
            {
                Id = path,
                FilePath = path,
                Title = string.IsNullOrWhiteSpace(meta.Title) ? source.Title : meta.Title,
                Artist = string.IsNullOrWhiteSpace(meta.Artist) ? source.Artist : meta.Artist,
                Album = string.IsNullOrWhiteSpace(meta.Album) ? source.Album : meta.Album,
                Duration = meta.Duration > TimeSpan.Zero ? meta.Duration : source.Duration,
                CoverKey = source.CoverKey,
                ProviderId = "local-file",
            };

            MainThread.BeginInvokeOnMainThread(() =>
            {
                _library.AllTracks.Add(track);
                _library.NotifyLibraryChanged();
            });
        }
        catch (Exception ex)
        {
            AppPaths.LogError("下载后入库", ex, path);
        }
    }

    /// <summary>失败任务重试。</summary>
    [RelayCommand]
    private void RetryTask(DownloadTaskItem? item)
    {
        if (item is null || item.State != DownloadTaskState.Failed) return;
        item.Message = string.Empty;
        item.State = DownloadTaskState.Queued;
        RefreshTaskSummary();
        _ = PumpAsync();
    }

    /// <summary>移除任务（进行中的不可移除）。</summary>
    [RelayCommand]
    private void RemoveTask(DownloadTaskItem? item)
    {
        if (item is null || item.IsRunning) return;
        Tasks.Remove(item);
        RefreshTaskSummary();
    }

    /// <summary>清空已完成 / 失败的任务。</summary>
    [RelayCommand]
    private void ClearFinishedTasks()
    {
        foreach (var item in Tasks.Where(t => !t.IsRunning).ToList())
            Tasks.Remove(item);
        RefreshTaskSummary();
    }

    #endregion

    #region 缓存

    /// <summary>扫描各音源缓存目录。</summary>
    [RelayCommand]
    public void RefreshCache()
    {
        CacheFiles.Clear();
        long total = 0;

        foreach (var (label, dir) in CacheSources())
        {
            if (!Directory.Exists(dir)) continue;

            FileInfo[] files;
            try
            {
                files = new DirectoryInfo(dir).GetFiles();
            }
            catch (Exception ex)
            {
                AppPaths.LogError("枚举缓存文件", ex, dir);
                continue;
            }

            foreach (var file in files)
            {
                total += file.Length;
                CacheFiles.Add(new CacheFileItem
                {
                    Name = file.Name,
                    FullPath = file.FullName,
                    SourceText = label,
                    SizeText = FormatSize(file.Length),
                });
            }
        }

        CacheSummary = CacheFiles.Count == 0
            ? "暂无缓冲文件"
            : $"{CacheFiles.Count} 个文件 · 共 {FormatSize(total)}";
        OnPropertyChanged(nameof(HasCache));
    }

    /// <summary>删除单个缓存文件。</summary>
    [RelayCommand]
    private void DeleteCacheFile(CacheFileItem? item)
    {
        if (item is null) return;
        DeleteFile(item.FullPath);
        RefreshCache();
    }

    /// <summary>清理全部缓存。</summary>
    [RelayCommand]
    private async Task ClearAllCacheAsync()
    {
        if (CacheFiles.Count == 0)
        {
            await AlertAsync("提示", "当前没有可清理的文件。");
            return;
        }

        var confirm = await ConfirmAsync("全部清理",
            "将删除所有已缓冲的音频文件。已下载到音乐库的歌曲不受影响。", "清理");
        if (!confirm) return;

        var failures = 0;
        foreach (var item in CacheFiles.ToList())
        {
            if (!DeleteFile(item.FullPath)) failures++;
        }

        RefreshCache();
        await AlertAsync("完成", failures == 0
            ? "缓冲文件已全部清理。"
            : $"已清理，但有 {failures} 个文件正被占用未能删除。");
    }

    private static IEnumerable<(string Label, string Dir)> CacheSources()
    {
        yield return ("通用音频缓存", AppPaths.AudioCacheDir);
        yield return ("插件音源缓存", AppPaths.PluginAudioCacheDir);
        yield return ("网易云缓存", AppPaths.NeteaseCacheDir);
        yield return ("QQ 音乐缓存", AppPaths.QqmusicCacheDir);
    }

    private static bool DeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            // 正在播放的文件删不掉，属预期情况
            AppPaths.LogError("删除缓存文件", ex, path);
            return false;
        }
    }

    #endregion

    #region 辅助

    private static bool SameTrack(Track a, Track b) => a.Id == b.Id && a.ProviderId == b.ProviderId;

    private static string SanitizeName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Trim();
    }

    private static string QualityName(AudioQuality q) => q switch
    {
        AudioQuality.Standard => "标准 (128kbps)",
        AudioQuality.Higher => "较高 (192kbps)",
        AudioQuality.ExHigh => "极高 (320kbps)",
        AudioQuality.Lossless => "无损 (FLAC)",
        _ => q.ToString(),
    };

    private static async Task<AudioQuality?> PickQualityAsync(IReadOnlyList<AudioQuality> qualities)
    {
        var names = qualities.Select(QualityName).ToArray();

        var choice = await MainThread.InvokeOnMainThreadAsync(() =>
            Shell.Current.DisplayActionSheet("选择下载音质", "取消", null, names));

        if (string.IsNullOrEmpty(choice) || choice == "取消") return null;

        var index = Array.IndexOf(names, choice);
        return index >= 0 ? qualities[index] : null;
    }

    private static Task AlertAsync(string title, string message) =>
        MainThread.InvokeOnMainThreadAsync(() => Shell.Current.DisplayAlert(title, message, "好"));

    private static Task<bool> ConfirmAsync(string title, string message, string accept) =>
        MainThread.InvokeOnMainThreadAsync(() => Shell.Current.DisplayAlert(title, message, accept, "取消"));

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F2} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F0} KB",
        _ => $"{bytes} B",
    };

    /// <summary>页面显示时刷新。</summary>
    public void Refresh()
    {
        RefreshCache();
        RefreshTaskSummary();
    }

    #endregion
}
