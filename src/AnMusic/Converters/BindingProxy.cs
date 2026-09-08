using System.Windows;

namespace AnMusic.Converters;

/// <summary>
/// 绑定代理：把 Window 的 DataContext（MainViewModel）转存为静态资源。
/// ContextMenu / Popup 内容不在窗口视觉树中，RelativeSource AncestorType 绑定会静默失败，
/// 需通过 Source={StaticResource VmProxy} 访问主 ViewModel 的命令。
/// </summary>
public sealed class BindingProxy : Freezable
{
    protected override Freezable CreateInstanceCore() => new BindingProxy();

    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(object), typeof(BindingProxy), new PropertyMetadata(null));

    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }
}
