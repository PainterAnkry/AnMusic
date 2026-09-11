using AnMusic.Android.Services;
using AnMusic.Android.ViewModels;
using AnMusic.Models;

namespace AnMusic.Android;

/// <summary>
/// 播放详情页：封面 / 歌词 / 播放列表三视图切换 + 常驻传输控制。
/// </summary>
public partial class PlayerPage : ContentPage
{
    private readonly PlayerViewModel _player;
    private readonly LibraryViewModel? _library;

    /// <summary>歌词自动滚动的节流控制。</summary>
    private int _lastScrolledLyricIndex = -1;

    /// <summary>页面首次出现后切换视图才需要动画，避免 ctor 里把默认视图也滑一遍。</summary>
    private bool _viewReady;

    public PlayerPage(PlayerViewModel player, bool startInQueueMode = false)
    {
        InitializeComponent();

        _player = player;
        BindingContext = player;

        // 收藏状态归曲库管，这里取一份用于初始化心形按钮
        _library = MauiProgram.Services.GetService<LibraryViewModel>();
        if (_player.CurrentTrack is { } track && _library is not null)
            _player.SetFavoriteState(_library.IsFavorite(track));

        _player.PropertyChanged += OnPlayerPropertyChanged;

        if (startInQueueMode) ShowQueue();
        else ShowCover();

        UpdateFavoriteVisual();
    }

    /// <summary>页面关闭时解绑，避免 ViewModel（单例）持有已销毁页面的引用。</summary>
    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _player.PropertyChanged -= OnPlayerPropertyChanged;
        StopCoverSpin();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (_player.IsCoverSpinEnabled && _player.IsPlaying) StartCoverSpin();

