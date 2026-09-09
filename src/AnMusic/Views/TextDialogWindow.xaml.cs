using System.Windows;

namespace AnMusic.Views;

/// <summary>
/// 通用文本查看弹窗（用户协议、免责声明等长文本的点击查看）。
/// </summary>
public partial class TextDialogWindow : Window
{
    public TextDialogWindow(string title, string content)
    {
        InitializeComponent();
        Title = title;
        ContentText.Text = content;
    }

    /// <summary>打开文本查看弹窗的辅助入口。</summary>
    public static void Show(string owner, string title, string content)
    {
        var win = new TextDialogWindow(title, content)
        {
            Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.Title == owner)
                    ?? Application.Current.MainWindow
        };
        win.ShowDialog();
    }
}
