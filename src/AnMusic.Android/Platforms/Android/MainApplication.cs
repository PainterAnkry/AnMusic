using Android.App;
using Android.Runtime;
using AnMusic.Android.Services;

namespace AnMusic.Android;

[Application]
public class MainApplication : MauiApplication
{
	public MainApplication(IntPtr handle, JniHandleOwnership ownership)
		: base(handle, ownership)
	{
	}

	/// <summary>
	/// 崩溃捕获必须赶在 MAUI 初始化（base.OnCreate → CreateMauiApp → AppShell）之前，
	/// 否则启动路径上的任何异常都只能表现为"闪退"，没有任何现场可查。
	/// FilesDir 此时已可用，且与 AppPaths.DataRoot 同基（…/files/AnMusic/crashes）。
	/// </summary>
	public override void OnCreate()
	{
		CrashReporter.Install(System.IO.Path.Combine(
			FilesDir?.AbsolutePath ?? string.Empty, "AnMusic", "crashes"));
		base.OnCreate();
	}

	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
