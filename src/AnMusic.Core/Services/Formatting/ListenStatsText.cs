namespace AnMusic.Services.Formatting;

/// <summary>
/// 听歌统计的展示文案（听歌排行用）：时长格式化与"播放 N 次 · 累计 X"组合。
/// 放在 Core 里各端共用，也便于单测。
/// </summary>
public static class ListenStatsText
{
    /// <summary>秒数 → "45 秒 / 12 分 30 秒 / 1 小时 3 分"。</summary>
    public static string Duration(double seconds)
    {
        if (seconds <= 0) return "0 秒";
        if (seconds < 60) return $"{(int)seconds} 秒";
        if (seconds < 3600) return $"{(int)(seconds / 60)} 分 {(int)(seconds % 60)} 秒";
        return $"{(int)(seconds / 3600)} 小时 {(int)(seconds % 3600 / 60)} 分";
    }

    /// <summary>
    /// 组合统计行：有次数时"播放 12 次 · 累计 1 小时 3 分"，
    /// 旧数据没有次数时只显示"累计 1 小时 3 分"。
    /// </summary>
    public static string Compose(int playCount, double seconds)
        => playCount > 0
            ? $"播放 {playCount} 次 · 累计 {Duration(seconds)}"
            : $"累计 {Duration(seconds)}";
}
