using System.Diagnostics;
using System.IO;
using System.Net.Http;
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
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private readonly CoverCacheService _covers;
    private readonly List<JsPluginProvider> _plugins = [];

    /// <summary>插件目录：用户把音乐源 .js 插件文件放进此目录即可接入。</summary>
    public static string PluginDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AnMusic", "plugins");

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

        // 先按清单下载远程插件，再统一扫描
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

    /// <summary>过滤文件名非法字符。</summary>
    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string((name ?? "plugin").Where(c => !invalid.Contains(c)).ToArray()).Trim();
        return string.IsNullOrEmpty(safe) ? "plugin" : safe;
    }
}
