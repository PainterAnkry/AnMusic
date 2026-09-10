using System.IO;
using AnMusic.Services.Net;
using AnMusic.Services.Providers;

namespace AnMusic.Tests;

/// <summary>封面缓存容量上限：超限时按最旧文件先删（防止缓存无限增长）。</summary>
public class CoverCacheLimitTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "anmusic-test-" + Guid.NewGuid().ToString("N"));

    public CoverCacheLimitTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>造文件并让写入时间递增（LRU 依据 LastWriteTimeUtc）。</summary>
    private void MakeFile(string name, int sizeBytes, DateTime writeTime)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[sizeBytes]);
        File.SetLastWriteTimeUtc(path, writeTime);
    }

    [Fact]
    public void 未超限时不删除任何文件()
    {
        MakeFile("a.jpg", 100, DateTime.UtcNow);
        MakeFile("b.jpg", 100, DateTime.UtcNow);

        CoverCacheService.EnforceLimit(_dir, maxBytes: 1000);

        Assert.Equal(2, Directory.GetFiles(_dir).Length);
    }

    [Fact]
    public void 超限时先删最旧的文件()
    {
        var now = DateTime.UtcNow;
        MakeFile("old.jpg", 400, now.AddHours(-3));
        MakeFile("mid.jpg", 400, now.AddHours(-2));
        MakeFile("new.jpg", 400, now.AddMinutes(-1)); // 合计 1200 > 上限，需降到 800

        CoverCacheService.EnforceLimit(_dir, maxBytes: 1000);

        var left = Directory.GetFiles(_dir).Select(Path.GetFileName).ToList();
        Assert.DoesNotContain("old.jpg", left);
        Assert.Contains("new.jpg", left);           // 最新的必须保住
        Assert.True(new DirectoryInfo(_dir).GetFiles().Sum(f => f.Length) <= 800);
    }

    [Fact]
    public void 目录不存在时不抛异常()
        => CoverCacheService.EnforceLimit(Path.Combine(_dir, "not-exist"), 100);

    [Fact]
    public void GetCacheSize统计字节数()
    {
        MakeFile("a.jpg", 512, DateTime.UtcNow);
        MakeFile("b.jpg", 256, DateTime.UtcNow);

        var size = new DirectoryInfo(_dir).GetFiles().Sum(f => f.Length);
        Assert.Equal(768, size);
    }
}

/// <summary>统一 HTTP 出口：代理地址的解析与容错。</summary>
public class HttpServiceTests
{
    [Fact]
    public void 配置代理后可读回()
    {
        HttpService.ConfigureProxy("http://127.0.0.1:7890");
        Assert.Equal("http://127.0.0.1:7890", HttpService.ProxyUrl);
    }

    [Fact]
    public void 空值等于关闭代理()
    {
        HttpService.ConfigureProxy("   ");
        Assert.Null(HttpService.ProxyUrl);
    }

    [Fact]
    public void 非法代理地址不会导致构造失败()
    {
        HttpService.ConfigureProxy("这不是一个地址");

        // 关键：即使是坏地址，取客户端也不能抛（否则所有在线功能一起挂）
        var handler = HttpService.CreateHandler();
        Assert.NotNull(handler);

        HttpService.ConfigureProxy("");
    }

    [Fact]
    public void 共享客户端带统一UA()
    {
        HttpService.ConfigureProxy("");
        var client = HttpService.Client;
        Assert.Contains(client.DefaultRequestHeaders.UserAgent, u => u.Product?.Name == "AnMusic");
    }

    [Fact]
    public void ApplyProxy对空代理不做改动()
    {
        HttpService.ConfigureProxy("");
        using var handler = new System.Net.Http.HttpClientHandler { UseProxy = false };
        HttpService.ApplyProxy(handler);
        Assert.False(handler.UseProxy);
    }
}
