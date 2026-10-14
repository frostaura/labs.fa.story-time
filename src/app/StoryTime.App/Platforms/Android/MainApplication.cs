using Android.App;
using Android.Runtime;

namespace StoryTime.App;

// Debug builds must reach the plain-HTTP LAN model server (scripts/serve-models.sh,
// 10.0.2.2:8787) for the sole sanctioned Debug model bootstrap — Android blocks cleartext
// HTTP by default, which silently failed the bootstrap (fell back to the template engine).
// Release keeps the platform default (no cleartext): the app is offline-only there.
#if DEBUG
[Application(UsesCleartextTraffic = true)]
#else
[Application]
#endif
public class MainApplication : MauiApplication
{
	public MainApplication(IntPtr handle, JniHandleOwnership ownership)
		: base(handle, ownership)
	{
	}

	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
