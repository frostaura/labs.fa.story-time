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
using StoryTime.Core.Engines.AppleFoundation;
using StoryTime.Core.Services;

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

	/// <summary>Story-engine swap point: Apple's on-device Foundation Models through the
	/// StoryTimeNative Swift shim (AppleFoundationStoryEngine), with the deterministic
	/// TemplateStoryEngine as the fallback. A device that can never run the model
	/// (deviceNotEligible) gets the template engine outright. Every other state registers the
	/// Apple engine, because it re-checks availability per story and renders the whole story
	/// from templates while the model is off, downloading or unsupported — so turning Apple
	/// Intelligence on takes effect on the next story without a relaunch. The status service
	/// gives the UI its plain-words guidance. Prompt text: Core/Engines/Prompts and
	/// Core/Engines/AppleFoundation.</summary>
	static void RegisterStoryEngine(IServiceCollection services)
	{
		var bridge = new Platforms.iOS.AppleFoundationModelsBridge();
		services.AddSingleton<IFoundationModelsBridge>(bridge);
		services.AddSingleton<IStoryEngineStatus>(_ => new StoryEngineStatus(bridge.GetAvailability));
		services.AddSingleton<IStoryEngine>(sp =>
		{
			var availability = bridge.GetAvailability();
			if (availability.Reason == FmUnavailableReason.DeviceNotEligible)
				return new TemplateStoryEngine(() => Random.Shared.Next());
			if (availability.IsAvailable) bridge.Prewarm("outline");
			return new AppleFoundationStoryEngine(bridge, sp.GetRequiredService<StoryTime.Core.Pipeline.SafetyFilter>(),
				logger: sp.GetService<ILogger<AppleFoundationStoryEngine>>());
		});
	}

	/// <summary>Narration-engine swap point. Today: PlatformTtsEngine (MAUI TextToSpeech, live
	/// playback, no rendered file). The narration lane replaces this body with
	/// <c>AppleSpeechTtsEngine</c> (AVSpeechSynthesizer.WriteUtterance → PCM → Core
	/// Engines/Narration: ProsodyDirector gaps + AudioShaper cozy mastering → WAV), keeping
	/// PlatformTtsEngine registered as its fallback — LiveTtsNarrationPlayer resolves the
	/// concrete PlatformTtsEngine for live-mode playback.</summary>
	static void RegisterTtsEngine(IServiceCollection services)
	{
		services.AddSingleton<PlatformTtsEngine>();
		services.AddSingleton<ITtsEngine>(sp => sp.GetRequiredService<PlatformTtsEngine>());
	}
}
