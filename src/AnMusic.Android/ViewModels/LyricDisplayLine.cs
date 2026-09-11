using AnMusic.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AnMusic.Android.ViewModels;

/// <summary>
/// 歌词行的展示包装：在 Core 的 <see cref="LyricLine"/> 之上补一个 <see cref="IsCurrent"/>，
/// 用于高亮当前行。
/// </summary>
/// <remarks>
/// 为什么不直接在页面 code-behind 里改 Label 的颜色：
/// MAUI 的 CollectionView 会回收容器，滚动后同一个 Label 实例会承载不同的数据行，
/// 按引用记住"上一行"必然错位，出现多个高亮行或高亮丢失。
/// 把状态放进数据项本身，模板重新绑定时自然就对了，与回收策略无关。
/// </remarks>
public sealed partial class LyricDisplayLine : ObservableObject
{
    public required TimeSpan Time { get; init; }
    public required string Text { get; init; }

    /// <summary>译文（点击「译」后由翻译服务填充；null = 不显示）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTranslation))]
    private string? _translation;

    /// <summary>是否为当前播放到的行（决定颜色与字号）。</summary>
    [ObservableProperty] private bool _isCurrent;

    /// <summary>正文颜色：当前行用强调色，其余用设置的歌词配色。</summary>
    [ObservableProperty] private Color _textColor = Colors.Gray;

    /// <summary>译文颜色：比正文再淡一档。</summary>
    [ObservableProperty] private Color _translationColor = Colors.Gray;

    /// <summary>当前行稍大一点，眼睛更容易跟上。</summary>
    [ObservableProperty] private double _fontSize = 15;

    public bool HasTranslation => !string.IsNullOrWhiteSpace(Translation);

    /// <summary>按高亮状态与配色重算视觉属性。</summary>
    public void ApplyVisual(bool isCurrent, Color baseColor, Color accentColor, double baseFontSize)
    {
        IsCurrent = isCurrent;
        TextColor = isCurrent ? accentColor : baseColor;
        TranslationColor = isCurrent ? accentColor : baseColor;
        FontSize = isCurrent ? baseFontSize + 2 : baseFontSize;
    }
}
