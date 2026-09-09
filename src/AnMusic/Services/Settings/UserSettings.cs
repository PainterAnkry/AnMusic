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

    /// <summary>动态壁纸索引：0=无（使用背景图/默认）, 1=鼠标跟随（流光）, 2=星河（缓慢飘移）。</summary>
    public int WallpaperIndex { get; set; } = 0;

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

    // 用户资料
    public string UserNickname { get; set; } = "音乐爱好者";
    public string? UserAvatarPath { get; set; }

    // 桌面歌词
    /// <summary>桌面歌词字号 14~60。</summary>
    public double DesktopLyricsFontSize { get; set; } = 30;
    /// <summary>桌面歌词底层背景不透明度 0~0.95。</summary>
    public double DesktopLyricsBgOpacity { get; set; } = 0.65;
    /// <summary>桌面歌词窗口宽度 400~1920。</summary>
    public double DesktopLyricsWidth { get; set; } = 960;

    /// <summary>主题强调色方案索引：0=科技蓝, 1=暗夜紫, 2=森林绿, 3=日落橙, 4=玫瑰红, 5=海洋青。</summary>
    public int AccentColorIndex { get; set; }

    /// <summary>关闭行为：0=每次询问, 1=后台运行, 2=直接关闭。</summary>
    public int CloseBehavior { get; set; }
}
