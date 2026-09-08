using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows;
using System.Windows.Media;
using AnMusic.Models;
using AnMusic.Services.Settings;
using AnMusic.ViewModels;

namespace AnMusic;

/// <summary>
/// 主窗口：MVVM DataContext 绑定 + 进度条拖动 + EQ 弹窗 + 窗口位置记忆 + 全局媒体键。
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly EqualizerViewModel _eqViewModel;
    private readonly UserSettingsService _settingsService;
    private EqualizerWindow? _eqWindow;

    public MainWindow(MainViewModel viewModel, EqualizerViewModel eqViewModel, UserSettingsService settingsService)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _eqViewModel = eqViewModel;
        _settingsService = settingsService;
        DataContext = _viewModel;

        RestoreWindowBounds();

        // 自定义背景图/主题切换时，调整面板不透明度（BeginInvoke 确保 ThemeService 已应用新主题色）
        _viewModel.Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SettingsViewModel.BackgroundImagePath) or nameof(SettingsViewModel.IsDarkTheme))
                Dispatcher.BeginInvoke(ApplyBackgroundTransparency);
        };
        ApplyBackgroundTransparency();
    }

    #region 自定义背景图时面板半透明

    /// <summary>设置背景图后，降低各面板不透明度让背景图透出；无背景图时恢复主题默认。</summary>
    private void ApplyBackgroundTransparency()
    {
        if (string.IsNullOrEmpty(_viewModel.Settings.BackgroundImagePath))
        {
            Resources.Remove("BgPanel");
            Resources.Remove("BgMainAlpha");
            Resources.Remove("BgContentAlpha");
            Resources.Remove("BgPlaybar");
            return;
        }

        // 面板 60%、列表/歌词区/标题行 40%、设置页 35% 不透明度；浅色主题下过高的白色蒙层会让图片完全透不出来
        Resources["BgPanel"] = MakeTranslucent("BgPanel", 0.60);
        Resources["BgMainAlpha"] = MakeTranslucent("BgMainAlpha", 0.40);
        Resources["BgContentAlpha"] = MakeTranslucent("BgContentAlpha", 0.35);
        Resources["BgPlaybar"] = MakeTranslucent("BgPlaybar", 0.60);
    }

    private static SolidColorBrush MakeTranslucent(string resourceKey, double opacity)
    {
        var baseColor = Application.Current.TryFindResource(resourceKey) is SolidColorBrush brush
            ? brush.Color
            : (Color)ColorConverter.ConvertFromString("#1F2227");
        var translucent = new SolidColorBrush(Color.FromArgb(
            (byte)Math.Round(opacity * 255), baseColor.R, baseColor.G, baseColor.B));
        translucent.Freeze();
        return translucent;
    }

    #endregion

    #region 自定义标题栏

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>无边框窗口最大化时四周会溢出屏幕，补内边距；还原时清除。</summary>
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        RootGrid.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
    }

    #endregion

    #region 窗口位置记忆

    private void RestoreWindowBounds()
    {
        var s = _settingsService.Settings;
        if (s.WindowWidth > 0) Width = s.WindowWidth;
        if (s.WindowHeight > 0) Height = s.WindowHeight;
        if (!double.IsNaN(s.WindowLeft)) Left = s.WindowLeft;
        if (!double.IsNaN(s.WindowTop)) Top = s.WindowTop;
        if (s.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void SaveWindowBounds()
    {
        var restoredBounds = WindowState is WindowState.Normal or WindowState.Maximized
            ? RestoreBounds
            : new Rect(Left, Top, Width, Height);

        _settingsService.Update(s =>
        {
            s.WindowMaximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal)
            {
                s.WindowLeft = Left;
                s.WindowTop = Top;
                s.WindowWidth = Width;
                s.WindowHeight = Height;
            }
            else if (restoredBounds.Width > 0)
            {
                // 最大化时保存还原后的尺寸
                s.WindowLeft = restoredBounds.Left;
                s.WindowTop = restoredBounds.Top;
                s.WindowWidth = restoredBounds.Width;
                s.WindowHeight = restoredBounds.Height;
            }
        });
    }

    #endregion

    #region 全局媒体键

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;
    private const int HOTKEY_ID_PLAYPAUSE = 1;
    private const int HOTKEY_ID_PREV = 2;
    private const int HOTKEY_ID_NEXT = 3;
    private const int HOTKEY_ID_STOP = 4;

    // 多媒体虚拟键码
    private const uint VK_MEDIA_PLAY_PAUSE = 0xB3;
    private const uint VK_MEDIA_PREV_TRACK = 0xB1;
    private const uint VK_MEDIA_NEXT_TRACK = 0xB2;
    private const uint VK_MEDIA_STOP = 0xB4;

    private void RegisterMediaKeys()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        RegisterHotKey(hwnd, HOTKEY_ID_PLAYPAUSE, MOD_NOREPEAT, VK_MEDIA_PLAY_PAUSE);
        RegisterHotKey(hwnd, HOTKEY_ID_PREV, MOD_NOREPEAT, VK_MEDIA_PREV_TRACK);
        RegisterHotKey(hwnd, HOTKEY_ID_NEXT, MOD_NOREPEAT, VK_MEDIA_NEXT_TRACK);
        RegisterHotKey(hwnd, HOTKEY_ID_STOP, MOD_NOREPEAT, VK_MEDIA_STOP);
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_HOTKEY) return IntPtr.Zero;
        handled = true;
        var bar = _viewModel.PlaybackBar;
        switch (wParam.ToInt32())
        {
            case HOTKEY_ID_PLAYPAUSE:
                if (bar.PlayPauseCommand.CanExecute(null)) bar.PlayPauseCommand.Execute(null);
                break;
            case HOTKEY_ID_PREV:
                if (bar.PreviousCommand.CanExecute(null)) bar.PreviousCommand.Execute(null);
                break;
            case HOTKEY_ID_NEXT:
                if (bar.NextCommand.CanExecute(null)) bar.NextCommand.Execute(null);
                break;
            case HOTKEY_ID_STOP:
                if (bar.IsLoaded) bar.PlayPauseCommand.Execute(null);
                break;
        }
        return IntPtr.Zero;
    }

    #endregion

    private async void TrackList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TrackList.SelectedItem is Track track)
        {
            await _viewModel.PlayTrackAsync(track);
        }
    }

    #region 侧边栏与右键菜单

    private void CreatePlaylistButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.CreatePlaylistCommand.Execute(null);
    }

    private void SidebarPlaylist_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.Border { DataContext: Models.Playlist playlist })
            _viewModel.SelectPlaylistCommand.Execute(playlist);
    }

    private void TrackList_ContextMenuOpening(object sender, System.Windows.Controls.ContextMenuEventArgs e)
    {
        // 右键未选中的行时先选中该行
        if (e.OriginalSource is System.Windows.DependencyObject source &&
            System.Windows.Controls.ItemsControl.ContainerFromElement(TrackList, source)
                is System.Windows.Controls.ListViewItem { Content: Track clicked })
        {
            TrackList.SelectedItem = clicked;
        }
        // 锁定右键目标曲目，供"添加到歌单"子菜单使用
        _viewModel.PendingMenuTrack = TrackList.SelectedItem as Track;
    }

    /// <summary>右键菜单"重命名歌单"：弹出输入对话框。</summary>
    private void RenamePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: Models.Playlist playlist }) return;

        var input = new System.Windows.Controls.TextBox
        {
            Text = playlist.Name,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        var okBtn = new System.Windows.Controls.Button
        {
            Content = "确定", IsDefault = true, Width = 72, Padding = new Thickness(0, 5, 0, 5), Cursor = System.Windows.Input.Cursors.Hand
        };
        var cancelBtn = new System.Windows.Controls.Button
        {
            Content = "取消", IsCancel = true, Width = 72, Padding = new Thickness(0, 5, 0, 5),
            Margin = new Thickness(8, 0, 0, 0), Cursor = System.Windows.Input.Cursors.Hand
        };

        var dialog = new Window
        {
            Title = "重命名歌单",
            Owner = this,
            Width = 340,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = (System.Windows.Media.Brush)FindResource("BgPanel"),
            Content = new System.Windows.Controls.StackPanel
            {
                Margin = new Thickness(16),
                Children =
                {
                    new System.Windows.Controls.TextBlock
                    {
                        Text = "歌单名称",
                        Foreground = (System.Windows.Media.Brush)FindResource("FgSecondary"),
                        FontSize = 12,
                        Margin = new Thickness(0, 0, 0, 6)
                    },
                    input,
                    new System.Windows.Controls.StackPanel
                    {
                        Orientation = System.Windows.Controls.Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Margin = new Thickness(0, 14, 0, 0),
                        Children = { okBtn, cancelBtn }
                    }
                }
            }
        };
        okBtn.Click += (_, _) => dialog.DialogResult = true;

        if (dialog.ShowDialog() == true)
            _viewModel.RenamePlaylist(playlist, input.Text);
    }

    #endregion

    #region 搜索历史下拉

    private void SearchBox_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        HistoryPopup.IsOpen = _viewModel.SearchHistory.Count > 0;
    }

    private void HistoryItem_Click(object sender, RoutedEventArgs e)
    {
        HistoryPopup.IsOpen = false;
        if ((sender as System.Windows.Controls.Button)?.DataContext is string keyword)
            _viewModel.SearchFromHistoryCommand.Execute(keyword);
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ClearSearchHistoryCommand.Execute(null);
        HistoryPopup.IsOpen = false;
    }

    /// <summary>点击弹层与搜索框以外区域时关闭搜索历史。</summary>
    private void CloseHistoryPopupIfOutside(object sender, MouseButtonEventArgs e)
    {
        if (!HistoryPopup.IsOpen) return;
        if (HistoryPopup.IsMouseOver || SearchBoxBorder.IsMouseOver) return;
        HistoryPopup.IsOpen = false;
    }

    #endregion

    private void ProgressSlider_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
    {
        _viewModel.PlaybackBar.BeginDrag();
    }

    private void ProgressSlider_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        _viewModel.PlaybackBar.EndDrag(ProgressSlider.Value);
    }

    /// <summary>点击进度条任意位置直接切换进度（配合 IsMoveToPointEnabled）。</summary>
    private void ProgressSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var bar = _viewModel.PlaybackBar;

        // 抓住滑块 → 走常规拖动流程
        if (e.OriginalSource is DependencyObject src && IsOverThumb(src))
        {
            bar.BeginDrag();
            return;
        }

        // 点击轨道 → 立即跳转到点击位置（MoveToPoint 随后把滑块移到该处，可继续拖动）
        if (sender is System.Windows.Controls.Slider s)
        {
            var p = e.GetPosition(s);
            var ratio = Math.Clamp(p.X / Math.Max(1.0, s.ActualWidth), 0, 1);
            var target = s.Minimum + ratio * (s.Maximum - s.Minimum);
            bar.BeginDrag();
            bar.SeekTo(target);
        }
    }

    /// <summary>命中点是否在滑块（Thumb）内。</summary>
    private static bool IsOverThumb(DependencyObject source)
    {
        while (source is not null)
        {
            if (source is System.Windows.Controls.Primitives.Thumb) return true;
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private void ProgressSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _viewModel.PlaybackBar.EndDrag(ProgressSlider.Value);
    }

    private void SearchTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            _viewModel.SearchCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void EqButton_Click(object sender, RoutedEventArgs e)
    {
        if (_eqWindow is null || !_eqWindow.IsLoaded)
        {
            _eqWindow = new EqualizerWindow(_eqViewModel) { Owner = this };
        }
        _eqWindow.Show();
        _eqWindow.Activate();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ShowSettingsCommand.Execute(null);
    }

    /// <summary>播放列表按钮：打开时刷新"接下来播放"，再次点击关闭。</summary>
    private void UpNextButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.PlaybackBar.RefreshUpNext();
        UpNextPopup.IsOpen = !UpNextPopup.IsOpen;
    }

    private void CoverImage_Click(object sender, MouseButtonEventArgs e)
    {
        _viewModel.ToggleLyricsCommand.Execute(null);
        e.Handled = true;
    }

    private void PlayBarCover_Click(object sender, MouseButtonEventArgs e)
    {
        _viewModel.ToggleLyricsCommand.Execute(null);
        e.Handled = true;
    }

    // Win11 DWM 圆角窗口属性
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Win11 DWM 圆角窗口；Win10 无此属性，静默忽略
        try
        {
            var hwndForRound = new WindowInteropHelper(this).Handle;
            var pref = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwndForRound, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }
        catch { /* 老系统或 DWM 不可用时忽略 */ }

        // 注册全局媒体键
        RegisterMediaKeys();
        if (HwndSource.FromHwnd(new WindowInteropHelper(this).Handle) is { } source)
            source.AddHook(HwndHook);
    }

    protected override void OnClosed(EventArgs e)
    {
        // 注销全局媒体键
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            UnregisterHotKey(hwnd, HOTKEY_ID_PLAYPAUSE);
            UnregisterHotKey(hwnd, HOTKEY_ID_PREV);
            UnregisterHotKey(hwnd, HOTKEY_ID_NEXT);
            UnregisterHotKey(hwnd, HOTKEY_ID_STOP);
        }
        base.OnClosed(e);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        SaveWindowBounds();
        base.OnClosing(e);
    }
}
