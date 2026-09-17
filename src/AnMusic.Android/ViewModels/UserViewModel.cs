using AnMusic.Models;
using AnMusic.Services;
using AnMusic.Services.Playlist;
using AnMusic.Services.Settings;
using AnMusic.Services.Stats;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AnMusic.Android.ViewModels;

/// <summary>
/// 用户资料 ViewModel：昵称 + 头像 + 曲库统计 + 听歌等级。
/// 侧边栏顶部用户卡、用户中心页共用同一份状态，改一处两处同步。
/// </summary>
public sealed partial class UserViewModel : ObservableObject
{
    private readonly UserSettingsService _settings;
    private readonly UserDataStore _store;
    private readonly IPlatformContext _platform;
    private readonly ListeningStatsService _stats;

    /// <summary>初始化期间为 true：不把读入的昵称再写回磁盘。</summary>
    private bool _suppressPersist;

    public UserViewModel(
        UserSettingsService settings,
        UserDataStore store,
        IPlatformContext platform,
        ListeningStatsService stats)
    {
        _settings = settings;
        _store = store;
        _platform = platform;
        _stats = stats;

        _suppressPersist = true;
        Nickname = string.IsNullOrWhiteSpace(settings.Settings.UserNickname)
            ? "音乐爱好者"
            : settings.Settings.UserNickname;
        _suppressPersist = false;

        RefreshAvatar();
        RefreshStats();

        // 听歌时长累加时同步刷新等级显示（用户中心/侧边栏正开着也能实时变化）
        _stats.StatsChanged += (_, _) => MainThread.BeginInvokeOnMainThread(RefreshLevel);
    }

    #region 昵称

    [ObservableProperty] private string _nickname = "音乐爱好者";

    partial void OnNicknameChanged(string value)
    {
        // 输入过程中只刷新界面（侧边栏用户卡实时跟随），落盘放到 CommitNickname，
        // 避免每敲一个字就写一次 settings.json
        OnPropertyChanged(nameof(NicknameInitial));
    }

    /// <summary>把输入框里的昵称正式保存（并把空白回落成默认名）。</summary>
    public void CommitNickname()
    {
        if (string.IsNullOrWhiteSpace(Nickname))
        {
            _suppressPersist = true;
            Nickname = "音乐爱好者";
            _suppressPersist = false;
            _settings.Update(s => s.UserNickname = "音乐爱好者");
            return;
        }
        _settings.Update(s => s.UserNickname = Nickname.Trim());
    }

    #endregion

    #region 头像

    /// <summary>头像图片源（file:// URI）；无头像时为 null。</summary>
    [ObservableProperty] private string? _avatarSource;

    /// <summary>是否已设置自定义头像（未设置时显示默认音符底）。</summary>
    public bool HasAvatar => !string.IsNullOrEmpty(AvatarSource);

    /// <summary>昵称首字，用作无头像时的文字头像。</summary>
    public string NicknameInitial =>
        string.IsNullOrWhiteSpace(Nickname) ? "♪" : Nickname.Trim()[..1].ToUpperInvariant();

    partial void OnAvatarSourceChanged(string? value) => OnPropertyChanged(nameof(HasAvatar));

    /// <summary>头像文件路径（保存在应用数据目录下）。</summary>
    public static string AvatarFilePath => Path.Combine(AppPaths.DataRoot, "avatar.png");

    /// <summary>设置新头像并持久化。</summary>
    public void SetAvatar(string filePath)
    {
        _settings.Update(s => s.UserAvatarPath = filePath);
        RefreshAvatar();
    }

    /// <summary>清除自定义头像，回到默认音符底。</summary>
    public void ClearAvatar()
    {
        _settings.Update(s => s.UserAvatarPath = null);
        RefreshAvatar();
    }

    /// <summary>重新读取头像（文件被裁剪页覆写后调用）。</summary>
    public void RefreshAvatar()
    {
        var path = _settings.Settings.UserAvatarPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            AvatarSource = null;
            return;
        }

        // 同一个路径被覆写后 MAUI 会命中图片缓存，加个时间戳强制刷新
        AvatarSource = $"{_platform.ToImageSourceUri(path)}?t={File.GetLastWriteTimeUtc(path).Ticks}";
    }

    #endregion

    #region 统计

    [ObservableProperty] private string _favoriteCountText = "0";
    [ObservableProperty] private string _playlistCountText = "0";
    [ObservableProperty] private string _recentCountText = "0";

    // ── 听歌等级（对齐桌面端的 1-6 级体系）──
    [ObservableProperty] private string _levelText = "Lv.1";
    [ObservableProperty] private string _levelProgressText = "0.0h / 0.5h";
    [ObservableProperty] private double _levelProgressValue;
    [ObservableProperty] private string _totalListeningText = "0 分钟";
    [ObservableProperty] private string _totalPlayCountText = "0 次";

    /// <summary>我的歌单（直接绑定到用户中心列表）。</summary>
    public IReadOnlyList<Playlist> Playlists => _store.Playlists;

    /// <summary>刷新曲库统计（侧边栏/用户中心打开时调用）。</summary>
    public void RefreshStats()
    {
        FavoriteCountText = _store.Favorites.Count.ToString();
        PlaylistCountText = _store.Playlists.Count.ToString();
        RecentCountText = _store.Recent.Count.ToString();
        OnPropertyChanged(nameof(Playlists));
        RefreshLevel();
    }

    /// <summary>刷新听歌等级与累计时长。</summary>
    public void RefreshLevel()
    {
        LevelText = $"Lv.{_stats.UserLevel}";
        LevelProgressText = _stats.UserLevelProgress;
        LevelProgressValue = _stats.UserLevelProgressValue;
        TotalListeningText = _stats.TotalListeningText;
        TotalPlayCountText = $"{_stats.TotalPlayCount} 次";
    }

    /// <summary>昵称 + 统计一起刷新。</summary>
    public void Refresh()
    {
        RefreshStats();
        RefreshAvatar();
    }

    #endregion
}
