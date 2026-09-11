using System.IO;
using AnMusic.Services.Announcements;
using AnMusic.ViewModels;

namespace AnMusic.Tests;

/// <summary>
/// 公告源解析：开发者写 announcements.json → 用户在「私信」里看到。
/// </summary>
/// <remarks>
/// 这条链路的特点是"写得随意、读得要硬"：公告是手写 JSON，格式错误不该让私信面板报错，
/// 版本定向写错也不该把公告发给不该看到的人。这里逐条锁住这些边界。
/// </remarks>
public class AnnouncementFeedTests
{
    private const string Current = "3.3.1";

    private static IReadOnlyList<Announcement> Parse(string json, string current = Current, string? expectError = null)
    {
        var list = AnnouncementFeed.Parse(json, current, out var error);
        if (expectError is null) Assert.Equal("", error);
        else Assert.Contains(expectError, error);
        return list;
    }

    [Fact]
    public void 解析基本公告()
    {
        var json = """
        {
          "messages": [
            { "id": "2026-09-12-plugins", "title": "插件接口变更说明",
              "body": "第一条\n第二条", "level": "important", "date": "2026-09-12",
              "url": "https://github.com/PainterAnkry/AnMusic/issues/1", "urlText": "详情" }
          ]
        }
        """;

        var a = Assert.Single(Parse(json));
        Assert.Equal("2026-09-12-plugins", a.Id);
        Assert.Equal("插件接口变更说明", a.Title);
        Assert.Contains("第二条", a.Body);
        Assert.Equal("⚠️", a.Icon);
        Assert.True(a.IsImportant);
        Assert.Equal("2026-09-12", a.Date);
        Assert.Equal("详情", a.UrlText);
    }

    [Theory]
    [InlineData("info", "📢")]
    [InlineData("important", "⚠️")]
    [InlineData("update", "🚀")]
    [InlineData("welcome", "👋")]
    [InlineData("", "📢")]        // 没写级别按普通公告
    [InlineData("unknown", "📢")] // 写了不认识的级别也不出错
    public void 级别决定图标(string level, string icon)
    {
        var json = $$"""{ "messages": [ { "title": "T", "level": "{{level}}" } ] }""";
        Assert.Equal(icon, Parse(json)[0].Icon);
    }

    [Fact]
    public void 置顶公告排在前面()
    {
        var json = """
        {
          "messages": [
            { "id": "a", "title": "普通一" },
            { "id": "b", "title": "置顶", "pinned": true },
            { "id": "c", "title": "普通二" }
          ]
        }
        """;

        Assert.Equal(["置顶", "普通一", "普通二"], Parse(json).Select(a => a.Title));
    }

    [Fact]
    public void 按最低版本定向()
    {
        var json = """
        { "messages": [
            { "id": "new-only", "title": "只发给新版", "minVersion": "3.3.0" },
            { "id": "future", "title": "只发给未来版本", "minVersion": "9.9.9" } ] }
        """;

        var list = Parse(json); // 当前 3.3.1
        Assert.Equal(["只发给新版"], list.Select(a => a.Title));
    }

    [Fact]
    public void 按最高版本定向_可用来催老版本升级()
    {
        var json = """
        { "messages": [
            { "id": "old-only", "title": "老版本请升级", "maxVersion": "3.2.0" },
            { "id": "all", "title": "所有人可见", "maxVersion": "3.3.1" } ] }
        """;

        var list = Parse(json); // 当前 3.3.1：第一条针对 <=3.2.0，用户已升级，不发
        Assert.Equal(["所有人可见"], list.Select(a => a.Title));

        // 换成一个 3.2.0 的老用户：两条都能看到
        var legacy = AnnouncementFeed.Parse(json, "3.2.0", out _);
        Assert.Equal(2, legacy.Count);
    }

    [Fact]
    public void 缺id时用标题兜底以便记住已读()
    {
        var a = Assert.Single(Parse("""{ "messages": [ { "title": "没有 id 的公告" } ] }"""));
        Assert.Equal("没有 id 的公告", a.Id);
    }

