using StoryTime.Contracts;
using StoryTime.Core.Pipeline;
using StoryTime.Core.Services;
using Xunit;

namespace StoryTime.Core.Tests;

public class CooldownServiceTests
{
    private static (CooldownService Cooldown, FakeClock Clock, InMemorySettingsStore Settings) Make()
    {
        var clock = new FakeClock();
        var settings = new InMemorySettingsStore();
        var entitlements = new StubEntitlements(settings, clock);
        return (new CooldownService(settings, clock, entitlements), clock, settings);
    }

    [Fact]
    public void Idle_HasNoRemaining()
    {
        var (cooldown, _, _) = Make();
        Assert.Null(cooldown.Remaining);
        Assert.Null(cooldown.NextWeaveAtLocal);
        Assert.False(cooldown.IsActive);
    }

    [Fact]
    public void Start_PersistsAnchorAndEnd()
    {
        var (cooldown, clock, settings) = Make();
        cooldown.Start();
        Assert.Equal(clock.MonotonicMs, settings.CooldownAnchorMonotonicMs);
        Assert.Equal(clock.UtcNow.AddMinutes(30), settings.CooldownEndsUtc);
    }

    [Fact]
    public void Remaining_CountsDownWithMonotonicTime()
    {
        var (cooldown, clock, _) = Make();
        cooldown.Start();
        clock.Advance(TimeSpan.FromMinutes(10));
        var remaining = cooldown.Remaining;
        Assert.NotNull(remaining);
        Assert.Equal(20, remaining.Value.TotalMinutes, precision: 1);
    }

    [Fact]
    public void WallClockFiddling_DoesNotSkipCooldown()
    {
        var (cooldown, clock, _) = Make();
        cooldown.Start();
        // Parent winds the wall clock forward 2 hours; monotonic time moved only 1 minute.
        clock.UtcNow += TimeSpan.FromHours(2);
        clock.MonotonicMs += 60_000;
        var remaining = cooldown.Remaining;
        Assert.NotNull(remaining);
        Assert.Equal(29, remaining.Value.TotalMinutes, precision: 1);
    }

    [Fact]
    public void Reboot_FallsBackToWallClock()
    {
        var (cooldown, clock, _) = Make();
        cooldown.Start();
        // Reboot: monotonic resets below the anchor; 10 wall minutes passed.
        clock.UtcNow += TimeSpan.FromMinutes(10);
        clock.MonotonicMs = 5_000;
        var remaining = cooldown.Remaining;
        Assert.NotNull(remaining);
        Assert.Equal(20, remaining.Value.TotalMinutes, precision: 1);
    }

    [Fact]
    public void Expiry_SelfClears()
    {
        var (cooldown, clock, settings) = Make();
        cooldown.Start();
        clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Null(cooldown.Remaining);
        Assert.Null(settings.CooldownAnchorMonotonicMs);
        Assert.Null(settings.CooldownEndsUtc);
    }

    [Fact]
    public void Remaining_NeverExceedsFullDuration()
    {
        var (cooldown, clock, settings) = Make();
        cooldown.Start();
        // Corrupted state: end pushed far into the future.
        settings.CooldownEndsUtc = clock.UtcNow.AddHours(5);
        settings.CooldownAnchorMonotonicMs = null;
        var remaining = cooldown.Remaining;
        Assert.NotNull(remaining);
        Assert.True(remaining.Value <= TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void NextWeaveAtLocal_MatchesRemaining()
    {
        var (cooldown, _, _) = Make();
        cooldown.Start();
        var next = cooldown.NextWeaveAtLocal;
        Assert.NotNull(next);
        var expected = DateTime.Now + cooldown.Remaining!.Value;
        Assert.True(Math.Abs((next.Value - expected).TotalSeconds) < 5);
    }
}

public class StubEntitlementsTests
{
    private static (StubEntitlements Entitlements, FakeClock Clock, InMemorySettingsStore Settings) Make()
    {
        var clock = new FakeClock();
        var settings = new InMemorySettingsStore();
        return (new StubEntitlements(settings, clock), clock, settings);
    }

    [Fact]
    public void Premium_IsFalseByDefault()
    {
        var (entitlements, _, _) = Make();
        Assert.False(entitlements.IsPremium);
    }

    // ---- Debug testing override (ISettingsStore.DebugPremiumOverride) ----

    [Fact]
    public void DebugOverride_UnlocksPremium()
    {
        var (entitlements, _, settings) = Make();
        settings.DebugPremiumOverride = true;
        Assert.True(entitlements.IsPremium);
    }

    [Fact]
    public void DebugOverride_ShortensCooldownToFifteen()
    {
        var (entitlements, _, settings) = Make();
        settings.DebugPremiumOverride = true;
        Assert.Equal(15, entitlements.CooldownMinutes);
    }

    [Fact]
    public void DebugOverride_AllowsFifteenMinuteStories()
    {
        var (entitlements, _, settings) = Make();
        settings.DebugPremiumOverride = true;
        Assert.True(entitlements.CanUseLength(StoryLength.Fifteen));
        Assert.True(entitlements.CanUseLength(StoryLength.Ten));
        Assert.True(entitlements.CanUseLength(StoryLength.Five));
    }

    [Fact]
    public void DebugOverride_RelockRestoresGating()
    {
        var (entitlements, _, settings) = Make();
        settings.DebugPremiumOverride = true;
        settings.DebugPremiumOverride = false;
        Assert.False(entitlements.IsPremium);
        Assert.Equal(30, entitlements.CooldownMinutes);
        Assert.False(entitlements.CanUseLength(StoryLength.Fifteen));
    }

    [Fact]
    public void Trial_IsSevenBeforeFirstRun()
    {
        var (entitlements, _, _) = Make();
        Assert.Equal(7, entitlements.TrialNightsLeft);
    }

    [Theory]
    [InlineData(0, 7)]
    [InlineData(1, 6)]
    [InlineData(3, 4)]
    [InlineData(7, 0)]
    [InlineData(30, 0)]
    public void Trial_CountsNightsSinceFirstRun(int daysAgo, int expected)
    {
        var (entitlements, clock, settings) = Make();
        settings.FirstRunUtc = clock.UtcNow.AddDays(-daysAgo);
        Assert.Equal(expected, entitlements.TrialNightsLeft);
    }

    [Fact]
    public void Trial_PartialNightFloors()
    {
        var (entitlements, clock, settings) = Make();
        settings.FirstRunUtc = clock.UtcNow.AddHours(-30); // 1.25 days → floor 1
        Assert.Equal(6, entitlements.TrialNightsLeft);
    }

    [Fact]
    public void Cooldown_IsThirtyMinutes()
    {
        var (entitlements, _, _) = Make();
        Assert.Equal(30, entitlements.CooldownMinutes);
    }

    [Theory]
    [InlineData(StoryLength.Five, true)]
    [InlineData(StoryLength.Ten, true)]
    [InlineData(StoryLength.Fifteen, false)]
    public void FifteenMinutes_IsPremiumGated(StoryLength length, bool allowed)
    {
        var (entitlements, _, _) = Make();
        Assert.Equal(allowed, entitlements.CanUseLength(length));
    }
}
