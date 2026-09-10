using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AnMusic.Services.Settings;
using AnMusic.ViewModels;

namespace AnMusic.Views;

/// <summary>
/// 桌面歌词窗口：无边框、透明、置顶；上一行/当前行/下一行三联布局，点击前后行跳转播放。
/// 拖拽移动、滚轮调字号、Ctrl+滚轮调不透明度、右键可锁定（锁定后仅可点击跳转，不可拖/关/滚）。
/// 字号/行距/不透明度/宽度/位置锁定状态自动持久化。
/// </summary>
public partial class DesktopLyricsWindow : Window
{
    private readonly UserSettingsService _settingsService;
    private readonly LyricViewModel _lyrics;
    private double _bgOpacity;
    private bool _isLocked;

    public DesktopLyricsWindow(LyricViewModel lyricViewModel, UserSettingsService settingsService)
    {
        InitializeComponent();
        DataContext = lyricViewModel;
        _lyrics = lyricViewModel;
        _settingsService = settingsService;

        var s = _settingsService.Settings;
        _isLocked = s.DesktopLyricsIsLocked;
        Width = Math.Clamp(s.DesktopLyricsWidth, 400, 1920);
        _bgOpacity = Math.Clamp(s.DesktopLyricsBgOpacity, 0, 0.95);
        ApplyLyricFontSize(Math.Clamp(s.DesktopLyricsFontSize, 14, 60), Math.Clamp(s.DesktopLyricsLineSpacing, 1.1, 2.2));
        ApplyBgOpacity(_bgOpacity);

        // 恢复上次位置（双屏记忆：确保落在当前虚拟屏幕范围内）
        if (!double.IsNaN(s.DesktopLyricsLeft) && !double.IsNaN(s.DesktopLyricsTop))
        {
            var vw = SystemParameters.VirtualScreenWidth;
            var vh = SystemParameters.VirtualScreenHeight;
            Left = Math.Clamp(s.DesktopLyricsLeft, -200, Math.Max(-200, vw - 400));
            Top = Math.Clamp(s.DesktopLyricsTop, -60, Math.Max(-60, vh - 80));
        }

        _lyrics.PropertyChanged += OnLyricsPropertyChanged;
        RefreshLineWindows();
        BuildContextMenu();
    }