        // 模态页进场：自底向上滑入；首次出现后再切换视图才挂动画
        Anim.PageEnter(this);
        _viewReady = true;
    }

    private void OnPlayerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerViewModel.CurrentLyricIndex):
                ScrollToCurrentLyric();
                break;

            case nameof(PlayerViewModel.IsFavorite):
                UpdateFavoriteVisual();
                break;

            case nameof(PlayerViewModel.IsPlaying):
                UpdateCoverSpin();
                break;
        }
    }

    #region 视图切换

    private void ShowCover()
    {
        CoverView.IsVisible = true;
        LyricView.IsVisible = false;
        QueueView.IsVisible = false;

        _player.IsLyricMode = false;
        _player.IsQueueVisible = false;

        if (_viewReady) Anim.ViewIn(CoverView);

        UpdateQueueButtonVisual();
        UpdateCoverSpin();
    }

    private void ShowLyrics()
    {
        CoverView.IsVisible = false;
        LyricView.IsVisible = true;
        QueueView.IsVisible = false;

        _player.IsLyricMode = true;
        _player.IsQueueVisible = false;

        if (_viewReady) Anim.ViewIn(LyricView);

        UpdateQueueButtonVisual();
        ScrollToCurrentLyric(force: true);
    }

    private void ShowQueue()
    {
        CoverView.IsVisible = false;
        LyricView.IsVisible = false;
        QueueView.IsVisible = true;

        _player.IsLyricMode = false;
        _player.IsQueueVisible = true;

        if (_viewReady) Anim.ViewIn(QueueView);

        UpdateQueueButtonVisual();
    }

    private void OnShowLyricsClicked(object? sender, EventArgs e) => ShowLyrics();

    private void OnHideLyricsClicked(object? sender, EventArgs e) => ShowCover();

    private void OnCoverTapped(object? sender, TappedEventArgs e) => ShowLyrics();

    private void OnHideQueueClicked(object? sender, EventArgs e) => ShowCover();

    /// <summary>
    /// 播放列表按钮：真正的开关。
    /// 上一版这里只调 ShowQueue()，第二次点击仍然进入同一个视图，
    /// 所以在播放列表里点它看起来「关不掉」——现在按当前状态取反。
    /// </summary>
    private void OnToggleQueueClicked(object? sender, EventArgs e)
    {
        if (QueueView.IsVisible) ShowCover();
        else ShowQueue();
    }

    /// <summary>播放列表按钮在展开时点亮成强调色，让状态一眼可见。</summary>
    private void UpdateQueueButtonVisual()
    {
        var active = QueueView.IsVisible;
        QueueButton.TextColor = active ? ThemeService.Get("AmPrimary") : ThemeService.Get("AmTextSecondary");
    }

    #endregion

    #region 封面旋转

    private const string SpinAnimationName = "coverSpin";

    private void UpdateCoverSpin()
    {
        if (_player.IsCoverSpinEnabled && _player.IsPlaying && CoverView.IsVisible) StartCoverSpin();
        else StopCoverSpin();
    }

    private void StartCoverSpin()
    {
        StopCoverSpin();

        // 24 秒转一圈：足够慢，不抢视线，又能看出在转
        var animation = new Animation(v => CoverImage.Rotation = v, 0, 360);
        animation.Commit(
            this,
            SpinAnimationName,
            rate: 16,
            length: 24000,
            easing: Easing.Linear,
            finished: null,
            repeat: () => _player.IsCoverSpinEnabled && _player.IsPlaying);
    }

    private void StopCoverSpin()
    {
        this.AbortAnimation(SpinAnimationName);
        CoverImage.Rotation = 0;
    }

    #endregion

    #region 歌词滚动

    /// <summary>把当前歌词行滚到可视区中部。</summary>
    private void ScrollToCurrentLyric(bool force = false)
    {
        var index = _player.CurrentLyricIndex;

        // 换歌时索引会先回到 -1，此时必须重置节流记录，
        // 否则下一首播到相同的行号时不会滚动（认为"已经滚过了"）。
        if (index < 0)
        {
            _lastScrolledLyricIndex = -1;
            return;
        }

        if (index >= _player.LyricLines.Count) return;

        // 同一行不重复滚动，避免每 100ms 的位置回调都把列表拽一次
        if (!force && index == _lastScrolledLyricIndex) return;
        _lastScrolledLyricIndex = index;

        try
        {
            LyricList.ScrollTo(index, position: ScrollToPosition.Center, animate: !force);
        }
        catch
        {
            // 列表尚未完成布局时 ScrollTo 可能抛异常，忽略即可（下次回调会再试）
        }
    }

    #endregion

    #region 顶部操作

    private async void OnCloseClicked(object? sender, EventArgs e)
        => await Navigation.PopModalAsync();

    private void OnFavoriteClicked(object? sender, EventArgs e)
    {
        if (_player.CurrentTrack is null) return;

        if (_library is not null)
        {
            _library.ToggleFavoriteCommand.Execute(_player.CurrentTrack);
            _player.SetFavoriteState(_library.IsFavorite(_player.CurrentTrack));
        }
        else
        {
            _player.ToggleFavoriteCommand.Execute(null);
        }
    }

    /// <summary>更新心形按钮：已收藏为红实心，未收藏为灰空心。</summary>
    private void UpdateFavoriteVisual()
    {
        FavoriteButton.Text = _player.IsFavorite ? "♥" : "♡";
        FavoriteButton.TextColor = _player.IsFavorite
            ? ThemeService.Get("AmLike")
            : ThemeService.Get("AmTextPrimary");
    }

    private async void OnMoreClicked(object? sender, EventArgs e)
    {
        if (_player.CurrentTrack is not { } track) return;

        var options = new List<string>
        {
            "歌曲信息",
            "定时关闭",
            "下一首播放",
            "加入歌单",
            _player.IsCoverSpinEnabled ? "关闭封面旋转" : "开启封面旋转",
        };

        var action = await DisplayActionSheet(track.Title, "取消", null, options.ToArray());
        if (string.IsNullOrEmpty(action) || action == "取消") return;

        switch (action)
        {
            case "歌曲信息":
                await DisplayAlert(
                    "歌曲信息",
                    $"标题：{track.Title}\n歌手：{track.Artist}\n专辑：{track.Album}\n来源：{track.ProviderId}",
                    "好");
                break;

            case "定时关闭":
                await ShowSleepTimerOptionsAsync();
                break;

            case "下一首播放":
                _player.InsertNextCommand.Execute(track);
                break;

            case "加入歌单":
                await AddToPlaylistAsync(track);
                break;

            case "关闭封面旋转":
                _player.IsCoverSpinEnabled = false;
                StopCoverSpin();
                break;

            case "开启封面旋转":
                _player.IsCoverSpinEnabled = true;
                UpdateCoverSpin();
                break;
        }
    }

    /// <summary>定时关闭：常用时长 + 播完当前曲目 + 取消。</summary>
    private async Task ShowSleepTimerOptionsAsync()
    {
        string[] options = ["15 分钟", "30 分钟", "45 分钟", "60 分钟", "播完当前曲目后停止", "取消定时"];
        var action = await DisplayActionSheet("定时关闭", "返回", null, options);
        if (string.IsNullOrEmpty(action) || action is "返回") return;

        switch (action)
        {
            case "播完当前曲目后停止":
                _player.StopAfterCurrentTrack();
                break;

            case "取消定时":
                _player.CancelSleepTimer();
                break;

            default:
                var minutes = int.Parse(action.Split(' ')[0]);
                _player.StartSleepTimer(minutes);
                break;
        }
    }

    private async Task AddToPlaylistAsync(Track track)
    {
        if (_library is null) return;

        var names = _library.Playlists.Select(p => p.Name).ToList();
        names.Add("＋ 新建歌单");

        var picked = await DisplayActionSheet("加入到…", "取消", null, names.ToArray());
        if (string.IsNullOrEmpty(picked) || picked == "取消") return;

        if (picked == "＋ 新建歌单")
        {
            var name = await DisplayPromptAsync("新建歌单", "输入歌单名称", "创建", "取消", "我的歌单");
            if (string.IsNullOrWhiteSpace(name)) return;
            _library.CreatePlaylistCommand.Execute(name);
            _library.AddToPlaylistByName(track, name);
            return;
        }

        _library.AddToPlaylistByName(track, picked);
        await DisplayAlert("已加入", $"「{track.Title}」已加入「{picked}」", "好");
    }

    private void OnCancelSleepClicked(object? sender, EventArgs e) => _player.CancelSleepTimer();

    #endregion

    #region 进度与队列

    private void OnSeekDragStarted(object? sender, EventArgs e) => _player.BeginSeek();

    private void OnSeekDragCompleted(object? sender, EventArgs e)
        => _player.CommitSeek(ProgressSlider.Value);

    private async void OnQueueItemTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Element)?.BindingContext is not Track track) return;

        await _player.PlayQueueItemCommand.ExecuteAsync(track);
        if (_library is not null)
            _player.SetFavoriteState(_library.IsFavorite(track));

        ShowCover();
    }

    /// <summary>队列行菜单：下一首播放 / 上移 / 下移 / 移出队列。</summary>
    private async void OnQueueItemMenuClicked(object? sender, EventArgs e)
    {
        if ((sender as Element)?.BindingContext is not Track track) return;

        string[] options = ["下一首播放", "上移", "下移", "从列表移除"];
        var action = await DisplayActionSheet(track.Title, "取消", null, options);
        if (string.IsNullOrEmpty(action) || action == "取消") return;

        switch (action)
        {
            case "下一首播放":
                _player.InsertNextCommand.Execute(track);
                break;

            case "上移":
                _player.MoveQueueItem(track, -1);
                break;

            case "下移":
                _player.MoveQueueItem(track, 1);
                break;

            case "从列表移除":
                _player.RemoveFromQueueCommand.Execute(track);
                break;
        }
    }

    #endregion
}
