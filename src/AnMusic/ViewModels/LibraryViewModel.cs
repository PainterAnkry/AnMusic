using System.Collections.ObjectModel;
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
    private readonly IMusicProvider _localProvider;
    private readonly PlaybackBarViewModel _playbackBar;
    private readonly IPlaylistQueue _queue;

    public ObservableCollection<Track> Tracks { get; } = [];

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusText = "点击「打开文件夹」加载音乐";

    public LibraryViewModel(
        LocalFileProvider localProvider,
        PlaybackBarViewModel playbackBar,
        IPlaylistQueue queue)
    {
        _localProvider = localProvider;
        _playbackBar = playbackBar;
        _queue = queue;
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
            StatusText = $"共 {tracks.Count} 首曲目";
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

    [RelayCommand]
    private async Task PlayTrackAsync(Track track)
    {
        // 将整个可见列表设为播放队列，从点击曲目开始
        _queue.SetItems(Tracks, Tracks.IndexOf(track));
        await _playbackBar.LoadAndPlayAsync(track);
    }
}
