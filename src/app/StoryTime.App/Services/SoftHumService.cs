using StoryTime.Core.Abstractions;
using StoryTime.Core.Audio;
using StoryTime.Contracts;

namespace StoryTime.App.Services;

/// <summary>Plays the soft hum — a gentle, audible ambient loop — for the duration of a
/// story run ("the soft hum plays", founder decision 2026-07-31). Driven by StoryMakingHost's
/// start/end calls, it renders (or cache-hits) the profile's palette-family loop, fades in
/// ~2 s after story start, and then supervises with a 2 s poll:
/// <list type="bullet">
/// <item>ducks OUT while any narration player is playing (teaser/story playback takes over;
/// the Premium story bed also shares the one IAmbientPlayer, which this service never
/// steals while narration runs) and fades back in when playback ends mid-story;</item>
/// <item>tracks the parent's Music volume live; an (effectively) zero slider is an opt-out
/// — no hum, and on iOS the backgrounded story run falls back to checkpoint/resume;</item>
/// <item>on iOS, an audio-session interruption (call, Siri, another app's playback) while
/// humming is treated as the user opting out for THIS story run: the hum stops and stays
/// stopped, and backgrounding checkpoints gracefully exactly as before this feature.</item>
/// </list>
/// iOS honesty note: the hum is a real, audible feature — never an inaudible keep-alive
/// (see SoftHum.MinAudibleVolume). That it also keeps the pipeline running with the screen
/// off is the point, and the App Store 2.5.4 exposure is recorded in docs/known-gaps.md
/// with the compliance gate owning the final call.</summary>
public sealed class SoftHumService : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly SoftHumTrackProvider _tracks;
    private readonly IAmbientPlayer _player;
    private readonly ISettingsStore _settings;
    private readonly MediaElementNarrationPlayer _filePlayer;
    private readonly LiveTtsNarrationPlayer _livePlayer;

    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private bool _optedOut;
    private bool _humAudible;

    public SoftHumService(SoftHumTrackProvider tracks, IAmbientPlayer player, ISettingsStore settings,
        MediaElementNarrationPlayer filePlayer, LiveTtsNarrationPlayer livePlayer)
    {
        _tracks = tracks;
        _player = player;
        _settings = settings;
        _filePlayer = filePlayer;
        _livePlayer = livePlayer;
        ObserveInterruptions();
    }

    public void OnMakingStarted(ChildProfile profile)
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _optedOut = false;
            var token = _cts.Token;
            _ = Task.Run(() => RunAsync(profile, token));
        }
    }

    public void OnMakingEnded()
    {
        lock (_gate)
        {
            _cts?.Cancel();
        }
        // The ready ping is the endpoint — never hum past the story run (fade-out is ~1.5 s
        // inside the player; the loop task's finally also stops, this is belt-and-braces).
        StopHum();
    }

    private async Task RunAsync(ChildProfile profile, CancellationToken ct)
    {
        try
        {
            // Compose (or cache-hit) while the fade-in delay runs — first render is
            // sub-second-to-few-seconds off-thread, repeat stories are instant.
            var renderTask = _tracks.GetOrRenderAsync(profile, ct);
            await Task.Delay(SoftHum.FadeInDelay, ct);
            var path = await renderTask;

            EnsurePlaybackSession();

            while (!ct.IsCancellationRequested)
            {
                // Guarded per iteration, not once around the loop: a single transient read
                // (MediaElement state during a handler swap, a released player) must cost
                // one poll, not the whole hum.
                try
                {
                    var narrating = _filePlayer.IsPlaying || _livePlayer.IsPlaying;
                    var target = SoftHum.VolumeFor(_settings.MusicVolume);

                    if (_optedOut || narrating || target <= 0)
                    {
                        StopHum();
                    }
                    else
                    {
                        // Same-path StartAsync while playing only retargets volume, so this
                        // doubles as the live Music-slider follower.
                        await _player.StartAsync(path, target, ct);
                        _humAudible = true;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { }

                await Task.Delay(PollInterval, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            // A hum that fails must never disturb the story run.
        }
        finally
        {
            StopHum();
        }
    }

    private void StopHum()
    {
        if (!_humAudible) return;
        _humAudible = false;
        _ = _player.StopAsync();
    }

    /// <summary>iOS: make sure the shared session is the Playback category and active before
    /// the hum starts — the first story run can precede the first narration playback, and only an
    /// active Playback session keeps the app running in the background.
    ///
    /// <para>Routed through the SAME configurator narration uses (Playback + SpokenAudio +
    /// AirPlay/A2DP options) so the hum can never leave the session in a shape that degrades
    /// narration's AirPlay route. The hum still writes NO Now Playing info — the lock screen
    /// and any AirPlay receiver must show the story, never the hum.</para></summary>
    private static void EnsurePlaybackSession()
    {
        Platforms.iOS.NativeAudio.ConfigureSession();
    }

    private void ObserveInterruptions()
    {
        AVFoundation.AVAudioSession.Notifications.ObserveInterruption((_, args) =>
        {
            if (args.InterruptionType != AVFoundation.AVAudioSessionInterruptionType.Began) return;
            lock (_gate)
            {
                if (_cts is null || _cts.IsCancellationRequested || !_humAudible) return;
                // The system took our audio (call, Siri, another app): treat it as the
                // user opting out of the hum for this story run. Backgrounding now checkpoints
                // gracefully via the coordinator's grace path, exactly as without the hum.
                _optedOut = true;
            }
            StopHum();
        });
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
