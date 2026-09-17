using AnMusic.Models;

namespace AnMusic.Services.Providers;

/// <summary>
/// 音乐源注册中心：聚合所有已注册的 IMusicProvider 与 IOnlineLyricProvider。
/// 支持 DI 静态注册 + 外部 .js 插件音源动态注册（Register/UnregisterJsPlugins）。
/// </summary>
public sealed class ProviderRegistry
{
    private readonly IReadOnlyList<IMusicProvider> _musicProviders;
    private readonly IReadOnlyList<IOnlineLyricProvider> _lyricProviders;
    private readonly object _gate = new();
    private readonly List<IMusicProvider> _dynamic = [];

    public ProviderRegistry(
        IEnumerable<IMusicProvider> musicProviders,
        IEnumerable<IOnlineLyricProvider> lyricProviders)
    {
        _musicProviders = musicProviders.ToList();
        _lyricProviders = lyricProviders.ToList();
    }

    /// <summary>全部音乐源（本地 + 在线 + 插件）。</summary>
    public IReadOnlyList<IMusicProvider> MusicProviders
    {
        get
        {
            lock (_gate)
                return _musicProviders.Concat(_dynamic).ToList();
        }
    }

    /// <summary>已注册的在线音乐源（内置 + 插件）。</summary>
    public IReadOnlyList<IOnlineMusicProvider> OnlineMusicProviders =>
        MusicProviders.OfType<IOnlineMusicProvider>().ToList();

    /// <summary>已注册的在线歌词源。</summary>
    public IReadOnlyList<IOnlineLyricProvider> LyricProviders => _lyricProviders;

    /// <summary>按 Id 查找音乐源（静态源优先，其次插件源）。</summary>
    public IMusicProvider? Find(string providerId)
    {
        var match = _musicProviders.FirstOrDefault(p => string.Equals(p.Id, providerId, StringComparison.Ordinal));
        if (match is not null) return match;
        lock (_gate)
            return _dynamic.FirstOrDefault(p => string.Equals(p.Id, providerId, StringComparison.Ordinal));
    }

    /// <summary>动态音源增删后触发（搜索页据此自动刷新音源选项卡，无需重进页面）。</summary>
    public event Action? ProvidersChanged;

    /// <summary>动态注册音乐源（外部 .js 插件加载后调用）。</summary>
    public void Register(IMusicProvider provider)
    {
        lock (_gate)
        {
            // 同 Id 的旧实例先移除（插件重装/升级后实例会变）
            _dynamic.RemoveAll(p => string.Equals(p.Id, provider.Id, StringComparison.Ordinal));
            _dynamic.Add(provider);
        }
        ProvidersChanged?.Invoke();
    }

    /// <summary>移除全部插件音源（重新加载插件时调用）。</summary>
    public void UnregisterJsPlugins()
    {
        lock (_gate) _dynamic.RemoveAll(p => p is JsPlugin.JsPluginProvider);
        ProvidersChanged?.Invoke();
    }
}
