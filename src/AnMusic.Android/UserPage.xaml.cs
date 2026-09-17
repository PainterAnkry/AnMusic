using AnMusic.Android.Services;
using AnMusic.Android.ViewModels;
using AnMusic.Models;
using AnMusic.Services;

namespace AnMusic.Android;

public partial class UserPage : ContentPage
{
    private readonly UserViewModel _user;
    private readonly LibraryViewModel? _library;

    public UserPage(UserViewModel user)
    {
        InitializeComponent();
        _user = user;
        _library = MauiProgram.Services.GetService<LibraryViewModel>();
        BindingContext = user;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _user.Refresh();
    }

    #region 导航

    private async void OnBackClicked(object? sender, EventArgs e)
    {
        try { await Shell.Current.GoToAsync("//MainPage"); }
        catch { await Navigation.PopAsync(); }
    }

    private void OnOpenFlyoutClicked(object? sender, EventArgs e)
        => Shell.Current.FlyoutIsPresented = true;

    private async void OnOpenFavoritesTapped(object? sender, TappedEventArgs e)
    {
        try
        {
            if (_library is not null) _library.CurrentTab = LibraryTab.Favorites;
            await Shell.Current.GoToAsync("//MainPage");
        }
        catch (Exception ex) { AppPaths.LogError("打开我喜欢", ex); }
    }

    #endregion

    #region Tab 切换

    private void OnTabMusicTapped(object? sender, TappedEventArgs e) => ShowTab("music");
    private void OnTabPodcastTapped(object? sender, TappedEventArgs e) => ShowTab("other", TabPodcast, IndPodcast);
    private void OnTabCommentsTapped(object? sender, TappedEventArgs e) => ShowTab("other", TabComments, IndComments);
    private void OnTabNotesTapped(object? sender, TappedEventArgs e) => ShowTab("other", TabNotes, IndNotes);

    private void ShowTab(string tab, Label? selected = null, VisualElement? indicator = null)
    {
        MusicContent.IsVisible = tab == "music";
        OtherContent.IsVisible = tab != "music";

        // 选中态：红色加粗 + 红色下划线
        var tabs = new[] { TabMusic, TabPodcast, TabComments, TabNotes };
        var inds = new[] { IndMusic, IndPodcast, IndComments, IndNotes };
        for (var i = 0; i < tabs.Length; i++)
        {
            var active = selected is null ? tabs[i] == TabMusic : tabs[i] == selected;
            tabs[i].TextColor = active
                ? (Color)Application.Current!.Resources["AmPrimary"]
                : (Color)Application.Current.Resources["AmTextSecondary"];
            tabs[i].FontAttributes = active ? FontAttributes.Bold : FontAttributes.None;
            inds[i].IsVisible = active;
        }

        Anim.ViewIn(tab == "music" ? MusicContent : OtherContent);
    }

    #endregion

    #region 头像

    private async void OnChangeAvatarTapped(object? sender, TappedEventArgs e)
    {
        try
        {
            var result = await MediaPicker.Default.PickPhotoAsync(new MediaPickerOptions
            {
                Title = "选择头像"
            });
            if (result is null) return;

            // 先把系统返回的图片复制到应用缓存，避免 URI 权限问题
            var tmp = Path.Combine(FileSystem.CacheDirectory, $"avatar_pick_{Guid.NewGuid():N}.jpg");
            using (var src = await result.OpenReadAsync())
            using (var dst = File.Create(tmp))
                await src.CopyToAsync(dst);

            var crop = new AvatarCropPage(tmp);
            await Navigation.PushModalAsync(crop);
            _ = await crop.Completion;
            _user.RefreshAvatar();
        }
        catch (Exception ex)
        {
            AppPaths.LogError("选择头像", ex);
            await DisplayAlert("出错", $"无法选择图片：{ex.Message}", "好");
        }
    }

    #endregion

    #region 歌单

    private async void OnCreatePlaylistClicked(object? sender, EventArgs e)
    {
        try
        {
            var name = await DisplayPromptAsync("新建歌单", "输入歌单名称", "创建", "取消", "我的歌单");
            if (string.IsNullOrWhiteSpace(name)) return;

            _library?.CreatePlaylistCommand.Execute(name);
            _user.RefreshStats();
        }
        catch (Exception ex) { AppPaths.LogError("新建歌单", ex); }
    }

    private async void OnPlaylistTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Element)?.BindingContext is not Playlist playlist) return;
        if (_library is null) return;

        try
        {
            _library.CurrentTab = LibraryTab.Playlists;
            _library.SelectedPlaylist = playlist;
            await Shell.Current.GoToAsync("//MainPage");
        }
        catch (Exception ex) { AppPaths.LogError("打开歌单", ex); }
    }

    #endregion
}
