using AnMusic.Services.Settings;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Platform;

namespace AnMusic.Android.Services;

/// <summary>
/// 皮肤描述：与桌面端 <c>AnMusic.Services.Settings.Skin</c> 保持同一套 Id 与配色，
/// 两端的 settings.json 可以直接互认（换端不丢主题）。
/// </summary>
/// <param name="Id">皮肤 Id（持久化值，勿随意改名）。</param>
/// <param name="Name">显示名。</param>
/// <param name="IsDark">是否为深色系。</param>
/// <param name="BgHex">背景底色。</param>
/// <param name="AccentHex">皮肤自带的配套强调色。</param>
/// <param name="AccentIndex">配套强调色在 <see cref="ThemeService.AccentNames"/> 中的下标。</param>
public sealed record Skin(string Id, string Name, bool IsDark, string BgHex, string AccentHex, int AccentIndex);

/// <summary>
/// 安卓端主题服务：把皮肤 + 强调色换算成一整套颜色令牌，写回应用资源字典。
/// </summary>
/// <remarks>
/// 关键约束：界面必须用 <c>{DynamicResource AmXxx}</c> 引用颜色，而不是 <c>{StaticResource}</c>。
/// StaticResource 在解析时就固化了值，运行时换肤不会生效；DynamicResource 会订阅资源变更通知。
/// 样式键（AmCard / AmIconButton 之类）用 Static 或 Dynamic 都一样，因为样式对象本身不重建。
/// </remarks>
public static class ThemeService
{
    /// <summary>全部皮肤（顺序即设置页展示顺序，与桌面端一致）。</summary>
    public static readonly IReadOnlyList<Skin> Skins =
    [
        new("Light",     "浅色",   false, "#F6F7F9", "#2B7DE9", 0),
        new("Dark",      "深色",   true,  "#17191D", "#3B8CFF", 0),
        new("DeepSpace", "深空蓝", true,  "#0D1420", "#06B6D4", 5),
        new("Midnight",  "午夜紫", true,  "#141020", "#8B5CF6", 1),
        new("Forest",    "护眼绿", false, "#F2F7F0", "#10B981", 2),
        new("Sunset",    "暖阳橙", false, "#FDF7EF", "#F97316", 3),
        new("Sakura",    "樱雾粉", false, "#FDF5F7", "#EC4899", 4),
    ];

    /// <summary>6 套强调色：主色 / 悬浮色 / 浅底色（与桌面端同一组数值）。</summary>
    private static readonly (Color Accent, Color Hover, Color Soft)[] AccentPalettes =
    [
        (Color.FromArgb("#2B7DE9"), Color.FromArgb("#4C93F0"), Color.FromArgb("#E3EEFC")), // 0 科技蓝
        (Color.FromArgb("#7C3AED"), Color.FromArgb("#9561F2"), Color.FromArgb("#ECE3FC")), // 1 暗夜紫
        (Color.FromArgb("#10B981"), Color.FromArgb("#34D399"), Color.FromArgb("#DCF7EA")), // 2 森林绿
        (Color.FromArgb("#F59E0B"), Color.FromArgb("#FBB734"), Color.FromArgb("#FDF2DC")), // 3 日落橙
        (Color.FromArgb("#E91E63"), Color.FromArgb("#F06292"), Color.FromArgb("#FCE3EC")), // 4 玫瑰红
        (Color.FromArgb("#06B6D4"), Color.FromArgb("#22D3EE"), Color.FromArgb("#DCF7FC")), // 5 海洋青
    ];

    /// <summary>强调色显示名（下标即 AccentColorIndex）。</summary>
    public static readonly IReadOnlyList<string> AccentNames =
        ["科技蓝", "暗夜紫", "森林绿", "日落橙", "玫瑰红", "海洋青"];

    /// <summary>当前皮肤 Id。</summary>
    public static string CurrentSkinId { get; private set; } = "Light";

    /// <summary>当前强调色下标。</summary>
    public static int CurrentAccentIndex { get; private set; }

    /// <summary>当前是否深色系。</summary>
    public static bool IsDark { get; private set; }

    /// <summary>当前皮肤对象（找不到时回落浅色）。</summary>
    public static Skin Current => Find(CurrentSkinId) ?? Skins[0];

    /// <summary>换肤完成通知（设置页刷新选中态用）。</summary>
    public static event Action? Changed;

