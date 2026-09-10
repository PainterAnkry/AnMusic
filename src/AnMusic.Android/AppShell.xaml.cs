namespace AnMusic.Android;

public partial class AppShell : Shell
{
	public AppShell()
	{
		InitializeComponent();

		// 二级页面路由（预留：后续「发现」「云村」「电台」「榜单」等页面在此登记）
		Routing.RegisterRoute(nameof(SettingsPage), typeof(SettingsPage));
		Routing.RegisterRoute(nameof(SearchPage), typeof(SearchPage));
	}
}
