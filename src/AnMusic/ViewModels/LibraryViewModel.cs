using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using AnMusic.Models;
using AnMusic.Services.Playlist;
using AnMusic.Services.Providers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace AnMusic.ViewModels;

/// <summary>
/// 音乐库 ViewModel：扫描本地目录、显示曲目列表、双击播放（设置队列）。
/// </summary>
public partial class LibraryViewModel : ObservableObject
{
    private readonly LocalFileProvider _localProvider;
    private readonly PlaybackBarViewModel _playbackBar;
    private readonly IPlaylistQueue _queue;
    private readonly Services.Settings.UserSettingsService _settingsService;

    public ObservableCollection<Track> Tracks { get; } = [];

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusText = "点击「打开文件夹」加载音乐";

    public LibraryViewModel(
        LocalFileProvider localProvider,
        PlaybackBarViewModel playbackBar,
        IPlaylistQueue queue,
        Services.Settings.UserSettingsService settingsService)
    {
        _localProvider = localProvider;
        _playbackBar = playbackBar;
        _queue = queue;
        _settingsService = settingsService;
    }

    [RelayCommand]
    private async Task SelectDirectoryAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择音乐文件夹"
        };

        if (dialog.ShowDialog() != true)
            return;

        await ScanDirectoryAsync(dialog.FolderName);
    }

    public async Task ScanDirectoryAsync(string directory)
    {
        IsLoading = true;
        StatusText = "正在扫描...";
        Tracks.Clear();

        try
        {
            var tracks = await _localProvider.SearchAsync(directory);
            foreach (var t in tracks)
                Tracks.Add(t);
            var extra = MergeExtraFiles();
            StatusText = extra > 0 ? $"共 {Tracks.Count} 首曲目（含手动添加 {extra} 首）" : $"共 {tracks.Count} 首曲目";
        }
        catch (Exception ex)
        {
            StatusText = $"扫描失败: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 把「打开本地文件」手动加进来的文件合并回曲库，返回合并数量。
    /// </summary>
    /// <remarks>
    /// 扫描会整体重建列表，所以每次扫完都要把这些散落文件补回去，否则重启后就没了。
    /// </remarks>
    private int MergeExtraFiles()
    {
        var files = _settingsService.Settings.ExtraLocalFiles;
        if (files is null || files.Count == 0) return 0;

        var known = Tracks
            .Select(t => t.FilePath)
            .Where(p => !string.IsNullOrEmpty(p))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var merged = 0;
        foreach (var file in files.ToList())
        {
            if (!File.Exists(file) || !known.Add(file)) continue;
            Tracks.Add(_localProvider.CreateTrackFromFile(file));
            merged++;
        }
        return merged;
    }

    [RelayCommand]
    private async Task PlayTrackAsync(Track track)
    {
        // 将整个可见列表设为播放队列，从点击曲目开始
        _queue.SetItems(Tracks, Tracks.IndexOf(track));
        await _playbackBar.LoadAndPlayAsync(track);
    }
}
