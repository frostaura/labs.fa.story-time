using CommunityToolkit.Maui;
using Microsoft.Extensions.Logging;
using Plugin.LocalNotification;
using SkiaSharp.Views.Maui.Controls.Hosting;
using StoryTime.App.Graphics;
using StoryTime.App.Services;
using StoryTime.App.Views;
using StoryTime.Core;
using StoryTime.Core.Abstractions;
using StoryTime.Core.Engines;

namespace StoryTime.App;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.UseMauiCommunityToolkit()
			// The required flag is the toolkit's foreground-media-service switch; iOS has no such service.
			.UseMauiCommunityToolkitMediaElement(false)
			.UseSkiaSharp()
			.UseLocalNotification()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("Outfit-Light.ttf", "OutfitLight");
				fonts.AddFont("Outfit-Regular.ttf", "OutfitRegular");
				fonts.AddFont("Outfit-Medium.ttf", "OutfitMedium");
				fonts.AddFont("Quicksand-Regular.ttf", "QuicksandRegular");
				fonts.AddFont("Quicksand-Medium.ttf", "QuicksandMedium");
			});

#if DEBUG
		builder.Logging.AddDebug();
#endif

		// Hearth entries are underline-only — strip the UITextField rounded-rect border so
		// the XAML hairline BoxView is the only decoration.
		Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("HearthBorderless", (handler, _) =>
			handler.PlatformView.BorderStyle = UIKit.UITextBorderStyle.None);

		// ---- Platform implementations FIRST (Core's TryAdd* then fills the gaps) ----
		// Story-change notifications must land on the UI thread: subscribers mutate
		// ObservableCollections bound to live CollectionViews, and the pipeline writes
		// stories from a background task. Core cannot reference MAUI, so the marshalling
		// delegate is injected here (see IStoryCatalog).
		builder.Services.AddSingleton(_ => new StoryTime.Core.Services.StoryCatalog
		{
			Dispatcher = action => MainThread.BeginInvokeOnMainThread(action),
		});
		builder.Services.AddSingleton<IStoryCatalog>(sp =>
			sp.GetRequiredService<StoryTime.Core.Services.StoryCatalog>());
		builder.Services.AddSingleton<ISettingsStore, PreferencesSettingsStore>();
		builder.Services.AddSingleton<INavigator, ShellNavigator>();
		builder.Services.AddSingleton<IBiometricGate, BiometricGate>();
		builder.Services.AddSingleton<IHaptics, PlatformHaptics>();
		builder.Services.AddSingleton<StoryTime.Core.Abstractions.INotificationService, LocalNotificationService>();
		builder.Services.AddSingleton<IAudioEncoder, PassThroughAudioEncoder>();
		builder.Services.AddSingleton<IPosterRasterizer, SkiaPosterRasterizer>();
		// ---- Engines: the two swap points for the Apple on-device lanes ----
		RegisterStoryEngine(builder.Services);
		RegisterTtsEngine(builder.Services);
		builder.Services.AddSingleton<IAmbientPlayer, AmbientPlayer>();          // Premium music bed (loops under narration)
		builder.Services.AddSingleton<IVoiceSamplePlayer, VoiceSamplePlayer>();  // narrator preview one-shots
		builder.Services.AddSingleton<MediaElementHost>();
		builder.Services.AddSingleton<MediaElementNarrationPlayer>();
		builder.Services.AddSingleton<LiveTtsNarrationPlayer>();
		builder.Services.AddSingleton<INarrationPlayerProvider, NarrationPlayerProvider>();
		// ---- Background weave + loom hum (2026-07-31) ----
		// The anchor is what keeps a weave alive when the app leaves the foreground; the hum
		// is the audible ambient loop that plays while it weaves (and, on iOS, the active
		// playback session that legitimately keeps the pipeline scheduled in the background).
		builder.Services.AddSingleton<IWeaveBackgroundAnchor, Platforms.iOS.WeaveBackgroundCoordinator>();
		// One cached loop per palette family in AppData — repeat weaves start instantly.
		builder.Services.AddSingleton<StoryTime.Core.Audio.LoomHumTrackProvider>();
		builder.Services.AddSingleton<LoomHumService>();
		builder.Services.AddSingleton<IWeaveHost, WeaveHostService>();
		// Weave sheet coordinator (app-foreground animation gate; the sheet state itself
		// is the singleton WeaveSheetViewModel registered by Core).
		builder.Services.AddSingleton<WeaveSheetController>();

		// ---- Core services, engines, view-models ----
		builder.Services.AddStoryTimeCore(new StoryTimeOptions
		{
			DatabasePath = Path.Combine(FileSystem.AppDataDirectory, "storytime.db3"),
			MediaDirectory = Path.Combine(FileSystem.AppDataDirectory, "media"),
		});

		// ---- Pages ----
		builder.Services.AddTransient<HomePage>();
		builder.Services.AddTransient<OnboardingProfilePage>();
		builder.Services.AddTransient<OnboardingVoicePage>();
		builder.Services.AddTransient<OnboardingRulesPage>();
		builder.Services.AddTransient<PlayerPage>();
		builder.Services.AddTransient<ShelfPage>();
		builder.Services.AddTransient<SettingsPage>();
		builder.Services.AddTransient<LibraryPage>();
		builder.Services.AddTransient<PremiumPage>();
		builder.Services.AddTransient<ContentFiltersPage>();
		Routing.RegisterRoute(StoryTime.Core.ViewModels.Routes.Premium, typeof(PremiumPage));
		Routing.RegisterRoute(StoryTime.Core.ViewModels.Routes.ContentFilters, typeof(ContentFiltersPage));

		return builder.Build();
	}

	/// <summary>Story-engine swap point. Today: the deterministic TemplateStoryEngine, so the
	/// app runs end-to-end with no model and no network. The Apple-models lane replaces this
	/// body with <c>AppleFoundationStoryEngine</c> (Foundation Models via the Swift shim,
	/// iOS 26+), keeping TemplateStoryEngine as its fallback when the system model is
	/// unavailable (Apple Intelligence off, device ineligible, model not yet downloaded).
	/// Prompt text for it lives in Core/Engines/Prompts/StoryPrompts.cs.</summary>
	static void RegisterStoryEngine(IServiceCollection services) =>
		services.AddSingleton<IStoryEngine>(_ => new TemplateStoryEngine(() => Random.Shared.Next()));

	/// <summary>Narration-engine swap point: <c>AppleSpeechTtsEngine</c> — SoothingNarrator
	/// (AVSpeechSynthesizer.WriteUtterance per utterance → PCM → Core Engines/Narration: measured
	/// pause map, mastering, −18 LUFS, pad → 48 kHz WAV). PlatformTtsEngine stays registered as
	/// its fallback and for LiveTtsNarrationPlayer, which resolves it concretely.</summary>
	static void RegisterTtsEngine(IServiceCollection services)
	{
		services.AddSingleton<PlatformTtsEngine>();
		services.AddSingleton<Platforms.iOS.Narration.AvSpeechUtteranceRenderer>();
		services.AddSingleton<IStorytellerVoices, Platforms.iOS.Narration.AppleStorytellerVoices>();
		services.AddSingleton<INetworkStatus, Platforms.iOS.Narration.NetworkPathStatus>();
		services.AddSingleton<AppleSpeechTtsEngine>();
		services.AddSingleton<ITtsEngine>(sp => sp.GetRequiredService<AppleSpeechTtsEngine>());
		// The mandatory storyteller-voice step (Serena Premium) that onboarding and launch route to.
		services.AddTransient<StorytellerVoicePage>();
		Routing.RegisterRoute(StoryTime.Core.ViewModels.Routes.StorytellerVoice, typeof(StorytellerVoicePage));
	}
}
