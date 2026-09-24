using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
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
    private readonly Services.Shortcuts.ShortcutService _shortcuts;
    private EqualizerWindow? _eqWindow;

    /// <summary>系统正在注销/关机（此时关闭窗口不应询问或转入后台，直接退出）。</summary>
    private bool _sessionEnding;

    public MainWindow(MainViewModel viewModel, EqualizerViewModel eqViewModel, UserSettingsService settingsService,
        Services.Shortcuts.ShortcutService shortcutService)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _eqViewModel = eqViewModel;
        _settingsService = settingsService;
        _shortcuts = shortcutService;
        DataContext = _viewModel;

        // 键盘快捷键：应用内按键匹配（窗口层拦截，文本框输入自动放行）
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        _shortcuts.Invoked += OnShortcutInvoked;                 // 全局热键触发
        _viewModel.SearchFocusRequested += FocusSearchBox;
        _viewModel.ToggleMainWindowRequested += ToggleMainWindowFromShortcut;

        // Windows 注销/关机时跳过关闭询问与托盘转入，直接退出
        Microsoft.Win32.SystemEvents.SessionEnding += (_, _) => _sessionEnding = true;

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

        // 页面/数据源切换（绑定替换 ItemsSource）→ 重置排序指示并重新应用过滤词
        System.ComponentModel.DependencyPropertyDescriptor.FromProperty(
                System.Windows.Controls.ItemsControl.ItemsSourceProperty,
                typeof(System.Windows.Controls.ItemsControl))
            .AddValueChanged(TrackList, OnTrackListItemsSourceChanged);

        // 过渡动画：设置页/歌词页出现时淡入上移；播放栏封面切歌时轻闪
        Views.Animations.AutoFadeInOnVisible(SettingsPageHost);
        Views.Animations.AutoFadeInOnVisible(LyricsOverlay);
        _viewModel.PlaybackBar.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlaybackBarViewModel.CoverPath))
                Views.Animations.SwapFade(PlayBarCover, 0.3, 260);
            // 歌词页的黑胶唱片跟随播放状态转动 / 停住
            if (e.PropertyName == nameof(PlaybackBarViewModel.IsPlaying))
                UpdateDiscSpin();
        };
        // 歌词页开关也要跟着启停（关着的时候没必要空转）
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsLyricsOpen)) UpdateDiscSpin();
        };

    }

    /// <summary>
    /// 歌词页黑胶唱片的转动：播放时匀速转（24 秒一圈），暂停即停在当前角度。
    /// </summary>
    /// <remarks>
    /// 用 DispatcherTimer 逐帧累加角度，而不是 Storyboard：
    /// 一来暂停/继续天然就是"停表/继续"，不需要管理动画时钟；
    /// 二来只在"歌词页打开且正在播放"时才走动，不白烧帧。
    /// </remarks>
    private DispatcherTimer? _discSpinTimer;
    private DateTime _discSpinLastTick;

    private const double DiscSecondsPerTurn = 24;

    private void UpdateDiscSpin()
    {
        if (DiscRotate is null) return; // 歌词页还没构建出来

        if (_viewModel.IsLyricsOpen && _viewModel.PlaybackBar.IsPlaying)
            StartDiscSpin();
        else
            StopDiscSpin();
    }

    private void StartDiscSpin()
    {
        if (_discSpinTimer is not null) return;

        _discSpinLastTick = DateTime.UtcNow;
        _discSpinTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33) // 约 30fps，转得很慢，够顺滑又不费
        };
        _discSpinTimer.Tick += (_, _) =>
        {
            var now = DateTime.UtcNow;
            var elapsed = (now - _discSpinLastTick).TotalSeconds;
            _discSpinLastTick = now;
            DiscRotate.Angle = (DiscRotate.Angle + elapsed / DiscSecondsPerTurn * 360) % 360;
        };
        _discSpinTimer.Start();
    }

    private void StopDiscSpin()
    {
        _discSpinTimer?.Stop();
        _discSpinTimer = null;
    }

    #region 快捷键

    /// <summary>应用内按键：命中绑定即执行动作并拦截事件。</summary>
    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_shortcuts.IsCapturing) return;                      // 设置页正在改键
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Services.Shortcuts.ShortcutKeys.IsModifierKey(key)) return;

        if (_shortcuts.TryMatch(key, Keyboard.Modifiers, IsTextInputFocused(), out var actionId) &&
            actionId is not null)
        {
            _viewModel.ExecuteShortcut(actionId);
            e.Handled = true;
        }
    }

    /// <summary>焦点是否在文本输入控件内：是则纯按键（空格/方向键等）让给输入框。</summary>
    private static bool IsTextInputFocused()
    {
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return true;
        if (Keyboard.FocusedElement is System.Windows.Controls.PasswordBox) return true;
        return Keyboard.FocusedElement is System.Windows.Controls.ComboBox { IsEditable: true };
    }

    /// <summary>全局热键回调（WndProc 在 UI 线程触发）。</summary>
    private void OnShortcutInvoked(string actionId) => _viewModel.ExecuteShortcut(actionId);

    /// <summary>快捷键「聚焦搜索框」：打开搜索区并把光标放进输入框。</summary>
    private void FocusSearchBox()
    {
        Dispatcher.BeginInvoke(() =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        });
    }

    /// <summary>快捷键「显示 / 隐藏主窗口」。</summary>
    private void ToggleMainWindowFromShortcut()
    {
        if (IsVisible && WindowState != WindowState.Minimized)
        {
            EnsureTrayIcon();
            if (_trayIcon is not null) _trayIcon.Visible = true;
            Hide();
        }
        else
        {
            ShowFromTray();
        }
    }

    #endregion

    #region 自定义背景图时面板半透明

    /// <summary>设置背景图后，降低各面板不透明度让背景图透出；无背景图时恢复主题默认。</summary>
    private void ApplyBackgroundTransparency()
    {
        if (string.IsNullOrEmpty(_viewModel.Settings.BackgroundImagePath))
        {
            Resources.Remove("BgMainAlpha");
            Resources.Remove("BgContentAlpha");
            Resources.Remove("BgPlaybar");
            return;
        }

        // 只让"内容区"半透明（壁纸透出来），窗口外壳保持皮肤本色：
        // BgPanel 是标题栏 / 视图标题行 / 侧边栏的底色，一旦跟着透明，
        // 浅色壁纸透上来会让深色皮肤的标题栏变成浅色、文字糊到看不清（换皮肤时尤其明显）。
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
                if (bar.PlayPauseCommand.CanExecute(null)) bar.PlayPauseCommand.Execute(null);
                break;
        }
        return IntPtr.Zero;
    }

    #endregion

    private async void TrackList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TrackList.SelectedItem is Track track)
        {
            // 按当前视图（含排序/过滤）顺序建立播放队列，保证“所见即所播”
            var ordered = CollectionViewSource.GetDefaultView(TrackList.ItemsSource)
                .Cast<Track>()
                .ToList();
            await _viewModel.PlayTrackAsync(track, ordered);
        }
    }

    #region 列表表头排序 + 当前列表过滤

    private string? _trackSortField; // "Title"/"Artist"/"Album"/"Duration"
    private bool _trackSortDescending;

    /// <summary>列表数据源切换（切页面/重扫/搜索）：复位排序指示，并把过滤词应用到新列表。</summary>
    private void OnTrackListItemsSourceChanged(object? sender, EventArgs e)
    {
        // 切换视图（全部音乐/歌单/搜索结果…）后保留用户的列排序，重新应用到新视图上
        ApplySort();
        UpdateHeaderSortGlyphs();
        ApplyListFilter(_viewModel.ListFilterText);

        // 视图切换时列表淡入（150ms 缓出，够顺滑又不拖手）
        Views.Animations.FadeIn(TrackList, 150, 0, 6);
    }

    /// <summary>表头按钮点击（每列表头均为带 Tag 的按钮，点击直接触发，不依赖表头内部事件冒泡）。</summary>
    /// <summary>
    /// 表头点击：同一列依次为 升序 → 降序 → 取消排序（恢复列表原始顺序）。
    /// 排序通过 CollectionView 的 SortDescriptions 实现，不改动源集合，
    /// 因此取消排序后能回到音乐库/歌单本来的顺序，且与列表过滤可叠加。
    /// </summary>
    private void TrackHeaderButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string field } || field.Length == 0) return;

        if (_trackSortField != field)
        {
            _trackSortField = field;
            _trackSortDescending = false;
        }
        else if (!_trackSortDescending)
        {
            _trackSortDescending = true;
        }
        else
        {
            _trackSortField = null;      // 第三次点击：取消排序
            _trackSortDescending = false;
        }

        ApplySort();
        UpdateHeaderSortGlyphs();
    }

    /// <summary>
    /// 应用/清除排序：排序只作用于当前列表的视图（CustomSort），
    /// 源集合顺序保持不变——取消排序即可回到原始顺序，列表过滤也依然生效。
    /// 歌名/歌手/专辑按不区分大小写比较，时长按时间值比较。
    /// </summary>
    private void ApplySort()
    {
        if (TrackList.ItemsSource is not System.Collections.IEnumerable source) return;

        // ListCollectionView 才支持 CustomSort；普通 CollectionView 则退化为 SortDescriptions
        if (System.Windows.Data.CollectionViewSource.GetDefaultView(source) is System.Windows.Data.ListCollectionView listView)
        {
            listView.CustomSort = string.IsNullOrEmpty(_trackSortField)
                ? null   // 取消排序：回到源集合本来的顺序
                : new Services.Formatting.TrackSortComparer(_trackSortField, _trackSortDescending);
            return;
        }

        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(source);
        view.SortDescriptions.Clear();
        if (string.IsNullOrEmpty(_trackSortField)) return;
        view.SortDescriptions.Add(new System.ComponentModel.SortDescription(
            _trackSortField switch
            {
                "Artist" => nameof(Models.Track.Artist),
                "Album" => nameof(Models.Track.Album),
                "Duration" => nameof(Models.Track.Duration),
                _ => nameof(Models.Track.Title)
            },
            _trackSortDescending
                ? System.ComponentModel.ListSortDirection.Descending
                : System.ComponentModel.ListSortDirection.Ascending));
    }

    /// <summary>表头按钮追加 ▲/▼ 排序指示。</summary>
    private void UpdateHeaderSortGlyphs()
    {
        if (TrackList.View is not System.Windows.Controls.GridView gv) return;
        foreach (var c in gv.Columns)
        {
            if (c.Header is not System.Windows.Controls.Button { Tag: string field } btn) continue;
            var baseName = field switch
            {
                "Title" => "标题",
                "Artist" => "艺术家",
                "Album" => "专辑",
                _ => "时长"
            };
            btn.Content = _trackSortField == field
                ? baseName + (_trackSortDescending ? " ▼" : " ▲")
                : baseName;
            btn.ToolTip = "点击切换：升序 → 降序 → 取消排序";
        }
    }

    /// <summary>当前列表过滤（默认视图 Filter；清空恢复）。</summary>
    private void ApplyListFilter(string? keyword)
    {
        keyword = keyword?.Trim();
        if (TrackList.ItemsSource is not System.Collections.IEnumerable src) return;
        var view = CollectionViewSource.GetDefaultView(src);
        if (string.IsNullOrEmpty(keyword))
        {
            view.Filter = null;
            return;
        }
        view.Filter = o => o is Track t &&
                           (t.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                            t.Artist.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                            t.Album.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private void FilterBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => ApplyListFilter(FilterBox.Text);

    private void FilterBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            FilterBox.Text = "";
            ApplyListFilter(null);
        }
    }

    #endregion

    #region 侧边栏与右键菜单

    private void CreatePlaylistButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.CreatePlaylistCommand.Execute(null);
    }

    /// <summary>从网易云/QQ音乐歌单分享链接导入（弹输入框 → 插件解析 → 新建歌单）。</summary>
    private async void ImportPlaylistButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.ImportPlaylistFromLinkCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            Views.UiDialog.Error("导入歌单失败", ex);
        }
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

    /// <summary>
    /// 列表里点击歌手名 / 专辑名：跳转到歌手页 / 专辑页（与右键菜单、歌词页点击走同一条路径）。
    /// B 站曲目的「专辑」列例外 —— 那是来源标记，点击改为打开该视频的分P 全集。
    /// </summary>
    private void CellSearch_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: Models.Track track } element) return;

        // B 站曲目的「专辑」列是来源标记（"B站视频"/"B站视频 · 共 12P"），拿去搜索没有意义：
        // 点它改为打开该视频的全部剧集（分P）——多分P 视频的搜索结果只有第 1 P
        if (element.Tag as string == "Album" && track.ProviderId == "bilibili")
        {
            e.Handled = true;
            _viewModel.OpenBilibiliPartsCommand.Execute(track);
            return;
        }

        var kind = element.Tag as string;
        if (kind == "Artist" && !string.IsNullOrWhiteSpace(track.Artist))
        {
            e.Handled = true;
            _ = _viewModel.ShowArtistPageAsync(track.Artist);
            return;
        }
        if (kind == "Album" && !string.IsNullOrWhiteSpace(track.Album))
        {
            e.Handled = true;
            _ = _viewModel.ShowAlbumPageAsync(track.Album);
        }
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
    /// <summary>播放条「更多」按钮：展开 桌面歌词 / 一起听 / 迷你悬浮卡片 菜单。</summary>
    private void MoreButton_Click(object sender, RoutedEventArgs e)
        => MorePopup.IsOpen = !MorePopup.IsOpen;

    /// <summary>更多 → 桌面歌词：开关独立置顶歌词窗（菜单保持可继续点其他项）。</summary>
    private void MoreDesktopLyrics_Click(object sender, RoutedEventArgs e)
        => _viewModel.ToggleDesktopLyricsCommand.Execute(null);

    /// <summary>更多 → 一起听：把一起听面板挂到「更多」按钮上弹出（面板单例，状态共享）。</summary>
    private void MoreListenTogether_Click(object sender, RoutedEventArgs e)
    {
        MorePopup.IsOpen = false;
        ListenTogetherPopup.PlacementTarget = MoreBtn;
        ListenTogetherPopup.IsOpen = true;
    }

    /// <summary>更多 → 迷你悬浮卡片：开关卡片窗（开启时主窗口自动最小化）。</summary>
    private void MoreMiniPlayer_Click(object sender, RoutedEventArgs e)
    {
        MorePopup.IsOpen = false;
        _viewModel.ToggleMiniPlayerCommand.Execute(null);
    }

    /// <summary>更多 → 分享当前歌曲：复制分享文本并弹窗展示。</summary>
    private void MoreShare_Click(object sender, RoutedEventArgs e)
    {
        MorePopup.IsOpen = false;
        _viewModel.ShareCurrentTrack();
    }

    /// <summary>主页卡片点击：执行卡片自带的既有命令。</summary>
    private void HomeCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: HomeCard card }) return;
        e.Handled = true;

        if (card.Command?.CanExecute(card.CommandParameter) == true)
            card.Command.Execute(card.CommandParameter);
    }

    /// <summary>
    /// 搜索结果「综合」页的歌手/专辑卡片区收到滚轮：卡片区自己滚，滚到尽头把滚轮接力给下方曲目列表。
    /// </summary>
    /// <remarks>
    /// 卡片区如果不转发，鼠标停在专辑卡上时整页滚不动
    /// （用户反馈："卡在专辑模块，看不到最下面的单曲列表"）；
    /// 只在尽头接力，滚到一半不会突然跳去滚列表。
    /// </remarks>
    private void SearchGroupPanel_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (FindScrollViewer(sender as DependencyObject) is { } panelScroll && CanStillScroll(panelScroll, e.Delta))
        {
            e.Handled = true;
            panelScroll.ScrollToVerticalOffset(panelScroll.VerticalOffset - e.Delta);
            return;
        }

        if (FindScrollViewer(TrackList) is not { } listScroll) return;
        e.Handled = true;
        listScroll.ScrollToVerticalOffset(listScroll.VerticalOffset - e.Delta);
    }

    /// <summary>滚轮往上（delta&gt;0）时还有上方内容 / 往下时还有下方内容。</summary>
    private static bool CanStillScroll(ScrollViewer sv, int delta)
        => delta > 0
            ? sv.VerticalOffset > 0.5
            : sv.VerticalOffset < sv.ScrollableHeight - 0.5;

    /// <summary>在可视树里找某个控件内部的 ScrollViewer（ListView 自带一个）。</summary>
    private static ScrollViewer? FindScrollViewer(DependencyObject? root)
    {
        if (root is null) return null;
        if (root is ScrollViewer sv) return sv;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found)
                return found;
        return null;
    }

    /// <summary>标题栏 🎨 皮肤按钮：开关皮肤选择面板。</summary>
    private void SkinButton_Click(object sender, RoutedEventArgs e)
        => SkinPopup.IsOpen = !SkinPopup.IsOpen;

    /// <summary>标题栏 私信 按钮：开关通知面板；打开即视为已读（小红点消失）。</summary>
    private void InboxButton_Click(object sender, RoutedEventArgs e)
    {
        InboxPopup.IsOpen = !InboxPopup.IsOpen;
        if (InboxPopup.IsOpen) _viewModel.Inbox.MarkRead();
    }

    /// <summary>选中某个皮肤后收起面板（切换即时生效，无需确认）。</summary>
    private void SkinOption_Click(object sender, RoutedEventArgs e)
        => SkinPopup.IsOpen = false;

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

    /// <summary>播放队列按钮：打开前刷新队列数据（再次点击关闭）。</summary>
    private void UpNextButton_Click(object sender, RoutedEventArgs e)
    {
        QueuePopup.IsOpen = false;
        _viewModel.RefreshQueuePanel();
        QueuePopup.IsOpen = true;
    }

    /// <summary>队列面板双击行 → 播放该曲目。</summary>
    private void QueueList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (QueueList.SelectedItem is ViewModels.MainViewModel.QueueRowItem row)
            _ = _viewModel.PlayQueueTrackCommand.ExecuteAsync(row.Track);
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

    /// <summary>歌词页点击歌手名 → 打开歌手页。</summary>
    private async void LyricArtist_Click(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.PlaybackBar.CurrentArtist is { Length: > 0 } artist)
        {
            _viewModel.ToggleLyricsCommand.Execute(null); // 收起歌词
            await _viewModel.ShowArtistPageAsync(artist);
        }
    }

    /// <summary>歌词页点击专辑名 → 打开专辑页。</summary>
    private async void LyricAlbum_Click(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.PlaybackBar.CurrentTrack?.Album is { Length: > 0 } album)
        {
            _viewModel.ToggleLyricsCommand.Execute(null); // 收起歌词
            await _viewModel.ShowAlbumPageAsync(album);
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

        // 用户自定义的全局快捷键（窗口隐藏/最小化时依旧有效）
        try
        {
            _shortcuts.AttachGlobalHost(new WindowInteropHelper(this).Handle);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Shortcut] 全局热键初始化失败: {ex.Message}");
        }
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

    /// <summary>托盘“退出”菜单触发：关闭窗口时跳过询问直接退出。</summary>
    private bool _forceExit;

    /// <summary>从托盘恢复主窗口（双击托盘 / 单实例通知 / 托盘菜单）。</summary>
    public void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        if (_trayIcon is not null) _trayIcon.Visible = false;
    }

    /// <summary>关闭方式选择结果。</summary>
    private enum CloseChoice { Background, Quit, Cancel }

    /// <summary>“每次询问”模式：弹出明确的三选一对话框（后台运行/直接退出/取消），Esc 或点 ✕ 视作取消。</summary>
    private static CloseChoice AskCloseChoice(Window owner)
    {
        var result = CloseChoice.Cancel;
        var win = new Window
        {
            Title = "关闭 AnMusic",
            Width = 430,
            SizeToContent = System.Windows.SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Owner = owner,
            Background = (Brush)(owner.TryFindResource("BgPanel") ?? System.Windows.Media.Brushes.White)
        };

        var msg = new System.Windows.Controls.TextBlock
        {
            Text = "关闭窗口后希望程序如何运行？\n\n后台运行：最小化到系统托盘，继续播放音乐；\n直接退出：结束程序，停止播放。\n\n默认行为可在 设置 → 关闭行为 中修改。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)(owner.TryFindResource("FgPrimary") ?? System.Windows.Media.Brushes.Black),
            Margin = new Thickness(6, 6, 6, 16)
        };

        var btnBg = new System.Windows.Controls.Button { Content = "后台运行", Width = 100, Margin = new Thickness(0, 0, 10, 0), Cursor = System.Windows.Input.Cursors.Hand };
        var btnQuit = new System.Windows.Controls.Button { Content = "直接退出", Width = 100, Margin = new Thickness(0, 0, 10, 0), Cursor = System.Windows.Input.Cursors.Hand, IsDefault = true };
        var btnCancel = new System.Windows.Controls.Button { Content = "取消", Width = 80, Cursor = System.Windows.Input.Cursors.Hand, IsCancel = true };
        btnBg.Click += (_, _) => { result = CloseChoice.Background; win.Close(); };
        btnQuit.Click += (_, _) => { result = CloseChoice.Quit; win.Close(); };
        btnCancel.Click += (_, _) => win.Close();
        // 复用主题按钮样式（若资源缺失则保持默认外观）
        foreach (var b in new[] { btnBg, btnQuit, btnCancel })
        {
            if (owner.TryFindResource("BtnStyle") is Style s) b.Style = s;
        }

        var bar = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        bar.Children.Add(btnBg);
        bar.Children.Add(btnQuit);
        bar.Children.Add(btnCancel);

        var root = new System.Windows.Controls.StackPanel { Margin = new Thickness(18, 14, 18, 14) };
        root.Children.Add(msg);
        root.Children.Add(bar);
        win.Content = root;
        win.ShowDialog();
        return result;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        var behavior = _settingsService.Settings.CloseBehavior;

        // 系统注销/关机：一律直接退出，不询问、不进托盘
        if (_sessionEnding || _forceExit)
        {
            SaveWindowBounds();
            if (_trayIcon is not null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }
            base.OnClosing(e);
            return;
        }

        if (behavior == 0) // 每次询问（不自动记住本次选择，避免误选后一直后台）
        {
            switch (AskCloseChoice(this))
            {
                case CloseChoice.Background: behavior = 1; break;
                case CloseChoice.Quit: behavior = 2; break;
                default:
                    e.Cancel = true; // 取消/Esc/关掉弹窗：维持窗口
                    return;
            }
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

    /// <summary>创建系统托盘图标：双击恢复窗口，右键菜单提供播放控制与退出。</summary>
    private void EnsureTrayIcon()
    {
        if (_trayIcon is not null) return;

        // 单文件发布下 Assembly.Location 为空，必须用进程路径（或基目录拼 exe 名）取图标
        var exePath = Environment.ProcessPath
                      ?? Path.Combine(AppContext.BaseDirectory, "AnMusic.exe");
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath)
                   ?? System.Drawing.SystemIcons.Application,
            Text = "AnMusic",
            Visible = false
        };
        _trayIcon.DoubleClick += (_, _) => ShowFromTray();

        // 右键菜单：播放控制 / 显示窗口 / 退出
        var menu = new System.Windows.Forms.ContextMenuStrip();
        var bar = _viewModel.PlaybackBar;

        var playPause = new System.Windows.Forms.ToolStripMenuItem("播放/暂停");
        playPause.Click += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (bar.PlayPauseCommand.CanExecute(null)) bar.PlayPauseCommand.Execute(null);
        });
        var prev = new System.Windows.Forms.ToolStripMenuItem("上一首");
        prev.Click += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (bar.PreviousCommand.CanExecute(null)) bar.PreviousCommand.Execute(null);
        });
        var next = new System.Windows.Forms.ToolStripMenuItem("下一首");
        next.Click += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (bar.NextCommand.CanExecute(null)) bar.NextCommand.Execute(null);
        });
        var show = new System.Windows.Forms.ToolStripMenuItem("显示主窗口");
        show.Click += (_, _) => Dispatcher.BeginInvoke(ShowFromTray);
        var miniCard = new System.Windows.Forms.ToolStripMenuItem("迷你播放器");
        miniCard.Click += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (_viewModel.ToggleMiniPlayerCommand.CanExecute(null))
                _viewModel.ToggleMiniPlayerCommand.Execute(null);
        });
        var quit = new System.Windows.Forms.ToolStripMenuItem("退出 AnMusic");
        quit.Click += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            _forceExit = true; // 跳过关闭询问，真正退出
            Close();
        });

        menu.Items.Add(playPause);
        menu.Items.Add(prev);
        menu.Items.Add(next);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(show);
        menu.Items.Add(miniCard);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(quit);
        _trayIcon.ContextMenuStrip = menu;
    }
}
