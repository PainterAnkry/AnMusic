using Android.Graphics;
using AnMusic.Android.Services;
using AnMusic.Services;
using Microsoft.Maui.Devices;

namespace AnMusic.Android;

/// <summary>
/// 头像裁剪页：固定方形取景框，图片可拖动与缩放，取景框内的内容就是最终头像。
/// </summary>
/// <remarks>
/// 布局约定（也是裁剪换算的依据）：
///   · 取景框 <see cref="CropHost"/> 是一个边长 S 的方形，且裁掉溢出内容；
///   · 图片以「铺满取景框」为基准缩放到 S × S 起步（zoom = 1），
///     所以图片显示尺寸 = 原图尺寸 × k × zoom，其中 k = S / min(原图宽, 原图高)；
///   · 图片始终居中对齐，位移用 TranslationX/Y 表示。
/// 因此把取景框中心换算回图片本地坐标、再除以「每源像素占多少显示像素」，
/// 就得到源图上要裁剪的矩形 —— 不需要考虑任何旋转（图已在归一化阶段摆正）。
/// </remarks>
public partial class AvatarCropPage : ContentPage
{
    private readonly string _sourcePath;
    private readonly TaskCompletionSource<bool> _completion = new();

    private double _squareSize;
    private int _sourceWidth = 1;
    private int _sourceHeight = 1;

    private double _zoom = 1;
    private double _pinchStartZoom = 1;
    private double _panStartX;
    private double _panStartY;

    /// <summary>裁剪是否成功完成（取消时为 false）。</summary>
    public Task<bool> Completion => _completion.Task;

    public AvatarCropPage(string normalizedImagePath)
    {
        InitializeComponent();

        _sourcePath = normalizedImagePath;

        SizeChanged += (_, _) => ApplyLayout();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        ApplyLayout();
    }

    #region 布局与变换

    /// <summary>按屏幕尺寸定出取景框边长，并把图片按「铺满」基准摆好。</summary>
    private void ApplyLayout()
    {
        var display = DeviceDisplay.MainDisplayInfo;
        var availableWidth = display.Width / display.Density;
        var availableHeight = display.Height / display.Density;

        // 留出顶栏、底部缩放条与边距后的可用空间
        _squareSize = Math.Max(120, Math.Min(availableWidth - 56, availableHeight * 0.52));

        CropHost.WidthRequest = _squareSize;
        CropHost.HeightRequest = _squareSize;

        if (_sourceWidth <= 1 || _sourceHeight <= 1)
        {
            var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
            BitmapFactory.DecodeFile(_sourcePath, bounds);
            _sourceWidth = Math.Max(1, bounds.OutWidth);
            _sourceHeight = Math.Max(1, bounds.OutHeight);
        }

        CropImage.Source = ImageSource.FromFile(_sourcePath);
        ApplyTransform();
    }

    /// <summary>把 zoom 与位移换算成图片的实际显示尺寸与偏移。</summary>
    private void ApplyTransform()
    {
        var baseScale = _squareSize / Math.Min(_sourceWidth, _sourceHeight);
        var scale = baseScale * _zoom;

        var displayWidth = _sourceWidth * scale;
        var displayHeight = _sourceHeight * scale;

        CropImage.WidthRequest = displayWidth;
        CropImage.HeightRequest = displayHeight;

        ClampTranslation(displayWidth, displayHeight);
    }

    /// <summary>把位移夹住，保证取景框内不会露出空白。</summary>
    private void ClampTranslation(double displayWidth, double displayHeight)
    {
        var maxX = Math.Max(0, (displayWidth - _squareSize) / 2);
        var maxY = Math.Max(0, (displayHeight - _squareSize) / 2);

        CropImage.TranslationX = Math.Clamp(CropImage.TranslationX, -maxX, maxX);
        CropImage.TranslationY = Math.Clamp(CropImage.TranslationY, -maxY, maxY);
    }

    #endregion

    #region 手势

    private void OnPinchUpdated(object? sender, PinchGestureUpdatedEventArgs e)
    {
        switch (e.Status)
        {
            case GestureStatus.Started:
                _pinchStartZoom = _zoom;
                break;

            case GestureStatus.Running:
                SetZoom(_pinchStartZoom * e.Scale);
                break;
        }
    }

    private void OnPanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _panStartX = CropImage.TranslationX;
                _panStartY = CropImage.TranslationY;
                break;

            case GestureStatus.Running:
                CropImage.TranslationX = _panStartX + e.TotalX;
                CropImage.TranslationY = _panStartY + e.TotalY;

                var baseScale = _squareSize / Math.Min(_sourceWidth, _sourceHeight);
                var scale = baseScale * _zoom;
                ClampTranslation(_sourceWidth * scale, _sourceHeight * scale);
                break;
        }
    }

    private void OnZoomChanged(object? sender, ValueChangedEventArgs e) => SetZoom(e.NewValue);

    private void SetZoom(double value)
    {
        _zoom = Math.Clamp(value, ZoomSlider.Minimum, ZoomSlider.Maximum);

        // 滑块与双指缩放共用一个值，赋值时注意避免回调互相触发
        if (Math.Abs(ZoomSlider.Value - _zoom) > 0.001) ZoomSlider.Value = _zoom;

        ApplyTransform();
    }

    #endregion

    #region 完成 / 取消

    private async void OnConfirmClicked(object? sender, EventArgs e)
    {
        try
        {
            var baseScale = _squareSize / Math.Min(_sourceWidth, _sourceHeight);
            var pixelsPerSourcePixel = baseScale * _zoom;

            // 取景框中心在图片本地坐标中的位置
            var centerLocalX = CropImage.WidthRequest / 2 - CropImage.TranslationX;
            var centerLocalY = CropImage.HeightRequest / 2 - CropImage.TranslationY;

            // 换算成源图像素
            var cropSize = _squareSize / pixelsPerSourcePixel;
            var cropX = (centerLocalX - _squareSize / 2) / pixelsPerSourcePixel;
            var cropY = (centerLocalY - _squareSize / 2) / pixelsPerSourcePixel;

            var output = ViewModels.UserViewModel.AvatarFilePath;
            var ok = await AvatarImageHelper.CropSquareAsync(_sourcePath, output, cropX, cropY, cropSize);

            if (!ok)
            {
                await DisplayAlert("裁剪失败", "这张图片无法处理，请换一张试试。", "好");
                return;
            }

            _completion.TrySetResult(true);
            await Navigation.PopModalAsync();
        }
        catch (Exception ex)
        {
            AppPaths.LogError("确认裁剪头像", ex);
            await DisplayAlert("裁剪失败", ex.Message, "好");
        }
    }

    private async void OnCancelClicked(object? sender, EventArgs e)
    {
        _completion.TrySetResult(false);
        try { await Navigation.PopModalAsync(); }
        catch { /* 已经是栈底 */ }
    }

    #endregion
}
