using System.Globalization;
using System.Windows.Data;

namespace AnMusic.Converters;

/// <summary>
/// TimeSpan → "m:ss" 或 "h:mm:ss" 文本。
/// </summary>
public sealed class TimeSpanToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is TimeSpan ts)
            return ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                : $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
        return "0:00";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// double 秒数 → "m:ss" 文本。
/// </summary>
public sealed class SecondsToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double sec)
        {
            var ts = TimeSpan.FromSeconds(sec);
            return ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                : $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
        }
        return "0:00";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// bool → Visibility（true=Visible, false=Collapsed）。
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is System.Windows.Visibility v && v == System.Windows.Visibility.Visible;
}

/// <summary>
/// bool IsPlaying → "⏸ 暂停"/"▶ 播放"。
/// </summary>
public sealed class BoolToPlayTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? "⏸" : "▶";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// bool IsLoading → "正在扫描..."/""。
/// </summary>
public sealed class BoolToLoadingTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? "正在扫描..." : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// bool → Accent 色 / FgSecondary 色（用于翻译按钮高亮）。
/// </summary>
public sealed class BoolToAccentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b && b)
            return System.Windows.Application.Current.TryFindResource("Accent") as System.Windows.Media.Brush
                   ?? System.Windows.Media.Brushes.Blue;
        return System.Windows.Application.Current.TryFindResource("FgSecondary") as System.Windows.Media.Brush
               ?? System.Windows.Media.Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// string 路径 → BitmapImage（用于封面图绑定）。
/// </summary>
/// <summary>字符串非空 → Collapsed（用于有头像时隐藏默认占位图标，避免双层叠加）。</summary>
public sealed class StringNotEmptyToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string s && !string.IsNullOrEmpty(s)
            ? System.Windows.Visibility.Collapsed
            : System.Windows.Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// ProviderId 等于 ConverterParameter 时显示：用于只在特定音源曲目上出现的菜单项
/// （如 B 站曲目的「查看全集（分P）」）。
/// </summary>
public sealed class ProviderIdToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var want = parameter?.ToString();
        var match = !string.IsNullOrEmpty(want)
                    && value is string id
                    && string.Equals(id, want, StringComparison.OrdinalIgnoreCase);
        return match ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class PathToImageConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrEmpty(path))
            return System.Windows.DependencyProperty.UnsetValue;

        try
        {
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreImageCache;

            // ConverterParameter 可指定"按展示尺寸解码"的目标宽度（等比缩放）：
            // 大图（尤其 4K 背景图）按原尺寸解码会长期占用几十 MB 内存
            var decodeWidth = 0;
            if (parameter is not null)
                int.TryParse(parameter.ToString(), out decodeWidth);
            if (decodeWidth > 0)
                bmp.DecodePixelWidth = decodeWidth;

            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return System.Windows.DependencyProperty.UnsetValue;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
