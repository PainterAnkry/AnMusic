using AnMusic.Models;
using AnMusic.Services.Formatting;
using AnMusic.Services.Playlist;

namespace AnMusic.Services.Stats;

/// <summary>
/// 听歌统计：累计播放时长、播放次数、用户等级、听歌排行。
/// </summary>
/// <remarks>
/// 这套逻辑原本内联在桌面端的 <c>MainViewModel</c> 里，安卓端移植时整块丢失，
/// 导致用户中心没有等级、也没有听歌排行。抽到 Core 后两端共用同一份计数规则，
/// 且直接复用 <see cref="UserDataStore"/> 里已有的 PlayStats / PlayCounts /
/// TotalListeningSeconds 三个字段（跨端通用，两端数据可互通）。
/// </remarks>
public sealed class ListeningStatsService
{
    /// <summary>落盘间隔（秒）：每累计这么多秒才写一次盘，避免 10Hz 的位置回调把磁盘写爆。</summary>
    private const double SaveIntervalSeconds = 30;

    private readonly UserDataStore _store;

    /// <summary>上次记录到的播放位置，用于按差值累加。</summary>
    private double _lastPosition;

    /// <summary>上次落盘时的累计秒数。</summary>
    private double _lastSavedSeconds;

    public ListeningStatsService(UserDataStore store)
    {
        _store = store;
        _lastSavedSeconds = store.TotalListeningSeconds;
    }

    /// <summary>曲目唯一键（与桌面端一致：ProviderId:Id），统计字典的 Key。</summary>
    public static string KeyOf(Track track) => $"{track.ProviderId}:{track.Id}";

    #region 计数

