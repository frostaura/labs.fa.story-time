using AVFoundation;
using CoreGraphics;
using Foundation;
using MediaPlayer;
using UIKit;

namespace StoryTime.App.Platforms.iOS;

/// <summary>Native-audio integration for narration (founder request 2026-07-31: "ensure our
/// players support the NATIVE players so things like AirPlay work").
///
/// <para>CommunityToolkit's MediaElement plays through AVPlayer, which is already AirPlay-,
/// CarPlay- and Bluetooth-capable — but three things decide whether that capability is
/// actually usable, and none of them were configured before this pass:</para>
/// <list type="number">
/// <item><b>The audio session.</b> Category must be <c>Playback</c> — <c>PlayAndRecord</c>
/// routes to the earpiece and degrades AirPlay — and mode <c>SpokenAudio</c>, which is what
/// tells iOS, CarPlay and Bluetooth car systems that this is speech: it pauses (rather than
/// ducks) against other spoken content and behaves correctly with automatic route switching.
/// Activated when playback starts.</item>
/// <item><b>Now Playing.</b> An AirPlay receiver, the lock screen, Control Center and a car
/// head unit all render <c>MPNowPlayingInfoCenter</c>. Title, narrator, poster artwork,
/// duration, elapsed time and playback rate are pushed here; elapsed+rate are what make an
/// external scrubber track correctly.</item>
/// <item><b>Remote commands.</b> Without <c>MPRemoteCommandCenter</c> handlers, the pause
/// button on a car stereo or a pair of AirPods does nothing to our player.</item>
/// </list>
///
/// <para><b>The hum must never own this surface.</b> Only narration writes Now Playing; the
/// loom hum and the Premium ambient bed play through <c>AVAudioPlayer</c> on the same session
/// and touch none of these APIs, so the lock screen always shows the STORY.</para></summary>
internal static class NativeAudio
{
    private const string Tag = "TWAudio";

    private static readonly object Gate = new();
    private static NSObject? _routeObserver;
    private static bool _commandsRegistered;
    private static Action? _onPlay;
    private static Action? _onPause;
    private static Action? _onStop;
    private static Action<double>? _onSeek;
    private static Func<double>? _position;
    private static Action? _onRouteLost;

    /// <summary>Playback + SpokenAudio, activated. Idempotent and safe to call on every load;
    /// AVAudioSession de-duplicates an unchanged category.</summary>
    internal static void ConfigureSession()
    {
        try
        {
            var session = AVAudioSession.SharedInstance();
            // AllowAirPlay is implied by Playback, but stating it keeps the intent readable;
            // AllowBluetoothA2DP covers speakers/car kits (the plain Bluetooth option is the
            // low-quality HFP path and is deliberately NOT set).
            session.SetCategory(AVAudioSessionCategory.Playback, AVAudioSessionCategoryOptions.AllowBluetoothA2DP
                | AVAudioSessionCategoryOptions.AllowAirPlay);
            session.SetMode(AVAudioSessionMode.SpokenAudio.GetConstant()!, out _);
            session.SetActive(true);
            LogRoute("session configured");
        }
        catch (Exception ex)
        {
            Log($"session config failed: {ex.GetType().Name}");
        }
    }

    /// <summary>Dumps the live category/mode/route — the only honest way to prove the routing
    /// story on a simulator, where no real AirPlay receiver exists.</summary>
    internal static void LogRoute(string reason)
    {
        try
        {
            var session = AVAudioSession.SharedInstance();
            var outputs = session.CurrentRoute?.Outputs;
            var names = outputs is null || outputs.Length == 0
                ? "(none)"
                : string.Join(", ", outputs.Select(o => $"{o.PortType}:{o.PortName}"));
            Log($"{reason} — category={session.Category} mode={session.Mode} route=[{names}]");
        }
        catch (Exception) { }
    }

    // ---------------- Now Playing ----------------

    internal static void SetNowPlaying(string title, string narrator, string? artworkPath,
        double durationSeconds, double elapsedSeconds, double rate)
    {
        try
        {
            var info = new MPNowPlayingInfo
            {
                Title = title,
                Artist = narrator,
                AlbumTitle = "StoryTime",
                PlaybackDuration = durationSeconds,
                ElapsedPlaybackTime = elapsedSeconds,
                PlaybackRate = rate,
                // Spoken word: tells car systems and CarPlay to treat this as an audiobook
                // rather than a music track (affects skip semantics and display).
                MediaType = MPNowPlayingInfoMediaType.Audio,
            };

            if (!string.IsNullOrEmpty(artworkPath) && File.Exists(artworkPath))
            {
                var image = UIImage.FromFile(artworkPath);
                if (image is not null)
                    info.Artwork = new MPMediaItemArtwork(image.Size, _ => image);
            }

            MPNowPlayingInfoCenter.DefaultCenter.NowPlaying = info;
            Log($"now-playing set: \"{title}\" / {narrator} / {durationSeconds:F1}s / artwork={(info.Artwork is not null)}");
        }
        catch (Exception ex)
        {
            Log($"now-playing set failed: {ex.GetType().Name}");
        }
    }

