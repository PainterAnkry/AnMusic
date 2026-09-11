namespace AnMusic.Services.Providers.Bilibili;

/// <summary>
/// B 站曲目 Id 的编解码：普通视频用 bvid 本身，多分P 的第 2 P 起用 <c>{bvid}_p{N}</c>。
/// </summary>
/// <remarks>
/// 为什么把分P 编进 Id：
/// <list type="bullet">
/// <item><c>Track.Id</c> 是收藏 / 歌单 / 播放队列的唯一键，同一视频的不同分P 必须能区分，
/// 否则收藏 P2 会覆盖 P1、加入歌单也会互相顶掉。</item>
/// <item>Id 会随用户数据落盘，必须自带恢复到"哪一 P"的全部信息。</item>
/// <item>第 1 P 刻意用裸 bvid：它就是这个视频本身，能与搜索结果里的同一条曲目合并去重
/// （同一份音频缓存、同一个收藏项）。</item>
/// </list>
/// bvid 的字符集是 <c>[A-Za-z0-9]</c>，不含下划线，因此 <c>_p数字</c> 后缀不会与真实 bvid 冲突。
/// </remarks>
public static class BiliTrackId
{
    private const string PartSeparator = "_p";

    /// <summary>构造分P 曲目 Id（page ≤ 1 时返回 bvid 本身）。</summary>
    public static string Build(string bvid, int page)
        => page <= 1 ? bvid : $"{bvid}{PartSeparator}{page}";

    /// <summary>从 Id 解析出 bvid 与分P 号；不是分P 形式的 Id 一律按第 1 P 处理。</summary>
    public static (string Bvid, int Page) Parse(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return (string.Empty, 1);

        var idx = id.LastIndexOf(PartSeparator, StringComparison.Ordinal);
        if (idx <= 0) return (id, 1);

        var tail = id.AsSpan(idx + PartSeparator.Length);
        if (tail.Length == 0 || !int.TryParse(tail, out var page) || page < 1)
            return (id, 1);

        return (id[..idx], page);
    }

    /// <summary>视频页地址（分P 带 <c>?p=N</c>，点开就是同一 P）。</summary>
    public static string ToVideoUrl(string bvid, int page)
        => page <= 1
            ? $"https://www.bilibili.com/video/{bvid}"
            : $"https://www.bilibili.com/video/{bvid}?p={page}";
}