    [Fact]
    public void 标题与正文都空的条目被跳过()
    {
        var json = """{ "messages": [ { "id": "empty" }, { "id": "ok", "title": "有效" } ] }""";
        Assert.Equal(["有效"], Parse(json).Select(a => a.Title));
    }

    [Theory]
    [InlineData("""{ "messages": [] }""")]
    [InlineData("""{ }""")]                          // 没有 messages
    [InlineData("""{ "messages": "oops" }""")]       // 类型不对
    [InlineData("""[]""")]                           // 根本不是对象
    public void 没有公告内容时返回空列表(string json)
        => Assert.Empty(Parse(json));

    [Fact]
    public void JSON语法错误给出说明但不抛异常()
    {
        var list = Parse("""{ "messages": [ { "title": "缺个括号" """, expectError: "格式错误");
        Assert.Empty(list);
    }

    [Fact]
    public void 空内容按没有公告处理()
    {
        Assert.Empty(Parse(""));
        Assert.Empty(Parse("   "));
    }

    /// <summary>仓库里真实的 announcements.json 必须永远是合法且可解析的（它会被直接推给用户）。</summary>
    [Fact]
    public void 仓库里的公告文件合法()
    {
        var path = Path.Combine(RepoRoot(), "announcements.json");
        Assert.True(File.Exists(path), "仓库根目录缺少 announcements.json");

        var list = AnnouncementFeed.Parse(File.ReadAllText(path), Current, out var error);

        Assert.Equal("", error);
        Assert.NotNull(list); // 允许暂时没有公告，但文件本身必须能解析
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AnMusic.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("找不到仓库根");
    }
}

/// <summary>公告 → 私信消息（未读判定与按钮文案）。</summary>
public class InboxAnnouncementTests
{
    private static Announcement Sample(string id = "a1", string url = "", string urlText = "", bool important = false)
        => new(id, "公告标题", "公告正文", important ? "important" : "info", "2026-09-12", "", "", url, urlText, false);

    [Fact]
    public void 没看过的公告算未读()
    {
        var messages = InboxViewModel.ComposeAnnouncements([Sample()], []);

        var m = Assert.Single(messages);
        Assert.True(m.IsUnread);
        Assert.Equal("公告标题", m.Title);
        Assert.Equal("2026-09-12", m.TimeText);
    }

    [Fact]
    public void 看过的公告不再算未读()
    {
        var messages = InboxViewModel.ComposeAnnouncements([Sample("a1")], ["a1"]);
        Assert.False(messages[0].IsUnread);
    }

    [Fact]
    public void 有链接时显示详情按钮_文案可自定义()
    {
        var withLink = InboxViewModel.ComposeAnnouncements([Sample(url: "https://example.com/x")], [])[0];
        Assert.True(withLink.HasActions);
        Assert.Equal("查看详情", withLink.LinkText); // 没写 urlText 时的默认文案

        var custom = InboxViewModel.ComposeAnnouncements(
            [Sample(url: "https://example.com/x", urlText: "去论坛看看")], [])[0];
        Assert.Equal("去论坛看看", custom.LinkText);
        Assert.Equal("https://example.com/x", custom.ReleaseUrl);
    }

    [Fact]
    public void 没有链接时不给按钮()
    {
        var m = InboxViewModel.ComposeAnnouncements([Sample()], [])[0];
        Assert.False(m.HasActions);
    }

    [Fact]
    public void 重要公告带强调标记()
    {
        var messages = InboxViewModel.ComposeAnnouncements([Sample(important: true)], []);
        Assert.True(messages[0].IsImportant);
        Assert.Equal("⚠️", messages[0].Icon);
    }

    [Fact]
    public void 公告正文同样会被清洗成纯文本()
    {
        var a = new Announcement("x", "标题", "## 小标题\n\n- **重点**：请升级到 `3.3.2`", "info");
        var body = InboxViewModel.ComposeAnnouncements([a], [])[0].Body;

        Assert.DoesNotContain("#", body);
        Assert.DoesNotContain("**", body);
        Assert.DoesNotContain("`", body);
        Assert.Contains("重点", body);
    }
}