    /// <summary>Keeps an external scrubber honest. iOS extrapolates position from
    /// (elapsed, rate, wall clock), so a stale rate makes a car head unit's progress bar drift
    /// or run while paused.</summary>
    internal static void UpdatePlaybackState(double elapsedSeconds, double rate)
    {
        try
        {
            var center = MPNowPlayingInfoCenter.DefaultCenter;
            var info = center.NowPlaying;
            if (info is null) return;
            info.ElapsedPlaybackTime = elapsedSeconds;
            info.PlaybackRate = rate;
            center.NowPlaying = info;
        }
        catch (Exception) { }
    }

    internal static void ClearNowPlaying()
    {
        try
        {
            // nil clears the entry (Apple semantics); the binding annotates the property non-null.
            MPNowPlayingInfoCenter.DefaultCenter.NowPlaying = null!;
            Log("now-playing cleared");
        }
        catch (Exception) { }
    }

    /// <summary>Diagnostic dump used by the QA route — proves what an AirPlay receiver or a
    /// lock screen would actually render.</summary>
    internal static void DumpNowPlaying()
    {
        try
        {
            var info = MPNowPlayingInfoCenter.DefaultCenter.NowPlaying;
            if (info is null) { Log("now-playing DUMP: (null)"); return; }
            Log($"now-playing DUMP: title=\"{info.Title}\" artist=\"{info.Artist}\" " +
                $"duration={info.PlaybackDuration}s elapsed={info.ElapsedPlaybackTime}s " +
                $"rate={info.PlaybackRate} artwork={(info.Artwork is not null)}");
        }
        catch (Exception) { }
    }

    // ---------------- remote commands + route changes ----------------

    /// <summary>Wires the lock screen / AirPods / car-stereo transport to our player. Handlers
    /// are stored, not captured per-registration, so re-loading a story re-points them without
    /// stacking duplicate targets on the command center.</summary>
    internal static void RegisterRemoteCommands(Action play, Action pause, Action stop,
        Action<double> seek, Func<double> position, Action onRouteLost)
    {
        lock (Gate)
        {
            _onPlay = play;
            _onPause = pause;
            _onStop = stop;
            _onSeek = seek;
            _position = position;
            _onRouteLost = onRouteLost;
            if (_commandsRegistered) return;
            _commandsRegistered = true;
        }

        try
        {
            var center = MPRemoteCommandCenter.Shared;

            center.PlayCommand.Enabled = true;
            center.PlayCommand.AddTarget(_ => Invoke(() => _onPlay?.Invoke()));

            center.PauseCommand.Enabled = true;
            center.PauseCommand.AddTarget(_ => Invoke(() => _onPause?.Invoke()));

            center.TogglePlayPauseCommand.Enabled = true;
            center.TogglePlayPauseCommand.AddTarget(_ => Invoke(() => _onPause?.Invoke()));

            center.StopCommand.Enabled = true;
            center.StopCommand.AddTarget(_ => Invoke(() => _onStop?.Invoke()));

            center.ChangePlaybackPositionCommand.Enabled = true;
            center.ChangePlaybackPositionCommand.AddTarget(evt =>
                evt is MPChangePlaybackPositionCommandEvent e
                    ? Invoke(() => _onSeek?.Invoke(e.PositionTime))
                    : MPRemoteCommandHandlerStatus.CommandFailed);

            // Audiobook-style skips rather than track skips — there is no "next story".
            center.SkipForwardCommand.Enabled = true;
            center.SkipForwardCommand.PreferredIntervals = [15.0];
            center.SkipForwardCommand.AddTarget(_ =>
                Invoke(() => _onSeek?.Invoke((_position?.Invoke() ?? 0) + 15)));

            center.SkipBackwardCommand.Enabled = true;
            center.SkipBackwardCommand.PreferredIntervals = [15.0];
            center.SkipBackwardCommand.AddTarget(_ =>
                Invoke(() => _onSeek?.Invoke(Math.Max(0, (_position?.Invoke() ?? 0) - 15))));

            // Nothing to skip to; leaving them enabled would show dead buttons in a car.
            center.NextTrackCommand.Enabled = false;
            center.PreviousTrackCommand.Enabled = false;

            Log("remote commands registered (play/pause/toggle/stop/seek/skip±15)");
        }
        catch (Exception ex)
        {
            Log($"remote command registration failed: {ex.GetType().Name}");
        }

        ObserveRouteChanges();
    }

    private static MPRemoteCommandHandlerStatus Invoke(Action action)
    {
        try
        {
            MainThread.BeginInvokeOnMainThread(action);
            return MPRemoteCommandHandlerStatus.Success;
        }
        catch (Exception)
        {
            return MPRemoteCommandHandlerStatus.CommandFailed;
        }
    }

    /// <summary>Headphones out / AirPlay receiver gone: iOS convention (and every audiobook
    /// app) is to PAUSE rather than blast the story out of the phone speaker at a sleeping
    /// child. A weave in flight is untouched — the loom keeps humming.</summary>
    private static void ObserveRouteChanges()
    {
        if (_routeObserver is not null) return;
        try
        {
            _routeObserver = AVAudioSession.Notifications.ObserveRouteChange((_, args) =>
            {
                LogRoute($"route change ({args.Reason})");
                if (args.Reason != AVAudioSessionRouteChangeReason.OldDeviceUnavailable) return;
                MainThread.BeginInvokeOnMainThread(() => _onRouteLost?.Invoke());
            });
        }
        catch (Exception) { }
    }

    private static void Log(string message) => Console.WriteLine($"{Tag} {message}");
}
