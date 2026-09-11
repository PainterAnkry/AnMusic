using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AnMusic.Views;

/// <summary>
/// 轻量动画助手：统一的淡入淡出 / 轻微位移过渡，让界面切换更顺滑。
/// 都使用缓出曲线（CubicEase）并保持时长较短（120~200ms），不拖慢操作手感。
/// </summary>
public static class Animations
{
    /// <summary>默认时长。</summary>
    public const int DefaultDurationMs = 170;

    /// <summary>
    /// 淡入（可选轻微上移），用于页面/视图切换。
    /// slideFrom &gt; 0 时元素从下方 slideFrom 像素处淡入上移。
    /// </summary>
    public static void FadeIn(UIElement? element, int milliseconds = DefaultDurationMs,
        double fromOpacity = 0, double slideFrom = 8)
    {
        if (element is null) return;

        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = fromOpacity;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop
        });
        element.Opacity = 1;

        if (slideFrom <= 0) return;

        var transform = EnsureTranslate(element);
        transform.BeginAnimation(TranslateTransform.YProperty, null);
        transform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(slideFrom, 0, TimeSpan.FromMilliseconds(milliseconds))
            {
                EasingFunction = ease,
                FillBehavior = FillBehavior.Stop
            });
        transform.Y = 0;
    }

    /// <summary>淡出到指定透明度（默认 0）。</summary>
    public static void FadeOut(UIElement? element, int milliseconds = 120, double toOpacity = 0)
    {
        if (element is null) return;

        element.BeginAnimation(UIElement.OpacityProperty, null);
        var anim = new DoubleAnimation(toOpacity, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            FillBehavior = FillBehavior.Stop
        };
        element.BeginAnimation(UIElement.OpacityProperty, anim);
        element.Opacity = toOpacity;
    }

    /// <summary>
    /// 元素每次可见时自动淡入（用于设置页/歌词页等按 Visibility 切换的面板）。
    /// 只需要在窗口构造时调用一次。
    /// </summary>
    public static void AutoFadeInOnVisible(UIElement? element, int milliseconds = DefaultDurationMs)
    {
        if (element is null) return;
        element.IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) FadeIn(element, milliseconds);
        };
    }

    /// <summary>内容变化时做一次"轻闪"（从较淡状态过渡到完全显示），用于封面/歌词换行。</summary>
    public static void SwapFade(UIElement? element, double fromOpacity = 0.25, int milliseconds = 220)
    {
        if (element is null) return;

        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(fromOpacity, 1,
            TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        });
        element.Opacity = 1;
    }

    /// <summary>画笔（如封面 ImageBrush）的轻闪过渡：从较淡过渡到完全不透明。</summary>
    public static void SwapFadeBrush(Brush? brush, double fromOpacity = 0.3, int milliseconds = 260)
    {
        if (brush is null) return;

        brush.BeginAnimation(Brush.OpacityProperty, null);
        brush.Opacity = fromOpacity;
        brush.BeginAnimation(Brush.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        });
        brush.Opacity = 1;
    }

    /// <summary>确保元素带有 TranslateTransform（用于位移动画），返回该变换。</summary>
    private static TranslateTransform EnsureTranslate(UIElement element)
    {
        if (element.RenderTransform is TranslateTransform existing) return existing;

        var transform = new TranslateTransform();
        element.RenderTransform = transform;
        return transform;
    }
}
