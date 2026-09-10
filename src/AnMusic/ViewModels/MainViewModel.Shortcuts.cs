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
/// MainViewModel 的快捷键动作分发部分（partial 拆分，便于维护）。
/// </summary>
public partial class MainViewModel
{

    /// <summary>请求把焦点移到顶部搜索框（由主窗口实现，ViewModel 不触碰控件）。</summary>
    public event Action? SearchFocusRequested;

    /// <summary>请求显示/隐藏主窗口（由主窗口实现，配合后台运行）。</summary>
    public event Action? ToggleMainWindowRequested;

    /// <summary>执行一个快捷键动作（应用内按键与全局热键共用入口）。</summary>
    public void ExecuteShortcut(string actionId)
    {
        switch (actionId)
        {
            case Services.Shortcuts.ShortcutActions.PlayPause:
                _playbackBar.PlayPauseCommand.Execute(null);
                break;
            case Services.Shortcuts.ShortcutActions.NextTrack:
                _playbackBar.NextCommand.Execute(null);
                break;
            case Services.Shortcuts.ShortcutActions.PrevTrack:
                _playbackBar.PreviousCommand.Execute(null);
                break;
            case Services.Shortcuts.ShortcutActions.VolumeUp:
                _playbackBar.Volume = Math.Clamp(_playbackBar.Volume + 0.05, 0, 1);
                break;
            case Services.Shortcuts.ShortcutActions.VolumeDown:
                _playbackBar.Volume = Math.Clamp(_playbackBar.Volume - 0.05, 0, 1);
                break;
            case Services.Shortcuts.ShortcutActions.ToggleMute:
                _playbackBar.ToggleMuteCommand.Execute(null);
                break;
            case Services.Shortcuts.ShortcutActions.SeekForward:
                SeekBySeconds(5);
                break;
            case Services.Shortcuts.ShortcutActions.SeekBackward:
                SeekBySeconds(-5);
                break;
            case Services.Shortcuts.ShortcutActions.CyclePlayMode:
                _playbackBar.CyclePlayModeCommand.Execute(null);
                break;
            case Services.Shortcuts.ShortcutActions.ToggleFavorite:
                ToggleCurrentFavorite();
                break;
            case Services.Shortcuts.ShortcutActions.ToggleLyricsPage:
                ToggleLyricsCommand.Execute(null);
                break;
            case Services.Shortcuts.ShortcutActions.ToggleDesktopLyrics:
                ToggleDesktopLyricsCommand.Execute(null);
                break;
            case Services.Shortcuts.ShortcutActions.ToggleMiniPlayer:
                ToggleMiniPlayerCommand.Execute(null);
                break;
            case Services.Shortcuts.ShortcutActions.FocusSearch:
                SearchFocusRequested?.Invoke();
                break;
            case Services.Shortcuts.ShortcutActions.OpenSettings:
                IsShowingSettings = true;
                break;
            case Services.Shortcuts.ShortcutActions.ToggleMainWindow:
                ToggleMainWindowRequested?.Invoke();
                break;
        }
    }

    /// <summary>相对当前位置快进/快退（秒），越界自动收敛到 0 ~ 总时长。</summary>
    private void SeekBySeconds(double deltaSeconds)
    {
        if (!_playbackBar.IsLoaded) return;
        var target = Math.Clamp(_playbackBar.PositionSeconds + deltaSeconds, 0, _playbackBar.DurationSeconds);
        _playbackBar.SeekTo(target);
    }
}
