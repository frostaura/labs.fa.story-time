using StoryTime.Contracts;
using StoryTime.Core.Abstractions;

namespace StoryTime.App.Services;

/// <summary>ISettingsStore over MAUI Preferences (registered before AddStoryTimeCore, so it
/// wins over the in-memory Core fallback).</summary>
public sealed class PreferencesSettingsStore : ISettingsStore
{
    private const string KeyOnboarding = "tw.onboarding-complete";
    private const string KeyApproval = "tw.approval-step";
    private const string KeyNotifications = "tw.story-ready-notifications";
    private const string KeyFirstRun = "tw.first-run-utc";
    private const string KeyCooldownAnchor = "tw.cooldown-anchor-mono";
    private const string KeyCooldownEnds = "tw.cooldown-ends-utc";
    private const string KeyDebugPremium = "tw.debug-premium-override";
    private const string KeyPremiumPurchased = "tw.premium-purchased";
    private const string KeyMusicVolume = "tw.music-volume";
    private const string KeyContentFilter = "tw.content-filter";

    public bool OnboardingComplete
    {
        get => Preferences.Get(KeyOnboarding, false);
        set => Preferences.Set(KeyOnboarding, value);
    }

    public bool ApprovalStep
    {
        get => Preferences.Get(KeyApproval, true);
        set => Preferences.Set(KeyApproval, value);
    }

    public bool StoryReadyNotifications
    {
        get => Preferences.Get(KeyNotifications, true);
        set => Preferences.Set(KeyNotifications, value);
    }

    /// <summary>Quality analytics was removed entirely (founder decision 2026-07-31).
    /// This clears the orphaned key from installs that predate the removal, so nothing
    /// keeps a stale opt-in on disk. Safe to delete once no such install remains.</summary>
    public PreferencesSettingsStore()
    {
        try { Preferences.Remove("tw.quality-analytics"); } catch (Exception) { /* first run / no store */ }
    }

    public DateTime? FirstRunUtc
    {
        get => GetTicks(KeyFirstRun);
        set => SetTicks(KeyFirstRun, value);
    }

    public long? CooldownAnchorMonotonicMs
    {
        get
        {
            var v = Preferences.Get(KeyCooldownAnchor, long.MinValue);
            return v == long.MinValue ? null : v;
        }
        set
        {
            if (value is null) Preferences.Remove(KeyCooldownAnchor);
            else Preferences.Set(KeyCooldownAnchor, value.Value);
        }
    }

    public DateTime? CooldownEndsUtc
    {
        get => GetTicks(KeyCooldownEnds);
        set => SetTicks(KeyCooldownEnds, value);
    }

    public bool DebugPremiumOverride
    {
        get => Preferences.Get(KeyDebugPremium, false);
        set => Preferences.Set(KeyDebugPremium, value);
    }

    public bool PremiumPurchased
    {
        get => Preferences.Get(KeyPremiumPurchased, false);
        set => Preferences.Set(KeyPremiumPurchased, value);
    }

    public double MusicVolume
    {
        get => Math.Clamp(Preferences.Get(KeyMusicVolume, 0.3), 0.0, 1.0);
        set => Preferences.Set(KeyMusicVolume, Math.Clamp(value, 0.0, 1.0));
    }

    public ContentFilterLevel ContentFilter
    {
        // Stored by enum name; unknown/corrupt values fall back to Gentle (the safe default).
        get => Enum.TryParse<ContentFilterLevel>(Preferences.Get(KeyContentFilter, nameof(ContentFilterLevel.Gentle)),
            out var level) ? level : ContentFilterLevel.Gentle;
        set => Preferences.Set(KeyContentFilter, value.ToString());
    }

    private static DateTime? GetTicks(string key)
    {
        var ticks = Preferences.Get(key, 0L);
        return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
    }

    private static void SetTicks(string key, DateTime? value)
    {
        if (value is null) Preferences.Remove(key);
        else Preferences.Set(key, value.Value.ToUniversalTime().Ticks);
    }
}
