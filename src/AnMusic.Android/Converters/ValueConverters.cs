using System.Globalization;

namespace AnMusic.Android.Converters;

/// <summary>字符串非空 -> true。用于「有封面才显示 Image、无封面显示占位」这类显隐判断。
/// 支持 <c>ConverterParameter="invert"</c> 取反。
/// </summary>
public sealed class HasValueConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasValue = value switch
        {
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            _ => true,
        };

        var invert = parameter is string p &&
                     p.Equals("invert", StringComparison.OrdinalIgnoreCase);

        return invert ? !hasValue : hasValue;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>字符串非空 -> true（HasValueConverter 别名，更直白）。
/// 不支持 invert：反转请用 InvertBoolConverter 套一层。</summary>
public sealed class IsNotEmptyConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s ? !string.IsNullOrWhiteSpace(s) : value is not null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 时长（秒）-> mm:ss / hh:mm:ss 文本。
/// </summary>
public sealed class DurationConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        TimeSpan span = value switch
        {
            TimeSpan t => t,
            double seconds => TimeSpan.FromSeconds(seconds),
            int seconds => TimeSpan.FromSeconds(seconds),
            long seconds => TimeSpan.FromSeconds(seconds),
            _ => TimeSpan.Zero,
        };

        return Format(span);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    /// <summary>把时长格式化为音乐播放器惯用的时间文本。</summary>
    public static string Format(TimeSpan time) =>
        time.TotalHours >= 1
            ? $"{(int)time.TotalHours:D2}:{time.Minutes:D2}:{time.Seconds:D2}"
            : $"{time.Minutes:D2}:{time.Seconds:D2}";
}

/// <summary>
/// 布尔取反。用于「正在播放的曲目才显示高亮」「未扫描完成才显示空状态」这类绑定。
/// </summary>
public sealed class InvertBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && !b;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && !b;
}

/// <summary>
/// 枚举/索引相等比较 -> bool。用于标签页选中态、播放模式图标高亮。
/// 用法：<c>ConverterParameter=All</c> 比较字符串，或 <c>ConverterParameter=0</c> 比较整数。
/// </summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null) return false;

        // 枚举 -> 与参数按名字比较
        if (value is Enum e)
            return string.Equals(e.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);

        // 数值 -> 按数字比较（容忍 "0" 与 0.0）
        if (double.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var left) &&
            double.TryParse(parameter.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var right))
            return Math.Abs(left - right) < double.Epsilon;

        return string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// bool -> FontAttributes：true 得 Bold，false 得 None。
/// 用于歌词当前行加粗，以及任何"选中即加粗"的行。
/// </summary>
public sealed class BoldWhenTrueConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? FontAttributes.Bold : FontAttributes.None;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
