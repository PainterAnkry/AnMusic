using System.Linq;
using AnMusic.Models;
using AnMusic.Services.Playlist;

namespace AnMusic.Tests;

/// <summary>播放队列：四种播放模式、随机、移除与移动（曾出现"放完就停"的回归）。</summary>
public class PlaylistQueueTests
{
    private static Track T(string id) => new() { Id = id, Title = "歌曲" + id };

    private static PlaylistQueue Queue(int count, int start = 0)
    {
        var q = new PlaylistQueue();
        q.SetItems(Enumerable.Range(0, count).Select(i => T(i.ToString())), start);
        return q;
    }

    [Fact]
    public void SetItems设置队列与当前项()
    {
        var q = Queue(3, 1);
        Assert.Equal(3, q.Queue.Count);
        Assert.Equal(1, q.CurrentIndex);
        Assert.Equal("1", q.Current?.Id);
    }

    [Fact]
    public void 顺序播放_到尾返回null且停在最后一首()
    {
        var q = Queue(3, 0);
        Assert.Equal("1", q.MoveNext()?.Id);
        Assert.Equal("2", q.MoveNext()?.Id);
        Assert.Null(q.MoveNext());              // 顺序播放：播完即停
        Assert.Equal(2, q.CurrentIndex);        // 仍停在最后一首，便于重播
    }

    [Fact]
    public void 列表循环_到尾回到第一首()
    {
        var q = Queue(3, 0);
        q.Repeat = RepeatMode.All;
        q.MoveNext();
        q.MoveNext();
        Assert.Equal("0", q.MoveNext()?.Id);
    }

    [Fact]
    public void 单曲循环_始终返回当前曲目()
    {
        var q = Queue(3, 1);
        q.Repeat = RepeatMode.One;
        Assert.Equal("1", q.MoveNext()?.Id);
        Assert.Equal("1", q.MoveNext()?.Id);
        Assert.Equal(1, q.CurrentIndex);
    }

    [Fact]
    public void 随机播放_只返回队列内的曲目且不会一直同一首()
    {
        var q = Queue(5, 0);
        q.Shuffle = true;
        q.Repeat = RepeatMode.All;

        var valid = q.Queue.Select(t => t.Id).ToHashSet();
        var seen = new HashSet<string>();
        for (var i = 0; i < 20; i++)
        {
            var next = q.MoveNext();
            Assert.NotNull(next);
            Assert.Contains(next!.Id, valid);   // 只播队列里的歌
            seen.Add(next.Id);
        }
        Assert.True(seen.Count > 1, "随机模式应当会换歌");
        Assert.True(seen.Count <= valid.Count);
    }

    [Fact]
    public void 随机播放_顺序模式下洗完一轮即结束()
    {
        var q = Queue(4, 0);
        q.Shuffle = true;
        q.Repeat = RepeatMode.None;

        var steps = 0;
        while (q.MoveNext() is not null)
        {
            steps++;
            Assert.True(steps < 20, "顺序 + 随机不应无限循环");
        }
        Assert.Equal(3, steps); // 4 首歌：开启随机后还能顺序走到其余 3 首
    }

    [Fact]
    public void 随机播放_列表循环模式下持续有下一首()
    {
        var q = Queue(4, 0);
        q.Shuffle = true;
        q.Repeat = RepeatMode.All;
        for (var i = 0; i < 30; i++)
            Assert.NotNull(q.MoveNext());
    }

    [Fact]
    public void 关闭随机后从当前曲目继续顺序播放()
    {
        var q = Queue(4, 0);
        q.Repeat = RepeatMode.All;             // 循环模式：末尾也有确定的下一首
        q.Shuffle = true;
        q.MoveNext();
        var idx = q.CurrentIndex;              // 随机跳到的位置

        q.Shuffle = false;
        var expected = q.Queue[(idx + 1) % q.Queue.Count].Id;
        Assert.Equal(expected, q.MoveNext()?.Id); // 关闭随机后从当前位置顺序推进
    }

    [Fact]
    public void 开启随机后按下一首不会立刻停播()
    {
        // 回归：洗牌序列若把当前曲目排在末尾，MoveNext 会直接返回 null 导致"开了随机反而停住"
        for (var round = 0; round < 50; round++)
        {
            var q = Queue(5, 0);
            q.Shuffle = true;
            q.Repeat = RepeatMode.None;
            Assert.NotNull(q.MoveNext());
        }
    }

