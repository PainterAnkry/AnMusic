using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AnMusic.Services;
using AnMusic.Services.Net;
using AnMusic.Services.Providers;
using AnMusic.Services.Providers.JsPlugin;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnMusic.Android.ViewModels;

/// <summary>预设源（XAML 直接绑到 Name/Url，所以是属性而非元组）。</summary>
public sealed partial class PluginPreset : ObservableObject
{
    public required string Name { get; init; }
    public string? Url { get; init; }

    /// <summary>主地址失败后依次尝试的镜像地址。</summary>
    public IReadOnlyList<string> Mirrors { get; init; } = [];

    /// <summary>保存到插件目录时使用的文件名（dist 类地址末段都是 index.js，必须指定）。</summary>
    public required string FileName { get; init; }

    public string? Description { get; init; }

    /// <summary>emoji 图标（卡片左侧圆形徽标）。</summary>
    public string Icon { get; init; } = "🎵";

    /// <summary>随 App 内置的修正版音源：无需下载，支持一键恢复。</summary>
    public bool IsBuiltIn { get; init; }

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isInstalled;
}

/// <summary>插件列表中的单个条目（远程或本地）。</summary>
public sealed partial class PluginItem : ObservableObject
{
    public required string FileName { get; init; }
    public required string DisplayName { get; init; }
    public required string Version { get; init; }
    public required string Source { get; init; }   // remote / local / bundled
    public required bool IsRemote { get; init; }

    /// <summary>随 App 内置：不允许删除，可在上方卡片一键恢复。</summary>
    public bool IsBuiltIn { get; init; }

    public bool IsRemoteManifest { get; init; }

    [ObservableProperty] private bool _isBusy;
}

/// <summary>
/// 音源插件管理：
/// 1. 常用源一键安装（gitee 直链为主、GitHub 源为镜像，国内网络也能下动）；
/// 2. 粘贴任意 .js 链接安装；粘贴 plugins.json 订阅链接批量安装；
/// 3. 查看已加载插件 / 删除 / 查看加载错误。
/// </summary>
/// <remarks>
/// 早期版本的预设指向 lyswhut/lx-music-source（洛雪协议，与本应用使用的 MusicFree
/// 协议不兼容）且路径 404，点了必然失败。现在预设全部换成官方 MusicFreePlugins：
/// v0.0 分支为「函数包裹」旧格式（宿主 JsPluginProvider 已做适配），
/// v0.1/dist 为标准 CommonJS，两种都能直接加载并用于搜索。
/// </remarks>
public sealed partial class PluginsViewModel : ObservableObject
{
    private readonly JsPluginLoader _loader;
    private readonly ProviderRegistry _registry;

    public PluginsViewModel(JsPluginLoader loader, ProviderRegistry registry)
    {
        _loader = loader;
        _registry = registry;
    }

    #region 状态

    public ObservableCollection<PluginItem> Plugins { get; } = [];

    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string _errorLog = string.Empty;
    [ObservableProperty] private string _installUrl = string.Empty;
    [ObservableProperty] private bool _isBusy;

    public bool HasErrors => !string.IsNullOrWhiteSpace(ErrorLog);

    partial void OnErrorLogChanged(string value) => OnPropertyChanged(nameof(HasErrors));

    #endregion

