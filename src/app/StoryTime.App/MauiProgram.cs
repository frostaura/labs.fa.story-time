using CommunityToolkit.Maui;
using Microsoft.Extensions.Logging;
using Plugin.LocalNotification;
using SkiaSharp.Views.Maui.Controls.Hosting;
using StoryTime.App.Graphics;
using StoryTime.App.Services;
using StoryTime.App.Views;
using StoryTime.Core;
using StoryTime.Core.Abstractions;

namespace StoryTime.App;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.UseMauiCommunityToolkit()
			.UseMauiCommunityToolkitMediaElement(isAndroidForegroundServiceEnabled: true)
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

		// Hearth entries are underline-only — strip the platform chrome (iOS rounded-rect
		// border, Android underline) so the XAML hairline BoxView is the only decoration.
		Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("HearthBorderless", (handler, _) =>
		{
#if IOS
			handler.PlatformView.BorderStyle = UIKit.UITextBorderStyle.None;
#elif ANDROID
			handler.PlatformView.BackgroundTintList =
				Android.Content.Res.ColorStateList.ValueOf(Android.Graphics.Color.Transparent);
#endif
		});

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
#if ANDROID
		builder.Services.AddSingleton<IModelProvider, Platforms.Android.AndroidModelProvider>();
#elif IOS
		builder.Services.AddSingleton<IModelProvider, Platforms.iOS.IosModelProvider>();
#else
		builder.Services.AddSingleton<IModelProvider, ModelProviderStub>();
#endif
		builder.Services.AddSingleton<IPosterRasterizer, SkiaPosterRasterizer>();
		builder.Services.AddSingleton<PlatformTtsEngine>();
		// Model-gated engines resolve lazily on first weave (docs/native-integration.md):
		// real llama.cpp/Kokoro when models are present, template/platform fallbacks otherwise.
		builder.Services.AddSingleton<IStoryEngine, ModelGatedStoryEngine>();
#if ANDROID || IOS
		// Neural Kokoro (sherpa) when models resolve; platform TTS fallback otherwise.
		builder.Services.AddSingleton<ITtsEngine, ModelGatedTtsEngine>();
#else
		builder.Services.AddSingleton<ITtsEngine>(sp => sp.GetRequiredService<PlatformTtsEngine>());
#endif
		builder.Services.AddSingleton<IAmbientPlayer, AmbientPlayer>();
		builder.Services.AddSingleton<IVoiceSamplePlayer, VoiceSamplePlayer>(); // narrator preview one-shots   // Premium music bed (loops under narration)
		builder.Services.AddSingleton<MediaElementHost>();
		builder.Services.AddSingleton<MediaElementNarrationPlayer>();
		builder.Services.AddSingleton<LiveTtsNarrationPlayer>();
		builder.Services.AddSingleton<INarrationPlayerProvider, NarrationPlayerProvider>();
		// ---- Background weave + loom hum (2026-07-31) ----
		// The anchor is what keeps a weave alive when the app leaves the foreground; the hum
		// is the audible ambient loop that plays while it weaves (and, on iOS, the active
		// playback session that legitimately keeps the pipeline scheduled in the background).
#if ANDROID
		builder.Services.AddSingleton<IWeaveBackgroundAnchor, Platforms.Android.AndroidWeaveBackgroundAnchor>();
#elif IOS
		builder.Services.AddSingleton<IWeaveBackgroundAnchor, Platforms.iOS.WeaveBackgroundCoordinator>();
#endif
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
}
