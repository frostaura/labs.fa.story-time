using StoryTime.Contracts;

namespace StoryTime.Core.Abstractions;

/// <summary>Playback seam so the Player UI works identically for file-backed narration
/// (MediaElement) and live platform-TTS narration (sentence-by-sentence SpeakAsync).
/// Chosen per story by whether AudioPath exists. (Additive Phase B abstraction.)</summary>
public interface INarrationPlayer
{
    /// <summary>True when this player can handle the given story record.</summary>
    bool CanPlay(StoryRecord story);

    Task LoadAsync(StoryRecord story, bool teaserOnly, CancellationToken ct);
    Task PlayAsync();
    Task PauseAsync();
    Task StopAsync();
    /// <summary>Seek to absolute position (live TTS snaps to the nearest sentence).</summary>
    Task SeekAsync(double seconds);

    bool IsPlaying { get; }
    double PositionSeconds { get; }
    double DurationSeconds { get; }

    /// <summary>Narration volume 0..1, applied live where the backend allows it
    /// (MediaElement). Live platform-TTS cannot re-level mid-utterance — there
    /// <see cref="SupportsVolume"/> is false and setting this is a no-op; the Player UI
    /// disables the Voice mix row. (Additive, 2026-07-30.)</summary>
    double Volume { get; set; }
    bool SupportsVolume { get; }

    /// <summary>Raised (on the UI thread in App implementations) as position advances.</summary>
    event EventHandler? PositionChanged;
    /// <summary>Raised when playback reaches the end.</summary>
    event EventHandler? Completed;
}

/// <summary>Picks the right INarrationPlayer for a story (file-backed vs live TTS).</summary>
public interface INarrationPlayerProvider
{
    INarrationPlayer GetFor(StoryRecord story);
}
