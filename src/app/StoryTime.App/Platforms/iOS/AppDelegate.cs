using Foundation;
using StoryTime.App.Platforms.iOS;
using UIKit;

namespace StoryTime.App;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

	public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
	{
		// BGTaskScheduler requires EVERY permitted identifier to be registered before the app
		// finishes launching — registering later throws. This is the opportunistic weave
		// resume path (see WeaveBackgroundCoordinator); registration is cheap and harmless
		// when no checkpoint exists.
		WeaveBackgroundCoordinator.RegisterResumeTask();
		return base.FinishedLaunching(application, launchOptions);
	}
}
