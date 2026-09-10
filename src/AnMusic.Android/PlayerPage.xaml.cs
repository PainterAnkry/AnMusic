using AnMusic.Android.ViewModels;
using AnMusic.Models;

namespace AnMusic.Android;

/// <summary>
/// 播放详情页：封面 / 歌词 / 队列三视图切换 + 常驻传输控制。
/// </summary>
public partial class PlayerPage : ContentPage
{
    private readonly PlayerViewModel _player;
    private readonly LibraryViewModel? _library;

    /// <summary>歌词自动滚动的节流控制。</summary>
    private int _lastScrolledLyricIndex = -1;

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

        UpdateFavoriteVisual();
    }

    /// <summary>页面关闭时解绑，避免 ViewModel（单例）持有已销毁页面的引用。</summary>
    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _player.PropertyChanged -= OnPlayerPropertyChanged;
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
        }
    }

    #region 视图切换

    private void ShowCover()
    {
        CoverView.IsVisible = true;
        LyricView.IsVisible = false;
        QueueView.IsVisible = false;
        _player.IsLyricMode = false;
    }

    private void ShowLyrics()
    {
        CoverView.IsVisible = false;
        LyricView.IsVisible = true;
        QueueView.IsVisible = false;
        _player.IsLyricMode = true;
        ScrollToCurrentLyric(force: true);
    }

    private void ShowQueue()
    {
        CoverView.IsVisible = false;
        LyricView.IsVisible = false;
        QueueView.IsVisible = true;
        _player.IsLyricMode = false;
    }

    private void OnShowLyricsClicked(object? sender, EventArgs e) => ShowLyrics();

    private void OnHideLyricsClicked(object? sender, EventArgs e) => ShowCover();

    private void OnShowQueueClicked(object? sender, EventArgs e) => ShowQueue();

    private void OnHideQueueClicked(object? sender, EventArgs e) => ShowCover();

    #endregion

    #region 歌词滚动

    /// <summary>把当前歌词行滚到可视区中部。</summary>
    private void ScrollToCurrentLyric(bool force = false)
    {
        var index = _player.CurrentLyricIndex;
        if (index < 0 || index >= _player.LyricLines.Count) return;

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
            ? Color.FromArgb("#EC4141")
            : Color.FromArgb("#1A1A1A");
    }

    private async void OnMoreClicked(object? sender, EventArgs e)
    {
        if (_player.CurrentTrack is not { } track) return;

        var options = new List<string> { "查看歌曲信息", "下一首播放", "加入歌单" };
        var action = await DisplayActionSheet(track.Title, "取消", null, options.ToArray());
        if (string.IsNullOrEmpty(action) || action == "取消") return;

        switch (action)
        {
            case "查看歌曲信息":
                await DisplayAlert(
                    "歌曲信息",
                    $"标题：{track.Title}\n歌手：{track.Artist}\n专辑：{track.Album}\n来源：{track.ProviderId}",
                    "好");
                break;

            case "加入歌单":
                if (_library is null) return;
                var names = _library.Playlists.Select(p => p.Name).ToList();
                if (names.Count == 0)
                {
                    await DisplayAlert("提示", "还没有歌单，请先到歌单页新建一个。", "好");
                    return;
                }

                var picked = await DisplayActionSheet("加入到…", "取消", null, names.ToArray());
                if (string.IsNullOrEmpty(picked) || picked == "取消") return;
                _library.AddToPlaylistByName(track, picked);
                await DisplayAlert("已加入", $"「{track.Title}」已加入「{picked}」", "好");
                break;

            case "下一首播放":
                await DisplayAlert("提示", "「下一首播放」将在后续版本支持。", "好");
                break;
        }
    }

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

    #endregion
}
