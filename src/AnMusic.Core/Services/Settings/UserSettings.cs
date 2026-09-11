namespace AnMusic.Services.Settings;

/// <summary>
/// 用户设置模型（持久化到 %AppData%\AnMusic\settings.json）。
/// </summary>
public sealed class UserSettings
{
    /// <summary>
    /// 皮肤 Id（如 "StarSea" / "Light" / "Dark"）。
    /// </summary>
    /// <remarks>
    /// 空 = 用户从未选过皮肤，由各端决定自己的默认皮肤（桌面端为品牌皮肤「星海蓝」，
    /// 安卓端沿用浅色）。不在这里硬编码某端的默认值，免得两端互相改对方的默认外观。
    /// </remarks>
    public string Theme { get; set; } = "";

    /// <summary>音乐库扫描目录（启动时自动加载）。</summary>
    public string? MusicDirectory { get; set; }

    /// <summary>下载保存目录（空 = 保存到音乐库目录，未设置音乐库时保存到「我的音乐\AnMusic」）。</summary>
    public string? DownloadDirectory { get; set; }

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
    /// <summary>桌面歌词行距系数 1.1~2.2。</summary>
    public double DesktopLyricsLineSpacing { get; set; } = 1.45;
    /// <summary>桌面歌词是否锁定（锁定时禁止拖拽/关闭，仅允许点击跳转行）。</summary>
    public bool DesktopLyricsIsLocked { get; set; }
    /// <summary>桌面歌词窗口位置（双屏记忆）。</summary>
    public double DesktopLyricsLeft { get; set; } = double.NaN;
    public double DesktopLyricsTop { get; set; } = double.NaN;

    // 迷你悬浮卡片播放器
    /// <summary>迷你卡片窗口左上角位置（NaN = 未设置，首次打开默认屏幕右下角）。</summary>
    public double MiniCardLeft { get; set; } = double.NaN;
    public double MiniCardTop { get; set; } = double.NaN;

    /// <summary>网络代理地址（如 http://127.0.0.1:7890；空 = 跟随系统设置）。</summary>
    public string ProxyUrl { get; set; } = "";

    // 键盘快捷键
    /// <summary>快捷键总开关。</summary>
    public bool ShortcutsEnabled { get; set; } = true;
    /// <summary>动作 Id → 按键串（规范串，如 "Ctrl+Shift+L"；空串 = 该动作未绑定）。</summary>
    public Dictionary<string, string>? ShortcutBindings { get; set; }
    /// <summary>需要在其他程序窗口中也生效的动作 Id（全局热键，需含修饰键）。</summary>
    public List<string>? GlobalShortcutIds { get; set; }

    /// <summary>
    /// 主题强调色方案索引：0=科技蓝, 1=暗夜紫, 2=森林绿, 3=日落橙, 4=玫瑰红, 5=海洋青, 6=品牌蓝。
    /// </summary>
    /// <remarks>
    /// -1 = 用户从未单独选过强调色：按"跟随皮肤自带的配套强调色"处理（各端自行换算，
    /// 不认识的下标一律夹取到合法区间，因此旧版本读到 -1 也不会出错）。
    /// </remarks>
    public int AccentColorIndex { get; set; } = -1;

    /// <summary>关闭行为：0=每次询问, 1=后台运行, 2=直接关闭。</summary>
    public int CloseBehavior { get; set; }

    /// <summary>
    /// 已经看过更新通知的版本号（"私信"里点开过就不再亮红点）。
    /// </summary>
    /// <remarks>空 = 没看过任何版本；只影响小红点，不影响设置页的检查更新。</remarks>
    public string LastSeenUpdateVersion { get; set; } = "";

    /// <summary>
    /// 已经读过的公告 Id 列表（"私信"里点开过就不再算未读）。
    /// </summary>
    /// <remarks>只保留最近若干条，避免长期使用后无限增长。</remarks>
    public List<string>? SeenAnnouncementIds { get; set; }

    /// <summary>
    /// 公告源地址（announcements.json 的完整 URL）。
    /// </summary>
    /// <remarks>
    /// 留空 = 用内置的 GitHub 官方仓库地址。想换成自建服务器 / Gitee 镜像时，
    /// 在设置页填一个地址即可，不需要发新版本。
    /// </remarks>
    public string AnnouncementUrl { get; set; } = "";
}
