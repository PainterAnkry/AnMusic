using AnMusic.Android.Services;
using AnMusic.Android.ViewModels;
using AnMusic.Services;
using AnMusic.Services.Settings;
using Microsoft.Maui.Controls.Shapes;

namespace AnMusic.Android;

/// <summary>
/// 应用外壳：承载左侧边栏（自定义 FlyoutContent）与全部一级页面。
/// 侧边栏的导航高亮、快捷换色由这里统一维护，避免每个页面各写一套。
/// </summary>
public partial class AppShell : Shell
{
    private readonly List<(Border Host, Label Text, string Route)> _navItems = [];
    private readonly UserViewModel? _user;

    public AppShell()
    {
        InitializeComponent();

        _user = MauiProgram.Services?.GetService<UserViewModel>();
        if (_user is not null) BindingContext = _user;

        CollectNavItems();
        BuildAccentBar();

        // 版本号不写死在 XAML 里：升级时忘了同步会误导用户，也影响「检查更新」的判断
        VersionLabel.Text = $"AnMusic {AppInfo.Current.VersionString}";

        // 导航到任意页面后刷新高亮（包含 Flyout 点击、程序内跳转、系统返回）
        Navigated += (_, _) =>
        {
            SafeUpdateActive();

            // 内容页切换过渡：淡入 + 上滑。ShellContent 缓存页面实例，
            // 这里每次导航对当前页做动画即可；模态页（播放详情）有自己的进场动画。
            try
            {
                if (CurrentPage is ContentPage entering)
                    Anim.PageEnter(entering);
            }
            catch
            {
                // 导航过程中的瞬时状态（模态页叠加时 CurrentPage 可能取不到），忽略
            }
        };

        // 打开侧边栏时刷新用户卡与统计，保证数据是最新的
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FlyoutIsPresented) && FlyoutIsPresented)
                _user?.Refresh();
        };

        // 换肤后资源色变了，需要按新主题重刷高亮与色点
        ThemeService.Changed += () =>
        {
            BuildAccentBar();
            SafeUpdateActive();
        };
    }

    #region 侧边栏导航

    private void CollectNavItems()
    {
        Border[] hosts =
        [
            NavMain, NavSearch, NavDiscover, NavStats, NavTogether,
            NavDownloads, NavUser, NavSettings, NavPlugins, NavImport,
        ];

        foreach (var host in hosts)
        {
            if (host.ClassId is not { Length: > 0 } route) continue;
            if (host.Content is not Grid grid) continue;
            if (grid.Children.Count < 2 || grid.Children[1] is not Label text) continue;

            _navItems.Add((host, text, route));
        }
    }

    private async void OnNavTapped(object? sender, TappedEventArgs e)
    {
        var route = e.Parameter as string;
        if (string.IsNullOrEmpty(route)) return;

        FlyoutIsPresented = false;

        try
        {
            await GoToAsync(route);
        }
        catch (Exception ex)
        {
            AppPaths.LogError("侧边栏导航", ex, route);
        }
    }

    private void OnUserCardTapped(object? sender, TappedEventArgs e)
    {
        FlyoutIsPresented = false;
        _ = GoToAsync("//UserPage");
    }

    private void SafeUpdateActive()
    {
        try
        {
            var route = CurrentState?.Location?.OriginalString ?? string.Empty;
            UpdateActive(route);
        }
        catch
        {
            // 模态页（播放详情）导航过程中 CurrentState 可能瞬时不完整，忽略
        }
    }

    /// <summary>把当前路由对应的导航项刷成强调色，其余恢复默认。</summary>
    private void UpdateActive(string route)
    {
        if (string.IsNullOrEmpty(route)) return;

        var active = ThemeService.Get("AmPrimary");
        var activeSoft = ThemeService.Get("AmPrimarySoft");
        var normal = ThemeService.Get("AmTextPrimary");

        foreach (var (host, text, itemRoute) in _navItems)
        {
            var isActive = route.Contains(itemRoute.TrimStart('/'), StringComparison.OrdinalIgnoreCase);

            host.BackgroundColor = isActive ? activeSoft : Colors.Transparent;
            text.TextColor = isActive ? active : normal;
            text.FontAttributes = isActive ? FontAttributes.Bold : FontAttributes.None;
        }
    }

    #endregion

    #region 快捷换色

    /// <summary>在侧边栏底部生成 6 个强调色点，点一下立刻换肤并落盘。</summary>
    private void BuildAccentBar()
    {
        AccentBar.Clear();

        for (var i = 0; i < ThemeService.AccentNames.Count; i++)
        {
            var index = i;
            var selected = ThemeService.CurrentAccentIndex == index;

            var dot = new Border
            {
                WidthRequest = 26,
                HeightRequest = 26,
                Padding = 0,
                StrokeThickness = selected ? 2 : 0,
                Stroke = selected ? ThemeService.Get("AmTextPrimary") : Colors.Transparent,
                StrokeShape = new RoundRectangle { CornerRadius = 13 },
                BackgroundColor = ThemeService.AccentColor(index),
            };

            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => ApplyAccent(index);
            dot.GestureRecognizers.Add(tap);

            AccentBar.Add(dot);
        }
    }

    private void ApplyAccent(int index)
    {
        ThemeService.Apply(ThemeService.CurrentSkinId, index);

        try
        {
            MauiProgram.Services?.GetService<UserSettingsService>()
                ?.Update(s => s.AccentColorIndex = index);
        }
        catch (Exception ex)
        {
            AppPaths.LogError("保存强调色", ex);
        }
    }

    #endregion
}
