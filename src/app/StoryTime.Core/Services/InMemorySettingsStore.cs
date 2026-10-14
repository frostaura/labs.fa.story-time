using StoryTime.Contracts;
using StoryTime.Core.Abstractions;

namespace StoryTime.Core.Services;

/// <summary>Core fallback for tests. App registers PreferencesSettingsStore,
/// which wins via TryAdd ordering.</summary>
public sealed class InMemorySettingsStore : ISettingsStore
{
    public bool OnboardingComplete { get; set; }
    public bool ApprovalStep { get; set; } = true;
    public bool StoryReadyNotifications { get; set; } = true;
    public DateTime? FirstRunUtc { get; set; }
    public long? CooldownAnchorMonotonicMs { get; set; }
    public DateTime? CooldownEndsUtc { get; set; }
    public bool DebugPremiumOverride { get; set; }
    public double MusicVolume { get; set; } = 0.3;
    public ContentFilterLevel ContentFilter { get; set; } = ContentFilterLevel.Gentle;
}
