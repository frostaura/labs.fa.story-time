using StoryTime.Core.Abstractions;

namespace StoryTime.App.Services;

/// <summary>One-shot wav playback for the narrator ▶ preview rings. The root cause this
/// class fixes (2026-07-31): RenderVoiceSampleAsync returns a rendered wav PATH once the
/// neural TTS is active, and nothing ever played it — previews were silently dead on any
/// device with models (the platform-TTS fallback masked it by speaking live).
///
/// <para>Every exit path traces (tag "TWVoice", matching the Core-side preview trace) so a
/// dead preview can never again look identical to a missing gesture. Playback stays
/// best-effort: a bad sample must not surface an error to a parent at bedtime.</para></summary>
public sealed class VoiceSamplePlayer : IVoiceSamplePlayer
{
    private static void Trace(string message)
    {
#if ANDROID
        Android.Util.Log.Debug("TWVoice", message);
#else
        Console.WriteLine($"TWVoice {message}");
#endif
    }

#if ANDROID
    private Android.Media.MediaPlayer? _player;

    public Task PlayAsync(string wavPath, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(wavPath)) { Trace("player: empty path, nothing to play"); return Task.CompletedTask; }
        if (!File.Exists(wavPath)) { Trace($"player: file missing {wavPath}"); return Task.CompletedTask; }
        try
        {
            // Cancel-and-replace: a repeat tap must interrupt the previous sample, not
            // stack a second voice on top of it.
            _player?.Release();
            _player = new Android.Media.MediaPlayer();
            _player.SetDataSource(wavPath);
            _player.Prepare();
            _player.Start();
            Trace($"player: started {new FileInfo(wavPath).Length} bytes {wavPath}");
        }
        catch (Exception ex)
        {
            Trace($"player FAILED: {ex.GetType().Name}: {ex.Message}");
        }
        return Task.CompletedTask;
    }
#elif IOS
    private AVFoundation.AVAudioPlayer? _player;   // field ref: must outlive the call

    public Task PlayAsync(string wavPath, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(wavPath)) { Trace("player: empty path, nothing to play"); return Task.CompletedTask; }
        if (!File.Exists(wavPath)) { Trace($"player: file missing {wavPath}"); return Task.CompletedTask; }
        try
        {
            // A one-shot preview needs an ACTIVE playback session; without this the sample
            // renders fine and plays into silence when the app has never started narration.
            var session = AVFoundation.AVAudioSession.SharedInstance();
            session.SetCategory(AVFoundation.AVAudioSessionCategory.Playback);
            session.SetActive(true);

            _player?.Stop();
            _player = AVFoundation.AVAudioPlayer.FromUrl(Foundation.NSUrl.FromFilename(wavPath), out var error);
            if (error is not null || _player is null)
            {
                Trace($"player FAILED to load: {error?.LocalizedDescription ?? "null player"}");
                return Task.CompletedTask;
            }
            _player.PrepareToPlay();
            var started = _player.Play();
            Trace($"player: Play() returned {started} for {wavPath}");
        }
        catch (Exception ex)
        {
            Trace($"player FAILED: {ex.GetType().Name}: {ex.Message}");
        }
        return Task.CompletedTask;
    }
#else
    public Task PlayAsync(string wavPath, CancellationToken ct)
    {
        Trace($"player: no platform implementation, skipped {wavPath}");
        return Task.CompletedTask;
    }
#endif
}
