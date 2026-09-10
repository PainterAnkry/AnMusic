using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AnMusic.Views.Pages;

/// <summary>
/// 设置页。顶部搜索框可按关键词过滤设置分组。
/// </summary>
public partial class SettingsPage : UserControl
{
    public SettingsPage()
    {
        InitializeComponent();
        // XAML 中 Parent 稍后才建立；初始按默认分类展示
        Loaded += (_, _) =>
        {
            if (DataContext is ViewModels.SettingsViewModel vm) vm.SelectCategory(vm.SelectedSettingsCategory);
            ApplyFilter(null);
        };
    }

    /// <summary>点击左侧分类：切换右侧显示的分区（搜索关键词同时清空，避免"搜了却没结果"）。</summary>
    private void SettingsCategory_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string category) return;
        if (DataContext is not ViewModels.SettingsViewModel vm) return;

        vm.SelectCategory(category);
        if (!string.IsNullOrEmpty(vm.SettingsFilter))
        {
            vm.SettingsFilter = "";   // 清空搜索，回到分类浏览
        }
        ApplyFilter(null);
    }

    private void SettingsSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter((DataContext as ViewModels.SettingsViewModel)?.SettingsFilter);
    }

    private void SettingsSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (DataContext is ViewModels.SettingsViewModel vm)
            {
                vm.SettingsFilter = "";
                ApplyFilter(null);
            }
        }
    }

    #region 快捷键改键

    /// <summary>当前正在等待按键的行。</summary>
    private ViewModels.ShortcutItemViewModel? _capturing;

    /// <summary>点「修改」：进入捕获状态，后续按键交给本页的 PreviewKeyDown。</summary>
    private void ShortcutEdit_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ViewModels.ShortcutItemViewModel item) return;
        if (DataContext is not ViewModels.SettingsViewModel vm) return;

        _capturing?.StopCapture();
        _capturing = item;
        item.BeginCapture();
        vm.BeginShortcutCapture(item);
        vm.SetStatus($"请按下「{item.Name}」的新按键…（Esc 取消，Delete 清除）");

        // 焦点收到页面本身；改键期间窗口层不再把按键当动作执行
        Focusable = true;
        Focus();
        Keyboard.Focus(this);
    }

    /// <summary>行内「重置」：恢复该项默认按键。</summary>
    private void ShortcutReset_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ViewModels.ShortcutItemViewModel item) return;
        if (DataContext is not ViewModels.SettingsViewModel vm) return;
        vm.ResetShortcutCommand.Execute(item);
    }

    /// <summary>改键捕获：Esc 取消，Delete/Back 清除，其余组合键写入该项。</summary>
    private void SettingsPage_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_capturing is null) return;
        e.Handled = true; // 捕获期间按键只用于改键，不触发播放等动作

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Services.Shortcuts.ShortcutKeys.IsModifierKey(key)) return; // 只按了修饰键：继续等真正的按键

        if (key == Key.Escape)
        {
            EndCapture("已取消改键");
            return;
        }
        if (key is Key.Delete or Key.Back)
        {
            EndCapture(_capturing.Clear());
            return;
        }

        EndCapture(_capturing.ApplyCapture(key, Keyboard.Modifiers));
    }

    private void EndCapture(string message)
    {
        _capturing?.StopCapture();
        _capturing = null;
        if (DataContext is ViewModels.SettingsViewModel vm)
        {
            vm.EndShortcutCapture();
            vm.SetStatus(message);
        }
    }

    #endregion

    /// <summary>收集可视化树内所有 TextBlock 文本（递归）。</summary>
    private static string GatherText(DependencyObject visual)
    {
        var parts = new System.Collections.Generic.List<string>();
        void Walk(DependencyObject obj)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++)
            {
                var child = VisualTreeHelper.GetChild(obj, i);
                if (child is TextBlock tb && tb.Text is { Length: > 0 } text)
                    parts.Add(text);
                Walk(child);
            }
        }
        Walk(visual);
        return string.Join(" ", parts);
    }

    /// <summary>
    /// 设置分区过滤：
    /// 1) 无关键词 → 只显示当前分类的分区（分类分页）；
    /// 2) 有关键词 → 跨全部分类搜索，只显示命中的分区，并滚动到第一个命中。
    /// 分区 Tag 形如 "sec:外观"。
    /// </summary>
    private void ApplyFilter(string? keyword)
    {
        keyword = keyword?.Trim();
        var searching = !string.IsNullOrEmpty(keyword);
        var category = (DataContext as ViewModels.SettingsViewModel)?.SelectedSettingsCategory ?? "";
        FrameworkElement? firstMatch = null;
        var visibleCount = 0;

        foreach (var child in RootPanel.Children)
        {
            if (child is not FrameworkElement fe || fe.Tag is not string tag ||
                !tag.StartsWith("sec:", StringComparison.Ordinal)) continue;

            var sectionCategory = tag["sec:".Length..];
            bool visible;
            if (searching)
            {
                visible = GatherText(fe).Contains(keyword!, System.StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                visible = string.Equals(sectionCategory, category, StringComparison.Ordinal);
            }

            fe.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (visible)
            {
                visibleCount++;
                firstMatch ??= fe;
            }
        }

        // 分类内没有分区（理论上不会发生）：明确提示，而不是把所有分类都摊开
        if (!searching && visibleCount == 0)
        {
            EmptyCategoryHint.Visibility = Visibility.Visible;
        }
        else
        {
            EmptyCategoryHint.Visibility = Visibility.Collapsed;
        }

        if (searching) firstMatch?.BringIntoView();
    }
}