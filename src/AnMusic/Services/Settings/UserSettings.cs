namespace AnMusic.Services.Settings;

/// <summary>
/// 用户设置模型（持久化到 %AppData%\AnMusic\settings.json）。
/// </summary>
public sealed class UserSettings
{
    /// <summary>主题："Dark" 或 "Light"。</summary>
    public string Theme { get; set; } = "Light";

    /// <summary>音乐库扫描目录（启动时自动加载）。</summary>
    public string? MusicDirectory { get; set; }

    /// <summary>默认音量 0~1。</summary>
    public double DefaultVolume { get; set; } = 1.0;

    /// <summary>在线歌词开关（默认关，合规考虑）。</summary>
    public bool EnableOnlineLyrics { get; set; } = false;

    /// <summary>自定义背景图片路径（空 = 使用纯主题背景）。</summary>
    public string BackgroundImagePath { get; set; } = "";

    /// <summary>背景图不透明度 0~1。</summary>
    public double BackgroundOpacity { get; set; } = 0.35;

    /// <summary>歌词正文字号 12~24。</summary>
    public double LyricFontSize { get; set; } = 14;

    /// <summary>歌词颜色方案索引：0=跟随主题, 1=白, 2=黑, 3=粉, 4=蓝, 5=绿。</summary>
    public int LyricColorIndex { get; set; } = 0;

    // 窗口位置尺寸
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 960;
    public double WindowHeight { get; set; } = 640;
    public bool WindowMaximized { get; set; }

    // 均衡器状态
    public float[]? EqualizerGains { get; set; }
    public bool EqualizerEnabled { get; set; } = true;
}
