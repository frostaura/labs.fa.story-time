using StoryTime.Core.Abstractions;

namespace StoryTime.Core.Pipeline;

/// <summary>Between-stories cooldown ("the loom rests"). Anchored to a persisted monotonic
/// timestamp so winding the wall clock forward does not skip it; wall-clock end time is the
/// fallback across reboots (monotonic resets at boot).</summary>
public sealed class CooldownService
{
    private readonly ISettingsStore _settings;
    private readonly IClock _clock;
    private readonly IEntitlements _entitlements;

    public CooldownService(ISettingsStore settings, IClock clock, IEntitlements entitlements)
    {
        _settings = settings;
        _clock = clock;
        _entitlements = entitlements;
    }

    /// <summary>Starts (or restarts) the cooldown using the current entitlement duration.</summary>
    public void Start()
    {
        var duration = TimeSpan.FromMinutes(_entitlements.CooldownMinutes);
        _settings.CooldownAnchorMonotonicMs = _clock.MonotonicMs;
        _settings.CooldownEndsUtc = _clock.UtcNow + duration;
    }

    public void Clear()
    {
        _settings.CooldownAnchorMonotonicMs = null;
        _settings.CooldownEndsUtc = null;
    }

    /// <summary>Time left, or null when no cooldown is active. Self-clears when elapsed.</summary>
    public TimeSpan? Remaining
    {
        get
        {
            // Testing mode (the Debug premium unlock) never waits on the loom — the cooldown
            // is a bedtime-calm feature, not something to fight while iterating. Note this
            // deliberately diverges from real premium timing (15 min); founder call 2026-07-30.
            if (_settings.DebugPremiumOverride) return null;

            var endsUtc = _settings.CooldownEndsUtc;
            if (endsUtc is null) return null;

            var duration = TimeSpan.FromMinutes(_entitlements.CooldownMinutes);
            TimeSpan remaining;

            var anchor = _settings.CooldownAnchorMonotonicMs;
            var nowMono = _clock.MonotonicMs;
            if (anchor is not null && nowMono >= anchor.Value)
            {
                // Monotonic path — resistant to wall-clock fiddling.
                var elapsed = TimeSpan.FromMilliseconds(nowMono - anchor.Value);
                remaining = duration - elapsed;
            }
            else
            {
                // Reboot (monotonic reset below anchor) — fall back to the wall clock.
                remaining = endsUtc.Value - _clock.UtcNow;
            }

            if (remaining <= TimeSpan.Zero)
            {
                Clear();
                return null;
            }
            // Never report more than a full cooldown (guards against corrupted state).
            return remaining > duration ? duration : remaining;
        }
    }

    /// <summary>Local wall-clock moment the next weave unlocks, or null when idle.</summary>
    public DateTime? NextWeaveAtLocal
    {
        get
        {
            var remaining = Remaining;
            return remaining is null ? null : DateTime.Now + remaining.Value;
        }
    }

    public bool IsActive => Remaining is not null;
}
