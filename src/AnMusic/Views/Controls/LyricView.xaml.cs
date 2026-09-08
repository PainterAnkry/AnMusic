using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AnMusic.Models;
using AnMusic.ViewModels;

namespace AnMusic.Views.Controls;

/// <summary>
/// LyricView 代码后置：歌词滚动定位到当前行（1/3 处）+ 点击行跳转。
/// </summary>
public partial class LyricView : UserControl
{
    private ScrollViewer? _scrollHost;

    public LyricView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _scrollHost = FindScrollViewer(LyricListBox);
    }

    private void LyricListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LyricListBox.SelectedIndex < 0 || _scrollHost is null)
            return;

        // 延迟执行以等待容器生成
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var container = LyricListBox.ItemContainerGenerator
                .ContainerFromIndex(LyricListBox.SelectedIndex) as FrameworkElement;
            if (container is null || _scrollHost is null)
                return;

            // 计算容器在 ScrollViewer 中的位置
            var transform = container.TransformToVisual(_scrollHost);
            var position = transform.Transform(new Point(0, 0));

            // 目标偏移：当前行位于视口 1/3 处
            var targetOffset = _scrollHost.VerticalOffset + position.Y - _scrollHost.ViewportHeight / 3;
            targetOffset = Math.Max(0, targetOffset);

            _scrollHost.ScrollToVerticalOffset(targetOffset);
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void LyricListBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // 查找点击的 ListBoxItem
        var hit = e.OriginalSource as DependencyObject;
        while (hit is not null && hit is not ListBoxItem)
            hit = VisualTreeHelper.GetParent(hit);

        if (hit is ListBoxItem item && item.DataContext is LyricLine line)
        {
            if (DataContext is LyricViewModel vm)
            {
                vm.SeekToLineCommand.Execute(line);
            }
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject element)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            var child = VisualTreeHelper.GetChild(element, i);
            if (child is ScrollViewer sv)
                return sv;
            var result = FindScrollViewer(child);
            if (result is not null)
                return result;
        }
        return null;
    }
}
