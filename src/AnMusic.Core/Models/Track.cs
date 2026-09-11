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

    /// <summary>
    /// 在线曲目播放时缓冲到本地的临时文件（只有运行时才有值）。
    /// </summary>
    /// <remarks>
    /// 必须与 <see cref="FilePath"/> 分开：缓冲文件只是"听过一次"的副产物，
    /// 既不是音乐库里的本地文件，也不该被当成"已下载"。
    /// 不持久化，也不随分享链接 / 一起听传给别的设备（别人的缓存路径没有意义）。
    /// </remarks>
    [System.Text.Json.Serialization.JsonIgnore]
    public string PlaybackCachePath { get; set; } = string.Empty;

    /// <summary>真正可用于播放的本地文件路径：本地曲目取 <see cref="FilePath"/>，在线曲目取播放缓冲。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string PlayablePath => IsLocalTrack
        ? FilePath
        : !string.IsNullOrEmpty(PlaybackCachePath) ? PlaybackCachePath : FilePath;

    /// <summary>是否是本地音乐库曲目（而非在线音源曲目）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsLocalTrack =>
        string.IsNullOrEmpty(ProviderId) || string.Equals(ProviderId, "local-file", StringComparison.OrdinalIgnoreCase);

    /// <summary>该曲目是否已经是本机上的音乐文件，无需再走在线下载。</summary>
    /// <remarks>
    /// 只看本地曲目的 <see cref="FilePath"/>：在线曲目的 FilePath（历史数据）与
    /// <see cref="PlaybackCachePath"/> 都只是缓冲，不能算"已下载"。
    /// </remarks>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsAlreadyLocalFile =>
        IsLocalTrack && !string.IsNullOrEmpty(FilePath) && File.Exists(FilePath);

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

    /// <summary>听歌排行等视图的展示文本（如"播放 12 次 · 累计 1 小时 3 分"）；仅界面用，不参与持久化。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string ListenStatText { get; set; } = string.Empty;

    /// <summary>在线来源页面链接（如 B 站视频页），本地曲目为空。</summary>
    public string SourceUrl { get; set; } = string.Empty;

    /// <summary>插件源的原始 musicItem JSON（播放/歌词时回传给插件，保留 songmid 等自定义字段）。</summary>
    public string? PluginData { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public override string ToString() => $"{Title} - {Artist}";
}
