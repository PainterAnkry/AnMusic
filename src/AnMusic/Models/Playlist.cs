using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AnMusic.Models;

/// <summary>
/// 用户歌单。实现 INotifyPropertyChanged：改名后侧边栏即时刷新。
/// </summary>
public sealed class Playlist : INotifyPropertyChanged
{
    private string _name = "新建歌单";

    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    // 注意：必须可写，System.Text.Json 反序列化时会替换集合实例；get-only 会导致歌单内容重启后丢失
    public ObservableCollection<Track> Tracks { get; set; } = [];

    public DateTime CreatedAt { get; init; } = DateTime.Now;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
