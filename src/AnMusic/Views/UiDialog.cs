using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;

namespace AnMusic.Views;

/// <summary>
/// 主题化弹窗（替代原生 MessageBox）：深色主题下不再弹出刺眼的白框，
/// 统一使用 BgPanel/FgPrimary/BtnStyle 等动态主题资源，随主题/强调色联动。
/// </summary>
public static class UiDialog
{
    /// <summary>弹窗类型：决定标题图标与左侧色条。</summary>
    public enum Kind
    {
        Info,
        Warn,
        Error,
        Question
    }

    public static void Info(string message, string title = "提示") => Show(message, title, Kind.Info);
    public static void Warn(string message, string title = "提示") => Show(message, title, Kind.Warn);
    public static void Error(string message, string title = "错误") => Show(message, title, Kind.Error);

    /// <summary>
    /// 错误弹窗（友好版）：把技术性异常翻译成用户能懂的一句话，完整异常写入 error.log。
    /// 调用方只需给"发生了什么"（如"加载失败"），不必再拼接 ex.Message。
    /// </summary>
    public static void Error(string what, Exception ex, string title = "错误")
    {
        Services.AppPaths.LogError(what, ex);
        Show(what + Environment.NewLine + Environment.NewLine + Describe(ex), title, Kind.Error);
    }

    /// <summary>把常见异常翻译成一句人话。</summary>
    public static string Describe(Exception ex) => ex switch
    {
        TaskCanceledException or TimeoutException => "网络请求超时，请检查网络连接或代理设置后重试。",
        System.Net.Http.HttpRequestException => "网络连接失败，请检查网络或代理设置后重试。",
        System.Net.Sockets.SocketException => "无法连接到服务器，请检查网络连接。",
        FileNotFoundException or DirectoryNotFoundException => "文件不存在或已被移动。",
        UnauthorizedAccessException => "没有访问权限，请换一个目录或检查文件权限。",
        IOException => "文件读写失败，可能被其他程序占用。",
        UriFormatException => "地址格式不正确。",
        // 应用内自己抛出的异常通常已是中文说明，直接展示
        InvalidOperationException or NotSupportedException or ArgumentException => ex.Message,
        _ => "发生了未知错误，详情已记录到日志文件（设置 → 关于 可查看数据目录）。"
    };
    public static void Question(string message, string title = "确认") => Show(message, title, Kind.Question);

    public static void Show(string message, string title, Kind kind = Kind.Info) => ShowCore(message, title, kind);

    private static void ShowCore(string message, string title, Kind kind)
    {
        var app = Application.Current;
        Window? owner = app?.MainWindow is { IsVisible: true } mw ? mw : null;

        var win = new Window
        {
            Title = title,
            Width = 430,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner,
            Owner = owner,
            Background = GetBrush(app, "BgPanel", Colors.White)
        };

        // 类型色（Info 用主题强调色；Warn/Error 用语义色，深浅主题都醒目）
        var color = kind switch
        {
            Kind.Warn => Color.FromRgb(0xE6, 0xA2, 0x3C),
            Kind.Error => Color.FromRgb(0xE5, 0x48, 0x4D),
            Kind.Question => Color.FromRgb(0x3B, 0x82, 0xF6),
            _ => GetAccent(app) ?? Color.FromRgb(0x3B, 0x82, 0xF6)
        };
        var iconText = kind switch
        {
            Kind.Warn => "⚠",
            Kind.Error => "✕",
            Kind.Question => "？",
            _ => "ℹ"
        };

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        layout.Children.Add(new Border
        {
            Background = new SolidColorBrush(color)
        });

        var body = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
        Grid.SetColumn(body, 1);
        layout.Children.Add(body);

        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(new TextBlock
        {
            Text = iconText,
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(color),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });
        header.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = GetBrush(app, "FgPrimary", Colors.Black),
            VerticalAlignment = VerticalAlignment.Center
        });
        body.Children.Add(header);

        body.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            LineHeight = 21,
            Foreground = GetBrush(app, "FgNormal", Colors.Black),
            Margin = new Thickness(0, 0, 0, 16)
        });

        var ok = new Button
        {
            Content = "知道了",
            IsDefault = true,
            IsCancel = true,
            MinWidth = 88,
            Padding = new Thickness(16, 6, 16, 6),
            Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        if (app?.MainWindow?.TryFindResource("BtnStyle") is Style style) ok.Style = style;
        ok.Click += (_, _) => win.Close();
        body.Children.Add(ok);

        win.Content = layout;
        win.ShowDialog();
    }

    private static Color? GetAccent(Application? app)
    {
        try
        {
            if (app?.TryFindResource("Accent") is SolidColorBrush b) return b.Color;
        }
        catch { }
        return null;
    }

    private static Brush GetBrush(Application? app, string key, Color fallback)
    {
        try
        {
            if (app?.TryFindResource(key) is Brush b) return b;
        }
        catch { }
        return new SolidColorBrush(fallback);
    }
}
