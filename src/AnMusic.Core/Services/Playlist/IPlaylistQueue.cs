using AnMusic.Models;

namespace AnMusic.Services.Playlist;

/// <summary>
/// 播放队列抽象：管理当前播放列表、索引、循环/随机模式。
/// </summary>
public interface IPlaylistQueue
{
    IReadOnlyList<Track> Queue { get; }
    int CurrentIndex { get; }
    Track? Current { get; }
    RepeatMode Repeat { get; set; }
    bool Shuffle { get; set; }

    void SetItems(IEnumerable<Track> tracks, int startIndex = 0);
    Track? MoveNext();
    Track? MovePrevious();
    Track? JumpTo(int index);

    /// <summary>从队列移除曲目（不允许移除当前正在播放的曲目），返回是否成功。</summary>
    bool RemoveTrack(Track track);

    /// <summary>把曲目在队列中上移/下移（delta = ±1；不允许移动当前正在播放的曲目），返回是否成功。</summary>
    bool MoveTrack(Track track, int delta);

    /// <summary>预览接下来将播放的曲目（不含当前曲目，按当前模式计算）。</summary>
    IReadOnlyList<Track> PeekNext(int count);

    /// <summary>把曲目插到当前曲目之后（"下一首播放"）。</summary>
    void InsertNext(Track track);

    /// <summary>把一批曲目追加到队列末尾（个性电台自动续播）。</summary>
    void Append(IEnumerable<Track> tracks);

    event EventHandler? CurrentChanged;
}
