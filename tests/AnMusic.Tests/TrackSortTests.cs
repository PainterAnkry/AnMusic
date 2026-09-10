using System.Collections.ObjectModel;
using System.Windows.Data;
using AnMusic.Models;
using AnMusic.Services.Formatting;

namespace AnMusic.Tests;

/// <summary>
/// 曲目列表排序：按视图排序（不改源集合），且可取消回到原顺序。
/// </summary>
public class TrackSortTests
{
    private static Track T(string title, string artist, string album, int seconds)
        => new() { Id = title, Title = title, Artist = artist, Album = album, Duration = TimeSpan.FromSeconds(seconds) };

    private static ObservableCollection<Track> Sample() =>
    [
        T("Brown",  "Zed",   "Beta",  200),
        T("apple",  "Anna",  "Alpha", 100),
        T("Cherry", "bob",   "Gamma", 300)
    ];

    private static List<string> Titles(ObservableCollection<Track> list)
        => CollectionViewSource.GetDefaultView(list).Cast<Track>().Select(t => t.Title).ToList();

    [Fact]
    public void 按标题升序_不区分大小写()
    {
        var list = Sample();
        var view = (ListCollectionView)CollectionViewSource.GetDefaultView(list);
        view.CustomSort = new TrackSortComparer(TrackSortComparer.FieldTitle, false);

        Assert.Equal(["apple", "Brown", "Cherry"], Titles(list));
    }

    [Fact]
    public void 按标题降序()
    {
        var list = Sample();
        var view = (ListCollectionView)CollectionViewSource.GetDefaultView(list);
        view.CustomSort = new TrackSortComparer(TrackSortComparer.FieldTitle, true);

        Assert.Equal(["Cherry", "Brown", "apple"], Titles(list));
    }

    [Fact]
    public void 按歌手升序_忽略大小写()
    {
        var list = Sample();
        var view = (ListCollectionView)CollectionViewSource.GetDefaultView(list);
        view.CustomSort = new TrackSortComparer(TrackSortComparer.FieldArtist, false);

        Assert.Equal(["Anna", "bob", "Zed"], view.Cast<Track>().Select(t => t.Artist).ToList());
    }

    [Fact]
    public void 按专辑与时长排序()
    {
        var list = Sample();
        var view = (ListCollectionView)CollectionViewSource.GetDefaultView(list);

        view.CustomSort = new TrackSortComparer(TrackSortComparer.FieldAlbum, false);
        Assert.Equal(["Alpha", "Beta", "Gamma"], view.Cast<Track>().Select(t => t.Album).ToList());

        view.CustomSort = new TrackSortComparer(TrackSortComparer.FieldDuration, false);
        Assert.Equal([100.0, 200, 300], view.Cast<Track>().Select(t => t.Duration.TotalSeconds).ToList());

        view.CustomSort = new TrackSortComparer(TrackSortComparer.FieldDuration, true);
        Assert.Equal([300.0, 200, 100], view.Cast<Track>().Select(t => t.Duration.TotalSeconds).ToList());
    }

    [Fact]
    public void 取消排序后回到源集合原顺序()
    {
        var list = Sample();
        var original = list.Select(t => t.Title).ToList();

        var view = (ListCollectionView)CollectionViewSource.GetDefaultView(list);
        view.CustomSort = new TrackSortComparer(TrackSortComparer.FieldTitle, false);
        Assert.NotEqual(original, Titles(list));      // 排序生效

        view.CustomSort = null;                        // 取消排序（第三次点击表头）
        Assert.Equal(original, Titles(list));          // 原顺序完好无损
    }
}
