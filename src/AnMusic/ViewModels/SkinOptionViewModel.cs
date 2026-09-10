using System.Windows.Media;
using AnMusic.Services.Settings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AnMusic.ViewModels;

/// <summary>
/// 皮肤面板里的一格：显示该皮肤的底色 + 强调色圆点 + 名称，点击即切换。
/// </summary>
public partial class SkinOptionViewModel : ObservableObject
{
    public SkinOptionViewModel(Skin skin)
    {
        Id = skin.Id;
        Name = skin.Name;
        PreviewBackground = ToBrush(skin.BgHex);
        PreviewAccent = ToBrush(skin.AccentHex);
        _isSelected = string.Equals(skin.Id, ThemeService.CurrentSkinId, StringComparison.OrdinalIgnoreCase);
    }

    public string Id { get; }

    public string Name { get; }

    /// <summary>皮肤底色预览。</summary>
    public Brush PreviewBackground { get; }

    /// <summary>皮肤强调色预览。</summary>
    public Brush PreviewAccent { get; }

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>按当前主题刷新选中态。</summary>
    public void RefreshSelection()
        => IsSelected = string.Equals(Id, ThemeService.CurrentSkinId, StringComparison.OrdinalIgnoreCase);

    /// <summary>#RRGGBB → 画刷；解析失败回落到透明（不影响使用）。</summary>
    private static Brush ToBrush(string hex)
    {
        try
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }
        catch
        {
            return Brushes.Transparent;
        }
    }
}
