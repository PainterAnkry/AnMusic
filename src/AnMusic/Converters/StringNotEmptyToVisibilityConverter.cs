using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AnMusic.Converters;

/// <summary>字符串非空可见转换器：空/空白折叠，非空显示。</summary>
public sealed class StringNotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
