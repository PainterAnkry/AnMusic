using AnMusic.Models;
using AnMusic.Services.Providers;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AnMusic.ViewModels;

/// <summary>下载任务状态。</summary>
public enum DownloadTaskState
{
    Queued,
    Downloading,
    Done,
    Failed
}

/// <summary>
/// 下载队列中的单个任务：状态/结果路径驱动下载管理面板（重试、打开目录、移除）。
/// </summary>
public partial class DownloadTaskItem : ObservableObject
{
    public required Track Track { get; init; }

    /// <summary>请求的音质（B站忽略）。</summary>
    public required AudioQuality Quality { get; init; }

    [ObservableProperty]
    private DownloadTaskState _state = DownloadTaskState.Queued;

    /// <summary>状态补充信息（失败原因等）。</summary>
    [ObservableProperty]
    private string _message = "";

    /// <summary>下载完成后的本地文件路径。</summary>
    [ObservableProperty]
    private string? _resultPath;

    public string Title => Track.Title;
    public string Artist => Track.Artist;

    /// <summary>面板状态文案。</summary>
    public string StateText => State switch
    {
        DownloadTaskState.Queued => "排队中",
        DownloadTaskState.Downloading => "下载中…",
        DownloadTaskState.Done => "已完成",
        DownloadTaskState.Failed => $"失败：{Message}",
        _ => ""
    };

    public bool IsQueuedOrRunning => State is DownloadTaskState.Queued or DownloadTaskState.Downloading;
    public bool CanRetry => State == DownloadTaskState.Failed;
    public bool CanOpen => State == DownloadTaskState.Done && !string.IsNullOrEmpty(ResultPath);

    partial void OnStateChanged(DownloadTaskState value) => RefreshDerived();
    partial void OnMessageChanged(string value) => RefreshDerived();
    partial void OnResultPathChanged(string? value) => RefreshDerived();

    private void RefreshDerived()
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(IsQueuedOrRunning));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanOpen));
    }
}
