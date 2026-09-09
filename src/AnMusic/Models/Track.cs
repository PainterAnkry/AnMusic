using System.ComponentModel;

namespace AnMusic.Models;

/// <summary>
/// 表示一首音乐曲目（本地文件或在线源）。
/// </summary>
public sealed class Track : INotifyPropertyChanged
{
    /// <summary>唯一标识（本地文件用文件路径，在线源用 provider 定义）。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>曲目标题。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>艺术家。</summary>
    public string Artist { get; set; } = "未知艺术家";

    /// <summary>专辑名。</summary>
    public string Album { get; set; } = string.Empty;

    /// <summary>时长。</summary>
    public TimeSpan Duration { get; set; }

    /// <summary>本地文件路径（在线源为空）。</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>远程封面 URL（搜索结果携带，后台下载到本地缓存后写入 CoverKey）。</summary>
    public string CoverUrl { get; set; } = string.Empty;

    private string? _coverKey;

    /// <summary>封面缓存键（本地文件路径哈希或远程封面缓存路径）；变化时通知 UI 刷新。</summary>
    public string? CoverKey
    {
        get => _coverKey;
        set
        {
            if (_coverKey == value) return;
            _coverKey = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverKey)));
        }
    }

    /// <summary>来源 Provider Id。</summary>
    public string ProviderId { get; set; } = "local-file";

    /// <summary>在线来源页面链接（如 B 站视频页），本地曲目为空。</summary>
    public string SourceUrl { get; set; } = string.Empty;

    /// <summary>插件源的原始 musicItem JSON（播放/歌词时回传给插件，保留 songmid 等自定义字段）。</summary>
    public string? PluginData { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public override string ToString() => $"{Title} - {Artist}";
}
