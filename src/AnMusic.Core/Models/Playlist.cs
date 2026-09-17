using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AnMusic.Models;

/// <summary>
/// 用户歌单。实现 INotifyPropertyChanged：改名后侧边栏即时刷新。
/// </summary>
public sealed class Playlist : INotifyPropertyChanged
{
    private string _name = "新建歌单";
    private string _coverPath = "";
    private ObservableCollection<Track> _tracks = [];

    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 自定义封面文件路径（空 = 用默认封面，见 <see cref="DefaultCoverTrack"/>）。
    /// </summary>
    /// <remarks>
    /// 选图时会复制到应用数据目录，所以原图被移动/删除也不影响；
    /// 只在文件不存在时才回落到默认封面（由界面层判断）。
    /// </remarks>
    public string CoverPath
    {
        get => _coverPath;
        set
        {
            if (_coverPath == value) return;
            _coverPath = value ?? "";
            OnPropertyChanged();
        }
    }

    // 注意：必须可写，System.Text.Json 反序列化时会替换集合实例；get-only 会导致歌单内容重启后丢失
    public ObservableCollection<Track> Tracks
    {
        get => _tracks;
        set
        {
            if (ReferenceEquals(_tracks, value)) return;
            _tracks.CollectionChanged -= OnTracksChanged;
            _tracks = value ?? [];
            _tracks.CollectionChanged += OnTracksChanged;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DefaultCoverTrack));
        }
    }

    /// <summary>
    /// 默认封面来源：最近添加的那首歌（歌单是追加写入的，所以取最后一首）。
    /// </summary>
    /// <remarks>
    /// 界面上绑的是这个曲目对象而不是路径 —— 封面是后台异步缓存的，
    /// 绑对象才能在封面下载完成后自动刷新。
    /// </remarks>
    public Track? DefaultCoverTrack => Tracks.Count == 0 ? null : Tracks[^1];

    public DateTime CreatedAt { get; init; } = DateTime.Now;

    public event PropertyChangedEventHandler? PropertyChanged;

    public Playlist() => _tracks.CollectionChanged += OnTracksChanged;

    private void OnTracksChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => OnPropertyChanged(nameof(DefaultCoverTrack));

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
