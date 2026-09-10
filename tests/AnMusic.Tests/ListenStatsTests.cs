using AnMusic.Services.Formatting;

namespace AnMusic.Tests;

/// <summary>听歌排行的统计文案（时长格式化与"播放 N 次 · 累计 X"）。</summary>
public class ListenStatsTextTests
{
    [Theory]
    [InlineData(0, "0 秒")]
    [InlineData(1, "1 秒")]
    [InlineData(59, "59 秒")]
    [InlineData(60, "1 分 0 秒")]
    [InlineData(754, "12 分 34 秒")]
    [InlineData(3600, "1 小时 0 分")]
    [InlineData(3780, "1 小时 3 分")]
    [InlineData(7384, "2 小时 3 分")]
    public void 时长格式化(double seconds, string expected)
        => Assert.Equal(expected, ListenStatsText.Duration(seconds));

    [Fact]
    public void 有时长有时次()
        => Assert.Equal("播放 12 次 · 累计 1 小时 3 分", ListenStatsText.Compose(12, 3780));

    [Fact]
    public void 旧数据没有次数时只显示时长()
        => Assert.Equal("累计 12 分 30 秒", ListenStatsText.Compose(0, 750));

    [Fact]
    public void 负数或异常值不崩溃()
    {
        Assert.Equal("0 秒", ListenStatsText.Duration(-5));
        Assert.Equal("累计 0 秒", ListenStatsText.Compose(0, -1));
    }
}
