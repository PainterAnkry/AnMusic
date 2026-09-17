using System.Runtime.CompilerServices;
using Microsoft.Maui.Controls;

namespace AnMusic.Android.Services;

/// <summary>
/// 统一的按压反馈：给任意控件挂上 <c>PressFeedback.IsEnabled="True"</c>，
/// 按下时缩到 0.96 + 轻微透明，松开/取消时在 ~130ms 内回弹（SinOut）。
/// 用 PointerGestureRecognizer 实现，不干扰元素自身的 TapGestureRecognizer / Command。
/// 所有异常吞掉——反馈动画绝不能影响业务点击。
/// </summary>
public static class PressFeedback
{
    public static readonly BindableProperty IsEnabledProperty =
        BindableProperty.CreateAttached(
            "IsEnabled",
            typeof(bool),
            typeof(PressFeedback),
            false,
            propertyChanged: OnChanged);

    public static bool GetIsEnabled(BindableObject view) => (bool)view.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(BindableObject view, bool value) => view.SetValue(IsEnabledProperty, value);

    /// <summary>按压时的缩放比例（默认 0.96）。</summary>
    public static readonly BindableProperty ScaleProperty =
        BindableProperty.CreateAttached("Scale", typeof(double), typeof(PressFeedback), 0.96);

    public static double GetScale(BindableObject view) => (double)view.GetValue(ScaleProperty);
    public static void SetScale(BindableObject view, double value) => view.SetValue(ScaleProperty, value);

    /// <summary>每个挂载元素对应的识别器，关闭附加属性时用于摘除。</summary>
    private static readonly ConditionalWeakTable<View, PointerGestureRecognizer> _recognizers = new();
    private static readonly ConditionalWeakTable<View, StrongBox<bool>> _pressed = new();

    private static void OnChanged(BindableObject bindable, object oldValue, object newValue)
    {
        // GestureRecognizers 挂在 View 上（Border / Grid / 常用交互容器都是 View）
        if (bindable is not View element) return;

        if (newValue is true)
        {
            if (_recognizers.TryGetValue(element, out _)) return;

            var recognizer = new PointerGestureRecognizer();
            recognizer.PointerPressed += (_, _) => Press(element);
            recognizer.PointerReleased += (_, _) => Release(element);
            recognizer.PointerExited += (_, _) => Release(element);
            element.GestureRecognizers.Add(recognizer);
            _recognizers.AddOrUpdate(element, recognizer);
        }
        else if (_recognizers.TryGetValue(element, out var existing))
        {
            element.GestureRecognizers.Remove(existing);
            _recognizers.Remove(element);
        }
    }

    private static void Press(View v)
    {
        try
        {
            var flag = _pressed.GetValue(v, _ => new StrongBox<bool>(false));
            if (flag.Value || !v.IsEnabled) return;
            flag.Value = true;
            v.CancelAnimations();
            v.ScaleToAsync(GetScale(v), 85, Easing.SinOut);
            v.FadeToAsync(0.82, 85, Easing.SinOut);
        }
        catch { }
    }

    private static void Release(View v)
    {
        try
        {
            if (_pressed.TryGetValue(v, out var flag)) flag.Value = false;
            v.CancelAnimations();
            v.ScaleToAsync(1, 130, Easing.SinOut);
            v.FadeToAsync(1, 130, Easing.SinOut);
        }
        catch { }
    }
}
