namespace StoryTime.Core.Abstractions;

/// <summary>Looping ambient-bed playback with live volume, mixing WITH (never instead of)
/// the narration player. Platform implementations (AVAudioPlayer / Android MediaPlayer)
/// fade in on start and fade out on stop (~1.5 s) and ramp volume changes smoothly; the
/// ViewModel owns the ducking policy and only ever states a target volume. (Additive
/// Premium-music seam, 2026-07-30.)</summary>
public interface IAmbientPlayer
{
    bool IsPlaying { get; }

    /// <summary>Start looping the wav at <paramref name="wavPath"/>, fading in to
    /// <paramref name="volume"/>. Restarting with the same path while playing only
    /// retargets the volume.</summary>
    Task StartAsync(string wavPath, double volume, CancellationToken ct);

    /// <summary>Retarget the bed volume (0..1); implementations ramp, never step.</summary>
    void SetVolume(double volume);

    /// <summary>Fade out and stop. Safe to call when not playing.</summary>
    Task StopAsync();
}
