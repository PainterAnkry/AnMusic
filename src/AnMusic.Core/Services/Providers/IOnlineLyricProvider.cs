namespace AnMusic.Services.Providers;

/// <summary>
/// 在线歌词提供者扩展点（仅接口占位，不内置实现，规避合规风险）。
/// 第三方实现可通过 DI 注册（例如 services.AddSingleton&lt;IOnlineLyricProvider, XxxProvider&gt;()），
/// 并在设置页开启「在线歌词」后生效。
/// 实现方需自行确保数据来源合法合规：内容版权、用户隐私、目标站点爬虫限制等。
/// </summary>
public interface IOnlineLyricProvider
{
    string Id { get; }
    string DisplayName { get; }

    /// <summary>按曲目信息获取歌词，返回 LRC 格式文本（未找到返回 null）。</summary>
    Task<string?> GetLrcAsync(string title, string artist, CancellationToken ct = default);
}
