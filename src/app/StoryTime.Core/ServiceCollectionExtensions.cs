using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StoryTime.Core.Abstractions;
using StoryTime.Core.Engines;
using StoryTime.Core.Persistence;
using StoryTime.Core.Pipeline;
using StoryTime.Core.Poster;
using StoryTime.Core.Services;
using StoryTime.Core.ViewModels;

namespace StoryTime.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers everything Core owns. Uses TryAdd* throughout so platform
    /// registrations made BEFORE this call win (architecture: platform impls first).</summary>
    public static IServiceCollection AddStoryTimeCore(this IServiceCollection services, StoryTimeOptions? options = null)
    {
        services.TryAddSingleton(options ?? new StoryTimeOptions());

        // Core services
        services.TryAddSingleton<IClock, SystemClock>();
        // Weave-strategy memory signal (overlapped vs sequential). TryAdd so the App head
        // may pre-register a richer signal without Core changes.
        services.TryAddSingleton<IDeviceCapabilities, DefaultDeviceCapabilities>();
        services.TryAddSingleton<ISettingsStore, InMemorySettingsStore>();
        services.TryAddSingleton<IHaptics, NullHaptics>();
        services.TryAddSingleton<IEntitlements, StubEntitlements>();
        // One notification seam for every story mutation; the store raises it, every list
        // view-model subscribes (see IStoryCatalog). The App layer replaces this
        // registration with one that marshals raises to the UI thread.
        services.TryAddSingleton<StoryCatalog>();
        services.TryAddSingleton<IStoryCatalog>(sp => sp.GetRequiredService<StoryCatalog>());
        services.TryAddSingleton<IStoryStore, SqliteStoryStore>();
        services.TryAddSingleton<IPosterComposer, PosterComposer>();
        services.TryAddSingleton<IPosterRasterizer, NullPosterRasterizer>();
        services.TryAddSingleton<Audio.AmbientTrackProvider>();
        services.TryAddSingleton<IAmbientPlayer, NullAmbientPlayer>();
        services.TryAddSingleton<SafetyFilter>();
        services.TryAddSingleton<CooldownService>();
        services.TryAddSingleton<WelcomeStoryFactory>();
        services.TryAddSingleton<PosterArtRefresher>();
        services.TryAddSingleton<StoryPipeline>();

        // Story engine fallback — the deterministic template engine. TryAdd: the App head
        // registers its engine first (MauiProgram.RegisterStoryEngine, the swap point for the
        // Apple Foundation Models engine), so this only serves tests and headless hosts.
        // ITtsEngine has no Core default: speech is platform-only (MauiProgram.RegisterTtsEngine).
        services.TryAddSingleton<IStoryEngine>(_ => new TemplateStoryEngine(() => Random.Shared.Next()));

        // Shared app state
        services.TryAddSingleton<OnboardingState>();
        services.TryAddSingleton<PlayerLaunchContext>();
        services.TryAddSingleton<KidModeService>();

        // ViewModels — transient except those that carry cross-page weave state.
        services.TryAddTransient<OnboardingProfileViewModel>();
        services.TryAddTransient<OnboardingVoiceViewModel>();
        services.TryAddTransient<OnboardingRulesViewModel>();
        services.TryAddTransient<HomeViewModel>();
        // Owned by HomeViewModel (one per Home instance): the "Tonight's recipe" shelf.
        services.TryAddTransient<RecipeShelfViewModel>();
        // Singleton by design: the weave sheet's expand/collapse state must survive page
        // navigation — every page's WeaveSheetHost binds to this one instance.
        services.TryAddSingleton<WeaveSheetViewModel>();
        services.TryAddTransient<PlayerViewModel>();
        services.TryAddSingleton<Abstractions.IVoiceSamplePlayer, Services.NullVoiceSamplePlayer>();
        services.TryAddTransient<ShelfViewModel>();
        services.TryAddTransient<SettingsViewModel>();
        services.TryAddTransient<ContentFiltersViewModel>();
        services.TryAddTransient<LibraryViewModel>();
        services.TryAddTransient<PremiumViewModel>();

        return services;
    }
}