    /// <summary>按 Id 查皮肤；兼容历史配置里的 "Dark"/"Light" 大小写。</summary>
    public static Skin? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        foreach (var skin in Skins)
        {
            if (string.Equals(skin.Id, id, StringComparison.OrdinalIgnoreCase)) return skin;
        }
        return null;
    }

    /// <summary>
    /// 应用主题。任一步失败都不影响界面可用（资源缺失时 XAML 会回落到 Colors.xaml 里的默认值）。
    /// </summary>
    /// <param name="skinId">皮肤 Id，null/未知时用浅色。</param>
    /// <param name="accentIndex">强调色下标，越界时夹取到合法区间。</param>
    public static void Apply(string? skinId, int accentIndex = 0)
    {
        var skin = Find(skinId) ?? Skins[0];
        accentIndex = Math.Clamp(accentIndex, 0, AccentPalettes.Length - 1);
        var palette = Build(skin, accentIndex);

        if (Application.Current is { } app)
        {
            var res = app.Resources;
            foreach (var (key, color) in palette)
                res[key] = color;

            // 原生控件（Entry / Slider / Switch 的默认外观）跟随明暗
            app.UserAppTheme = skin.IsDark ? AppTheme.Dark : AppTheme.Light;
        }

        CurrentSkinId = skin.Id;
        CurrentAccentIndex = accentIndex;
        IsDark = skin.IsDark;

        ApplySystemBars(palette);

        Changed?.Invoke();
    }

    /// <summary>取某个强调色方案的主色（侧边栏/设置页的色点预览用）。</summary>
    public static Color AccentColor(int index) =>
        AccentPalettes[Math.Clamp(index, 0, AccentPalettes.Length - 1)].Accent;

    /// <summary>取一个主题色（供代码里动态设置颜色用，避免硬编码）。</summary>
    public static Color Get(string key) =>
        Application.Current?.Resources.TryGetValue(key, out var value) == true && value is Color color
            ? color
            : Colors.Gray;

    /// <summary>把六个系统栏/装饰色刷成与主题一致。</summary>
    private static void ApplySystemBars(IReadOnlyDictionary<string, Color> palette)
    {
        try
        {
            var window = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Window;
            if (window is null) return;

            var surface = palette["AmBg"];
            window.SetStatusBarColor(surface.ToPlatform());
            window.SetNavigationBarColor(palette["AmSurface"].ToPlatform());

            // 浅色底 -> 深色图标；深色底 -> 浅色图标
            var lightIcons = IsDark;
            var controller = new AndroidX.Core.View.WindowInsetsControllerCompat(window, window.DecorView);
            controller.AppearanceLightStatusBars = !lightIcons;
            controller.AppearanceLightNavigationBars = !lightIcons;

            // Android 15 起强制边到边，直接用 SetStatusBarColor 已无效，
            // 这里不再额外处理，交给系统默认的透明状态栏。
        }
        catch
        {
            // 系统栏着色只是锦上添花，任何机型不支持都不应影响主题本身
        }
    }

    /// <summary>由皮肤 + 强调色推导出全部颜色令牌。</summary>
    private static Dictionary<string, Color> Build(Skin skin, int accentIndex)
    {
        var bg = Color.FromArgb(skin.BgHex);
        var (accent, hover, soft) = AccentPalettes[accentIndex];

        Color surface, surfaceAlt, divider, chipBg, skeleton, coverPlaceholder;
        Color textPrimary, textSecondary, textTertiary;

        if (skin.IsDark)
        {
            surface = Mix(bg, Colors.White, 0.07);
            surfaceAlt = Mix(bg, Colors.White, 0.035);
            divider = Mix(bg, Colors.White, 0.13);
            chipBg = Mix(bg, Colors.White, 0.11);
            skeleton = Mix(bg, Colors.White, 0.09);
            coverPlaceholder = Mix(bg, Colors.White, 0.16);
            textPrimary = Mix(bg, Colors.White, 0.93);
            textSecondary = Mix(bg, Colors.White, 0.68);
            textTertiary = Mix(bg, Colors.White, 0.46);
        }
        else
        {
            surface = Colors.White;
            surfaceAlt = Mix(bg, Colors.White, 0.62);
            divider = Mix(bg, Colors.Black, 0.07);
            chipBg = Mix(bg, Colors.Black, 0.045);
            skeleton = Mix(bg, Colors.Black, 0.06);
            coverPlaceholder = Mix(bg, Colors.Black, 0.12);
            textPrimary = Color.FromArgb("#1A1A1A");
            textSecondary = Color.FromArgb("#5F5F68");
            textTertiary = Color.FromArgb("#9A9AA3");
        }

        // 深色底上的浅色 Soft 会刺眼，改成向背景靠拢的强调色浅染
        var primarySoft = skin.IsDark ? Mix(bg, accent, 0.26) : soft;

        return new Dictionary<string, Color>
        {
            // 品牌 / 强调
            ["AmPrimary"] = accent,
            ["AmPrimaryDark"] = Mix(accent, Colors.Black, 0.18),
            ["AmPrimaryHover"] = hover,
            ["AmPrimarySoft"] = primarySoft,
            ["AmAccent"] = accent,
            ["AmTextOnPrimary"] = Colors.White,

            // 背景层级
            ["AmBg"] = bg,
            ["AmSurface"] = surface,
            ["AmSurfaceAlt"] = surfaceAlt,
            ["AmDivider"] = divider,
            ["AmOverlay"] = Color.FromRgba(0, 0, 0, 0.42),
            ["AmPlayerBg"] = surface,
            ["AmChipBg"] = chipBg,
            ["AmSkeleton"] = skeleton,
            ["AmCoverPlaceholder"] = coverPlaceholder,

            // 文字层级
            ["AmTextPrimary"] = textPrimary,
            ["AmTextSecondary"] = textSecondary,
            ["AmTextTertiary"] = textTertiary,

            // 功能色
            ["AmSuccess"] = Color.FromArgb("#2FB86B"),
            ["AmWarning"] = Color.FromArgb("#F59E0B"),
            ["AmDanger"] = Color.FromArgb("#E5484D"),
            ["AmLike"] = Color.FromArgb("#E5484D"),
        };
    }

    /// <summary>线性混色：<paramref name="t"/> 为 0 取 a，为 1 取 b。</summary>
    private static Color Mix(Color a, Color b, double t)
    {
        var k = (float)Math.Clamp(t, 0, 1);
        return new Color(
            a.Red + (b.Red - a.Red) * k,
            a.Green + (b.Green - a.Green) * k,
            a.Blue + (b.Blue - a.Blue) * k,
            a.Alpha + (b.Alpha - a.Alpha) * k);
    }
}