    /// <summary>
    /// 曲目开始播放：播放次数 +1 并立即落盘（次数变化是离散事件，不能攒）。
    /// </summary>
    public void OnTrackStarted(Track track)
    {
        if (track is null) return;

        var key = KeyOf(track);
        _store.PlayCounts.TryGetValue(key, out var count);
        _store.PlayCounts[key] = count + 1;
        _store.Save();

        StatsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 播放位置推进时调用（播放器每次上报位置都会走到这里）。
    /// 只累加 0~5 秒的正向增量：拖动进度条会产出巨大的跳变，必须滤掉，
    /// 否则听歌时长会被一次拖动灌进几十分钟。
    /// </summary>
    public void OnPositionChanged(Track? track, double positionSeconds, bool isPlaying)
    {
        if (track is null || !isPlaying)
        {
            _lastPosition = positionSeconds;
            return;
        }

        var delta = positionSeconds - _lastPosition;
        if (delta is > 0 and < 5)
        {
            var prevHours = TotalHours;
            var prevLevel = UserLevel;

            var key = KeyOf(track);
            _store.PlayStats.TryGetValue(key, out var total);
            _store.PlayStats[key] = total + delta;
            _store.TotalListeningSeconds += delta;

            if (UserLevel != prevLevel || (int)(TotalHours * 2) != (int)(prevHours * 2))
                StatsChanged?.Invoke(this, EventArgs.Empty);

            // 每跨过一个 SaveIntervalSeconds 才落盘一次
            if (_store.TotalListeningSeconds - _lastSavedSeconds >= SaveIntervalSeconds)
            {
                _lastSavedSeconds = _store.TotalListeningSeconds;
                _store.Save();
            }
        }

        _lastPosition = positionSeconds;
    }

    /// <summary>切换曲目时重置差值基准，避免把上一首的位置算进这一首。</summary>
    public void ResetPositionBaseline() => _lastPosition = 0;

    /// <summary>强制落盘（退出播放、页面离开等时机调用）。</summary>
    public void Save()
    {
        _lastSavedSeconds = _store.TotalListeningSeconds;
        _store.Save();
    }

    /// <summary>统计数字变化（等级/时长），供界面刷新。</summary>
    public event EventHandler? StatsChanged;

    #endregion

    #region 等级

    private double TotalHours => _store.TotalListeningSeconds / 3600.0;

    /// <summary>按累计听歌小时数计算等级区间（等级、本级起点、下一级所需）。</summary>
    public static (int Level, double Cur, double Next) GetLevelInfo(double hours) => hours switch
    {
        < 0.5 => (1, 0.0, 0.5),
        < 2 => (2, 0.5, 2.0),
        < 6 => (3, 2.0, 6.0),
        < 15 => (4, 6.0, 15.0),
        < 40 => (5, 15.0, 40.0),
        _ => (6, 40.0, double.PositiveInfinity)
    };

    /// <summary>用户等级（1-6），按累计听歌时长计算。</summary>
    public int UserLevel => GetLevelInfo(TotalHours).Level;

    /// <summary>等级进度文案："1.2h / 2h" 或 "累计 52.1h · 已满级"。</summary>
    public string UserLevelProgress
    {
        get
        {
            var hours = TotalHours;
            var (_, _, next) = GetLevelInfo(hours);
            return double.IsPositiveInfinity(next)
                ? $"累计 {hours:F1}h · 已满级"
                : $"{hours:F1}h / {next}h";
        }
    }

    /// <summary>经验进度条值（0-100）。</summary>
    public double UserLevelProgressValue
    {
        get
        {
            var hours = TotalHours;
            var (_, cur, next) = GetLevelInfo(hours);
            if (double.IsPositiveInfinity(next)) return 100;
            return Math.Clamp((hours - cur) / (next - cur) * 100, 0, 100);
        }
    }

    /// <summary>累计听歌时长文本（"3.2 小时" / "48 分钟"）。</summary>
    public string TotalListeningText
    {
        get
        {
            var ts = TimeSpan.FromSeconds(_store.TotalListeningSeconds);
            return ts.TotalHours >= 1 ? $"{ts.TotalHours:F1} 小时" : $"{ts.TotalMinutes:F0} 分钟";
        }
    }

    /// <summary>累计播放总次数。</summary>
    public int TotalPlayCount => _store.PlayCounts.Values.Sum();

    #endregion

    #region 听歌排行

    /// <summary>
    /// 听歌排行：按累计播放时长降序。
    /// </summary>
    /// <param name="knownTracks">
    /// 可用于还原曲目信息的候选集合（本地曲库 + 我喜欢 + 歌单 + 最近播放）。
    /// 统计字典里只存了 Key，必须靠这个把 Key 还原成可展示的 <see cref="Track"/>。
    /// </param>
    /// <param name="max">最多返回多少条。</param>
    public IReadOnlyList<Track> BuildRanking(IEnumerable<Track> knownTracks, int max = 100)
    {
        // Key → 曲目（同名曲目以先出现的为准）
        var lookup = new Dictionary<string, Track>(StringComparer.Ordinal);
        foreach (var t in knownTracks)
        {
            if (t is null) continue;
            lookup.TryAdd(KeyOf(t), t);
        }

        var list = new List<Track>();
        foreach (var (key, seconds) in _store.PlayStats)
        {
            if (seconds <= 0) continue;
            if (!lookup.TryGetValue(key, out var template)) continue;

            _store.PlayCounts.TryGetValue(key, out var count);

            // 复制一份再挂展示文本：直接改模板会污染曲库里的同一实例
            list.Add(new Track
            {
                Id = template.Id,
                Title = template.Title,
                Artist = template.Artist,
                Album = template.Album,
                Duration = template.Duration,
                FilePath = template.FilePath,
                PlaybackCachePath = template.PlaybackCachePath,
                CoverKey = template.CoverKey,
                CoverUrl = template.CoverUrl,
                ProviderId = template.ProviderId,
                SourceUrl = template.SourceUrl,
                PluginData = template.PluginData,
                ListenStatText = ListenStatsText.Compose(count, seconds),
            });
        }

        return list
            .OrderByDescending(t => _store.PlayStats.TryGetValue(KeyOf(t), out var s) ? s : 0)
            .Take(max)
            .ToList();
    }

    /// <summary>清空听歌统计（设置页「重置听歌统计」用）。</summary>
    public void Clear()
    {
        _store.PlayStats.Clear();
        _store.PlayCounts.Clear();
        _store.TotalListeningSeconds = 0;
        _lastSavedSeconds = 0;
        _store.Save();
        StatsChanged?.Invoke(this, EventArgs.Empty);
    }

    #endregion
}