    private void OnLyricsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LyricViewModel.CurrentIndex) or
            nameof(LyricViewModel.CurrentLineText) or
            nameof(LyricViewModel.Lines))
        {
            Dispatcher.BeginInvoke(RefreshLineWindows);
        }
    }

    /// <summary>同步三行文本（前/当前/后）与锁定状态的外观。</summary>
    private void RefreshLineWindows()
    {
        var lines = _lyrics.Lines;
        var idx = _lyrics.CurrentIndex;
        if (lines.Count == 0 || idx < 0)
        {
            PrevText.Text = "";
            NextText.Text = "";
            return;
        }
        PrevText.Text = idx > 0 ? lines[idx - 1].Text : "";
        NextText.Text = idx + 1 < lines.Count ? lines[idx + 1].Text : "";
        // 锁定模式只显示当前行（精简视线）；解锁时前后行仅在存在时占位
        var showNeighbors = !_isLocked;
        PrevText.Visibility = showNeighbors && idx > 0 ? Visibility.Visible : Visibility.Collapsed;
        NextText.Visibility = showNeighbors && idx + 1 < lines.Count ? Visibility.Visible : Visibility.Collapsed;
        // 译文行交给样式判断（空译文自动隐藏），锁定模式下强制隐藏
        if (_isLocked) TransText.Visibility = Visibility.Collapsed;
        else TransText.ClearValue(VisibilityProperty);
    }

    /// <summary>应用字号与行距并自适应窗口高度（主行 + 前后行 + 可选译文行）。</summary>
    private void ApplyLyricFontSize(double size, double lineSpacing)
    {
        LyricText.FontSize = size;
        LyricText.LineHeight = size * lineSpacing;
        PrevText.FontSize = Math.Max(12, size * 0.55);
        PrevText.LineHeight = PrevText.FontSize * 1.4;
        NextText.FontSize = PrevText.FontSize;
        NextText.LineHeight = PrevText.LineHeight;
        TransText.FontSize = Math.Max(12, size * 0.55);
        var visible = _isLocked ? 1 : 3;
        Height = Math.Clamp(size * 1.15 * visible + size * 0.6 + 56, 90, 360);
    }

    /// <summary>应用背景不透明度（黑底）。</summary>
    private void ApplyBgOpacity(double opacity)
    {
        var a = (byte)Math.Clamp(opacity * 255, 0, 242);
        BgBorder.Background = new SolidColorBrush(Color.FromArgb(a, 0, 0, 0));
    }

    private void SaveSettings()
    {
        var s = _settingsService.Settings;
        s.DesktopLyricsFontSize = LyricText.FontSize;
        s.DesktopLyricsLineSpacing = LyricText.LineHeight / Math.Max(1.0, LyricText.FontSize);
        s.DesktopLyricsBgOpacity = _bgOpacity;
        s.DesktopLyricsWidth = Width;
        s.DesktopLyricsLeft = Left;
        s.DesktopLyricsTop = Top;
        s.DesktopLyricsIsLocked = _isLocked;
        try { _settingsService.Save(); } catch (Exception ex) { Services.AppPaths.LogError("保存桌面歌词设置", ex); }
    }

    private int CurrentLineIndex => _lyrics.CurrentIndex;

    /// <summary>点击上一行：跳转到上一行时间并在不破坏播放的前提下刷新当前行。</summary>
    private void PrevText_Click(object sender, MouseButtonEventArgs e)
    {
        if (_isLocked || CurrentLineIndex <= 0) { e.Handled = true; return; }
        SeekToLine(CurrentLineIndex - 1);
        e.Handled = true;
    }

    /// <summary>点击下一行：跳转到下一行时间。</summary>
    private void NextText_Click(object sender, MouseButtonEventArgs e)
    {
        if (_isLocked) { e.Handled = true; return; } // 锁定状态不跳转（防止误触）
        var idx = CurrentLineIndex;
        if (idx < 0 || idx + 1 >= _lyrics.Lines.Count) { e.Handled = true; return; }
        SeekToLine(idx + 1);
        e.Handled = true;
    }

    private void SeekToLine(int index)
    {
        var lines = _lyrics.Lines;
        if (index < 0 || index >= lines.Count) return;
        _lyrics.SeekToLineCommand.Execute(lines[index]);
    }

    /// <summary>滚轮：直接调字号；Ctrl+滚轮：调行距；Shift+滚轮：调背景不透明度。</summary>
    private void Window_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_isLocked) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            var spacing = Math.Clamp(LyricText.LineHeight / Math.Max(1.0, LyricText.FontSize) + Math.Sign(e.Delta) * 0.1, 1.1, 2.2);
            ApplyLyricFontSize(LyricText.FontSize, spacing);
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            _bgOpacity = Math.Clamp(_bgOpacity + Math.Sign(e.Delta) * 0.05, 0, 0.95);
            ApplyBgOpacity(_bgOpacity);
        }
        else
        {
            ApplyLyricFontSize(Math.Clamp(LyricText.FontSize + Math.Sign(e.Delta) * 2, 14, 60),
                LyricText.LineHeight / Math.Max(1.0, LyricText.FontSize));
        }
        SaveSettings();
        e.Handled = true;
    }

    /// <summary>右键菜单：锁定/字号/行距/背景/宽度/关闭。</summary>
    private void BuildContextMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();

        System.Windows.Controls.MenuItem Add(string header, RoutedEventHandler onClick)
        {
            var item = new System.Windows.Controls.MenuItem { Header = header };
            item.Click += onClick;
            menu.Items.Add(item);
            return item;
        }

        Add(_isLocked ? "🔓 解锁（允许拖动/关闭）" : "🔒 锁定（固定位置防误操作）", (_, _) =>
        {
            _isLocked = !_isLocked;
            RefreshLineWindows();
            BuildContextMenu();
            SaveSettings();
        });
        menu.Items.Add(new System.Windows.Controls.Separator());
        Add("字号 +", (_, _) => { ApplyLyricFontSize(Math.Clamp(LyricText.FontSize + 2, 14, 60), LyricText.LineHeight / Math.Max(1.0, LyricText.FontSize)); SaveSettings(); });
        Add("字号 -", (_, _) => { ApplyLyricFontSize(Math.Clamp(LyricText.FontSize - 2, 14, 60), LyricText.LineHeight / Math.Max(1.0, LyricText.FontSize)); SaveSettings(); });
        Add("行距 +", (_, _) => { var sp = Math.Clamp(LyricText.LineHeight / Math.Max(1.0, LyricText.FontSize) + 0.1, 1.1, 2.2); ApplyLyricFontSize(LyricText.FontSize, sp); SaveSettings(); });
        Add("行距 -", (_, _) => { var sp = Math.Clamp(LyricText.LineHeight / Math.Max(1.0, LyricText.FontSize) - 0.1, 1.1, 2.2); ApplyLyricFontSize(LyricText.FontSize, sp); SaveSettings(); });
        menu.Items.Add(new System.Windows.Controls.Separator());
        Add("背景更浓", (_, _) => { _bgOpacity = Math.Clamp(_bgOpacity + 0.1, 0, 0.95); ApplyBgOpacity(_bgOpacity); SaveSettings(); });
        Add("背景更淡", (_, _) => { _bgOpacity = Math.Clamp(_bgOpacity - 0.1, 0, 0.95); ApplyBgOpacity(_bgOpacity); SaveSettings(); });
        menu.Items.Add(new System.Windows.Controls.Separator());
        Add("加宽", (_, _) => { Width = Math.Clamp(Width + 120, 400, 1920); SaveSettings(); });
        Add("变窄", (_, _) => { Width = Math.Clamp(Width - 120, 400, 1920); SaveSettings(); });
        menu.Items.Add(new System.Windows.Controls.Separator());
        Add("关闭桌面歌词", (_, _) => Close());

        ContextMenu = menu;
    }

    /// <summary>鼠标拖拽移动窗口位置（锁定状态禁用）。</summary>
    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isLocked) { e.Handled = true; return; }
        if (e.ChangedButton == MouseButton.Left)
        {
            try { DragMove(); SaveSettings(); } catch { /* 拖拽打断不处理 */ }
        }
    }

    /// <summary>双击关闭窗口（锁定状态禁用）。</summary>
    private void Window_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_isLocked) { e.Handled = true; return; }
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        SaveSettings();
        _lyrics.PropertyChanged -= OnLyricsPropertyChanged;
        // 通知 MainViewModel 同步切换状态（避免按钮态不一致）
        if (Application.Current.MainWindow?.DataContext is MainViewModel vm)
        {
            vm.NotifyDesktopLyricsClosed();
        }
        base.OnClosed(e);
    }
}