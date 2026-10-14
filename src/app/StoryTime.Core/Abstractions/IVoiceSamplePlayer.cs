namespace StoryTime.Core.Abstractions;

/// <summary>Plays a short one-shot voice-sample wav (the ▶ preview rings). Distinct from
/// narration players (file/seek/lock-screen) and the ambient loop. An empty path is a
/// no-op — the platform-TTS fallback speaks live during rendering and returns "".</summary>
public interface IVoiceSamplePlayer
{
    Task PlayAsync(string wavPath, CancellationToken ct);
}
