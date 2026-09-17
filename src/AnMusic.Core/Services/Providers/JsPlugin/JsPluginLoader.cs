using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace AnMusic.Services.Providers.JsPlugin;

/// <summary>插件清单项（plugins.json 中的一条远程插件）。</summary>
public sealed class JsPluginManifestItem
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string Version { get; set; } = "";
}

/// <summary>插件清单文件模型。</summary>
public sealed class JsPluginManifest
{
    public List<JsPluginManifestItem> Plugins { get; set; } = [];
}

/// <summary>
/// 外部 .js 音源插件加载器：
/// 1. 读取 %AppData%\AnMusic\plugins\plugins.json 清单，自动下载远程 .js 插件（按版本缓存，版本变更时重新下载）；
/// 2. 扫描插件目录下全部 .js 文件，经 Jint 执行并包装为 JsPluginProvider。
/// </summary>
public sealed class JsPluginLoader
{
    /// <summary>统一 HTTP 出口（含 UA / 超时 / 代理设置）。</summary>
    private static HttpClient Http => Services.Net.HttpService.Client;

    /// <summary>
    /// 是否随加载释放内置修正版音源插件。各端宿主按需开启（安卓端开启）。
    /// 内置插件会在扫描前写入插件目录（带版本戳，随 App 更新自动覆盖）。
    /// </summary>
    public static bool EnableBuiltinPlugins { get; set; }

    /// <summary>内置插件版本号：修正端点后 +1，旧版本戳会触发重新释放。</summary>
    private const int BuiltinPluginVersion = 2;

    /// <summary>(资源逻辑名, 释放文件名)</summary>
    private static readonly (string Resource, string File)[] BuiltinPlugins =
    [
        ("AnMusic.Assets.Plugins.Builtin.anmusic-netease.js", "anmusic-netease.js"),
        ("AnMusic.Assets.Plugins.Builtin.anmusic-qq.js", "anmusic-qq.js"),
        ("AnMusic.Assets.Plugins.Builtin.anmusic-kugou.js", "anmusic-kugou.js"),
        ("AnMusic.Assets.Plugins.Builtin.anmusic-kuwo.js", "anmusic-kuwo.js"),
    ];

    private readonly CoverCacheService _covers;
    private readonly List<JsPluginProvider> _plugins = [];

    /// <summary>插件目录：用户把音乐源 .js 插件文件放进此目录即可接入。</summary>
    public static string PluginDir => Services.AppPaths.PluginsDir;

    /// <summary>清单文件路径（包含远程插件列表，放进去即自动下载安装）。</summary>
    public static string ManifestPath => Path.Combine(PluginDir, "plugins.json");

    public JsPluginLoader(CoverCacheService covers) => _covers = covers;

    /// <summary>已加载的插件列表。</summary>
    public IReadOnlyList<JsPluginProvider> Plugins => _plugins;

    /// <summary>同步清单：下载 plugins.json 中声明的远程插件到本地（已有同名版本文件则跳过），返回错误列表。</summary>
    public async Task<List<string>> SyncFromManifestAsync()
    {
        var errors = new List<string>();
        if (!File.Exists(ManifestPath))
            return errors;

        JsPluginManifest? manifest;
        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            await using var fs = File.OpenRead(ManifestPath);
            manifest = await JsonSerializer.DeserializeAsync<JsPluginManifest>(fs, options);
        }
        catch (Exception ex)
        {
            errors.Add($"plugins.json: 解析失败 {ex.Message}");
            Services.AppPaths.LogError("解析插件清单", ex);
            return errors;
        }

        if (manifest?.Plugins is not { Count: > 0 }) return errors;

        foreach (var item in manifest.Plugins)
        {
            if (string.IsNullOrWhiteSpace(item.Url))
                continue;

            // 去掉误粘贴的 Markdown 反引号
            var url = item.Url.Trim().Trim('`').Trim();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                errors.Add($"{item.Name}: 无效 URL");
                continue;
            }

            var fileName = $"{SanitizeFileName(item.Name)}_{SanitizeFileName(item.Version)}.js";
            var dest = Path.Combine(PluginDir, fileName);

            // 已存在同版本文件则跳过下载
            if (File.Exists(dest) && new FileInfo(dest).Length > 0)
                continue;

