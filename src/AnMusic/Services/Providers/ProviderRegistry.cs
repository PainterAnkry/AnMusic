using AnMusic.Models;

namespace AnMusic.Services.Providers;

/// <summary>
/// 音乐源注册中心：聚合所有已注册的 IMusicProvider 与 IOnlineLyricProvider。
/// 在线音乐源目前为扩展点占位（无内置实现）；第三方实现注册进 DI 后会自动出现在此处。
/// </summary>
public sealed class ProviderRegistry
{
    private readonly IReadOnlyList<IMusicProvider> _musicProviders;
    private readonly IReadOnlyList<IOnlineLyricProvider> _lyricProviders;

    public ProviderRegistry(
        IEnumerable<IMusicProvider> musicProviders,
        IEnumerable<IOnlineLyricProvider> lyricProviders)
    {
        _musicProviders = musicProviders.ToList();
        _lyricProviders = lyricProviders.ToList();
    }

    /// <summary>全部音乐源（本地 + 在线）。</summary>
    public IReadOnlyList<IMusicProvider> MusicProviders => _musicProviders;

    /// <summary>已注册的在线音乐源（当前为空，留作扩展点）。</summary>
    public IReadOnlyList<IOnlineMusicProvider> OnlineMusicProviders =>
        _musicProviders.OfType<IOnlineMusicProvider>().ToList();

    /// <summary>已注册的在线歌词源（当前为空，留作扩展点）。</summary>
    public IReadOnlyList<IOnlineLyricProvider> LyricProviders => _lyricProviders;

    /// <summary>按 Id 查找音乐源。</summary>
    public IMusicProvider? Find(string providerId) =>
        _musicProviders.FirstOrDefault(p => string.Equals(p.Id, providerId, StringComparison.Ordinal));
}
