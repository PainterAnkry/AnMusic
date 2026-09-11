using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AnMusic.Views.Controls;

/// <summary>
/// 轻量加载指示器（旋转弧线）：仅在自己可见时转动，隐藏时停止动画，不占 CPU。
/// 用法：&lt;controls:LoadingSpinner Width="28" Height="28"/&gt;
/// </summary>
public partial class LoadingSpinner : UserControl
{
    private readonly RotateTransform _rotation = new();

    public LoadingSpinner()
    {
        InitializeComponent();

        // 直接在代码里持有变换并做属性级动画：比 Storyboard 指定目标更稳，也便于停止
        ArcHost.RenderTransform = _rotation;

        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) StartSpin();
            else StopSpin();
        };
        Unloaded += (_, _) => StopSpin();
    }

    /// <summary>当前旋转角度（测试/诊断用）。</summary>
    public double CurrentAngle => _rotation.Angle;

    private void StartSpin()
    {
        var animation = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        _rotation.BeginAnimation(RotateTransform.AngleProperty, animation);
    }

    private void StopSpin()
        => _rotation.BeginAnimation(RotateTransform.AngleProperty, null);
}
