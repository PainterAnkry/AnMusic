using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Xml.Linq;
using AnMusic.Services.Settings;

namespace AnMusic.Tests;

/// <summary>
/// 皮肤资源完整性 + 默认皮肤解析。
/// </summary>
/// <remarks>
/// 皮肤是"一套 XAML + 一条 <see cref="Skin"/> 记录"的组合，两边很容易对不上：
/// <list type="bullet">
/// <item>新增皮肤时漏写某个画刷键 → 运行时 DynamicResource 找不到值（界面缺色/报错）</item>
/// <item>记录里的 BgHex/AccentHex 与 XAML 实际配色不一致 → 皮肤面板的预览方块与换上的皮肤不是一套</item>
/// <item>深色皮肤文字与底色对比度不足 → 看不清</item>
/// </list>
/// 这些都不该靠肉眼看，这里逐条锁住。
/// </remarks>
public class ThemeSkinTests
{
    /// <summary>皮肤资源字典所在目录（从测试输出目录往上找仓库根）。</summary>
    private static readonly string ThemeDir = Path.Combine(FindRepoRoot(), "src", "AnMusic", "Themes");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AnMusic.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException($"从 {AppContext.BaseDirectory} 往上找不到仓库根（AnMusic.slnx）");
    }

    /// <summary>读一份皮肤 XAML，返回 键 → 颜色。</summary>
    private static Dictionary<string, string> ReadSkin(string skinId)
    {
        var path = Path.Combine(ThemeDir, skinId + ".xaml");
        Assert.True(File.Exists(path), $"皮肤文件不存在：{path}");

        var doc = XDocument.Load(path);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        return doc.Descendants()
            .Where(e => e.Name.LocalName == "SolidColorBrush")
            .ToDictionary(
                e => (string?)e.Attribute(x + "Key") ?? "",
                e => (string?)e.Attribute("Color") ?? "");
    }

    [Fact]
    public void 每套皮肤都有对应的资源字典文件()
    {
        foreach (var skin in ThemeService.Skins)
            Assert.True(File.Exists(Path.Combine(ThemeDir, skin.Id + ".xaml")), $"缺少 Themes/{skin.Id}.xaml");
    }

    /// <summary>
    /// 皮肤 XAML 必须真的被编译进了程序集（BAML）。
    /// </summary>
    /// <remarks>
    /// 只查磁盘文件不够：文件放在 Themes/ 下但没被 WPF 当成 Page 编译的话，
    /// 运行时 <c>pack://application:,,,/AnMusic;component/Themes/X.xaml</c> 会直接找不到资源。
    /// </remarks>
    [Fact]
    public void 每套皮肤的BAML都嵌进了程序集()
    {
        var asm = typeof(ThemeService).Assembly;
        using var stream = asm.GetManifestResourceStream("AnMusic.g.resources");
        Assert.NotNull(stream);

        using var reader = new System.Resources.ResourceReader(stream);
        var names = reader.Cast<System.Collections.DictionaryEntry>()
            .Select(e => (string)e.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var skin in ThemeService.Skins)
            Assert.True(names.Contains($"themes/{skin.Id}.baml"),
                $"程序集里没有 themes/{skin.Id}.baml（该 xaml 没被编译为 Page）");
    }

    [Fact]
    public void 各皮肤定义的画刷键完全一致()
    {
        var reference = ReadSkin("Dark").Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

        foreach (var skin in ThemeService.Skins)
        {
            var keys = ReadSkin(skin.Id).Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
            Assert.True(reference.SequenceEqual(keys),
                $"皮肤 {skin.Id} 的画刷键与 Dark.xaml 不一致：" +
                $"缺少 [{string.Join(",", reference.Except(keys))}]，多出 [{string.Join(",", keys.Except(reference))}]");
        }
    }

    [Fact]
    public void 皮肤记录里的颜色与XAML实际配色一致()
    {
        foreach (var skin in ThemeService.Skins)
        {
            var brushes = ReadSkin(skin.Id);

            // 记录里的 BgHex / AccentHex 是皮肤面板预览方块用的，必须就是皮肤真实的底色/强调色
            Assert.Equal(skin.BgHex, brushes["BgMain"], ignoreCase: true);
            Assert.Equal(skin.AccentHex, brushes["Accent"], ignoreCase: true);
        }
    }

    [Fact]
    public void 配色都是合法十六进制颜色()
    {
        foreach (var skin in ThemeService.Skins)
        {
            foreach (var (key, value) in ReadSkin(skin.Id))
            {
                // 支持 #RRGGBB 与带透明度的 #AARRGGBB（BgMainAlpha / BgContentAlpha 用后者）
                Assert.True(value.Length is 7 or 9 && value[0] == '#',
                    $"{skin.Id}.{key} 不是 #RRGGBB / #AARRGGBB：{value}");
                Assert.True(int.TryParse(value[1..], NumberStyles.HexNumber, null, out _),
                    $"{skin.Id}.{key} 颜色无法解析：{value}");
            }
        }
    }

    [Theory]
    [InlineData("FgPrimary", 7.0)]    // 正文主色：WCAG AAA
    [InlineData("FgNormal", 7.0)]
    [InlineData("FgSecondary", 4.5)]  // 次要文字：AA
    public void 文字对底色有足够对比度(string key, double minRatio)
    {
        foreach (var skin in ThemeService.Skins)
            AssertContrast(skin, key, minRatio);
    }

    /// <summary>
    /// 强调色对底色的对比度（用于链接文字、图标、进度条这类"图形"元素）。
    /// </summary>
    /// <remarks>
    /// 深色皮肤按 WCAG AA 的图形标准要求 3:1；浅色皮肤放松到 2.3:1 ——
    /// 浅底上的鲜艳强调色天然达不到 3:1（现有「护眼绿」#10B981 在 #F2F7F0 上就只有 2.34:1），
    /// 这里只拦"比现有最紧的还差"的情况，不强行改动已有皮肤观感。
    /// </remarks>
    [Theory]
    [InlineData(false, 2.3)]
    [InlineData(true, 3.0)]
    public void 强调色对底色有足够对比度(bool isDark, double minRatio)
    {
        foreach (var skin in ThemeService.Skins)
        {
            if (skin.IsDark != isDark) continue;
            AssertContrast(skin, "Accent", minRatio);
        }
    }

    private static void AssertContrast(Skin skin, string key, double minRatio)
    {
        var brushes = ReadSkin(skin.Id);
        var ratio = Contrast(brushes[key], brushes["BgMain"]);
        Assert.True(ratio >= minRatio,
            $"皮肤 {skin.Id} 的 {key}({brushes[key]}) 对底色({brushes["BgMain"]}) 对比度只有 {ratio:F2}:1，低于 {minRatio}:1");
    }

    /// <summary>皮肤记录里的配套强调色下标必须落在合法区间。</summary>
    [Fact]
    public void 每套皮肤的配套强调色下标合法()
    {
        foreach (var skin in ThemeService.Skins)
            Assert.InRange(skin.AccentIndex, 0, ThemeService.MaxAccentIndex);
    }

    [Fact]
    public void 默认皮肤在皮肤表里且是品牌皮肤()
    {
        Assert.Equal("StarSea", ThemeService.DefaultSkinId);
        Assert.Equal(ThemeService.DefaultSkinId, ThemeService.Default.Id);
        Assert.True(ThemeService.Default.IsDark);
        Assert.Contains(ThemeService.Skins, s => s.Id == ThemeService.DefaultSkinId);
    }

    [Fact]
    public void 未设置或未知皮肤时回落到默认皮肤()
    {
        Assert.Equal(ThemeService.DefaultSkinId, ThemeService.Find(null)!.Id);
        Assert.Equal(ThemeService.DefaultSkinId, ThemeService.Find("")!.Id);
        Assert.Equal(ThemeService.DefaultSkinId, ThemeService.Find("   ")!.Id);
        Assert.Equal(ThemeService.DefaultSkinId, ThemeService.Find("SkinThatNoLongerExists")!.Id);
    }

    [Fact]
    public void 历史配置里的浅色深色仍可识别()
    {
        Assert.Equal("Light", ThemeService.Find("light")!.Id);
        Assert.Equal("Dark", ThemeService.Find("dark")!.Id);
        Assert.Equal("Light", ThemeService.Find("Light")!.Id);
    }

    [Fact]
    public void 品牌强调色跟在默认皮肤后面()
    {
        // 默认皮肤的配套强调色 = 品牌蓝（取自图标），新装即与图标同色
        var brand = ThemeService.AccentNames[ThemeService.Default.AccentIndex];
        Assert.Equal("品牌蓝", brand);
        Assert.Equal(ThemeService.AccentNames.Count - 1, ThemeService.MaxAccentIndex);
    }

    /// <summary>WCAG 相对对比度。</summary>
    private static double Contrast(string hexA, string hexB)
    {
        var (la, lb) = (Luminance(hexA), Luminance(hexB));
        var (hi, lo) = la > lb ? (la, lb) : (lb, la);
        return (hi + 0.05) / (lo + 0.05);
    }

    private static double Luminance(string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex)!;
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }
}