            try
            {
                Directory.CreateDirectory(PluginDir);
                using var req = new HttpRequestMessage(HttpMethod.Get, uri);
                req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AnMusic");
                using var resp = await Http.SendAsync(req);
                resp.EnsureSuccessStatusCode();

                var bytes = await resp.Content.ReadAsByteArrayAsync();
                if (bytes.Length == 0)
                {
                    errors.Add($"{item.Name}: 下载内容为空");
                    continue;
                }

                var tmp = dest + ".tmp";
                await File.WriteAllBytesAsync(tmp, bytes);
                File.Move(tmp, dest, true);
                Trace.WriteLine($"[JsPlugin] 清单下载完成: {fileName} ({bytes.Length} bytes)");
            }
            catch (Exception ex)
            {
                errors.Add($"{item.Name}: 下载失败 {ex.Message}");
                Services.AppPaths.LogError("下载音源插件", ex, item.Name);
            }
        }
        return errors;
    }

    /// <summary>扫描插件目录加载全部 .js 插件，返回错误列表（文件名: 原因）。</summary>
    public async Task<List<string>> LoadAllAsync()
    {
        _plugins.Clear();
        var errors = new List<string>();

        try
        {
            Directory.CreateDirectory(PluginDir);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[JsPlugin] 创建插件目录失败: {ex.Message}");
            return errors;
        }

        // 先释放内置修正版音源插件，再按清单下载远程插件，最后统一扫描
        await EnsureBuiltinPluginsAsync();
        var manifestErrors = await SyncFromManifestAsync();
        errors.AddRange(manifestErrors);

        foreach (var file in Directory.EnumerateFiles(PluginDir, "*.js").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                _plugins.Add(new JsPluginProvider(file, _covers));
                Trace.WriteLine($"[JsPlugin] 已加载: {Path.GetFileName(file)}");
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
                Trace.WriteLine($"[JsPlugin] 加载失败 {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        // 记录加载结果与每个插件解码后的真实标识（混淆插件的 platform 只能运行时求值），便于排查匹配问题
        try
        {
            var infoPath = Path.Combine(PluginDir, "plugins-info.log");
            var lines = _plugins.Select(p => $"{p.FileName} -> id={p.Id}, name={p.DisplayName}, version={p.Version}");
            File.WriteAllLines(infoPath, lines);
        }
        catch { /* 日志失败不影响加载 */ }
        try
        {
            var logPath = Path.Combine(PluginDir, "load-errors.log");
            File.WriteAllText(logPath, errors.Count > 0 ? string.Join(Environment.NewLine, errors) : "");
        }
        catch { /* 日志失败不影响加载 */ }

        return errors;
    }

    /// <summary>
    /// 释放内置修正版音源插件到插件目录（按版本戳幂等覆盖）。
    /// 已被用户删除的内置插件也会随版本更新重新写入——内置源是"开箱即能搜歌"的底线。
    /// </summary>
    private static async Task EnsureBuiltinPluginsAsync()
    {
        if (!EnableBuiltinPlugins) return;
        try
        {
            var stampPath = Path.Combine(PluginDir, ".builtin-ver");
            var stamp = File.Exists(stampPath) ? File.ReadAllText(stampPath).Trim() : "";
            var versionMatch = stamp == BuiltinPluginVersion.ToString();

            var asm = Assembly.GetExecutingAssembly();
            var wroteAny = false;
            foreach (var (resource, fileName) in BuiltinPlugins)
            {
                var dest = Path.Combine(PluginDir, fileName);

                // 版本戳匹配且文件仍在：跳过。用户误删的内置文件会立即补回（无需等版本更新）。
                if (versionMatch && File.Exists(dest) && new FileInfo(dest).Length > 0)
                    continue;

                using var stream = asm.GetManifestResourceStream(resource);
                if (stream is null)
                {
                    Trace.WriteLine($"[JsPlugin] 内置插件资源缺失: {resource}");
                    continue;
                }
                var tmp = dest + ".tmp";
                await using (var fs = File.Create(tmp))
                    await stream.CopyToAsync(fs);
                File.Move(tmp, dest, true);
                wroteAny = true;
                Trace.WriteLine($"[JsPlugin] 内置插件已释放: {fileName}");
            }
            if (wroteAny || !versionMatch)
                File.WriteAllText(stampPath, BuiltinPluginVersion.ToString());
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[JsPlugin] 内置插件释放失败: {ex.Message}");
            Services.AppPaths.LogError("释放内置插件", ex);
        }
    }

    /// <summary>过滤文件名非法字符。</summary>
    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string((name ?? "plugin").Where(c => !invalid.Contains(c)).ToArray()).Trim();
        return string.IsNullOrEmpty(safe) ? "plugin" : safe;
    }
}
