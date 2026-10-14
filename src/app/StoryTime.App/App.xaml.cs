using StoryTime.Core.Abstractions;

namespace StoryTime.App;

public partial class App : Application
{
	private readonly IServiceProvider _services;
	private bool _startupRan;

	public App(IServiceProvider services)
	{
		_services = services;
		InitializeComponent();
		UserAppTheme = AppTheme.Dark; // Hearth is dark-only
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var shell = new AppShell();
		// Run startup AFTER the Shell has actually loaded — navigating from Window.Created
		// (before the platform view attaches) leaves Shell's stack in a state where every
		// later pop/absolute navigation deadlocks (observed on Android, MAUI 10.0.60).
		shell.Loaded += async (_, _) => await OnShellReadyAsync();

		var window = new Window(shell);
		// Foreground gate for the weave sheet's animations (ember pulse, shimmer):
		// backgrounded app = no animation loops (battery care).
		var sheetController = _services.GetRequiredService<Services.WeaveSheetController>();
		window.Stopped += (_, _) => sheetController.OnAppBackgrounded();
		window.Resumed += (_, _) => sheetController.OnAppForegrounded();
		return window;
	}

	private async Task OnShellReadyAsync()
	{
		if (_startupRan) return;
		_startupRan = true;
		try
		{
			var store = _services.GetRequiredService<IStoryStore>();
			await store.InitializeAsync();

			// Platform copy split for the weave sheet (iOS: suspend/resume wording).
			var weaveSheet = _services.GetRequiredService<StoryTime.Core.ViewModels.WeaveSheetViewModel>();
			weaveSheet.IsIos = DeviceInfo.Platform == DevicePlatform.iOS;

			// Resume a checkpointed weave from a previous run, if any (the sheet's
			// mini-bar auto-surfaces from the first progress event).
			var weaveHost = _services.GetRequiredService<IWeaveHost>();
			await weaveHost.TryResumeAsync();

			// QA hook: start a REAL weave at cold start (STORYTIME_QA_WEAVE=1;
			// SIMCTL_CHILD_ prefix on the iOS sim). It exists because iOS simulator tap
			// automation is TCC-blocked, so the background-weave path has no other way to
			// be exercised there. Never fires without the env var.
			if (Environment.GetEnvironmentVariable("STORYTIME_QA_WEAVE") == "1" && !weaveHost.IsWeaving)
			{
				var qaProfile = await store.GetActiveProfileAsync();
				if (qaProfile is not null) await weaveHost.StartAsync(qaProfile);
			}

			var settings = _services.GetRequiredService<ISettingsStore>();
			if (!settings.OnboardingComplete)
			{
				await Shell.Current.GoToAsync(StoryTime.Core.ViewModels.Routes.OnboardingProfile);
				return;
			}

			// QA hook: cold-start any route (STORYTIME_QA_ROUTE; SIMCTL_CHILD_ prefix on iOS sim).
			// "weaving" is special since the page became the weave sheet: stay on Home
			// and open the sheet expanded instead of navigating. "weaving-collapsed"
			// docks the mini-bar instead (iOS sim QA is screenshot-only — TCC).
			var qaRoute = Environment.GetEnvironmentVariable("STORYTIME_QA_ROUTE");
			if (qaRoute == StoryTime.Core.ViewModels.Routes.Weaving)
				await weaveSheet.ExpandForQaAsync();
			else if (qaRoute == "weaving-collapsed")
			{
				await weaveSheet.ExpandForQaAsync();
				weaveSheet.State = StoryTime.Core.ViewModels.WeaveSheetState.Collapsed;
			}
			else if (!string.IsNullOrWhiteSpace(qaRoute))
				await Shell.Current.GoToAsync(qaRoute);
		}
		catch (Exception)
		{
			// Startup navigation must never take the app down; home remains usable.
		}
	}
}
