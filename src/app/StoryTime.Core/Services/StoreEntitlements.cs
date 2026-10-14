using StoryTime.Contracts;
using StoryTime.Core.Abstractions;

namespace StoryTime.Core.Services;

/// <summary>Entitlements backed by the locally recorded App Store purchase
/// (<see cref="ISettingsStore.PremiumPurchased"/>, written only by <see cref="PremiumLedger"/>).
/// Everything premium-gated — 15-minute stories, the 15-minute wait between stories, music —
/// follows from <see cref="IsPremium"/>.</summary>
public sealed class StoreEntitlements : IEntitlements
{
    private readonly ISettingsStore _settings;
    private readonly IClock _clock;

    public StoreEntitlements(ISettingsStore settings, IClock clock)
    {
        _settings = settings;
        _clock = clock;
    }

    /// <summary>True once Premium was bought or restored on this device. The Debug-only
    /// testing override also unlocks it; the UI that sets it is compiled out of Release.</summary>
    public bool IsPremium => _settings.PremiumPurchased || _settings.DebugPremiumOverride;

    public int TrialNightsLeft
    {
        get
        {
            var firstRun = _settings.FirstRunUtc;
            if (firstRun is null) return 7;
            var nights = (int)Math.Floor((_clock.UtcNow - firstRun.Value).TotalDays);
            if (nights < 0) nights = 0;
            return Math.Max(0, 7 - nights);
        }
    }

    public int CooldownMinutes => IsPremium ? 15 : 30;

    public bool CanUseLength(StoryLength length) => IsPremium || length != StoryLength.Fifteen;
}
