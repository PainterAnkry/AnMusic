namespace AnMusic.Android.Services;

/// <summary>
/// 轻量过渡动画：页面进入与视图切换共用同一套节奏（200~280ms、SinOut），
/// 保证全局手感一致。所有方法内部吞异常——动画是锦上添花，绝不能带崩页面。
/// </summary>
public static class Anim
{
    /// <summary>Shell 内容页进入：淡入 + 自底向上 24px 滑入。</summary>
    public static void PageEnter(Page page)
    {
        try
        {
            page.Opacity = 0;
            page.TranslationY = 24;
            page.FadeTo(1, 240, Easing.SinOut);
            page.TranslateTo(0, 0, 240, Easing.SinOut);
        }
        catch { }
    }

    /// <summary>播放页等模态进入：自屏幕底部上滑 + 淡入（280ms）。</summary>
    public static void ModalEnter(Page page)
    {
        try
        {
            page.Opacity = 0;
            page.TranslationY = 240;
            page.FadeTo(1, 280, Easing.SinOut);
            page.TranslateTo(0, 0, 300, Easing.CubicOut);
        }
        catch { }
    }

    /// <summary>模态退出：下滑 + 淡出，完成后再真正 Pop，返回值表示动画已正常执行。</summary>
    public static async Task ModalExitAsync(Page page)
    {
        try
        {
            await Task.WhenAll(
                page.FadeTo(0, 220, Easing.SinIn),
                page.TranslateTo(0, 240, 240, Easing.CubicIn));
        }
        catch { }
    }

    /// <summary>播放页封面/歌词/队列三视图切换：淡入 + 16px 滑入。</summary>
    public static void ViewIn(VisualElement view)
    {
        try
        {
            view.Opacity = 0;
            view.TranslationY = 16;
            view.FadeTo(1, 200, Easing.SinOut);
            view.TranslateTo(0, 0, 200, Easing.SinOut);
        }
        catch { }
    }

    /// <summary>左右方向的视图切入（如封面 ↔ 歌词横向切换备选）。</summary>
    public static void ViewInHorizontal(VisualElement view, bool fromRight = true)
    {
        try
        {
            view.Opacity = 0;
            view.TranslationX = fromRight ? 28 : -28;
            view.FadeTo(1, 220, Easing.SinOut);
            view.TranslateTo(0, 0, 220, Easing.SinOut);
        }
        catch { }
    }
}
