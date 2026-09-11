namespace AnMusic.Android.Services;

/// <summary>
/// 轻量过渡动画：页面进入与视图切换共用同一套节奏（200~240ms、SinOut），
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
}