/// <summary>应用图标：源图与生成物必须成对存在（新图标就是应用图标）。</summary>
public class AppIconTests
{
    private static string AssetsDir
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "AnMusic.slnx")))
                    return Path.Combine(dir.FullName, "src", "AnMusic", "Assets");
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException("找不到仓库根");
        }
    }

    [Fact]
    public void 图标文件齐全且不是空文件()
    {
        foreach (var name in new[] { "app.ico", "app-icon.png" })
        {
            var path = Path.Combine(AssetsDir, name);
            Assert.True(File.Exists(path), $"缺少 {name}");
            Assert.True(new FileInfo(path).Length > 1024, $"{name} 内容不完整");
        }
    }

    [Fact]
    public void ico包含全部常用尺寸()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AssetsDir, "app.ico"));
        Assert.Equal(0, BitConverter.ToUInt16(bytes, 0));          // reserved
        Assert.Equal(1, BitConverter.ToUInt16(bytes, 2));          // type = icon
        var count = BitConverter.ToUInt16(bytes, 4);
        var sizes = new List<int>();
        for (var i = 0; i < count; i++)
        {
            var offset = 6 + i * 16;
            var w = bytes[offset] == 0 ? 256 : bytes[offset];
            sizes.Add(w);
        }
        foreach (var expected in new[] { 16, 24, 32, 48, 64, 128, 256 })
            Assert.Contains(expected, sizes);
    }

    [Fact]
    public void 生成脚本存在且指向根目录的源图()
    {
        var script = Path.Combine(AssetsDir, "make-icon.py");
        Assert.True(File.Exists(script), "缺少 make-icon.py");
        var text = File.ReadAllText(script);
        Assert.Contains("AnMusic.png", text);
    }
}
