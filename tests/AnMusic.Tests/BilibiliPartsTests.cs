using AnMusic.Services.Providers;
using AnMusic.Services.Providers.Bilibili;

namespace AnMusic.Tests;

/// <summary>
/// B 站分P（全集）曲目 Id 的编解码。
/// </summary>
/// <remarks>
/// 多分P 视频在搜索结果里只有一条，展开全集后每个分P 都是一条独立曲目；
/// 而 <c>Track.Id</c> 同时是收藏 / 歌单 / 播放队列的唯一键，且会随用户数据落盘，
/// 所以分P 信息必须编进 Id 并能无损还原（否则收藏 P2 会顶掉 P1，重启后还会播错分P）。
/// </remarks>
public class BiliTrackIdTests
{
    private const string Bvid = "BV1GJ411x7h7";

    [Fact]
    public void 第1P沿用裸bvid_与搜索结果里的同一条曲目合并()
    {
        Assert.Equal(Bvid, BiliTrackId.Build(Bvid, 1));
        Assert.Equal(Bvid, BiliTrackId.Build(Bvid, 0)); // 异常页码按第 1 P 处理
    }

    [Fact]
    public void 第2P起带页码后缀()
    {
        Assert.Equal($"{Bvid}_p2", BiliTrackId.Build(Bvid, 2));
        Assert.Equal($"{Bvid}_p12", BiliTrackId.Build(Bvid, 12));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(37)]
    public void 编解码可无损往返(int page)
    {
        var (bvid, parsed) = BiliTrackId.Parse(BiliTrackId.Build(Bvid, page));

        Assert.Equal(Bvid, bvid);
        Assert.Equal(page, parsed);
    }

    [Fact]
    public void 普通视频Id按第1P处理()
    {
        var (bvid, page) = BiliTrackId.Parse(Bvid);

        Assert.Equal(Bvid, bvid);
        Assert.Equal(1, page);
    }

    [Theory]
    [InlineData("BV1x_p")]        // 后缀不完整
    [InlineData("BV1x_p0")]       // 页码非法
    [InlineData("BV1x_p-1")]
    [InlineData("BV1x_p2x")]      // 后缀不是数字
    [InlineData("BV1x_p1_p2x")]
    public void 不是分P形式的Id不被误解(string id)
    {
        var (bvid, page) = BiliTrackId.Parse(id);

        Assert.Equal(id, bvid); // 整串当 bvid：宁可播第 1 P，也不能把 bvid 截错
        Assert.Equal(1, page);
    }

    [Fact]
    public void 空Id安全返回()
    {
        Assert.Equal(string.Empty, BiliTrackId.Parse(null).Bvid);
        Assert.Equal(string.Empty, BiliTrackId.Parse("").Bvid);
        Assert.Equal(1, BiliTrackId.Parse("   ").Page);
    }

    [Fact]
    public void 视频页链接带分P参数()
    {
        Assert.Equal($"https://www.bilibili.com/video/{Bvid}", BiliTrackId.ToVideoUrl(Bvid, 1));
        Assert.Equal($"https://www.bilibili.com/video/{Bvid}?p=3", BiliTrackId.ToVideoUrl(Bvid, 3));
    }

    [Fact]
    public void 按分P取cid_取不到时退回视频级cid()
    {
        var info = new BiliVideoInfo(
            "111", "标题", "UP主", TimeSpan.FromMinutes(10), "https://i0.hdslb.com/a.jpg",
            [
                new BiliPart("111", 1, "P1 开场", TimeSpan.FromMinutes(3)),
                new BiliPart("222", 2, "P2 正片", TimeSpan.FromMinutes(7)),
            ]);

        Assert.Equal("222", info.CidOfPage(2));
        Assert.Equal("111", info.CidOfPage(1));
        Assert.Equal("111", info.CidOfPage(9));   // 页码越界：退回视频级 cid，不至于没声音
        Assert.Equal(2, info.PartCount);
    }

    [Fact]
    public void 没有分P列表时按单P处理()
    {
        var info = new BiliVideoInfo("111", "标题", "UP主", TimeSpan.FromMinutes(3), "", []);

        Assert.Equal(1, info.PartCount);
        Assert.Equal("111", info.CidOfPage(3));
    }
}

/// <summary>
/// 封面 URL 规整。
/// </summary>
/// <remarks>
/// 线上问题：B 站搜索接口返回的封面是协议相对地址，直接交给 HttpClient 会抛
/// "An invalid request URI was provided"，表现就是"B站搜到的歌全都没有封面"
/// （error.log 里刷满 <c>//i2.hdslb.com/bfs/archive/xxx.jpg</c>）。
/// </remarks>
public class CoverUrlTests
{
    // 直接取自用户机器上 error.log 的真实失败样本
    [Theory]
    [InlineData("//i2.hdslb.com/bfs/archive/3c8249148b2675eb8082e6f99758f9b4fea02963.jpg")]
    [InlineData("//i0.hdslb.com/bfs/archive/b3e0071da5b96b4a0a541f69aec8ab1a905920bd.jpg")]
    [InlineData("//i2.hdslb.com/bfs/archive/5c0773f87ad667997d8a266473b25aca3728b5dc.png")]
    public void 协议相对地址补全https(string raw)
        => Assert.StartsWith("https://", CoverUrl.Normalize(raw));

    [Fact]
    public void 协议相对地址内容不变()
        => Assert.Equal(
            "https://i2.hdslb.com/bfs/archive/abc.jpg",
            CoverUrl.Normalize("//i2.hdslb.com/bfs/archive/abc.jpg"));

    [Fact]
    public void 去掉CDN尺寸后缀_拿原始图()
        => Assert.Equal(
            "https://i0.hdslb.com/bfs/archive/abc.jpg",
            CoverUrl.Normalize("https://i0.hdslb.com/bfs/archive/abc.jpg@480w_270h_1c.webp"));

    [Fact]
    public void 不误伤query里的at()
        => Assert.Equal(
            "https://img.example.com/a.jpg?sign=@abc",
            CoverUrl.Normalize("https://img.example.com/a.jpg?sign=@abc"));

    [Fact]
    public void 正常绝对地址原样返回()
    {
        Assert.Equal("https://p1.music.126.net/a.jpg", CoverUrl.Normalize("https://p1.music.126.net/a.jpg"));
        Assert.Equal("http://img.example.com/a.png", CoverUrl.Normalize("http://img.example.com/a.png"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 空值返回空串(string? raw)
        => Assert.Equal(string.Empty, CoverUrl.Normalize(raw));

    [Fact]
    public void 首尾空白被去掉()
        => Assert.Equal("https://i0.hdslb.com/a.jpg", CoverUrl.Normalize("  //i0.hdslb.com/a.jpg  "));
}
