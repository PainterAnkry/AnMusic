using AnMusic.Models;

namespace AnMusic.Services.Playlist;

/// <summary>
/// 播放队列实现：支持 Repeat(None/One/All) 与 Shuffle(Fisher-Yates + history 栈)。
/// Shuffle 时 MovePrevious 回到刚听过的曲目（Spotify-like）。
/// </summary>
public sealed class PlaylistQueue : IPlaylistQueue
{
    private List<Track> _items = [];
    private int _currentIndex = -1;
    private List<int> _shuffleOrder = [];
    private int _shufflePosition = -1;
    private readonly Stack<int> _shuffleHistory = new();
    private bool _shuffle;

    public IReadOnlyList<Track> Queue => _items;
    public int CurrentIndex => _currentIndex;
    public Track? Current => _currentIndex >= 0 && _currentIndex < _items.Count
        ? _items[_currentIndex]
        : null;

    public RepeatMode Repeat { get; set; } = RepeatMode.None;

    public bool Shuffle
    {
        get => _shuffle;
        set
        {
            _shuffle = value;
            if (value && _items.Count > 0)
                RegenerateShuffleOrder();
            else
            {
                _shuffleOrder.Clear();
                _shuffleHistory.Clear();
                _shufflePosition = -1;
            }
        }
    }

    public event EventHandler? CurrentChanged;

    public void SetItems(IEnumerable<Track> tracks, int startIndex = 0)
    {
        _items = tracks.ToList();
        _currentIndex = Math.Clamp(startIndex, 0, Math.Max(0, _items.Count - 1));
        _shuffleHistory.Clear();
        if (_shuffle && _items.Count > 0)
            RegenerateShuffleOrder();
        RaiseCurrentChanged();
    }

    public Track? MoveNext()
    {
        if (_items.Count == 0) return null;

        if (Repeat == RepeatMode.One)
        {
            RaiseCurrentChanged();
            return Current;
        }

        if (_shuffle)
        {
            // 记录当前到 history
            if (_currentIndex >= 0)
                _shuffleHistory.Push(_currentIndex);

            _shufflePosition++;
            if (_shufflePosition >= _shuffleOrder.Count)
            {
                if (Repeat == RepeatMode.All)
                {
                    RegenerateShuffleOrder();
                    _shufflePosition = 0;
                }
                else
                    return null;
            }
            _currentIndex = _shuffleOrder[_shufflePosition];
        }
        else
        {
            _currentIndex++;
            if (_currentIndex >= _items.Count)
            {
                if (Repeat == RepeatMode.All)
                    _currentIndex = 0;
                else
                {
                    _currentIndex = _items.Count - 1;
                    return null;
                }
            }
        }

        RaiseCurrentChanged();
        return Current;
    }

    public Track? MovePrevious()
    {
        if (_items.Count == 0) return null;

        if (_shuffle && _shuffleHistory.Count > 0)
        {
            _currentIndex = _shuffleHistory.Pop();
            _shufflePosition = Math.Max(0, _shufflePosition - 1);
            RaiseCurrentChanged();
            return Current;
        }

        _currentIndex--;
        if (_currentIndex < 0)
        {
            _currentIndex = Repeat == RepeatMode.All ? _items.Count - 1 : 0;
        }

        RaiseCurrentChanged();
        return Current;
    }

    public Track? JumpTo(int index)
    {
        if (index < 0 || index >= _items.Count) return null;
        _currentIndex = index;
        if (_shuffle)
        {
            // 重置 shuffle 位置
            _shufflePosition = _shuffleOrder.IndexOf(index);
            if (_shufflePosition < 0) _shufflePosition = -1;
        }
        RaiseCurrentChanged();
        return Current;
    }

    /// <summary>预览接下来将播放的曲目（按当前模式计算；随机模式为洗牌顺序近似）。</summary>
    public IReadOnlyList<Track> PeekNext(int count)
    {
        var result = new List<Track>();
        if (_items.Count == 0 || count <= 0) return result;

        if (Repeat == RepeatMode.One)
        {
            for (var i = 0; i < count; i++)
                result.Add(_items[Math.Clamp(_currentIndex, 0, _items.Count - 1)]);
            return result;
        }

        if (_shuffle)
        {
            var pos = _shufflePosition;
            for (var i = 0; i < count; i++)
            {
                pos++;
                if (pos >= _shuffleOrder.Count)
                {
                    if (Repeat != RepeatMode.All) break;
                    pos = 0;
                }
                result.Add(_items[_shuffleOrder[pos]]);
            }
            return result;
        }

        var idx = _currentIndex;
        for (var i = 0; i < count; i++)
        {
            idx++;
            if (idx >= _items.Count)
            {
                if (Repeat != RepeatMode.All) break;
                idx = 0;
            }
            result.Add(_items[idx]);
        }
        return result;
    }

    /// <summary>把曲目插到当前曲目之后（"下一首播放"）；已在队列中的先移除避免重复。</summary>
    public void InsertNext(Track track)
    {
        var existing = _items.FindIndex(t => ReferenceEquals(t, track)
                                             || (t.Id == track.Id && t.ProviderId == track.ProviderId));
        if (existing == _currentIndex)
            return; // 就是当前曲目，无需插入

        if (existing >= 0)
        {
            _items.RemoveAt(existing);
            if (existing < _currentIndex) _currentIndex--;
        }

        var insertIndex = Math.Clamp(_currentIndex + 1, 0, _items.Count);
        _items.Insert(insertIndex, track);
        if (_shuffle) RegenerateShuffleOrder();
    }

    /// <summary>把一批曲目追加到队列末尾；随机模式下重建洗牌顺序。</summary>
    public void Append(IEnumerable<Track> tracks)
    {
        var added = tracks as ICollection<Track> ?? tracks.ToList();
        if (added.Count == 0) return;
        _items.AddRange(added);
        if (_shuffle) RegenerateShuffleOrder();
    }

    private void RegenerateShuffleOrder()
    {
        var indices = Enumerable.Range(0, _items.Count).ToList();
        // Fisher-Yates
        var rng = new Random();
        for (int i = indices.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (indices[i], indices[j]) = (indices[j], indices[i]);
        }
        _shuffleOrder = indices;
        _shufflePosition = _shuffleOrder.IndexOf(_currentIndex);
    }

    private void RaiseCurrentChanged() => CurrentChanged?.Invoke(this, EventArgs.Empty);
}
