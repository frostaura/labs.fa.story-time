using StoryTime.Contracts;
using StoryTime.Core.Abstractions;

namespace StoryTime.Core.Services;

/// <summary>Local entitlement stub until the M4 monetization decision. No billing exists.</summary>
public sealed class StubEntitlements : IEntitlements
{
    private readonly ISettingsStore _settings;
    private readonly IClock _clock;

    public StubEntitlements(ISettingsStore settings, IClock clock)
    {
        _settings = settings;
        _clock = clock;
    }

    /// <summary>No billing exists until M4. True only via the Debug-only testing override
    /// flipped on the PremiumPage; everything premium-gated (15-min length, 15-min
    /// cooldown) follows from this single property.</summary>
    public bool IsPremium => _settings.DebugPremiumOverride;

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