    [Fact]
    public void 随机播放_顺序模式下一轮恰好覆盖其余每首歌一次()
    {
        var q = Queue(6, 0);
        q.Shuffle = true;
        q.Repeat = RepeatMode.None;

        var played = new List<string>();
        while (q.MoveNext() is { } next) played.Add(next.Id);

        var expected = q.Queue.Select(t => t.Id).Where(id => id != "0").OrderBy(x => x).ToList();
        Assert.Equal(expected, played.OrderBy(x => x).ToList()); // 其余 5 首各播一次且不重复
    }

    [Fact]
    public void MovePrevious在队首停住或循环()
    {
        var q = Queue(3, 0);
        Assert.Equal("0", q.MovePrevious()?.Id);   // 顺序模式：停住
        Assert.Equal(0, q.CurrentIndex);

        q.Repeat = RepeatMode.All;
        Assert.Equal("2", q.MovePrevious()?.Id);   // 循环模式：绕到末尾
    }

    [Fact]
    public void JumpTo跳转并忽略越界()
    {
        var q = Queue(3, 0);
        Assert.Equal("2", q.JumpTo(2)?.Id);
        Assert.Null(q.JumpTo(3));
        Assert.Null(q.JumpTo(-1));
        Assert.Equal(2, q.CurrentIndex);
    }

    [Fact]
    public void RemoveTrack_允许移除其他曲目并修正当前索引()
    {
        var q = Queue(4, 2);
        Assert.True(q.RemoveTrack(q.Queue[0]!));
        Assert.Equal(3, q.Queue.Count);
        Assert.Equal(1, q.CurrentIndex);            // 当前曲目前移一位，仍指向同一首
        Assert.Equal("2", q.Current?.Id);
    }

    [Fact]
    public void RemoveTrack_拒绝移除当前播放曲目()
    {
        var q = Queue(3, 1);
        Assert.False(q.RemoveTrack(q.Queue[1]!));
        Assert.Equal(3, q.Queue.Count);
    }

    [Fact]
    public void MoveTrack_上下移动并保持当前曲目指向()
    {
        var q = Queue(4, 0);
        var target = q.Queue[2]!;

        Assert.True(q.MoveTrack(target, -1));
        Assert.Equal(1, q.Queue.ToList().IndexOf(target));

        Assert.True(q.MoveTrack(target, +1));
        Assert.Equal(2, q.Queue.ToList().IndexOf(target));

        Assert.False(q.MoveTrack(q.Queue[0]!, -1));  // 已在顶部，不动
    }

    [Fact]
    public void MoveTrack_拒绝移动当前播放曲目()
    {
        // 设计如此：正在播放的曲目不允许在队列里挪位置（界面上的上移/下移对当前行无效）
        var q = Queue(3, 1);
        var current = q.Current!;
        Assert.False(q.MoveTrack(current, +1));
        Assert.False(q.MoveTrack(current, -1));
        Assert.Equal(1, q.CurrentIndex);
        Assert.Same(current, q.Current);
    }

    [Fact]
    public void MoveTrack_跨过当前曲目时索引自动补偿()
    {
        var q = Queue(4, 2);
        var current = q.Current!;

        // 把第 0 首下移到位置 3，会跨过当前曲目 → 当前索引应向左补偿
        Assert.True(q.MoveTrack(q.Queue[0]!, +1));
        Assert.Same(current, q.Current);
        Assert.Equal(2, q.Queue.ToList().IndexOf(current));
    }

    [Fact]
    public void PeekNext预览后续曲目且不改动状态()
    {
        var q = Queue(5, 0);
        var next = q.PeekNext(3);
        Assert.Equal(3, next.Count);
        Assert.Equal("1", next[0].Id);
        Assert.Equal(0, q.CurrentIndex);            // 预览不改变队列状态
    }

    [Fact]
    public void Append追加曲目不改变当前项()
    {
        var q = Queue(2, 0);
        q.Append([T("99")]);
        Assert.Equal(3, q.Queue.Count);
        Assert.Equal(0, q.CurrentIndex);
    }

    [Fact]
    public void InsertNext插到当前曲目之后()
    {
        var q = Queue(3, 0);
        q.InsertNext(T("99"));
        Assert.Equal("99", q.Queue[1].Id);
        Assert.Equal(0, q.CurrentIndex);
    }

    [Fact]
    public void 空队列各操作安全返回null()
    {
        var q = new PlaylistQueue();
        q.SetItems([], 0);
        Assert.Null(q.Current);
        Assert.Null(q.MoveNext());
        Assert.Null(q.MovePrevious());
        Assert.Empty(q.PeekNext(5));
    }

    [Fact]
    public void CurrentChanged在切歌时触发()
    {
        var q = Queue(3, 0);
        var fired = 0;
        q.CurrentChanged += (_, _) => fired++;
        q.MoveNext();
        q.JumpTo(2);
        Assert.Equal(2, fired);
    }
}
