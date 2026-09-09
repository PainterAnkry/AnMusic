using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AnMusic.Views;

/// <summary>
/// 头像裁剪窗口：加载图片后拖动鼠标框选头像区域，确定后按"所见即所得"生成 256×256 居中圆形 PNG。
/// 关键点：
/// 1. 框选范围被限制在实际图像内容矩形内（自动剔除两侧留白），框什么就裁什么，不会因选区越界产生偏移；
/// 2. 画布坐标 → 图像像素按同一显示矩形换算（DPI 感知），与 Image 控件实际渲染完全一致；
/// 3. 输出圆形以画布中心居中、按正方形等比适配，各显示端只需把方形 PNG 居中套入圆形框即可。
/// </summary>
public partial class AvatarCropWindow : Window
{
    /// <summary>最小有效框选边长（DIP）。</summary>
    private const double MinSelectionSide = 8;

    /// <summary>输出 PNG 边长（像素）。</summary>
    private const int OutputSize = 256;

    private readonly BitmapSource? _source;
    private Rect _imageRect; // 图像内容在 CropCanvas 坐标中的实际显示矩形（与 Image 渲染一致）
    private Point? _dragStart;

    /// <summary>裁剪结果文件路径（确定后有效）。</summary>
    public string? CroppedImagePath { get; private set; }

    private static readonly string OutputDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AnMusic");

    public AvatarCropWindow(string imagePath)
    {
        InitializeComponent();

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(imagePath);
            bmp.EndInit();
            bmp.Freeze();
            _source = bmp;
            CropImage.Source = bmp;
        }
        catch (Exception ex)
        {
            _source = null;
            MessageBox.Show($"图片加载失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        // 布局就绪后计算图像显示矩形；窗口大小固定（NoResize），仅首次计算即可
        Loaded += (_, _) => UpdateImageRect();
    }

    /// <summary>按 DPI 换算图像在画布区域内的实际显示矩形（与 Stretch=Uniform 的 Image 渲染位置一致）。</summary>
    private void UpdateImageRect()
    {
        if (_source is null) return;
        var cw = CropCanvas.ActualWidth;
        var ch = CropCanvas.ActualHeight;
        if (cw <= 0 || ch <= 0) return;

        // 图像在 WPF 中的固有 DIP 尺寸 = 像素 × 96 / DPI
        double iw = _source.PixelWidth * 96.0 / Math.Max(1.0, _source.DpiX);
        double ih = _source.PixelHeight * 96.0 / Math.Max(1.0, _source.DpiY);
        var scale = Math.Min(cw / iw, ch / ih);
        var w = iw * scale;
        var h = ih * scale;
        _imageRect = new Rect((cw - w) / 2, (ch - h) / 2, w, h);
    }

    private void CropCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_source is null || _imageRect.Width <= 0) return;

        var p = e.GetPosition(CropCanvas);
        if (!_imageRect.Contains(p)) return; // 仅允许在图片内容内开始框选

        _dragStart = p;
        Canvas.SetLeft(SelRect, p.X);
        Canvas.SetTop(SelRect, p.Y);
        SelRect.Width = 0;
        SelRect.Height = 0;
        SelRect.Visibility = Visibility.Visible;
        CropCanvas.CaptureMouse();
    }

    private void CropCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || _source is null) return;

        var pos = e.GetPosition(CropCanvas);
        // 强制正方形选区（圆形头像），边长取拖动位移较小者
        var side = Math.Min(Math.Abs(pos.X - start.X), Math.Abs(pos.Y - start.Y));
        var left = pos.X < start.X ? start.X - side : start.X;
        var top = pos.Y < start.Y ? start.Y - side : start.Y;

        // 选区整体平移进图像内容矩形，并限制边长不超过图像范围，避免裁出留白/偏移
        if (left + side > _imageRect.Right) left = _imageRect.Right - side;
        if (top + side > _imageRect.Bottom) top = _imageRect.Bottom - side;
        if (left < _imageRect.Left) left = _imageRect.Left;
        if (top < _imageRect.Top) top = _imageRect.Top;
        side = Math.Min(side, _imageRect.Width);
        side = Math.Min(side, _imageRect.Height);

        Canvas.SetLeft(SelRect, Math.Max(left, _imageRect.Left));
        Canvas.SetTop(SelRect, Math.Max(top, _imageRect.Top));
        SelRect.Width = side;
        SelRect.Height = side;
    }

    private void CropCanvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragStart = null;
        CropCanvas.ReleaseMouseCapture();
    }

    /// <summary>重新框选：清除当前选区。</summary>
    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        SelRect.Visibility = Visibility.Collapsed;
        SelRect.Width = 0;
        SelRect.Height = 0;
    }

    /// <summary>把框选区域裁剪为居中圆形 PNG 并关闭窗口。</summary>
    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_source is null) return;

        if (SelRect.Visibility != Visibility.Visible || SelRect.Width < MinSelectionSide ||
            _imageRect.Width <= 0 || _imageRect.Height <= 0)
        {
            MessageBox.Show("请先拖动鼠标框选头像区域", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 画布坐标 → 图像像素坐标（同一显示矩形换算，显示与像素一一对应）
        var kx = _source.PixelWidth / _imageRect.Width;
        var ky = _source.PixelHeight / _imageRect.Height;
        var ix = (Canvas.GetLeft(SelRect) - _imageRect.Left) * kx;
        var iy = (Canvas.GetTop(SelRect) - _imageRect.Top) * ky;
        // 选区是正方形；源像素横纵比例不一致时取较小换算系数保证圆形不变形
        var sidePx = (int)Math.Round(SelRect.Width * Math.Min(kx, ky));
        sidePx = Math.Clamp(sidePx, 1, Math.Min(_source.PixelWidth, _source.PixelHeight));
        var px = (int)Math.Round(Math.Clamp(ix, 0, _source.PixelWidth - sidePx));
        var py = (int)Math.Round(Math.Clamp(iy, 0, _source.PixelHeight - sidePx));

        try
        {
            var cropped = new CroppedBitmap(_source, new Int32Rect(px, py, sidePx, sidePx));

            // 输出 OutputSize×OutputSize 方形 PNG：圆形内容以画布中心居中（四角透明），
            // 留 1px 边距容纳抗锯齿，圆周正好落在画布中心
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var brush = new ImageBrush(cropped)
                {
                    Stretch = Stretch.Fill
                };
                var circle = new EllipseGeometry(new Rect(0, 0, OutputSize, OutputSize));
                dc.PushClip(circle);
                dc.DrawRectangle(brush, null, new Rect(0, 0, OutputSize, OutputSize));
            }

            var rtb = new RenderTargetBitmap(OutputSize, OutputSize, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));

            Directory.CreateDirectory(OutputDir);
            var path = Path.Combine(OutputDir, $"avatar_{DateTime.Now:yyyyMMddHHmmss}.png");
            using var fs = File.Create(path);
            encoder.Save(fs);

            CroppedImagePath = path;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"裁剪失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
