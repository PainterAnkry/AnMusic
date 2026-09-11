using System.Text.Json;
using AnMusic.Services.Providers.Bilibili;

namespace AnMusic.Tests;

/// <summary>
/// /x/web-interface/view 响应解析（分P 列表是"查看全集"功能的数据源）。
/// </summary>
/// <remarks>
/// 样本取自真实接口（2026-09 实测 <c>api.bilibili.com/x/web-interface/view</c>，字段原样保留）：
/// 单P 视频的 <c>pages</c> 只有一个元素，多P 视频就是同一结构重复 N 次 ——
/// 因此下面用"真实单P 样本 + 同结构的 3P 样本"覆盖两条分支。
/// 分P 解析一旦退化（例如只取 video 级 cid），表现就是"点开全集，每一集都在放同一段"。
/// </remarks>
public class BiliVideoInfoParsingTests
{
    /// <summary>真实响应（BV1GJ411x7h7，已裁掉与本解析无关的字段）。</summary>
    private const string SinglePartJson = """
    {
      "bvid": "BV1GJ411x7h7",
      "videos": 1,
      "pic": "http://i1.hdslb.com/bfs/archive/5242750857121e05146d5d5b13a47a2a6dd36e98.jpg",
      "title": "【官方 MV】Never Gonna Give You Up - Rick Astley",
      "duration": 213,
      "owner": { "mid": 486906719, "name": "索尼音乐中国" },
      "cid": 137649199,
      "pages": [
        {
          "cid": 137649199,
          "page": 1,
          "from": "vupload",
          "part": "Never Gonna Give You Up - Rick Astley",
          "duration": 213
        }
      ]
    }
    """;

    /// <summary>同结构的多分P 样本（pages 重复 N 次）。</summary>
    private const string MultiPartJson = """
    {
      "bvid": "BV1GJ411x7h7",
      "videos": 3,
      "pic": "//i2.hdslb.com/bfs/archive/3c8249148b2675eb8082e6f99758f9b4fea02963.jpg",
      "title": "某张专辑全曲",
      "duration": 1200,
      "owner": { "mid": 1, "name": "某UP主" },
      "cid": 1001,
      "pages": [
        { "cid": 1001, "page": 1, "from": "vupload", "part": "01 第一首", "duration": 300 },
        { "cid": 1002, "page": 2, "from": "vupload", "part": "02 第二首", "duration": 500 },
        { "cid": 1003, "page": 3, "from": "vupload", "part": "03 第三首", "duration": 400 }
      ]
    }
    """;

    private static BiliVideoInfo Parse(string json)
        => BilibiliApiClient.ParseVideoInfo(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void 单P视频解析出标题作者时长与封面()
    {
        var info = Parse(SinglePartJson);

        Assert.Equal("【官方 MV】Never Gonna Give You Up - Rick Astley", info.Title);
        Assert.Equal("索尼音乐中国", info.Author);
        Assert.Equal(TimeSpan.FromSeconds(213), info.Duration);
        Assert.Equal("http://i1.hdslb.com/bfs/archive/5242750857121e05146d5d5b13a47a2a6dd36e98.jpg", info.Cover);
        Assert.Equal(1, info.PartCount);
        Assert.Equal("137649199", info.Cid);
    }

    [Fact]
    public void 多P视频解析出每一个分P()
    {
        var info = Parse(MultiPartJson);

        Assert.Equal(3, info.PartCount);
        Assert.Equal([1, 2, 3], info.Parts.Select(p => p.Page));
        Assert.Equal(["1001", "1002", "1003"], info.Parts.Select(p => p.Cid));
        Assert.Equal("02 第二首", info.Parts[1].Title);
        Assert.Equal(TimeSpan.FromSeconds(500), info.Parts[1].Duration);
    }

    [Fact]
    public void 每个分P取到自己的cid()
    {
        var info = Parse(MultiPartJson);

        Assert.Equal("1002", info.CidOfPage(2));
        Assert.Equal("1003", info.CidOfPage(3));
        Assert.Equal("1001", info.CidOfPage(1));
    }

    [Fact]
    public void 分P封面是协议相对地址时被补全()
        => Assert.Equal(
            "https://i2.hdslb.com/bfs/archive/3c8249148b2675eb8082e6f99758f9b4fea02963.jpg",
            Parse(MultiPartJson).Cover);

    [Fact]
    public void 缺字段时不抛异常()
    {
        var info = Parse("""{ "title": "只有标题" }""");

        Assert.Equal("只有标题", info.Title);
        Assert.Equal("", info.Cid);
        Assert.Equal("", info.Cover);
        Assert.Equal(1, info.PartCount);
        Assert.Equal(TimeSpan.Zero, info.Duration);
    }

    [Fact]
    public void 分P缺part标题时退化成P序号()
    {
        var json = """
        { "cid": 7, "title": "T", "duration": 60,
          "pages": [ { "cid": 7, "page": 1, "duration": 30 }, { "cid": 8, "page": 2 } ] }
        """;
        var info = Parse(json);

        Assert.Equal(2, info.PartCount);
        Assert.Equal("", info.Parts[0].Title);   // 标题补全由 Provider 负责（空则显示 P{序号}）
        Assert.Equal("8", info.Parts[1].Cid);
        Assert.Equal(TimeSpan.Zero, info.Parts[1].Duration);
    }
}
