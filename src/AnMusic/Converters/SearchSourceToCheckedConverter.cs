using System.Globalization;
using System.Windows.Data;
using AnMusic.ViewModels;

namespace AnMusic.Converters;

/// <summary>SearchSource 枚举到 RadioButton IsChecked 的转换器组。</summary>
public class SearchSourceToCheckedConverter : IValueConverter
{
    public SearchSource Target { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is SearchSource s && s == Target;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? Target : (object)Binding.DoNothing;
}

public sealed class SrcAllCheckedConverter : SearchSourceToCheckedConverter { public SrcAllCheckedConverter() => Target = SearchSource.All; }
public sealed class SrcLocalCheckedConverter : SearchSourceToCheckedConverter { public SrcLocalCheckedConverter() => Target = SearchSource.Local; }
public sealed class SrcBiliCheckedConverter : SearchSourceToCheckedConverter { public SrcBiliCheckedConverter() => Target = SearchSource.Bilibili; }
