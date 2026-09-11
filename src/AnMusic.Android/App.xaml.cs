using AnMusic.Android.Services;
using AnMusic.Services.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace AnMusic.Android;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();

		// 在窗口创建前把用户上次选的皮肤刷进资源字典，
		// 这样首屏就是正确配色，不会先闪一下默认浅色再跳变。
		ApplySavedTheme();
	}

	private static void ApplySavedTheme()
	{
		try
		{
			// 服务容器在 CreateMauiApp 末尾已就绪；万一取不到就地读一次配置文件兜底
			var settings = MauiProgram.Services?.GetService<UserSettingsService>()
			               ?? new UserSettingsService();

			ThemeService.Apply(settings.Settings.Theme, settings.Settings.AccentColorIndex);
		}
		catch
		{
			ThemeService.Apply("Light", 0);
		}
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new AppShell());
	}
}
