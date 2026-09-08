using System.Globalization;
using System.Windows.Data;

namespace AnMusic.Converters;

/// <summary>多布尔值全为 false 时返回 Visible，任一为 true 返回 Collapsed。
/// 用于"打开文件夹"按钮：设置页或搜索页打开时都隐藏。</summary>
public sealed class AllFalseToVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        foreach (var v in values)
            if (v is bool b && b) return System.Windows.Visibility.Collapsed;
        return System.Windows.Visibility.Visible;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
