using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace AnMusic.Services.Update;

/// <summary>从 Release JSON 解析出的更新信息。</summary>
public sealed record UpdateInfo(string LatestVersion, string? AssetUrl, string? AssetName);

/// <summary>
/// 更新检查的纯逻辑部分（与 UI 无关，便于单测）：
/// GitHub Release JSON 解析 + 版本号比较。
/// </summary>
public static class UpdateFeed
{
    /// <summary>解析 releases/latest 的 JSON；缺少版本号时返回 null。</summary>
    public static UpdateInfo? ParseRelease(JsonElement release)
    {
        var tag = release.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() ?? "" : "";
        var version = tag.TrimStart('v', 'V').Trim();
        if (string.IsNullOrEmpty(version)) return null;

        var (url, name) = PickInstallerAsset(release);
        return new UpdateInfo(version, url, name);
    }

    /// <summary>从 assets 里挑安装包：优先 Setup（安装版），其次 Portable（便携版）。</summary>
    public static (string? Url, string? Name) PickInstallerAsset(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return (null, null);

        string? setupUrl = null, setupName = null, portableUrl = null, portableName = null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object) continue;

            var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "" : "";
            var url = asset.TryGetProperty("browser_download_url", out var urlElement) ? urlElement.GetString() : null;
            if (string.IsNullOrEmpty(url)) continue;
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

            if (name.Contains("Setup", StringComparison.OrdinalIgnoreCase))
            {
                setupUrl ??= url;
                setupName ??= name;
            }
            else if (name.Contains("Portable", StringComparison.OrdinalIgnoreCase))
            {
                portableUrl ??= url;
                portableName ??= name;
            }
        }

        return setupUrl is not null ? (setupUrl, setupName) : (portableUrl, portableName);
    }

    /// <summary>candidate 是否比 current 新（按 x.y.z 逐段数字比较，缺失段按 0）。</summary>
    public static bool IsNewer(string? candidate, string? current)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;

        static int[] Parts(string v) => v.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => int.TryParse(new string(p.TakeWhile(char.IsDigit).ToArray()), out var n) ? n : 0)
            .ToArray();

        var a = Parts(candidate);
        var b = Parts(current ?? "");
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y) return x > y;
        }
        return false;
    }

    /// <summary>下载进度：已下载字节数 + 总字节数（0 表示服务端未提供长度，此时只能显示已下载量）。</summary>
    public readonly record struct DownloadProgress(long Read, long Total)
    {
        /// <summary>百分比（总长度未知时返回 -1）。</summary>
        public double Percent => Total > 0 ? Read * 100.0 / Total : -1;
    }

    /// <summary>
    /// 流式下载安装包到指定路径，边下边回报进度。
    /// 与 UI 无关，便于单独验证；调用方负责提示与启动安装程序。
    /// </summary>
    public static async Task DownloadAsync(string url, string targetPath,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        using var http = Net.HttpService.CreateClient(timeout: TimeSpan.FromMinutes(30));
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength ?? 0;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(targetPath);

        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
            // 总长度未知（chunked 响应）时也要上报，否则界面看不到任何进展
            progress?.Report(new DownloadProgress(read, total));
        }
    }
}
