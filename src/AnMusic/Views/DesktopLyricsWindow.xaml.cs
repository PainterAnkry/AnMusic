using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AnMusic.Services.Settings;
using AnMusic.ViewModels;

namespace AnMusic.Views;

/// <summary>
/// 桌面歌词窗口：无边框、透明、置顶；鼠标拖拽移动，双击关闭。
/// 滚轮调字号，Ctrl+滚轮调背景不透明度；右键菜单提供完整调整项；设置自动持久化。
/// </summary>
public partial class DesktopLyricsWindow : Window
{
    private readonly UserSettingsService _settingsService;
    private double _bgOpacity;

    public DesktopLyricsWindow(LyricViewModel lyricViewModel, UserSettingsService settingsService)
    {
        InitializeComponent();
        DataContext = lyricViewModel;
        _settingsService = settingsService;

        var s = _settingsService.Settings;
        Width = Math.Clamp(s.DesktopLyricsWidth, 400, 1920);
        _bgOpacity = Math.Clamp(s.DesktopLyricsBgOpacity, 0, 0.95);
        ApplyLyricFontSize(Math.Clamp(s.DesktopLyricsFontSize, 14, 60));
        ApplyBgOpacity(_bgOpacity);

        BuildContextMenu();
    }

    /// <summary>应用字号并自适应窗口高度（主行 + 可选译文行）。</summary>
    private void ApplyLyricFontSize(double size)
    {
        LyricText.FontSize = size;
        TransText.FontSize = Math.Max(12, size * 0.55);
        Height = Math.Clamp(size * 2.6 + 48, 90, 300);
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
        s.DesktopLyricsBgOpacity = _bgOpacity;
        s.DesktopLyricsWidth = Width;
        try { _settingsService.Save(); } catch { /* 保存失败不影响使用 */ }
    }

    /// <summary>滚轮：直接调字号；Ctrl+滚轮：调背景不透明度。</summary>
    private void Window_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _bgOpacity = Math.Clamp(_bgOpacity + Math.Sign(e.Delta) * 0.05, 0, 0.95);
            ApplyBgOpacity(_bgOpacity);
        }
        else
        {
            ApplyLyricFontSize(Math.Clamp(LyricText.FontSize + Math.Sign(e.Delta) * 2, 14, 60));
        }
        SaveSettings();
        e.Handled = true;
    }

    /// <summary>右键菜单：字号 / 背景不透明度 / 宽度 / 关闭。</summary>
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

        Add("字号 +", (_, _) => { ApplyLyricFontSize(Math.Clamp(LyricText.FontSize + 2, 14, 60)); SaveSettings(); });
        Add("字号 -", (_, _) => { ApplyLyricFontSize(Math.Clamp(LyricText.FontSize - 2, 14, 60)); SaveSettings(); });
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

    /// <summary>鼠标拖拽移动窗口位置（利用 DragMove，透明窗口原生支持）。</summary>
    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            try { DragMove(); } catch { /* DragMove 可能因鼠标已离开而抛异常，忽略 */ }
        }
    }

    /// <summary>双击关闭窗口（关闭后 MainViewModel 的 IsDesktopLyricsOpen 状态由命令同步）。</summary>
    private void Window_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        // 通知 MainViewModel 同步切换状态（避免按钮态不一致）
        if (Application.Current.MainWindow?.DataContext is MainViewModel vm)
        {
            vm.NotifyDesktopLyricsClosed();
        }
        base.OnClosed(e);
    }
}
