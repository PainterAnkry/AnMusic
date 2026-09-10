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

/// <summary>
/// MainViewModel 的播放队列管理部分（partial 拆分，便于维护）。
/// </summary>
public partial class MainViewModel
{

    /// <summary>队列面板行：曲目 + 是否为当前播放项。</summary>
    public sealed class QueueRowItem
    {
        public required Track Track { get; init; }
        public bool IsCurrent { get; init; }
    }

    /// <summary>队列面板展示数据（打开面板 / 队列变化时重建）。</summary>
    public ObservableCollection<QueueRowItem> QueueRows { get; } = [];

    /// <summary>下载队列（下载管理面板数据源）。</summary>
    public ObservableCollection<DownloadTaskItem> DownloadTasks { get; } = [];

    /// <summary>下载管理面板是否打开（侧栏入口高亮联动）。</summary>
    [ObservableProperty]
    private bool _isDownloadPanelOpen;

    /// <summary>重建队列面板数据（code-behind 打开面板前调用）。</summary>
    public void RefreshQueuePanel()
    {
        var cur = _queue.Current;
        QueueRows.Clear();
        foreach (var t in _queue.Queue)
        {
            QueueRows.Add(new QueueRowItem
            {
                Track = t,
                IsCurrent = ReferenceEquals(t, cur) ||
                            (cur is not null && t.Id == cur.Id && t.ProviderId == cur.ProviderId)
            });
        }
    }

    /// <summary>在队列面板中双击曲目 → 定位队列索引并播放（含歌词加载与最近播放记录）。</summary>
    [RelayCommand]
    private async Task PlayQueueTrackAsync(Track? track)
    {
        if (track is null) return;

        // 双击的正是当前曲目：从头重播即可，避免无谓重载
        var cur = _playbackBar.CurrentTrack;
        if (_playbackBar.IsLoaded && cur is not null &&
            (ReferenceEquals(cur, track) || (cur.Id == track.Id && cur.ProviderId == track.ProviderId)))
        {
            _playbackBar.SeekTo(0);
            RefreshQueuePanel();
            return;
        }

        var index = -1;
        for (var i = 0; i < _queue.Queue.Count; i++)
        {
            var t = _queue.Queue[i];
            if (ReferenceEquals(t, track) || (t.Id == track.Id && t.ProviderId == track.ProviderId))
            {
                index = i;
                break;
            }
        }
        if (index >= 0) _queue.JumpTo(index);
        await _playbackBar.LoadAndPlayAsync(track);
        await Lyrics.LoadLyricsAsync(track);
        RecordRecent(track);
        RefreshQueuePanel();
    }

    /// <summary>从队列移除曲目（当前播放曲目不可移除，直接忽略）。</summary>
    [RelayCommand]
    private void RemoveQueueTrack(Track? track)
    {
        if (_queue.RemoveTrack(track)) RefreshQueuePanel();
    }

    /// <summary>队列内上移一格。</summary>
    [RelayCommand]
    private void MoveQueueTrackUp(Track? track)
    {
        if (_queue.MoveTrack(track!, -1)) RefreshQueuePanel();
    }

    /// <summary>队列内下移一格。</summary>
    [RelayCommand]
    private void MoveQueueTrackDown(Track? track)
    {
        if (_queue.MoveTrack(track!, 1)) RefreshQueuePanel();
    }
}
