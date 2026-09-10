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
/// MainViewModel 的在线曲目下载部分（partial 拆分，便于维护）。
/// </summary>
public partial class MainViewModel
{

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

    /// <summary>右键“下载”→ 加入下载队列（可连续加入多个，自动逐个下载；网易云/QQ音乐支持选择音质）。</summary>
    [RelayCommand]
    private async Task DownloadTrackAsync(Track? track)
    {
        if (track is null) return;

        if (!string.IsNullOrEmpty(track.FilePath) && File.Exists(track.FilePath))
        {
            Views.UiDialog.Info("该曲目已是本地文件，无需下载", "提示");
            return;
        }

        if (_registry.Find(track.ProviderId) is not IOnlineMusicProvider online)
        {
            Views.UiDialog.Warn("该曲目没有对应的在线源，无法下载", "提示");
            return;
        }

        // 已在队列中（含失败待重试）：给出提示，不重复入队
        var existing = DownloadTasks.FirstOrDefault(t =>
            t.Track.Id == track.Id && t.Track.ProviderId == track.ProviderId);
        if (existing is not null)
        {
            SearchStatus = existing.State switch
            {
                DownloadTaskState.Failed => $"「{track.Title}」下载失败，请在下载面板中重试",
                DownloadTaskState.Done => $"「{track.Title}」已下载完成，可在下载面板打开位置",
                _ => $"「{track.Title}」已在下载队列中"
            };
            return;
        }

        // 网易云 / QQ 音乐：弹出音质选择
        AudioQuality quality = AudioQuality.ExHigh;
        if (track.ProviderId is "netease" or "qqmusic")
        {
            var qualities = await online.GetAvailableQualitiesAsync(track);
            quality = ShowQualityDialog(qualities);
            if (quality == AudioQuality.Standard && qualities.Count > 0 && qualities[0] != AudioQuality.Standard)
                return; // 用户取消
        }

        DownloadTasks.Add(new DownloadTaskItem { Track = track, Quality = quality });
        SearchStatus = $"⬇ 已加入下载队列: {track.Title}";
        _ = PumpDownloadQueueAsync();
    }

    /// <summary>下载队列泵：顺序执行所有“排队中”任务。</summary>
    private bool _downloadPumpRunning;

    private async Task PumpDownloadQueueAsync()
    {
        if (_downloadPumpRunning) return;
        _downloadPumpRunning = true;
        try
        {
            while (DownloadTasks.FirstOrDefault(t => t.State == DownloadTaskState.Queued) is { } item)
                await ExecuteDownloadAsync(item);
        }
        finally
        {
            _downloadPumpRunning = false;
        }
    }

    private async Task ExecuteDownloadAsync(DownloadTaskItem item)
    {
        var track = item.Track;
        item.State = DownloadTaskState.Downloading;
        item.Message = "";
        SearchStatus = $"⬇ 正在下载: {track.Title}";
        try
        {
            string cachedPath;
            if (_registry.Find(track.ProviderId) is not IOnlineMusicProvider online)
                throw new InvalidOperationException("该曲目没有对应的在线源");

            if (track.ProviderId is "netease" or "qqmusic")
                cachedPath = await online.DownloadAsync(track, item.Quality);
            else
                cachedPath = await online.ResolveToLocalAsync(track);

            var dir = GetDownloadDirectory();
            Directory.CreateDirectory(dir);
            var ext = Path.GetExtension(cachedPath);
            if (string.IsNullOrEmpty(ext)) ext = ".mp3";
            var targetPath = UniquePath(Path.Combine(dir, SanitizeFileName($"{track.Artist} - {track.Title}") + ext));
            File.Copy(cachedPath, targetPath);

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

            item.ResultPath = targetPath;
            item.State = DownloadTaskState.Done;
            SearchStatus = $"✔ 已下载: {Path.GetFileName(targetPath)}（{dir}）";
        }
        catch (Exception ex)
        {
            item.State = DownloadTaskState.Failed;
                Services.AppPaths.LogError("下载歌曲", ex, item.Track.Title);
            item.Message = ex.Message;
            SearchStatus = $"下载失败: {track.Title}（可在下载面板重试）";
        }
    }

    /// <summary>失败任务重试（重新排队）。</summary>
    [RelayCommand]
    private void RetryDownloadTask(DownloadTaskItem? item)
    {
        if (item is null || item.State != DownloadTaskState.Failed) return;
        item.Message = "";
        item.State = DownloadTaskState.Queued;
        _ = PumpDownloadQueueAsync();
    }

    /// <summary>打开下载完成文件的所在目录并选中。</summary>
    [RelayCommand]
    private void OpenDownloadResult(DownloadTaskItem? item)
    {
        if (item?.ResultPath is not { Length: > 0 } path || !File.Exists(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch { /* 打开失败不提示 */ }
    }

    /// <summary>移除已完成/失败/排队中的任务（下载中的任务不可移除）。</summary>
    [RelayCommand]
    private void RemoveDownloadTask(DownloadTaskItem? item)
    {
        if (item is null || item.State == DownloadTaskState.Downloading) return;
        DownloadTasks.Remove(item);
    }

    /// <summary>弹出音质选择对话框，返回所选音质；取消返回 Standard（调用方据此判断）。</summary>
    private static AudioQuality ShowQualityDialog(IReadOnlyList<AudioQuality> qualities)
    {
        var names = qualities.Select(q => q switch
        {
            AudioQuality.Standard => "标准 (128kbps)",
            AudioQuality.Higher => "较高 (192kbps)",
            AudioQuality.ExHigh => "极高 (320kbps)",
            AudioQuality.Lossless => "无损 (FLAC)",
            _ => q.ToString()
        }).ToArray();

        var dialog = new Window
        {
            Title = "选择音质",
            Width = 280,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("BgPanel")
        };

        var combo = new System.Windows.Controls.ComboBox
        {
            ItemsSource = names,
            SelectedIndex = 0,
            Margin = new Thickness(16),
            Padding = new Thickness(8, 6, 8, 6)
        };

        var okBtn = new System.Windows.Controls.Button
        {
            Content = "确定", IsDefault = true, Width = 80, Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(0, 6, 0, 6)
        };
        var cancelBtn = new System.Windows.Controls.Button
        {
            Content = "取消", IsCancel = true, Width = 80, Padding = new Thickness(0, 6, 0, 6)
        };

        var panel = new System.Windows.Controls.StackPanel();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "请选择下载音质",
            Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("FgPrimary"),
            Margin = new Thickness(16, 12, 16, 0)
        });
        panel.Children.Add(combo);
        var btnPanel = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 16, 12)
        };
        btnPanel.Children.Add(okBtn);
        btnPanel.Children.Add(cancelBtn);
        panel.Children.Add(btnPanel);
        dialog.Content = panel;

        okBtn.Click += (_, _) => dialog.DialogResult = true;

        if (dialog.ShowDialog() == true && combo.SelectedIndex >= 0)
            return qualities[combo.SelectedIndex];
        return AudioQuality.Standard;
    }

    /// <summary>下载保存目录：优先用户设置的自定义下载目录，其次音乐库目录，最后「我的音乐\AnMusic」。</summary>
    private string GetDownloadDirectory()
    {
        var downloadDir = Settings.DownloadDirectory;
        if (!string.IsNullOrWhiteSpace(downloadDir) && Path.IsPathRooted(downloadDir))
            return downloadDir;

        var musicDir = Settings.MusicDirectory;
        if (!string.IsNullOrWhiteSpace(musicDir) && Path.IsPathRooted(musicDir))
            return musicDir;

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "AnMusic");
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
}
