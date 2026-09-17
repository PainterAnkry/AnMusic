using System.Windows;
using System.Windows.Media;

namespace AnMusic.Services.Settings;

/// <summary>皮肤描述：Id 即资源字典文件名（Themes/{Id}.xaml）。</summary>
public sealed record Skin(string Id, string Name, bool IsDark, string BgHex, string AccentHex, int AccentIndex);

/// <summary>
/// 主题/皮肤服务：运行时切换 Themes/*.xaml 资源字典（画刷键名一致，DynamicResource 自动刷新）。
/// 皮肤决定底色与文字色，强调色可在此基础上单独调整（0-5 套方案）。
/// </summary>
public static class ThemeService
{
    private const string ThemeFolder = "Themes/";

    /// <summary>
    /// 默认皮肤：未选择过皮肤时（首次运行 / 设置里清空）用它。
    /// </summary>
    /// <remarks>
    /// 「星海蓝」是围绕应用图标 AnMusic.png 配的品牌皮肤（深靛蓝底 + 品牌蓝强调），
    /// 所以新装即与图标同一套观感。
    /// </remarks>
    public const string DefaultSkinId = "StarSea";

    /// <summary>全部皮肤（顺序即设置页/皮肤面板展示顺序）。</summary>
    public static readonly IReadOnlyList<Skin> Skins =
    [
        new("Light",     "浅色",   false, "#F6F7F9", "#2B7DE9", 0),
        new("Dark",      "深色",   true,  "#17191D", "#3B8CFF", 0),
        new("StarSea",   "星海蓝", true,  "#0A0F42", "#4C8DFF", 6),
        new("DeepSpace", "深空蓝", true,  "#0D1420", "#06B6D4", 5),
        new("Midnight",  "午夜紫", true,  "#141020", "#8B5CF6", 1),
        new("Forest",    "护眼绿", false, "#F2F7F0", "#10B981", 2),
        new("Sunset",    "暖阳橙", false, "#FDF7EF", "#F97316", 3),
        new("Sakura",    "樱雾粉", false, "#FDF5F7", "#EC4899", 4),
    ];

    /// <summary>默认皮肤对象（配置缺失时的落点）。</summary>
    public static Skin Default => Find(DefaultSkinId)!;

    /// <summary>当前皮肤 Id。</summary>
    public static string CurrentSkinId { get; private set; } = DefaultSkinId;

    /// <summary>当前皮肤（找不到时回落到默认皮肤）。</summary>
    public static Skin Current => Find(CurrentSkinId)!;

    public static bool IsDark { get; private set; }

    /// <summary>皮肤切换完成（用于刷新界面上的皮肤预览选中态）。</summary>
    public static event Action<Skin>? SkinChanged;

