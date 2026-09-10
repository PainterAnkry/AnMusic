namespace AnMusic.Services.Shortcuts;

/// <summary>
/// 一个可绑定快捷键的动作（Id 用于持久化，名称/说明用于设置页展示）。
/// </summary>
public sealed record ShortcutAction(string Id, string Name, string Tip, string DefaultGesture);

/// <summary>
/// 全部快捷键动作的登记表：动作 Id、展示名、默认按键。
/// 新增动作 = 在这里加一条 + 在 MainViewModel.ExecuteShortcut 里加一个分支。
/// </summary>
public static class ShortcutActions
{
    public const string PlayPause = "PlayPause";
    public const string NextTrack = "NextTrack";
    public const string PrevTrack = "PrevTrack";
    public const string VolumeUp = "VolumeUp";
    public const string VolumeDown = "VolumeDown";
    public const string ToggleMute = "ToggleMute";
    public const string SeekForward = "SeekForward";
    public const string SeekBackward = "SeekBackward";
    public const string CyclePlayMode = "CyclePlayMode";
    public const string ToggleFavorite = "ToggleFavorite";
    public const string ToggleLyricsPage = "ToggleLyricsPage";
    public const string ToggleDesktopLyrics = "ToggleDesktopLyrics";
    public const string ToggleMiniPlayer = "ToggleMiniPlayer";
    public const string FocusSearch = "FocusSearch";
    public const string OpenSettings = "OpenSettings";
    public const string ToggleMainWindow = "ToggleMainWindow";

    /// <summary>全部动作（顺序即设置页展示顺序）。</summary>
    public static readonly IReadOnlyList<ShortcutAction> All =
    [
        new(PlayPause,          "播放 / 暂停",     "播放或暂停当前曲目",             "Space"),
        new(NextTrack,          "下一首",         "切换到队列下一首",               "Ctrl+Right"),
        new(PrevTrack,          "上一首",         "切换到队列上一首",               "Ctrl+Left"),
        new(VolumeUp,           "音量 +",         "音量提高 5%",                    "Ctrl+Up"),
        new(VolumeDown,         "音量 −",         "音量降低 5%",                    "Ctrl+Down"),
        new(ToggleMute,         "静音 / 取消静音", "一键静音，再按恢复原音量",        "Ctrl+M"),
        new(SeekForward,        "快进 5 秒",      "播放位置前进 5 秒",              "Shift+Right"),
        new(SeekBackward,       "快退 5 秒",      "播放位置后退 5 秒",              "Shift+Left"),
        new(CyclePlayMode,      "切换播放模式",    "顺序 → 列表循环 → 单曲循环 → 随机", "Ctrl+P"),
        new(ToggleFavorite,     "收藏当前歌曲",    "加入或移出「我喜欢」",            "Ctrl+D"),
        new(ToggleLyricsPage,   "歌词页开关",      "打开 / 关闭全屏歌词页",           "Ctrl+L"),
        new(ToggleDesktopLyrics,"桌面歌词开关",    "显示 / 隐藏独立桌面歌词悬浮窗",    "Ctrl+Shift+L"),
        new(ToggleMiniPlayer,   "迷你悬浮卡片",    "显示 / 收起迷你悬浮卡片播放器",    "Ctrl+Shift+M"),
        new(FocusSearch,        "聚焦搜索框",      "光标跳到顶部搜索框",              "Ctrl+F"),
        // 注意：按键串要用 Key 枚举名（逗号键是 OemComma，展示时显示为 “Ctrl+,”）
        new(OpenSettings,       "打开设置",        "打开设置页",                     "Ctrl+OemComma"),
        new(ToggleMainWindow,   "显示 / 隐藏主窗口", "主窗口显示隐藏切换（配合后台运行）", "Ctrl+Shift+H"),
    ];

    /// <summary>默认按键表（Id → 规范按键串）。</summary>
    public static Dictionary<string, string> DefaultBindings() =>
        All.ToDictionary(a => a.Id, a => a.DefaultGesture);
}
