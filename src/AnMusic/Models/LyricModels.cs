namespace AnMusic.Models;

using System.ComponentModel;
using System.Runtime.CompilerServices;

/// <summary>
/// 单行歌词。
/// </summary>
public sealed class LyricLine : INotifyPropertyChanged
{
    public TimeSpan Time { get; init; }
    public string Text { get; init; } = "";
    public bool IsInstrumental { get; init; }

    private string? _translation;
    /// <summary>翻译文本（点击"译"按钮后填充；null 表示不显示）。</summary>
    public string? Translation
    {
        get => _translation;
        set
        {
            if (_translation == value) return;
            _translation = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public override string ToString() => $"[{Time:mm\\:ss\\.ff}] {Text}";
}

/// <summary>
/// 歌词文档：已排序的歌词行集合，支持按时间二分查找。
/// </summary>
public sealed class LyricDocument
{
    public List<LyricLine> Lines { get; } = [];
    public double OffsetMs { get; set; }
    public bool IsSynced { get; private set; }

    public void MarkSynced(bool synced) => IsSynced = synced;

    /// <summary>二分查找当前时间对应的歌词行索引（最后一条 Time <= position）。</summary>
    public int LineIndexAt(TimeSpan position)
    {
        if (Lines.Count == 0) return -1;

        var adjusted = position - TimeSpan.FromMilliseconds(OffsetMs);
        if (adjusted < TimeSpan.Zero) return -1;

        int lo = 0, hi = Lines.Count - 1, result = -1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (Lines[mid].Time <= adjusted)
            {
                result = mid;
                lo = mid + 1;
            }
            else
                hi = mid - 1;
        }
        return result;
    }

    public LyricLine? LineAt(TimeSpan position)
    {
        var idx = LineIndexAt(position);
        return idx >= 0 ? Lines[idx] : null;
    }
}
