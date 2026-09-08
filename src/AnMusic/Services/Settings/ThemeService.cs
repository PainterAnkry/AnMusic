using System.Windows;

namespace AnMusic.Services.Settings;

/// <summary>
/// 主题服务：运行时在 Dark/Light 资源字典间切换（画刷键名一致，DynamicResource 自动刷新）。
/// </summary>
public static class ThemeService
{
    private const string DarkSource = "Themes/Dark.xaml";
    private const string LightSource = "Themes/Light.xaml";

    public static bool IsDark { get; private set; } = true;

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
}
