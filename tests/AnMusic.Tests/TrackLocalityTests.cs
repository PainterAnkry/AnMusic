using System.IO;
using System.Text.Json;
using AnMusic.Models;

namespace AnMusic.Tests;

/// <summary>
/// 「播放缓冲」与「本地文件」的区分。
/// </summary>
/// <remarks>
/// 线上反馈的 bug：网易云搜到歌 → 在线听 → 再点「下载到本地」，却弹「该曲目已是本地文件，无需下载」。
/// 根因是在线音源把播放缓冲文件路径写进了 <see cref="Track.FilePath"/>，
/// 而下载入口用 <c>File.Exists(FilePath)</c> 判断"是否已下载"，于是"听过一次"被当成了"已下载"。
/// 现在缓冲单独放在 <see cref="Track.PlaybackCachePath"/>，判断统一走 <see cref="Track.IsAlreadyLocalFile"/>。
/// </remarks>
public class TrackLocalityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "anmusic-tests-" + Guid.NewGuid().ToString("N"));

    public TrackLocalityTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 清理失败不影响断言 */ }
    }

    /// <summary>造一个真实存在的临时文件，模拟"磁盘上有这个文件"。</summary>
    private string CreateTempFile(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "audio");
        return path;
    }

    private static Track NetEase() => new()
    {
        Id = "1901371647",
        Title = "浮生",
        Artist = "Chevy",
        ProviderId = "netease",
    };

    [Fact]
    public void 在线曲目刚搜出来时既没有本地文件也没有缓冲()
    {
        var track = NetEase();

        Assert.False(track.IsLocalTrack);
        Assert.Equal("", track.PlayablePath);
        Assert.False(track.IsAlreadyLocalFile);
    }

    [Fact]
    public void 在线曲目在线听过之后仍然允许下载()
    {
        var track = NetEase();
        track.PlaybackCachePath = CreateTempFile("1901371647.mp3");

        // 关键断言：缓冲存在 ≠ 已下载，下载入口必须继续放行
        Assert.False(track.IsAlreadyLocalFile);
        // 缓冲承担播放，但不能占用 FilePath 的"本地文件"语义
        Assert.Equal("", track.FilePath);
        Assert.Equal(track.PlaybackCachePath, track.PlayablePath);
    }

    [Fact]
    public void 旧版本残留的在线曲目Path也不算已下载()
    {
        // 修复前版本会把播放缓冲写进 FilePath 并随用户数据落盘，升级后仍是脏数据
        var track = NetEase();
        track.FilePath = CreateTempFile("legacy-cache.mp3");

        Assert.False(track.IsAlreadyLocalFile);

        // 新缓冲写入后，播放应改用新缓冲（旧路径可能已被"清理缓存"删掉）
        track.PlaybackCachePath = CreateTempFile("fresh.mp3");
        Assert.Equal(track.PlaybackCachePath, track.PlayablePath);
    }

    [Fact]
    public void 本地曲目文件存在时才算已下载()
    {
        var path = CreateTempFile("本地歌 - 张三.flac");
        var track = new Track { Id = path, FilePath = path, Title = "本地歌", Artist = "张三", ProviderId = "local-file" };

        Assert.True(track.IsLocalTrack);
        Assert.True(track.IsAlreadyLocalFile);
        Assert.Equal(path, track.PlayablePath);
    }

    [Fact]
    public void 本地曲目文件已删除时不算已下载()
    {
        var track = new Track
        {
            Id = "gone", FilePath = Path.Combine(_dir, "gone.mp3"),
            Title = "找不到的歌", Artist = "未知", ProviderId = "local-file"
        };

        Assert.False(track.IsAlreadyLocalFile);
        Assert.Equal(track.FilePath, track.PlayablePath); // 本地曲目只看 FilePath，由调用方决定报错还是重新缓冲
    }

    [Fact]
    public void 没有来源标注的曲目按本地处理()
    {
        var path = CreateTempFile("未标注.mp3");
        var track = new Track { Id = path, FilePath = path, ProviderId = "" };

        Assert.True(track.IsLocalTrack);
        Assert.True(track.IsAlreadyLocalFile);
    }

    [Fact]
    public void 播放缓冲不写进用户数据()
    {
        var track = NetEase();
        track.PlaybackCachePath = CreateTempFile("1901371647.mp3");

        // 缓存路径是本机运行时的东西：落盘会污染歌单，分享/一起听传给别人更是无效路径
        var json = JsonSerializer.Serialize(track);
        Assert.DoesNotContain(nameof(Track.PlaybackCachePath), json);

        // 反序列化（没有该字段）后也要是干净状态，不能残留上一次的缓冲
        var restored = JsonSerializer.Deserialize<Track>(json)!;
        Assert.Equal("", restored.PlaybackCachePath);
        Assert.False(restored.IsAlreadyLocalFile);
    }
}