    #region 加载 / 刷新

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusText = "正在加载插件…";
        try
        {
            await _loader.LoadAllAsync();

            // 启动时清单同步下载的插件也要注册进音源中心
            _registry.UnregisterJsPlugins();
            foreach (var plugin in _loader.Plugins)
                _registry.Register(plugin);

            RefreshList();
            ReloadErrorLog();
            StatusText = $"已加载 {_loader.Plugins.Count} 个插件";
        }
        catch (Exception ex)
        {
            StatusText = $"加载失败：{ex.Message}";
            AppPaths.LogError("加载音源插件", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>把插件列表与错误日志从磁盘重新读一遍（不强刷插件）。</summary>
    public void RefreshList()
    {
        Plugins.Clear();

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in _loader.Plugins)
        {
            files.Add(p.FileName);
            Plugins.Add(new PluginItem
            {
                FileName = p.FileName,
                DisplayName = p.DisplayName,
                Version = p.Version,
                Source = "local",
                IsRemote = false,
                IsBuiltIn = BuiltInFileNames.Contains(p.FileName),
            });
        }

        foreach (var preset in Presets)
            preset.IsInstalled = files.Contains(preset.FileName);

        if (Plugins.Count == 0)
            StatusText = "还没装任何插件。点上方常用源即可一键安装，也可以粘贴 .js 直链";
    }

    private void ReloadErrorLog()
    {
        try
        {
            var logPath = Path.Combine(JsPluginLoader.PluginDir, "load-errors.log");
            ErrorLog = File.Exists(logPath) ? File.ReadAllText(logPath, Encoding.UTF8) : string.Empty;
        }
        catch (Exception ex)
        {
            ErrorLog = $"读取日志失败：{ex.Message}";
        }
    }

    #endregion

    #region 下载

    /// <summary>下载插件专用的浏览器 UA（raw 站点会拒绝匿名/播放器 UA）。</summary>
    private const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36";

    /// <summary>
    /// 按候选地址依次尝试下载，全部失败才返回 null。
    /// gitee 直链在国内最稳，GitHub raw 经常超时，jsDelivr/代理作为补充镜像。
    /// </summary>
    private static async Task<(byte[] Bytes, string FinalUrl)?> DownloadWithMirrorsAsync(
        IEnumerable<string> urls, string progressPrefix, Action<string>? report = null)
    {
        var candidates = urls
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Exception? lastError = null;
        for (var i = 0; i < candidates.Count; i++)
        {
            var url = candidates[i];
            report?.Invoke($"{progressPrefix}（线路 {i + 1}/{candidates.Count}）…");
            try
            {
                using var client = HttpService.CreateClient(timeout: TimeSpan.FromSeconds(30));
                client.DefaultRequestHeaders.Remove("User-Agent");
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", BrowserUserAgent);

                using var resp = await client.GetAsync(url);
                if (!resp.IsSuccessStatusCode)
                {
                    lastError = new HttpRequestException($"HTTP {(int)resp.StatusCode}");
                    continue;
                }

                var body = await resp.Content.ReadAsByteArrayAsync();
                if (body.Length == 0)
                {
                    lastError = new HttpRequestException("内容为空");
                    continue;
                }

                var preview = Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, 256));
                if (preview.TrimStart().StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
                    preview.TrimStart().StartsWith("<html", StringComparison.OrdinalIgnoreCase))
                {
                    lastError = new HttpRequestException("地址返回的是网页而不是 .js 文件");
                    continue;
                }

                return (body, url);
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        if (lastError is not null)
            AppPaths.LogError("下载音源插件", lastError, string.Join(" | ", candidates));
        return null;
    }

    /// <summary>为任意 GitHub raw 链接补充国内可达的镜像候选。</summary>
    private static IEnumerable<string> ExpandMirrors(string url)
    {
        yield return url;

        // raw.githubusercontent.com/owner/repo/(refs/heads/)branch/path
        if (url.Contains("raw.githubusercontent.com/", StringComparison.OrdinalIgnoreCase))
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                url,
                @"raw\.githubusercontent\.com/([^/]+)/([^/]+)/(?:refs/heads/)?([^/]+)/(.+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var owner = m.Groups[1].Value;
                var repo = m.Groups[2].Value;
                var branch = m.Groups[3].Value;
                var path = m.Groups[4].Value;

                yield return $"https://cdn.jsdelivr.net/gh/{owner}/{repo}@{branch}/{path}";
                yield return $"https://gitee.com/{owner}/{repo}/raw/{branch}/{path}";
                yield return $"https://ghfast.top/https://raw.githubusercontent.com/{owner}/{repo}/{branch}/{path}";
            }
        }
    }

    /// <summary>把下载好的字节写入插件目录（先临时文件再替换，避免半拉文件）。</summary>
    private static async Task SavePluginAsync(byte[] body, string fileName)
    {
        Directory.CreateDirectory(JsPluginLoader.PluginDir);
        var dest = Path.Combine(JsPluginLoader.PluginDir, fileName);
        var tmp = dest + $".{Guid.NewGuid():N}"[..8] + ".tmp";
        await File.WriteAllBytesAsync(tmp, body);
        File.Move(tmp, dest, true);
    }

    /// <summary>重新扫描插件目录并注册到音源中心。</summary>
    private async Task ReloadAndRegisterAsync()
    {
        var errors = await _loader.LoadAllAsync();
        _registry.UnregisterJsPlugins();
        foreach (var plugin in _loader.Plugins)
            _registry.Register(plugin);

        RefreshList();
        ReloadErrorLog();
        if (errors.Count > 0)
            StatusText = $"文件已保存，但加载时有 {errors.Count} 个错误；下方「加载错误」会显示详情";
    }

    #endregion

    #region 安装 / 删除

    [RelayCommand]
    private async Task InstallUrlAsync()
    {
        var url = InstallUrl?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(url))
        {
            StatusText = "请粘贴 .js 文件链接或 plugins.json 订阅链接";
            return;
        }
        if (!Uri.TryCreate(url.Trim('`'), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            StatusText = "链接格式不对，必须是 http(s) URL";
            return;
        }

        if (IsBusy) return;
        IsBusy = true;

        try
        {
            // 订阅链接：下载 plugins.json 后批量安装其中声明的全部插件
            var path = uri.AbsolutePath;
            if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                await InstallSubscriptionAsync(uri.ToString());
            }
            else
            {
                var name = path.TrimEnd('/').Split('/').LastOrDefault(s => !string.IsNullOrWhiteSpace(s))
                           ?? $"plugin_{DateTime.Now:yyyyMMdd_HHmmss}.js";
                if (!name.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                    name = $"plugin_{DateTime.Now:yyyyMMdd_HHmmss}.js";

                var ok = await DownloadAndSaveAsync(url, name, "正在下载插件");
                if (ok)
                {
                    await ReloadAndRegisterAsync();
                    if (string.IsNullOrEmpty(ErrorLog))
                    {
                        StatusText = $"安装成功：{name}（共 {_loader.Plugins.Count} 个插件）";
                        InstallUrl = string.Empty;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            StatusText = $"安装失败：{ex.Message}";
            AppPaths.LogError("安装音源插件", ex, url);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>常用源一键安装（内置源执行恢复，远程源执行多线路下载）。</summary>
    [RelayCommand]
    private async Task InstallPresetAsync(PluginPreset? preset)
    {
        if (preset is null || preset.IsBusy || IsBusy) return;

        preset.IsBusy = true;
        IsBusy = true;
        try
        {
            if (preset.IsBuiltIn)
            {
                // 删除损坏文件后重新扫描，加载器会从 App 内嵌资源重新释放
                var path = Path.Combine(JsPluginLoader.PluginDir, preset.FileName);
                if (File.Exists(path)) File.Delete(path);
                await ReloadAndRegisterAsync();
                StatusText = $"「{preset.Name}」已恢复为内置版本，可以去搜索页选这个音源搜歌";
                return;
            }

            if (string.IsNullOrEmpty(preset.Url)) return;
            var urls = new[] { preset.Url }.Concat(preset.Mirrors);
            var ok = await DownloadAndSaveAsync(urls, preset.FileName, $"正在安装「{preset.Name}」");
            if (ok)
            {
                await ReloadAndRegisterAsync();
                if (string.IsNullOrEmpty(ErrorLog))
                    StatusText = $"「{preset.Name}」安装成功，可以去搜索页选这个音源搜歌了";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"「{preset.Name}」操作失败：{ex.Message}";
            AppPaths.LogError("安装预设音源插件", ex, preset.Url ?? preset.Name);
        }
        finally
        {
            preset.IsBusy = false;
            IsBusy = false;
        }
    }

    /// <summary>下载单个地址（自动补镜像）并保存，失败时写好状态文案，返回是否成功。</summary>
    private async Task<bool> DownloadAndSaveAsync(string url, string fileName, string prefix)
        => await DownloadAndSaveAsync(ExpandMirrors(url), fileName, prefix);

    private async Task<bool> DownloadAndSaveAsync(IEnumerable<string> urls, string fileName, string prefix)
    {
        var result = await DownloadWithMirrorsAsync(urls, prefix, msg => StatusText = msg);
        if (result is not { } data)
        {
            StatusText = "所有下载线路都失败了：可能是网络受限，可在设置中配置代理后重试，或用浏览器下载 .js 后手动放入插件目录";
            return false;
        }

        await SavePluginAsync(data.Bytes, fileName);
        return true;
    }

    /// <summary>批量安装 plugins.json 订阅（MusicFree 标准订阅格式）。</summary>
    private async Task InstallSubscriptionAsync(string subscriptionUrl)
    {
        StatusText = "正在读取订阅清单…";
        var result = await DownloadWithMirrorsAsync(ExpandMirrors(subscriptionUrl), "正在读取订阅清单");
        if (result is not { } data)
        {
            StatusText = "订阅清单下载失败，请检查链接或网络";
            return;
        }

        List<SubscriptionEntry> entries;
        try
        {
            using var doc = JsonDocument.Parse(data.Bytes);
            if (!doc.RootElement.TryGetProperty("plugins", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
            {
                StatusText = "订阅清单格式不对：缺少 plugins 数组";
                return;
            }
            entries = arr.EnumerateArray()
                .Select(e => new SubscriptionEntry(
                    e.TryGetProperty("name", out var n) ? n.GetString() ?? "plugin" : "plugin",
                    e.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "",
                    e.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null))
                .Where(e => !string.IsNullOrWhiteSpace(e.Url))
                .ToList();
        }
        catch (JsonException)
        {
            StatusText = "订阅清单不是有效的 JSON";
            return;
        }

        if (entries.Count == 0)
        {
            StatusText = "订阅清单里没有可安装的插件";
            return;
        }

        var okCount = 0;
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            StatusText = $"订阅安装中 {i + 1}/{entries.Count}：{entry.Name}";

            var fileName = SanitizeFileName(entry.Name) + ".js";
            var downloaded = await DownloadWithMirrorsAsync(
                ExpandMirrors(entry.Url!), $"正在下载「{entry.Name}」");
            if (downloaded is { } pluginData)
            {
                try
                {
                    await SavePluginAsync(pluginData.Bytes, fileName);
                    okCount++;
                }
                catch (Exception ex)
                {
                    AppPaths.LogError("保存订阅插件", ex, entry.Name);
                }
            }
        }

        await ReloadAndRegisterAsync();
        StatusText = $"订阅安装完成：成功 {okCount}/{entries.Count} 个";
        if (okCount > 0 && string.IsNullOrEmpty(ErrorLog)) InstallUrl = string.Empty;
    }

    private sealed record SubscriptionEntry(string Name, string? Url, string? Version);

    [RelayCommand]
    private async Task DeletePluginAsync(PluginItem? item)
    {
        if (item is null) return;
        if (item.IsBuiltIn)
        {
            StatusText = "「" + item.DisplayName + "」是内置音源，不能删除；如已损坏可用上方「恢复」按钮还原";
            return;
        }
        try
        {
            var path = Path.Combine(JsPluginLoader.PluginDir, item.FileName);
            if (File.Exists(path)) File.Delete(path);
            StatusText = $"已删除 {item.FileName}";

            await _loader.LoadAllAsync();
            _registry.UnregisterJsPlugins();
            foreach (var plugin in _loader.Plugins)
                _registry.Register(plugin);

            RefreshList();
            ReloadErrorLog();
        }
        catch (Exception ex)
        {
            StatusText = $"删除失败：{ex.Message}";
            AppPaths.LogError("删除插件", ex, item.FileName);
        }
    }

    [RelayCommand]
    private void ClearErrorLog()
    {
        try
        {
            var logPath = Path.Combine(JsPluginLoader.PluginDir, "load-errors.log");
            if (File.Exists(logPath)) File.Delete(logPath);
            ErrorLog = string.Empty;
        }
        catch (Exception ex)
        {
            StatusText = $"清空日志失败：{ex.Message}";
        }
    }

    /// <summary>过滤文件名非法字符。</summary>
    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string((name ?? "plugin").Where(c => !invalid.Contains(c)).ToArray()).Trim();
        return string.IsNullOrEmpty(safe) ? "plugin" : safe;
    }

    #endregion

    #region 预设源

    /// <summary>内置修正版插件文件名（与 Core 释放的文件一一对应）。</summary>
    private static readonly HashSet<string> BuiltInFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "anmusic-netease.js", "anmusic-qq.js", "anmusic-kugou.js", "anmusic-kuwo.js",
    };

    /// <summary>
    /// 预设源：
    /// 前 4 个是随 App 内置、端点已修正的音源（免签名接口，开箱即用、可一键恢复）；
    /// B站音轨为经实测可用的官方 MusicFree v0.1 社区插件（gitee 直链 + GitHub 镜像）。
    /// 旧版网易云/QQ/酷狗/酷我社区插件因平台接口加密改版已失效，不再提供以免误导。
    /// </summary>
    public static IReadOnlyList<PluginPreset> Presets { get; } =
    [
        new()
        {
            Name = "网易云",
            FileName = "anmusic-netease.js",
            Description = "内置 · 搜索与播放（免费曲库）",
            Icon = "☁️",
            IsBuiltIn = true,
        },
        new()
        {
            Name = "QQ音乐",
            FileName = "anmusic-qq.js",
            Description = "内置 · 搜索与播放（免费曲库）",
            Icon = "🎧",
            IsBuiltIn = true,
        },
        new()
        {
            Name = "酷狗",
            FileName = "anmusic-kugou.js",
            Description = "内置 · 搜索与播放",
            Icon = "🐶",
            IsBuiltIn = true,
        },
        new()
        {
            Name = "酷我",
            FileName = "anmusic-kuwo.js",
            Description = "内置 · 搜索与播放",
            Icon = "🎵",
            IsBuiltIn = true,
        },
        new()
        {
            Name = "B站音轨",
            FileName = "bilibili.js",
            Description = "社区源 · B站视频音轨搜索与播放",
            Icon = "📺",
            Url = "https://gitee.com/maotoumao/MusicFreePlugins/raw/v0.1/dist/bilibili/index.js",
            Mirrors = ["https://raw.githubusercontent.com/maotoumao/MusicFreePlugins/refs/heads/v0.1/dist/bilibili/index.js"],
        },
    ];

    /// <summary>官方订阅：包含更多社区源（批量安装），可用性以各源实际情况为准。</summary>
    public const string OfficialSubscriptionUrl = "https://gitee.com/maotoumao/MusicFreePlugins/raw/master/plugins.json";

    [RelayCommand]
    private void UseOfficialSubscription()
        => InstallUrl = OfficialSubscriptionUrl;

    #endregion
}
