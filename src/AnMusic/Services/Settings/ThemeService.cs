using System.Windows;
using System.Windows.Media;

namespace AnMusic.Services.Settings;

/// <summary>
/// 主题服务：运行时在 Dark/Light 资源字典间切换（画刷键名一致，DynamicResource 自动刷新）。
/// 支持多种强调色方案。
/// </summary>
public static class ThemeService
{
    private const string DarkSource = "Themes/Dark.xaml";
    private const string LightSource = "Themes/Light.xaml";

    public static bool IsDark { get; private set; } = true;

    /// <summary>6 套强调色方案：主色、悬浮色、浅色。</summary>
    private static readonly (Color Accent, Color AccentHover, Color AccentSoft)[] AccentPalettes =
    {
        (Color.FromRgb(0x2B, 0x7D, 0xE9), Color.FromRgb(0x4C, 0x93, 0xF0), Color.FromRgb(0xE3, 0xEE, 0xFC)), // 0 科技蓝
        (Color.FromRgb(0x7C, 0x3A, 0xED), Color.FromRgb(0x95, 0x61, 0xF2), Color.FromRgb(0xEC, 0xE3, 0xFC)), // 1 暗夜紫
        (Color.FromRgb(0x10, 0xB9, 0x81), Color.FromRgb(0x34, 0xD3, 0x99), Color.FromRgb(0xDC, 0xF7, 0xEA)), // 2 森林绿
        (Color.FromRgb(0xF5, 0x9E, 0x0B), Color.FromRgb(0xFB, 0xB7, 0x34), Color.FromRgb(0xFD, 0xF2, 0xDC)), // 3 日落橙
        (Color.FromRgb(0xE9, 0x1E, 0x63), Color.FromRgb(0xF0, 0x62, 0x92), Color.FromRgb(0xFC, 0xE3, 0xEC)), // 4 玫瑰红
        (Color.FromRgb(0x06, 0xB6, 0xD4), Color.FromRgb(0x22, 0xD3, 0xEE), Color.FromRgb(0xDC, 0xF7, 0xFC)), // 5 海洋青
    };

    /// <summary>应用主题（true=深色）。</summary>
    public static void Apply(bool dark)
    {
        var app = Application.Current;
        if (app is null) return;

        var merged = app.Resources.MergedDictionaries;
        var newDict = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/AnMusic;component/{(dark ? DarkSource : LightSource)}")
        };

        // 移除旧主题字典（Dark.xaml / Light.xaml）
        for (int i = merged.Count - 1; i >= 0; i--)
        {
            var src = merged[i].Source?.OriginalString;
            if (src is not null && (src.EndsWith(DarkSource, StringComparison.OrdinalIgnoreCase)
                                    || src.EndsWith(LightSource, StringComparison.OrdinalIgnoreCase)))
            {
                merged.RemoveAt(i);
            }
        }

        merged.Insert(0, newDict);
        IsDark = dark;
    }

    /// <summary>应用强调色方案（0-5）。</summary>
    public static void ApplyAccent(int index)
    {
        var app = Application.Current;
        if (app is null) return;

        index = Math.Clamp(index, 0, AccentPalettes.Length - 1);
        var (accent, hover, soft) = AccentPalettes[index];

        app.Resources["Accent"] = new SolidColorBrush(accent);
        app.Resources["AccentHover"] = new SolidColorBrush(hover);
        app.Resources["AccentSoft"] = new SolidColorBrush(soft);
    }
}
