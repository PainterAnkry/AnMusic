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

        // "在线一起听" 面板内的 ✕ 按钮请求关闭弹窗
        _viewModel.ListenTogether.PanelToggleRequested += () =>
            Dispatcher.BeginInvoke(() => ListenTogetherPopup.IsOpen = false);

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
        if (sender is System.Windows.Controls.Slider s)
            _viewModel.PlaybackBar.EndDrag(s.Value);
        else
            _viewModel.PlaybackBar.EndDrag(ProgressSlider.Value);
    }

    /// <summary>
    /// 进度条按下：抓住滑块时交给 Thumb 自带拖拽；点击轨道时立即按点击位置线性映射跳转并进入拖动状态，
    /// 松手时 EndDrag 收尾（拖动过则 Seek 到最后预览位置，未拖动则与按下位置一致）。
    /// 注意不能依赖 Slider.IsMoveToPointEnabled：它由 Slider 的类处理程序实现，会在本实例处理程序
    /// 之前把事件标记为 Handled，只移动滑块外观而不触发播放器 Seek——正是"点击轨道无法跳转"的原因。
    /// </summary>
    private void ProgressSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var bar = _viewModel.PlaybackBar;

        // 抓住滑块 → 交给 Thumb 拖拽（不拦截事件，确保 Thumb 自身捕获鼠标后正常拖动）
        if (e.OriginalSource is DependencyObject src && IsOverThumb(src))
        {
            bar.BeginDrag();
            return;
        }

        // 点击轨道：无已加载曲目时不拦截，保持控件默认行为
        if (sender is not System.Windows.Controls.Slider s || !bar.IsLoaded) return;

        // 值由点击位置线性映射得出（不含滑块宽度补偿/当前值推算），点击即定位播放
        var target = GetTrackValueAt(s, e);
        s.SetCurrentValue(System.Windows.Controls.Primitives.RangeBase.ValueProperty, target);
        bar.SeekTo(target);
        bar.BeginDrag();
        s.CaptureMouse(); // 按住可从点击处继续拖动，在滑块外松开也能正常收尾
        e.Handled = true; // 阻止 RepeatButton 按 LargeChange 级进并二次捕获鼠标
    }

    /// <summary>按住进度条拖动时实时预览位置（真实 Seek 在 MouseUp 统一收尾）。</summary>
    private void ProgressSlider_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (sender is not System.Windows.Controls.Slider s || !s.IsMouseCaptured) return;
        var bar = _viewModel.PlaybackBar;
        if (!bar.IsLoaded) return;
        // 更新滑块与时间文本（绑定推送），引擎在 MouseUp 的 EndDrag 统一 Seek
        bar.PositionSeconds = GetTrackValueAt(s, e);
    }

    /// <summary>按点击位置线性映射进度值：点击轨道 60% 处即跳转到总时长 60%，
    /// 不做滑块宽度/当前值相关的推算，保证“点哪播哪”无累计偏差。</summary>
    private static double GetTrackValueAt(System.Windows.Controls.Slider slider, MouseEventArgs e)
    {
        double ratio;
        if (slider.Template?.FindName("PART_Track", slider) is System.Windows.Controls.Primitives.Track track &&
            track.ActualWidth > 0)
        {
            var x = e.GetPosition(track).X;
            ratio = Math.Clamp(x / track.ActualWidth, 0, 1);
        }
        else
        {
            // 兜底：模板不可用时按控件宽度线性映射
            var p = e.GetPosition(slider);
            ratio = Math.Clamp(p.X / Math.Max(1.0, slider.ActualWidth), 0, 1);
        }
        return slider.Minimum + ratio * (slider.Maximum - slider.Minimum);
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
        if (sender is System.Windows.Controls.Slider s)
        {
            if (s.IsMouseCaptured) s.ReleaseMouseCapture();
            _viewModel.PlaybackBar.EndDrag(s.Value);
        }
        else
        {
            _viewModel.PlaybackBar.EndDrag(ProgressSlider.Value);
        }
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

    /// <summary>在线一起听：切换面板弹窗。</summary>
    private void ListenTogetherButton_Click(object sender, RoutedEventArgs e)
    {
        ListenTogetherPopup.IsOpen = !ListenTogetherPopup.IsOpen;
    }

    /// <summary>用户按钮：切换资料下拉面板（昵称/等级/经验在面板内查看与修改）。</summary>
    private void UserButton_Click(object sender, RoutedEventArgs e)
    {
        UserPopup.IsOpen = !UserPopup.IsOpen;
    }

    /// <summary>资料面板头像：选择图片并打开自由裁剪窗口。</summary>
    private void PopupAvatar_Click(object sender, MouseButtonEventArgs e)
    {
        UserPopup.IsOpen = false;
        _viewModel.ChangeAvatarCommand.Execute(null);
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

    /// <summary>歌词页点击歌手名 → 搜索该歌手。</summary>
    private async void LyricArtist_Click(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.PlaybackBar.CurrentArtist is { Length: > 0 } artist)
        {
            _viewModel.ToggleLyricsCommand.Execute(null); // 收起歌词
            await _viewModel.SearchForTextAsync(artist);
        }
    }

    /// <summary>歌词页点击专辑名 → 搜索该专辑。</summary>
    private async void LyricAlbum_Click(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.PlaybackBar.CurrentTrack?.Album is { Length: > 0 } album)
        {
            _viewModel.ToggleLyricsCommand.Execute(null); // 收起歌词
            await _viewModel.SearchForTextAsync(album);
        }
    }

    /// <summary>歌词搜索框回车 → 按输入的歌名在线匹配歌词（支持“歌名 - 歌手”）。</summary>
    private void LyricSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _viewModel.Lyrics.SearchLyricsByNameCommand.Execute(null);
            e.Handled = true;
        }
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

    private System.Windows.Forms.NotifyIcon? _trayIcon;

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        var behavior = _settingsService.Settings.CloseBehavior;

        if (behavior == 0) // 每次询问
        {
            var result = MessageBox.Show(
                "是要后台运行还是直接关闭程序？\n\n选择后台运行：程序将最小化到系统托盘，继续播放音乐。\n选择直接关闭：退出程序。\n\n可在设置中修改默认行为。",
                "关闭确认", MessageBoxButton.YesNoCancel, MessageBoxImage.Question,
                MessageBoxResult.Yes);
            if (result == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
                return;
            }
            behavior = result == MessageBoxResult.Yes ? 1 : 2;
            // 记住选择
            _settingsService.Update(s => s.CloseBehavior = behavior);
        }

        if (behavior == 1) // 后台运行
        {
            e.Cancel = true;
            this.Hide();
            EnsureTrayIcon();
            _trayIcon!.Visible = true;
            _trayIcon.ShowBalloonTip(2000, "AnMusic", "正在后台运行，双击托盘图标恢复", System.Windows.Forms.ToolTipIcon.Info);
            return;
        }

        // 直接关闭
        SaveWindowBounds();
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        base.OnClosing(e);
    }

    /// <summary>创建系统托盘图标（双击恢复窗口）。</summary>
    private void EnsureTrayIcon()
    {
        if (_trayIcon is not null) return;
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(
                Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location),
            Text = "AnMusic",
            Visible = false
        };
        _trayIcon.DoubleClick += (_, _) =>
        {
            this.Show();
            this.WindowState = WindowState.Normal;
            this.Activate();
            _trayIcon.Visible = false;
        };
    }
}
