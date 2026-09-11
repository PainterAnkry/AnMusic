using AnMusic.Models;
using AnMusic.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnMusic.Android.ViewModels;

/// <summary>电台模式。</summary>
public enum RadioMode
{
    /// <summary>全部本地曲目随机。</summary>
    ShuffleAll,
    /// <summary>只播我喜欢。</summary>
    Favorites,
    /// <summary>按歌手轮播（先随机抽一个艺人，再洗这个艺人的曲序）。</summary>
    ByArtist,
    /// <summary>冷门曲优先（按播放次数升序，越少听越先推）。</summary>
    Fresh,
    /// <summary>按歌手/专辑/风格一致原则，从上次播放的曲目衍生出同艺人或同专辑的下一首。</summary>
    Discovery,
}

/// <summary>
/// 个性电台：5 种模式，全部基于现有曲库数据，无在线依赖。
/// 启动后会把队列整体灌进 PlayerViewModel，由现有的队列循环/单曲循环逻辑负责连播。
/// </summary>
public sealed partial class RadioViewModel : ObservableObject
{
    private readonly LibraryViewModel _library;
    private readonly PlayerViewModel _player;

    public RadioViewModel(LibraryViewModel library, PlayerViewModel player)
    {
        _library = library;
        _player = player;

        Modes = [
            new RadioModeOption(RadioMode.ShuffleAll, "🎲", "随机本地",     "全部本地曲目随机播放"),
            new RadioModeOption(RadioMode.Favorites,  "♥", "我喜欢",       "只播「我喜欢」里的歌"),
            new RadioModeOption(RadioMode.ByArtist,   "👤", "按歌手轮播",   "同一个艺人集中播放，再切下一个"),
            new RadioModeOption(RadioMode.Fresh,      "✨", "冷门曲优先",   "播放次数越少的曲目越先出现"),
            new RadioModeOption(RadioMode.Discovery,  "🔀", "延伸发现",     "围绕上次播放的歌手/专辑扩展"),
        ];
    }

    public IReadOnlyList<RadioModeOption> Modes { get; }

    [ObservableProperty] private string _statusText = string.Empty;

    [RelayCommand]
    public async Task StartAsync(RadioModeOption? option)
    {
        if (option is null) return;
        if (_library.AllTracks.Count == 0)
        {
            StatusText = "本地还没音乐，先去「我的音乐」扫描一次吧";
            return;
        }

        try
        {
            var queue = option.Mode switch
            {
                RadioMode.ShuffleAll => Shuffle(_library.AllTracks.ToList()),
                RadioMode.Favorites  => Shuffle(_library.Favorites.ToList()),
                RadioMode.ByArtist   => ByArtist(_library.AllTracks.ToList()),
                RadioMode.Fresh      => Fresh(_library),
                RadioMode.Discovery  => Discovery(_library),
                _ => _library.AllTracks.ToList(),
            };

            if (queue.Count == 0)
            {
                StatusText = option.Mode switch
                {
                    RadioMode.Favorites => "还没有添加任何喜欢，先去收藏一些再听",
                    _ => "暂无可播放的曲目",
                };
                return;
            }

            // 强制洗牌模式连播
            _player.PlayMode = PlayModeKind.Shuffle;
            await _player.PlayQueueAsync(queue, 0);
            StatusText = $"已开始「{option.Title}」，共 {queue.Count} 首";
        }
        catch (Exception ex)
        {
            StatusText = $"启动失败：{ex.Message}";
            AppPaths.LogError("启动电台", ex, option.Mode.ToString());
        }
    }

    /// <summary>纯随机洗牌。</summary>
    private static List<Track> Shuffle(IReadOnlyList<Track> source)
    {
        var list = source.ToList();
        var rng = Random.Shared;
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list;
    }

    /// <summary>按艺人分组，随机抽若干艺人，每个艺人内的曲目随机洗牌后串成队列。</summary>
    private static List<Track> ByArtist(IReadOnlyList<Track> source)
    {
        var groups = source
            .Where(t => !string.IsNullOrWhiteSpace(t.Artist))
            .GroupBy(t => t.Artist)
            .OrderBy(_ => Random.Shared.Next())
            .Take(8); // 最多 8 个艺人，避免一次队过长
        var queue = new List<Track>();
        foreach (var g in groups)
            queue.AddRange(Shuffle(g.ToList()));
        return queue;
    }

    /// <summary>冷门优先：按播放次数升序，同次数再随机洗。</summary>
    private static List<Track> Fresh(LibraryViewModel library)
    {
        var counts = library.AllTracks.ToDictionary(
            t => $"{t.ProviderId}:{t.Id}",
            _ => 0);
        // 听歌统计里已有播放次数；这里保守按收藏/最近播放叠加估算
        foreach (var t in library.Favorites) counts[$"{t.ProviderId}:{t.Id}"] += 100; // 收藏加权重
        foreach (var t in library.Recent) counts[$"{t.ProviderId}:{t.Id}"] += 10; // 最近听过加权重

        var list = library.AllTracks.ToList();
        list.Sort((a, b) =>
        {
            var ca = counts.GetValueOrDefault($"{a.ProviderId}:{a.Id}");
            var cb = counts.GetValueOrDefault($"{b.ProviderId}:{b.Id}");
            return ca.CompareTo(cb); // 升序：越少播放越先
        });
        return list;
    }

    /// <summary>延伸发现：上次播放的曲目所在的艺人/专辑，作为这一轮的种子。</summary>
    private static List<Track> Discovery(LibraryViewModel library)
    {
        var seed = _player_Recent(library);
        if (seed is null) return Shuffle(library.AllTracks.ToList());

        var seedArtist = seed.Artist ?? string.Empty;
        var sameArtist = library.AllTracks
            .Where(t => !string.IsNullOrWhiteSpace(seedArtist) && t.Artist == seedArtist)
            .ToList();
        var others = library.AllTracks
            .Where(t => !sameArtist.Contains(t))
            .ToList();

        // 同艺人集中播（但打乱） + 其他艺人各取一首交叉
        var queue = new List<Track>();
        queue.AddRange(Shuffle(sameArtist));
        foreach (var t in Shuffle(others).Take(20))
            queue.Add(t);
        return queue;

        static Track? _player_Recent(LibraryViewModel l) =>
            l.Recent.FirstOrDefault() ?? l.AllTracks.FirstOrDefault();
    }
}

/// <summary>UI 端的电台模式选项。</summary>
public sealed record RadioModeOption(RadioMode Mode, string Icon, string Title, string Desc)
{
    public override string ToString() => $"{Icon} {Title}";
}

internal static class EnumerableTakeManyExt
{
    /// <summary>对 IEnumerable 多次取（每次 1 个）直到总数达 max 或耗尽。</summary>
    public static IEnumerable<T> TakeMany<T>(this IEnumerable<IEnumerable<T>> source, int max)
    {
        var taken = 0;
        foreach (var group in source)
        {
            if (taken >= max) yield break;
            foreach (var item in group)
            {
                yield return item;
                taken++;
                if (taken >= max) yield break;
            }
        }
    }
}