using StoryTime.App.Views;
using StoryTime.Core.ViewModels;

namespace StoryTime.App;

public partial class AppShell : Shell
{
	public AppShell()
	{
		InitializeComponent();

		// Push routes (flat - none starts with a tab/root route name).
		Routing.RegisterRoute(Routes.OnboardingProfile, typeof(OnboardingProfilePage));
		Routing.RegisterRoute(Routes.OnboardingVoice, typeof(OnboardingVoicePage));
		Routing.RegisterRoute(Routes.OnboardingRules, typeof(OnboardingRulesPage));
		// No Weaving route any more: generation lives in the weave sheet overlay
		// (WeaveSheetHost on every non-modal page). Routes.Weaving survives only as the
		// QA cold-start hook, intercepted in App.OnShellReadyAsync.
		Routing.RegisterRoute(Routes.Player, typeof(PlayerPage));       // modal (page sets PresentationMode)
		Routing.RegisterRoute(Routes.Shelf, typeof(ShelfPage));         // modal (kid lock)
		Routing.RegisterRoute(Routes.Settings, typeof(SettingsPage));
		Routing.RegisterRoute(Routes.Library, typeof(LibraryPage));
	}
}
