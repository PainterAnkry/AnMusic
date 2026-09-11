using System.Text.Json;
using AnMusic.Services.Update;
using AnMusic.ViewModels;

namespace AnMusic.Tests;

/// <summary>
/// 标题栏「私信」的内容生成：版本升级提示。
/// </summary>
/// <remarks>
/// 私信面板是用户看到"有新版本"的地方，错了会很显眼：误报会天天亮红点，
/// 漏报会让人停在旧版本。这里锁住判定规则（比当前版本新才算升级、看过就不再算未读）
/// 与文案（版本对比、更新说明摘要）。
/// </remarks>
public class InboxContentTests
{
    private const string Current = "3.3.1";

    private static UpdateInfo Info(
        string version,
        string? assetUrl = "https://example.com/AnMusic-Setup-3.3.2.exe",
        string notes = "",
        string htmlUrl = "https://github.com/PainterAnkry/AnMusic/releases/tag/v3.3.2",
        string publishedAt = "")
        => new(version, assetUrl, assetUrl is null ? null : "AnMusic-Setup-3.3.2.exe", notes, htmlUrl, publishedAt);

    [Fact]
    public void 有更新时给出升级消息且未读()
    {
        var content = InboxViewModel.Compose(Info("3.3.2", notes: "修了下载的 bug"), Current, seenVersion: "");

        var message = Assert.Single(content.Messages);
        Assert.True(content.HasUnread);
        Assert.True(message.IsUnread);
        Assert.Equal("1 条新消息", content.Summary);
        Assert.Contains("v3.3.2", message.Title);
        Assert.Contains($"当前 v{Current} → 最新 v3.3.2", message.Body);
        Assert.Contains("修了下载的 bug", message.Body);
    }

    [Fact]
    public void 看过之后不再算未读()
    {
        var content = InboxViewModel.Compose(Info("3.3.2"), Current, seenVersion: "3.3.2");

        Assert.False(content.HasUnread);
        Assert.False(content.Messages[0].IsUnread);
        Assert.Contains("已看过 v3.3.2", content.Summary);
        // 消息本身仍然留着，用户回头还能点"下载并安装"
        Assert.Single(content.Messages);
    }

    [Theory]
    [InlineData("3.3.1")] // 同版本
    [InlineData("3.3.0")] // 旧版本
    public void 不比当前版本新时不报升级(string latest)
    {
        var content = InboxViewModel.Compose(Info(latest), Current, seenVersion: "");

        var message = Assert.Single(content.Messages);
        Assert.False(content.HasUnread);
        Assert.Equal("已是最新版本", message.Title);
        Assert.Equal("暂无新消息", content.Summary);
        Assert.False(message.CanInstall);
    }

    [Fact]
    public void 没有安装包时不给下载入口()
    {
        var content = InboxViewModel.Compose(Info("3.3.2", assetUrl: null), Current, "");

        Assert.False(content.Messages[0].CanInstall);
        // 但"查看完整说明"仍然可用
        Assert.True(content.Messages[0].HasActions);
        Assert.NotEmpty(content.Messages[0].ReleaseUrl);
    }

    [Fact]
    public void 没有更新说明时给出兜底文案()
    {
        var content = InboxViewModel.Compose(Info("3.3.2", notes: "   "), Current, "");

        Assert.Contains("没有填写更新说明", content.Messages[0].Body);
    }

    [Fact]
    public void 更新说明去掉Markdown标记并压缩空行()
    {
        var notes = """
        ## AnMusic v3.3.2

        ### 本次修复（v3.3.2）

        - 修复在线歌曲听过之后下载不了
        - 修复 B 站封面缺失


        ### 下载说明
        > 覆盖安装即可
        """;
        var content = InboxViewModel.Compose(Info("3.3.2", notes: notes), Current, "");
        var body = content.Messages[0].Body;

        Assert.Contains("AnMusic v3.3.2", body);
        Assert.DoesNotContain("#", body);
        Assert.DoesNotContain("> ", body);
        Assert.DoesNotContain("- 修复", body);   // 列表符号被去掉，文字保留
        Assert.Contains("修复在线歌曲听过之后下载不了", body);
        Assert.DoesNotContain("\n\n\n", body);   // 连续空行已压缩
    }

    [Fact]
    public void 超长说明被截断并提示更多()
    {
        var notes = string.Join("\n", Enumerable.Range(0, 200).Select(i => $"- 第 {i} 条改动"));
        var body = InboxViewModel.Compose(Info("3.3.2", notes: notes), Current, "").Messages[0].Body;

        Assert.EndsWith("…", body);
        Assert.True(body.Length < 600, $"正文过长：{body.Length}");
    }

    [Fact]
    public void 发布时间格式化为日期()
    {
        var content = InboxViewModel.Compose(Info("3.3.2", publishedAt: "2026-09-12T04:37:13Z"), Current, "");

        Assert.Contains("发布", content.Messages[0].TimeText);
        Assert.Contains("9月", content.Messages[0].TimeText);   // Z 时间转本地后仍是 9 月
    }

    [Fact]
    public void 发布时间缺失或非法时不显示()
    {
        Assert.Equal("", InboxViewModel.Compose(Info("3.3.2", publishedAt: ""), Current, "").Messages[0].TimeText);
        Assert.Equal("", InboxViewModel.Compose(Info("3.3.2", publishedAt: "不是时间"), Current, "").Messages[0].TimeText);
    }
}

/// <summary>Release JSON 里"私信"需要的字段（正文/页面地址/发布时间）解析。</summary>
public class UpdateFeedNotesTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void 解析出说明正文与页面地址()
    {
        var json = """
        {
          "tag_name": "v3.3.2",
          "body": "### 修复\n- 下载 bug",
          "html_url": "https://github.com/PainterAnkry/AnMusic/releases/tag/v3.3.2",
          "published_at": "2026-09-12T04:37:13Z",
          "assets": [ { "name": "AnMusic-Setup-3.3.2.exe", "browser_download_url": "https://example.com/s.exe" } ]
        }
        """;

        var info = UpdateFeed.ParseRelease(Parse(json));

        Assert.NotNull(info);
        Assert.Equal("3.3.2", info!.LatestVersion);
        Assert.Equal("https://example.com/s.exe", info.AssetUrl);
        Assert.Contains("下载 bug", info.Notes);
        Assert.Contains("/releases/tag/v3.3.2", info.HtmlUrl);
        Assert.Equal("2026-09-12T04:37:13Z", info.PublishedAt);
    }

    [Fact]
    public void 缺少新字段时回落到空串()
    {
        var info = UpdateFeed.ParseRelease(Parse("""{ "tag_name": "v3.3.2", "assets": [] }"""));

        Assert.NotNull(info);
        Assert.Equal("", info!.Notes);
        Assert.Equal("", info.HtmlUrl);
        Assert.Equal("", info.PublishedAt);
    }

    [Fact]
    public void 字段类型不对也不抛异常()
    {
        var json = """{ "tag_name": "v3.3.2", "body": 123, "html_url": null, "published_at": [] }""";

        var info = UpdateFeed.ParseRelease(Parse(json));

        Assert.NotNull(info);
        Assert.Equal("", info!.Notes);
        Assert.Equal("", info.HtmlUrl);
        Assert.Equal("", info.PublishedAt);
    }
}
