using System.Collections;
using AnMusic.Models;

namespace AnMusic.Services.Formatting;

/// <summary>
/// 曲目列表排序比较器：歌名/歌手/专辑不区分大小写，时长按时间值；
/// 降序时取反。用于列表视图的 CustomSort（不改动源集合，取消排序即可恢复原顺序）。
/// </summary>
public sealed class TrackSortComparer(string field, bool descending) : IComparer
{
    /// <summary>可排序字段（与表头 Tag 对应）。</summary>
    public const string FieldTitle = "Title";
    public const string FieldArtist = "Artist";
    public const string FieldAlbum = "Album";
    public const string FieldDuration = "Duration";

    public int Compare(object? x, object? y)
    {
        if (x is not Track a || y is not Track b) return 0;

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
