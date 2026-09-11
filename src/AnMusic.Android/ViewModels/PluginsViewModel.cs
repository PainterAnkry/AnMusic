using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using AnMusic.Services;
using AnMusic.Services.Providers;
using AnMusic.Services.Providers.JsPlugin;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnMusic.Android.ViewModels;

/// <summary>预设源（XAML 直接绑到 Name/Url，所以是属性而非元组）。</summary>
public sealed class PluginPreset
{
    public required string Name { get; init; }
    public required string Url { get; init; }
}

/// <summary>插件列表中的单个条目（远程或本地）。</summary>
public sealed partial class PluginItem : ObservableObject
{
    public required string FileName { get; init; }
    public required string DisplayName { get; init; }
    public required string Version { get; init; }
    public required string Source { get; init; }   // remote / local / bundled
    public required bool IsRemote { get; init; }

    public bool IsRemoteManifest { get; init; }

    [ObservableProperty] private bool _isBusy;
}

/// <summary>
/// 音源插件管理：粘贴 .js URL 安装、查看已加载列表、查看加载错误日志、刷新。
/// </summary>
/// <remarks>
/// 设计取舍：app 不内置任何在线曲库，必须由用户装 JS 插件。
/// 之前界面只在 DiscoverPage 提了一句"设置 → 音源插件"，但设置页里根本没这个入口，
/// 用户根本不知道插件要装在哪、装什么。这里提供可视化操作：
/// 1. 输入 .js URL → 一键安装并立即刷新注册
/// 2. 列出已加载插件（远程/本地）+ 删除
/// 3. 显示 load-errors.log 原文
/// 4. 一键重新加载
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

        foreach (var p in _loader.Plugins)
        {
            Plugins.Add(new PluginItem
            {
                FileName = p.FileName,
                DisplayName = p.DisplayName,
                Version = p.Version,
                Source = "local",
                IsRemote = false,
            });
        }

        // 没装任何插件时给个温和提示
        if (Plugins.Count == 0)
            StatusText = "还没装任何插件。粘贴 .js 链接到下方即可安装；常用入口：https://github.com/lyswhut/lx-music-source";
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

    #region 安装 / 删除

    [RelayCommand]
    private async Task InstallUrlAsync()
    {
        var url = InstallUrl?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(url))
        {
            StatusText = "请粘贴 .js 文件链接";
            return;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            StatusText = "链接格式不对，必须是 http(s) URL";
            return;
        }

        if (IsBusy) return;
        IsBusy = true;
        StatusText = "正在下载…";

        try
        {
            Directory.CreateDirectory(JsPluginLoader.PluginDir);
            // 文件名从 URL 末段推断，必要时去 query
            var pathOnly = uri.AbsolutePath.TrimEnd('/');
            var name = pathOnly.Split('/').LastOrDefault(s => !string.IsNullOrWhiteSpace(s));
            if (string.IsNullOrEmpty(name) || !name.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                name = $"plugin_{DateTime.Now:yyyyMMdd_HHmmss}.js";

            var dest = Path.Combine(JsPluginLoader.PluginDir, name);

            using var resp = await AnMusic.Services.Net.HttpService.Client.GetAsync(uri);
            resp.EnsureSuccessStatusCode();
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            if (bytes.Length == 0)
            {
                StatusText = "下载内容为空";
                return;
            }
            await File.WriteAllBytesAsync(dest, bytes);

            // 立即重新加载并刷新注册
            await _loader.LoadAllAsync();
            _registry.UnregisterJsPlugins();
            foreach (var plugin in _loader.Plugins)
                _registry.Register(plugin);

            RefreshList();
            ReloadErrorLog();
            StatusText = $"安装成功：{name}（共 {_loader.Plugins.Count} 个插件）";
            InstallUrl = string.Empty;
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

    [RelayCommand]
    private async Task DeletePluginAsync(PluginItem? item)
    {
        if (item is null) return;
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

    /// <summary>提供建议的常用 .js 源链接（公开维护的 LX 音源仓库的 raw 文件）。
/// 不打包进 app —— 第三方源随时可能变，由用户自己维护。</summary>
    public static IReadOnlyList<PluginPreset> Presets { get; } =
    [
        new() { Name = "网易云", Url = "https://raw.githubusercontent.com/lyswhut/lx-music-source/master/js/netease.js" },
        new() { Name = "QQ 音乐", Url = "https://raw.githubusercontent.com/lyswhut/lx-music-source/master/js/tencent.js" },
        new() { Name = "酷我",   Url = "https://raw.githubusercontent.com/lyswhut/lx-music-source/master/js/kuwo.js" },
        new() { Name = "咪咕",   Url = "https://raw.githubusercontent.com/lyswhut/lx-music-source/master/js/migu.js" },
        new() { Name = "B 站",   Url = "https://raw.githubusercontent.com/lyswhut/lx-music-source/master/js/bilibili.js" },
    ];

    [RelayCommand]
    private void UsePreset(PluginPreset preset)
    {
        if (preset is not null) InstallUrl = preset.Url;
    }

    #endregion
}