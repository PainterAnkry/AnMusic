using System.Collections;
using AnMusic.Models;

namespace AnMusic.Services.Formatting;

/// <summary>
/// 曲目列表排序比较器：歌名/歌手/专辑不区分大小写，时长按时间值；
/// 降序时取反。用于列表视图的 CustomSort（不改动源集合，取消排序即可恢复原顺序）。
/// </summary>
/// <remarks>
/// 同时实现非泛型 <see cref="IComparer"/> 与 <see cref="IComparer{T}"/>：
/// 桌面端 WPF 的 <c>ListCollectionView.CustomSort</c> 只认非泛型版本，
/// 而安卓端用 <c>List&lt;Track&gt;.Sort(IComparer&lt;Track&gt;)</c> 需要泛型版本，
/// 两边共用同一套排序规则，避免各写一份导致行为不一致。
/// </remarks>
public sealed class TrackSortComparer(string field, bool descending) : IComparer, IComparer<Track>
{
    /// <summary>可排序字段（与表头 Tag 对应）。</summary>
    public const string FieldTitle = "Title";
    public const string FieldArtist = "Artist";
    public const string FieldAlbum = "Album";
    public const string FieldDuration = "Duration";

    public int Compare(object? x, object? y)
    {
        if (x is not Track a || y is not Track b) return 0;
        return Compare(a, b);
    }

    public int Compare(Track? a, Track? b)
    {
        if (a is null || b is null) return 0;

        var result = field switch
        {
            FieldArtist => string.Compare(a.Artist, b.Artist, StringComparison.OrdinalIgnoreCase),
            FieldAlbum => string.Compare(a.Album, b.Album, StringComparison.OrdinalIgnoreCase),
            FieldDuration => a.Duration.CompareTo(b.Duration),
            _ => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase)
        };
        return descending ? -result : result;
    }
}
