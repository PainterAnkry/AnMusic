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

    /// <summary>预览接下来将播放的曲目（不含当前曲目，按当前模式计算）。</summary>
    IReadOnlyList<Track> PeekNext(int count);

    /// <summary>把曲目插到当前曲目之后（"下一首播放"）。</summary>
    void InsertNext(Track track);

    /// <summary>把一批曲目追加到队列末尾（个性电台自动续播）。</summary>
    void Append(IEnumerable<Track> tracks);

    event EventHandler? CurrentChanged;
}
