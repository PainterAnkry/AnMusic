using AnMusic.Android.Services;
using AnMusic.Android.ViewModels;
using AnMusic.Services;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Media;
using Microsoft.Maui.Storage;

namespace AnMusic.Android;

/// <summary>
/// 用户中心：头像（相册选图 + 方形裁剪）与昵称。
/// </summary>
public partial class UserPage : ContentPage
{
    private readonly UserViewModel _user;

    /// <summary>防止重复触发选图流程。</summary>
    private bool _picking;

    public UserPage(UserViewModel user)
    {
        InitializeComponent();
        _user = user;
        BindingContext = user;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _user.Refresh();
    }

    private void OnOpenFlyoutClicked(object? sender, EventArgs e)
        => Shell.Current.FlyoutIsPresented = true;

    private async void OnBackClicked(object? sender, EventArgs e)
    {
        try { await Shell.Current.GoToAsync("//MainPage"); }
        catch { await Navigation.PopAsync(); }
    }

    #region 昵称

    private void OnNicknameCommitted(object? sender, EventArgs e) => _user.CommitNickname();

    #endregion

    #region 头像

    private async void OnChangeAvatarTapped(object? sender, TappedEventArgs e)
    {
        if (_picking) return;
        _picking = true;

        try
        {
            var picked = await MediaPicker.Default.PickPhotoAsync();
            if (picked is null) return;

            // 相册返回的是流，先落到缓存文件才能用位图 API 处理
            var rawPath = Path.Combine(FileSystem.CacheDirectory, "avatar_pick.jpg");
            await using (var source = await picked.OpenReadAsync())
            await using (var target = File.Create(rawPath))
            {
                await source.CopyToAsync(target);
            }

            // 归一化：按 EXIF 摆正 + 限制最长边，避免原图过大导致 OOM
            var normalizedPath = Path.Combine(FileSystem.CacheDirectory, "avatar_crop.png");
            var normalized = await AvatarImageHelper.NormalizeAsync(rawPath, normalizedPath);

            if (normalized is null)
            {
                await DisplayAlert("无法读取", "这张图片无法解析，请换一张试试。", "好");
                return;
            }

            var cropPage = new AvatarCropPage(normalized);
            await Navigation.PushModalAsync(cropPage);

            if (await cropPage.Completion)
                _user.RefreshAvatar();
        }
        catch (FeatureNotSupportedException)
        {
            await DisplayAlert("不支持", "当前设备不支持从相册选图。", "好");
        }
        catch (PermissionException)
        {
            await DisplayAlert("需要权限", "请在系统设置中允许 AnMusic 读取照片后重试。", "好");
        }
        catch (Exception ex)
        {
            AppPaths.LogError("选择头像", ex);
            await DisplayAlert("选择失败", ex.Message, "好");
        }
        finally
        {
            _picking = false;
        }
    }

    private async void OnRemoveAvatarTapped(object? sender, TappedEventArgs e)
    {
        var confirm = await DisplayAlert("移除头像", "确定要恢复为默认头像吗？", "移除", "取消");
        if (!confirm) return;

        try
        {
            var path = UserViewModel.AvatarFilePath;
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            AppPaths.LogError("删除头像文件", ex);
        }

        _user.ClearAvatar();
    }

    #endregion
}