    /// <summary>
    /// 按 Id 查皮肤。
    /// </summary>
    /// <remarks>
    /// 空值 / 未知 Id 一律回落到 <see cref="Default"/>：设置里没存过皮肤（首次运行）、
    /// 或存的是已经删掉的皮肤名，都应该直接看到品牌默认皮肤，而不是空白或报错。
    /// 历史配置里的 "light"/"dark" 小写写法仍按浅色/深色认。
    /// </remarks>
    public static Skin? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return Default;
        foreach (var skin in Skins)
        {
            if (string.Equals(skin.Id, id, StringComparison.OrdinalIgnoreCase)) return skin;
        }
        return id.ToLowerInvariant() switch
        {
            "light" => Skins[0],
            "dark" => Skins[1],
            _ => Default
        };
    }

    /// <summary>应用皮肤（未知 Id 回落默认皮肤）。</summary>
    public static void ApplySkin(string? skinId)
    {
        var skin = Find(skinId) ?? Default;
        var app = Application.Current;
        if (app is null) return;

        var merged = app.Resources.MergedDictionaries;

        // 移除全部旧皮肤字典（Themes/ 下的都算），再插入新皮肤
        for (var i = merged.Count - 1; i >= 0; i--)
        {
            var src = merged[i].Source?.OriginalString;
            if (src is not null && src.Contains("/" + ThemeFolder, StringComparison.OrdinalIgnoreCase))
                merged.RemoveAt(i);
        }

        merged.Insert(0, new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/AnMusic;component/{ThemeFolder}{skin.Id}.xaml")
        });

        CurrentSkinId = skin.Id;
        IsDark = skin.IsDark;
        SkinChanged?.Invoke(skin);
    }

    /// <summary>兼容旧调用：true = 深色，false = 浅色。</summary>
    public static void Apply(bool dark) => ApplySkin(dark ? "Dark" : "Light");

    /// <summary>7 套强调色方案：主色、悬浮色、浅色。</summary>
    private static readonly (Color Accent, Color AccentHover, Color AccentSoft)[] AccentPalettes =
    {
        (Color.FromRgb(0x2B, 0x7D, 0xE9), Color.FromRgb(0x4C, 0x93, 0xF0), Color.FromRgb(0xE3, 0xEE, 0xFC)), // 0 科技蓝
        (Color.FromRgb(0x7C, 0x3A, 0xED), Color.FromRgb(0x95, 0x61, 0xF2), Color.FromRgb(0xEC, 0xE3, 0xFC)), // 1 暗夜紫
        (Color.FromRgb(0x10, 0xB9, 0x81), Color.FromRgb(0x34, 0xD3, 0x99), Color.FromRgb(0xDC, 0xF7, 0xEA)), // 2 森林绿
        (Color.FromRgb(0xF5, 0x9E, 0x0B), Color.FromRgb(0xFB, 0xB7, 0x34), Color.FromRgb(0xFD, 0xF2, 0xDC)), // 3 日落橙
        (Color.FromRgb(0xE9, 0x1E, 0x63), Color.FromRgb(0xF0, 0x62, 0x92), Color.FromRgb(0xFC, 0xE3, 0xEC)), // 4 玫瑰红
        (Color.FromRgb(0x06, 0xB6, 0xD4), Color.FromRgb(0x22, 0xD3, 0xEE), Color.FromRgb(0xDC, 0xF7, 0xFC)), // 5 海洋青
        (Color.FromRgb(0x4C, 0x8D, 0xFF), Color.FromRgb(0x6E, 0xA5, 0xFF), Color.FromRgb(0xE4, 0xED, 0xFF)), // 6 品牌蓝（取自图标）
    };

    /// <summary>强调色方案显示名。</summary>
    public static readonly IReadOnlyList<string> AccentNames =
        ["科技蓝", "暗夜紫", "森林绿", "日落橙", "玫瑰红", "海洋青", "品牌蓝"];

    /// <summary>强调色下标上限（夹取用，随方案增减自动跟随）。</summary>
    public static int MaxAccentIndex => AccentPalettes.Length - 1;

    /// <summary>应用强调色方案（0-6）。</summary>
    public static void ApplyAccent(int index)
    {
        var app = Application.Current;
        if (app is null) return;

        index = Math.Clamp(index, 0, AccentPalettes.Length - 1);
        var (accent, hover, soft) = AccentPalettes[index];

        // 调色板里的 soft 是"浅色底"（给浅色主题用的近乎白色）。
        // 深色皮肤上直接套用，搜索状态条 / 选中项底色会变成一条刺眼的白带
        // （用户反馈的"主页下方一横白色"就是它），所以深色皮肤改成
        // "向皮肤底色靠拢的强调色浅染"——与安卓端同一套做法。
        if (IsDark)
            soft = Mix(BackgroundColor(app), accent, 0.26);

        app.Resources["Accent"] = new SolidColorBrush(accent);
        app.Resources["AccentHover"] = new SolidColorBrush(hover);
        app.Resources["AccentSoft"] = new SolidColorBrush(soft);
    }

    /// <summary>当前皮肤的底色（拿不到时按深色兜底）。</summary>
    private static Color BackgroundColor(Application app)
        => app.TryFindResource("BgMain") is SolidColorBrush brush
            ? brush.Color
            : Color.FromRgb(0x17, 0x19, 0x1D);

    /// <summary>线性混色：t=0 取 a，t=1 取 b。</summary>
    private static Color Mix(Color a, Color b, double t)
    {
        var k = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(a.R + (b.R - a.R) * k),
            (byte)Math.Round(a.G + (b.G - a.G) * k),
            (byte)Math.Round(a.B + (b.B - a.B) * k));
    }
}
