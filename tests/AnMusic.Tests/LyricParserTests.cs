using System.Linq;
using AnMusic.Services.Lyrics;
using AnMusic.Models;

namespace AnMusic.Tests;

/// <summary>LRC 解析：时间标签、多时间戳、offset、纯文本与容错。</summary>
public class LyricParserTests
{
    private readonly LrcParser _parser = new();

    [Fact]
    public void 解析基本时间标签并排序()
    {
        var doc = _parser.Parse("""
            [00:12.50]第二行
            [00:03.20]第一行
            """);

        Assert.Equal(2, doc.Lines.Count);
        Assert.Equal(TimeSpan.FromSeconds(3.2), doc.Lines[0].Time);
        Assert.Equal("第一行", doc.Lines[0].Text);
        Assert.Equal(TimeSpan.FromSeconds(12.5), doc.Lines[1].Time);
        Assert.True(doc.IsSynced);
    }

    [Fact]
    public void 一行多个时间戳会展开成多行()
    {
        var doc = _parser.Parse("[00:01.00][00:31.00]副歌");

        Assert.Equal(2, doc.Lines.Count);
        Assert.All(doc.Lines, l => Assert.Equal("副歌", l.Text));
        Assert.Equal(TimeSpan.FromSeconds(1), doc.Lines[0].Time);
        Assert.Equal(TimeSpan.FromSeconds(31), doc.Lines[1].Time);
    }

    [Theory]
    [InlineData("[01:02]文本")]          // 无毫秒
    [InlineData("[01:02.5]文本")]        // 一位小数
    [InlineData("[01:02.345]文本")]      // 三位小数
    [InlineData("[100:02.34]文本")]      // 超过 99 分钟
    public void 兼容多种时间格式(string line)
    {
        var doc = _parser.Parse(line);
        Assert.Single(doc.Lines);
        Assert.Equal("文本", doc.Lines[0].Text);
        Assert.True(doc.Lines[0].Time > TimeSpan.Zero);
    }

    [Fact]
    public void 解析offset偏移()
    {
        var doc = _parser.Parse("""
            [offset:+500]
            [00:10.00]歌词
            """);

        Assert.Equal(500, doc.OffsetMs);
        Assert.Single(doc.Lines);       // offset 行本身不算歌词
        Assert.Equal("歌词", doc.Lines[0].Text);
    }

    [Fact]
    public void 纯文本歌词标记为未同步()
    {
        var doc = _parser.Parse("""
            这是一首没有时间轴的歌词
            第二句
            """);

        Assert.Equal(2, doc.Lines.Count);
        Assert.False(doc.IsSynced);
    }

    [Fact]
    public void 空文本返回空文档()
    {
        Assert.Empty(_parser.Parse("").Lines);
        Assert.Empty(_parser.Parse("   ").Lines);
        Assert.False(_parser.Parse("").IsSynced);
    }

    [Fact]
    public void 忽略空行与前后空白()
    {
        var doc = _parser.Parse("\n\n  [00:01.00]  仅一行  \n\n");
        Assert.Single(doc.Lines);
        Assert.Equal("仅一行", doc.Lines[0].Text);
    }

    [Fact]
    public void LineIndexAt按时间定位且考虑offset()
    {
        var doc = _parser.Parse("""
            [00:00.00]第一句
            [00:10.00]第二句
            [00:20.00]第三句
            """);

        Assert.Equal(0, doc.LineIndexAt(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, doc.LineIndexAt(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, doc.LineIndexAt(TimeSpan.FromSeconds(19.9)));
        Assert.Equal(2, doc.LineIndexAt(TimeSpan.FromSeconds(25)));
        Assert.Equal(1, doc.LineIndexAt(TimeSpan.FromSeconds(12) + TimeSpan.FromSeconds(3))); // 13s → 第二句

        doc.OffsetMs = 5000; // 整体延后 5 秒
        Assert.Equal(0, doc.LineIndexAt(TimeSpan.FromSeconds(9)));
        Assert.Equal(1, doc.LineIndexAt(TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public void LineIndexAt早于第一句或空文档返回负一()
    {
        var doc = _parser.Parse("[00:05.00]稍后才开始");
        Assert.Equal(-1, doc.LineIndexAt(TimeSpan.Zero));
        Assert.Equal(-1, new LyricDocument().LineIndexAt(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void LineAt返回对应歌词行()
    {
        var doc = _parser.Parse("""
            [00:00.00]第一句
            [00:10.00]第二句
            """);

        Assert.Equal("第二句", doc.LineAt(TimeSpan.FromSeconds(12))?.Text);
        Assert.Null(doc.LineAt(TimeSpan.FromSeconds(-1)));
    }
}
