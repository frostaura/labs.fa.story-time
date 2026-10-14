using StoryTime.Core.Engines.Narration;

namespace StoryTime.Core.Abstractions;

/// <summary>
/// The installed system voices the narrator can use, re-ranked live. The iOS implementation
/// enumerates AVSpeechSynthesisVoice, re-checks on AvailableVoicesDidChange and on return to
/// the foreground, and persists the chosen voice. Founder rule (2026-10-14): onboarding
/// cannot finish, and the app routes back to the storyteller-voice step, until
/// <see cref="HasStorytellerVoice"/> is true. There is ONE storyteller; the old three
/// narrator personas are gone (every NarratorVoice value narrates with it).
/// </summary>
public interface IStorytellerVoices
{
    /// <summary>The voice narration uses now (null when no eligible voice exists).</summary>
    StorytellerVoice? Current { get; }

    /// <summary>The required storyteller (Serena Premium) is installed. Nothing less counts —
    /// not even enhanced Serena (founder decision 2026-10-14).</summary>
    bool HasStorytellerVoice => Current?.MeetsRequirement == true;

    /// <summary>Raised (on any thread) whenever a re-check changed the ranking.</summary>
    event EventHandler? Changed;

    /// <summary>Re-enumerate the installed voices now.</summary>
    void Refresh();
}

/// <summary>How the device is connected — only to word the voice-download notice.
/// Read from the OS path monitor; never makes a network request.</summary>
public enum NetworkKind { Unknown, WiFi, Cellular, Offline }

public interface INetworkStatus
{
    NetworkKind Current { get; }
    event EventHandler? Changed;
}

/// <summary>Fixed voices for tests and non-iOS heads.</summary>
public sealed class StaticStorytellerVoices(StorytellerVoice? current) : IStorytellerVoices
{
    public StorytellerVoice? Current { get; set; } = current;
    public event EventHandler? Changed;
    public void Refresh() => Changed?.Invoke(this, EventArgs.Empty);
}

public sealed class UnknownNetworkStatus : INetworkStatus
{
    public NetworkKind Current => NetworkKind.Unknown;
    public event EventHandler? Changed { add { } remove { } }
}
