using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using AnMusic.Services.Settings;
using AnMusic.ViewModels;

namespace AnMusic.Views;

/// <summary>
/// 迷你悬浮卡片播放器：独立置顶小窗，只保留核心播放元素。
/// 布局：左封面 / 中歌名+歌手 / 右（收藏·音量·播放列表·收起·关闭）/ 下（上一首·播放·下一首 + 进度条）。
/// 空白处拖动移动窗口；进度条支持拖拽与点击轨道跳转；音量与播放列表为弹层。
/// </summary>
public partial class MiniPlayerWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly UserSettingsService _settingsService;
    private readonly Services.Shortcuts.ShortcutService _shortcuts;
    private double _lastAudibleVolume = 0.8;

    public MiniPlayerWindow(MainViewModel viewModel, UserSettingsService settingsService,
        Services.Shortcuts.ShortcutService shortcutService)
    {
        InitializeComponent();
        _vm = viewModel;
        _settingsService = settingsService;
        _shortcuts = shortcutService;
        DataContext = viewModel;

        // 卡片同样响应快捷键（空白处无输入框，纯按键也直接生效）
        PreviewKeyDown += MiniPlayer_PreviewKeyDown;

        var s = _settingsService.Settings;
        _lastAudibleVolume = Math.Clamp(viewModel.PlaybackBar.Volume, 0.01, 1);
        ApplyStartupPosition(s);

        // 当前曲目变化时，若播放列表弹层正开着则同步刷新
        viewModel.PlaybackBar.PropertyChanged += OnPlaybackPropertyChanged;
    }

    /// <summary>恢复上次位置（双屏记忆）：越界时回落到虚拟屏幕内，首次打开落在右下角。</summary>
    private void ApplyStartupPosition(UserSettings s)
    {
        var vw = SystemParameters.VirtualScreenWidth;
        var vh = SystemParameters.VirtualScreenHeight;
        var vLeft = SystemParameters.VirtualScreenLeft;
        var vTop = SystemParameters.VirtualScreenTop;

        if (!double.IsNaN(s.MiniCardLeft) && !double.IsNaN(s.MiniCardTop) &&
            s.MiniCardLeft > vLeft - Width && s.MiniCardLeft < vLeft + vw &&
            s.MiniCardTop > vTop - Height && s.MiniCardTop < vTop + vh)
        {
            Left = s.MiniCardLeft;
            Top = s.MiniCardTop;
            WindowStartupLocation = WindowStartupLocation.Manual;
            return;
        }

        // 默认：工作区右下角
        var wa = SystemParameters.WorkArea;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = Math.Max(wa.Left, wa.Right - Width - 24);
        Top = Math.Max(wa.Top, wa.Bottom - Height - 24);
    }

    private void OnPlaybackPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaybackBarViewModel.CurrentTrack) && QueuePopup.IsOpen)
            _vm.RefreshQueuePanel();
    }

    private void SavePosition()
    {
        var s = _settingsService.Settings;
        s.MiniCardLeft = Left;
        s.MiniCardTop = Top;
        try { _settingsService.Save(); } catch (Exception ex) { Services.AppPaths.LogError("保存迷你卡片位置", ex); }
    }

    /// <summary>卡片内按键：命中快捷键绑定即执行（如空格播放/暂停、Ctrl+方向切歌）。</summary>
    private void MiniPlayer_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_shortcuts.IsCapturing) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Services.Shortcuts.ShortcutKeys.IsModifierKey(key)) return;

        if (_shortcuts.TryMatch(key, Keyboard.Modifiers, isTextInputFocused: false, out var actionId) &&
            actionId is not null)
        {
            _vm.ExecuteShortcut(actionId);
            e.Handled = true;
        }
    }

    /// <summary>卡片空白处按住拖动窗口（按钮/Slider 自行处理点击，不会走到这里）。</summary>
    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try
        {
            DragMove();
            SavePosition();
        }
        catch { /* 拖拽被打断（如鼠标捕获丢失）忽略 */ }
        e.Handled = true;
    }

    /// <summary>封面：打开主窗口歌词页。</summary>
    private void Cover_Click(object sender, MouseButtonEventArgs e)
    {
        _vm.ToggleLyricsCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>收起卡片：隐藏窗口，可从主界面 🎛 或托盘菜单恢复。</summary>
    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        SavePosition();
        Hide();
        _vm.NotifyMiniPlayerHidden();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        SavePosition();
        Close();
    }

    private void VolumeButton_Click(object sender, RoutedEventArgs e)
        => VolumePopup.IsOpen = !VolumePopup.IsOpen;

    private void Mute_Click(object sender, RoutedEventArgs e)
        => _vm.PlaybackBar.ToggleMuteCommand.Execute(null); // 与快捷键共用同一套静音/恢复逻辑

    private void QueueButton_Click(object sender, RoutedEventArgs e)
    {
        _vm.RefreshQueuePanel();
        QueuePopup.IsOpen = !QueuePopup.IsOpen;
    }

    private void QueueRow_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MainViewModel.QueueRowItem row)
        {
            QueuePopup.IsOpen = false;
            _vm.PlayQueueTrackCommand.Execute(row.Track);
        }
        e.Handled = true;
    }

    #region 进度条：拖拽 + 点击轨道跳转（与主播放栏同一套交互）

    private void ProgressSlider_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
        => _vm.PlaybackBar.BeginDrag();

    private void ProgressSlider_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (sender is System.Windows.Controls.Slider s)
            _vm.PlaybackBar.EndDrag(s.Value);
    }

    /// <summary>
    /// 按下：抓住滑块交给 Thumb 拖拽；点击轨道则按点击位置线性映射立即跳转并进入拖动状态。
    /// 不能用 Slider.IsMoveToPointEnabled：Slider 类处理程序会先标记 Handled，只移动外观而不触发 Seek。
    /// </summary>
    private void ProgressSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var bar = _vm.PlaybackBar;

        if (e.OriginalSource is DependencyObject src && IsOverThumb(src))
        {
            bar.BeginDrag();
            return;
        }

        if (sender is not System.Windows.Controls.Slider s || !bar.IsLoaded) return;

        var target = GetTrackValueAt(s, e);
        s.SetCurrentValue(System.Windows.Controls.Primitives.RangeBase.ValueProperty, target);
        bar.SeekTo(target);
        bar.BeginDrag();
        s.CaptureMouse();
        e.Handled = true;
    }

    private void ProgressSlider_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (sender is not System.Windows.Controls.Slider s || !s.IsMouseCaptured) return;
        var bar = _vm.PlaybackBar;
        if (!bar.IsLoaded) return;
        bar.PositionSeconds = GetTrackValueAt(s, e);
    }

    private void ProgressSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.Slider s)
        {
            if (s.IsMouseCaptured) s.ReleaseMouseCapture();
            _vm.PlaybackBar.EndDrag(s.Value);
        }
    }

    /// <summary>按点击位置线性映射进度值，保证“点哪播哪”。</summary>
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
            var p = e.GetPosition(slider);
            ratio = Math.Clamp(p.X / Math.Max(1.0, slider.ActualWidth), 0, 1);
        }
        return slider.Minimum + ratio * (slider.Maximum - slider.Minimum);
    }

    private static bool IsOverThumb(DependencyObject source)
    {
        while (source is not null)
        {
            if (source is System.Windows.Controls.Primitives.Thumb) return true;
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    #endregion

    protected override void OnClosed(EventArgs e)
    {
        _vm.PlaybackBar.PropertyChanged -= OnPlaybackPropertyChanged;
        SavePosition();
        _vm.NotifyMiniPlayerClosed();
        base.OnClosed(e);
    }
}
