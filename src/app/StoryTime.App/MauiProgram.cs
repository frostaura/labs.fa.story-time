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
			.ConfigureMauiHandlers(handlers =>
				handlers.AddHandler<StoryTime.App.Controls.SheetBackdrop, Platforms.iOS.SheetBackdropHandler>())
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
		RegisterEntitlements(builder.Services);
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
		// ---- Background story run + soft hum (2026-07-31) ----
		// The anchor is what keeps a story run alive when the app leaves the foreground; the hum
		// is the audible ambient loop that plays while the story is made (and, on iOS, the active
		// playback session that legitimately keeps the pipeline scheduled in the background).
		builder.Services.AddSingleton<IMakingBackgroundAnchor, Platforms.iOS.MakingBackgroundCoordinator>();
		// One cached loop per palette family in AppData — repeat stories start instantly.
		builder.Services.AddSingleton<StoryTime.Core.Audio.SoftHumTrackProvider>();
		builder.Services.AddSingleton<SoftHumService>();
		builder.Services.AddSingleton<IStoryMakingHost, StoryMakingHost>();
		// Making sheet coordinator (app-foreground animation gate; the sheet state itself
		// is the singleton MakingSheetViewModel registered by Core).
		builder.Services.AddSingleton<MakingSheetController>();

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

	/// <summary>Premium: one on-device, one-off StoreKit 2 purchase (no subscription, no server)
	/// through the StoryTimeNative Swift shim. Core's StoreEntitlements and PremiumLedger read and
	/// record it; the Transaction.updates listener starts at launch through an
	/// IMauiInitializeService so Ask to Buy approvals, refunds and revocations are delivered.</summary>
	static void RegisterEntitlements(IServiceCollection services)
	{
		services.AddSingleton<IAppStoreBridge, Platforms.iOS.AppStoreBridge>();
		services.AddSingleton<StoreKitPremiumStore>();
		services.AddSingleton<IPremiumStore>(sp => sp.GetRequiredService<StoreKitPremiumStore>());
		services.AddSingleton<IMauiInitializeService, Platforms.iOS.StoreKitListenerStarter>();
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
