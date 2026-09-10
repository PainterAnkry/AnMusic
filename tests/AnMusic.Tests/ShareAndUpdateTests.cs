using System.Text.Json;
using AnMusic.Models;
using AnMusic.Services;
using AnMusic.Services.Update;

namespace AnMusic.Tests;

/// <summary>AnMusic 分享链接（anmusic://）的生成与解析。</summary>
public class ShareLinkTests
{
    private static Track Sample() => new()
    {
        Id = "1901371647",
        Title = "测试 歌曲 & 特殊+字符",
        Artist = "某位歌手",
        ProviderId = "netease",
        SourceUrl = "https://music.163.com/#/song?id=1901371647"
    };

    [Fact]
    public void 生成并解析链接_字段完整还原()
    {
        var track = Sample();
        var link = ShareLink.Build(track);

        Assert.StartsWith("anmusic://song?", link);

        var parsed = ShareLink.TryParse(link);
        Assert.NotNull(parsed);
        Assert.Equal(track.ProviderId, parsed!.ProviderId);
        Assert.Equal(track.Id, parsed.Id);
        Assert.Equal(track.Title, parsed.Title);       // 含 & + 空格也能还原
        Assert.Equal(track.Artist, parsed.Artist);
        Assert.Equal(track.SourceUrl, parsed.SourceUrl);
    }

    [Fact]
    public void 本地文件链接用路径作为id()
    {
        var track = new Track
        {
            Id = @"C:\Music\我的歌 - 张三.flac",
            FilePath = @"C:\Music\我的歌 - 张三.flac",
            Title = "我的歌",
            Artist = "张三",
            ProviderId = "local-file"
        };

        var parsed = ShareLink.TryParse(ShareLink.Build(track));
        Assert.NotNull(parsed);
        Assert.Equal("local-file", parsed!.ProviderId);
        Assert.Equal(track.Id, parsed.Id);
    }

    [Theory]
    [InlineData("anmusic://song?provider=netease&id=1")]
    [InlineData("ANMUSIC://song?provider=bilibili&id=BV1xx&title=T")]
    public void 识别本软件链接(string text)
        => Assert.True(ShareLink.IsShareLink(text));

    [Theory]
    [InlineData("https://music.163.com/#/song?id=1")]
    [InlineData("https://y.qq.com/n/ryqq/playlist/1")]
    [InlineData("周杰伦 晴天")]
    [InlineData("")]
    [InlineData(null)]
    public void 不误判普通文本(string? text)
        => Assert.False(ShareLink.IsShareLink(text));

    [Theory]
    [InlineData("anmusic://song")]                     // 没有参数
    [InlineData("anmusic://song?provider=&id=&title=")] // 参数全空
    [InlineData("https://example.com/song?provider=n&id=1")]
    public void 无效链接解析失败(string text)
        => Assert.Null(ShareLink.TryParse(text));

    [Fact]
    public void 缺歌手或标题时仍可解析()
    {
        var parsed = ShareLink.TryParse("anmusic://song?provider=qqmusic&id=0039MnYb0qxYhV&title=夜曲");
        Assert.NotNull(parsed);
        Assert.Equal("qqmusic", parsed!.ProviderId);
        Assert.Equal("夜曲", parsed.Title);
        Assert.Equal("", parsed.Artist);
    }
}

/// <summary>更新检查：Release JSON 解析与版本号比较。</summary>
public class UpdateFeedTests
{
    private const string ReleaseJson = """
    {
      "tag_name": "v3.1.0",
      "name": "AnMusic v3.1.0",
      "assets": [
        { "name": "AnMusic-Portable-3.1.0.exe", "browser_download_url": "https://example.com/portable.exe" },
        { "name": "AnMusic-Setup-3.1.0.exe", "browser_download_url": "https://example.com/setup.exe" },
        { "name": "checksums.txt", "browser_download_url": "https://example.com/sums.txt" }
      ]
    }
    """;

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void 解析出版本号并优先选择安装版()
    {
        var info = UpdateFeed.ParseRelease(Parse(ReleaseJson));

        Assert.NotNull(info);
        Assert.Equal("3.1.0", info!.LatestVersion);
        Assert.Equal("https://example.com/setup.exe", info.AssetUrl);
        Assert.Equal("AnMusic-Setup-3.1.0.exe", info.AssetName);
    }

    [Fact]
    public void 没有安装版时回退到便携版()
    {
        var json = """
        { "tag_name": "v3.1.0", "assets": [ { "name": "AnMusic-Portable-3.1.0.exe", "browser_download_url": "https://example.com/p.exe" } ] }
        """;
        var info = UpdateFeed.ParseRelease(Parse(json));

        Assert.Equal("https://example.com/p.exe", info!.AssetUrl);
    }

    [Fact]
    public void 没有资产时不返回下载地址()
    {
        var info = UpdateFeed.ParseRelease(Parse("""{ "tag_name": "v9.9.9" }"""));
        Assert.Equal("9.9.9", info!.LatestVersion);
        Assert.Null(info.AssetUrl);
    }

    [Fact]
    public void 缺少tag时解析失败()
        => Assert.Null(UpdateFeed.ParseRelease(Parse("""{ "name": "no tag" }""")));

    [Theory]
    [InlineData("3.0.1", "3.0.0", true)]
    [InlineData("3.1.0", "3.0.9", true)]
    [InlineData("4.0.0", "3.9.9", true)]
    [InlineData("3.0.0", "3.0.0", false)]
    [InlineData("2.9.9", "3.0.0", false)]
    [InlineData("3.0", "3.0.0", false)]
    [InlineData("", "3.0.0", false)]
    public void 版本号比较(string candidate, string current, bool expected)
        => Assert.Equal(expected, UpdateFeed.IsNewer(candidate, current));

    [Fact]
    public void 版本号中的非数字后缀不影响比较()
        => Assert.True(UpdateFeed.IsNewer("3.1.0-beta", "3.0.5"));
}
