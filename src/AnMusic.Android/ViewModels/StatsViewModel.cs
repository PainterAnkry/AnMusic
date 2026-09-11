using System.Collections.ObjectModel;
using AnMusic.Models;
using AnMusic.Services.Stats;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnMusic.Android.ViewModels;

/// <summary>排行条目：曲目 + 名次。MAUI 的列表没有内置索引绑定，故用包装类型显式带出名次。</summary>
public sealed class RankedTrack
{
    public required int Rank { get; init; }
    public required Track Track { get; init; }

    public string RankText => Rank switch
    {
        1 => "🥇",
        2 => "🥈",
        3 => "🥉",
        _ => Rank.ToString(),
    };

    public string Title => Track.Title;
    public string Artist => Track.Artist;
    public string ListenStatText => Track.ListenStatText;
    public string? CoverKey => Track.CoverKey;
    public TimeSpan Duration => Track.Duration;
}

/// <summary>
/// 听歌排行 ViewModel：按累计播放时长排序，展示"播放 N 次 · 累计 X"。
/// 对应桌面端的「听歌排行」视图，数据来自 Core 的 <see cref="ListeningStatsService"/>。
/// </summary>
public sealed partial class StatsViewModel : ObservableObject
{
    private readonly ListeningStatsService _stats;
    private readonly LibraryViewModel _library;
    private readonly PlayerViewModel _player;

    public StatsViewModel(ListeningStatsService stats, LibraryViewModel library, PlayerViewModel player)
    {
        _stats = stats;
        _library = library;
        _player = player;
        Refresh();
    }

    #region 状态

    /// <summary>排行条目（已按累计时长降序）。</summary>
    public ObservableCollection<RankedTrack> Ranking { get; } = [];

    [ObservableProperty] private string _summaryText = "暂无听歌记录";
    [ObservableProperty] private string _levelText = "Lv.1";
    [ObservableProperty] private string _levelProgressText = "0.0h / 0.5h";
    [ObservableProperty] private double _levelProgressValue;
    [ObservableProperty] private string _totalListeningText = "0 分钟";
    [ObservableProperty] private string _totalPlayCountText = "0 次";

    public bool IsEmpty => Ranking.Count == 0;

    #endregion

    /// <summary>重新统计并刷新排行（页面显示时调用）。</summary>
    [RelayCommand]
    public void Refresh()
    {
        // 统计表里只存了 "ProviderId:Id" 这样的键，需要用曲库里所有已知曲目把它还原成可展示的曲目
        var known = _library.AllTracks
            .Concat(_library.Favorites)
            .Concat(_library.Playlists.SelectMany(p => p.Tracks))
            .Concat(_library.Recent)
            .ToList();

        Ranking.Clear();
        var rank = 1;
        foreach (var track in _stats.BuildRanking(known, max: 200))
            Ranking.Add(new RankedTrack { Rank = rank++, Track = track });

        SummaryText = Ranking.Count == 0
            ? "暂无听歌记录，播放几首歌后这里会出现排行"
            : $"共 {Ranking.Count} 首有播放记录";

        LevelText = $"Lv.{_stats.UserLevel}";
        LevelProgressText = _stats.UserLevelProgress;
        LevelProgressValue = _stats.UserLevelProgressValue;
        TotalListeningText = _stats.TotalListeningText;
        TotalPlayCountText = $"{_stats.TotalPlayCount} 次";

        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>点击排行条目即从该位置开始连着播下去。</summary>
    [RelayCommand]
    private async Task PlayAsync(RankedTrack? item)
    {
        if (item is null) return;
        var index = Ranking.IndexOf(item);
        await _player.PlayQueueAsync(Ranking.Select(r => r.Track).ToList(), Math.Max(0, index));
    }

    /// <summary>清空听歌统计。</summary>
    [RelayCommand]
    private async Task ClearAsync()
    {
        var confirm = await MainThread.InvokeOnMainThreadAsync(() =>
            Shell.Current.DisplayAlert("清空听歌统计", "将清空播放次数与累计听歌时长，此操作不可撤销。", "清空", "取消"));
        if (!confirm) return;

        _stats.Clear();
        Refresh();
    }
}
