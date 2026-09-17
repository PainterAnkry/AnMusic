using System.Collections.ObjectModel;
using System.Text.Json;
using AnMusic.Models;

namespace AnMusic.Tests;

/// <summary>
/// 歌单封面：自定义封面 + 默认封面（最近添加的那首歌）。
/// </summary>
/// <remarks>
/// 默认封面必须跟着"最近添加"走，而不是第一首 —— 歌单是追加写入的，
/// 用户刚加进去的那首歌才是他心里的"这个歌单的样子"。
/// 另外 Tracks 会被 JSON 反序列化整体替换（见模型里的注释），
/// 换实例后默认封面通知必须还能工作，否则界面会停在旧封面上。
/// </remarks>
public class PlaylistCoverTests
{
    private static Track Song(string title) => new() { Id = title, Title = title, ProviderId = "local-file" };

    [Fact]
    public void 空歌单没有默认封面()
        => Assert.Null(new Playlist().DefaultCoverTrack);

    [Fact]
    public void 默认封面取最近添加的那首歌()
    {
        var playlist = new Playlist();
        playlist.Tracks.Add(Song("第一首"));
        playlist.Tracks.Add(Song("第二首"));
        playlist.Tracks.Add(Song("最新一首"));

        Assert.Equal("最新一首", playlist.DefaultCoverTrack!.Title);
    }

    [Fact]
    public void 加歌后默认封面跟着变()
    {
        var playlist = new Playlist();
        playlist.Tracks.Add(Song("老歌"));

        var changes = 0;
        playlist.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Playlist.DefaultCoverTrack)) changes++;
        };

        playlist.Tracks.Add(Song("新歌"));

        Assert.Equal("新歌", playlist.DefaultCoverTrack!.Title);
        Assert.True(changes > 0, "加歌后应当通知界面刷新默认封面");
    }

    [Fact]
    public void 移走最后一首后默认封面回退到前一首()
    {
        var playlist = new Playlist();
        var first = Song("保留");
        var last = Song("会被删掉");
        playlist.Tracks.Add(first);
        playlist.Tracks.Add(last);

        playlist.Tracks.Remove(last);

        Assert.Same(first, playlist.DefaultCoverTrack);
    }

    [Fact]
    public void 整体替换曲目集合后仍能自动刷新()
    {
        // 模拟 System.Text.Json 反序列化：直接换掉集合实例
        var playlist = new Playlist { Tracks = new ObservableCollection<Track> { Song("旧的") } };

        var changed = false;
        playlist.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Playlist.DefaultCoverTrack)) changed = true;
        };

        playlist.Tracks.Add(Song("反序列化后加进来的"));

        Assert.True(changed, "换了集合实例后仍要订阅到变化");
        Assert.Equal("反序列化后加进来的", playlist.DefaultCoverTrack!.Title);
    }

    [Fact]
    public void 自定义封面可读写且变化时通知()
    {
        var playlist = new Playlist();
        var notified = false;
        playlist.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Playlist.CoverPath)) notified = true;
        };

        playlist.CoverPath = @"C:\covers\p1.png";

        Assert.Equal(@"C:\covers\p1.png", playlist.CoverPath);
        Assert.True(notified);
    }

    [Fact]
    public void 自定义封面置空即恢复默认()
    {
        var playlist = new Playlist();
        playlist.Tracks.Add(Song("最近添加"));
        playlist.CoverPath = @"C:\covers\p1.png";

        playlist.CoverPath = "";

        Assert.Equal("", playlist.CoverPath);
        Assert.Equal("最近添加", playlist.DefaultCoverTrack!.Title);
    }

    [Fact]
    public void 封面字段会随用户数据一起存盘()
    {
        var playlist = new Playlist { Name = "深夜循环", CoverPath = @"C:\covers\p1.png" };
        playlist.Tracks.Add(Song("浮生"));

        var json = JsonSerializer.Serialize(playlist);
        var restored = JsonSerializer.Deserialize<Playlist>(json)!;

        Assert.Equal(@"C:\covers\p1.png", restored.CoverPath);
        Assert.Equal("深夜循环", restored.Name);
        Assert.Single(restored.Tracks);
        Assert.Equal("浮生", restored.DefaultCoverTrack!.Title);
    }
}
