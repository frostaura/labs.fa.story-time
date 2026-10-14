using System.Text.Json;
using CommunityToolkit.Maui.Views;
using StoryTime.Contracts;
using StoryTime.Core.Abstractions;
using StoryTime.Core.Pipeline;

namespace StoryTime.App.Services;

/// <summary>The PlayerPage parks its MediaElement here so the file-backed narration player
/// (a DI singleton with no view access) can drive it.</summary>
public sealed class MediaElementHost
{
    public MediaElement? Element { get; set; }
}

/// <summary>File-backed narration (real audio files, MediaElement playback + seek) — and the
/// app's ONLY route to the platform's native audio stack.
///
/// <para><b>Native-player integration (founder request 2026-07-31).</b> MediaElement plays
/// through AVPlayer, so AirPlay / AirPlay 2, Bluetooth and CarPlay are all reachable — but
/// only if the story is announced to
/// the system. Three things do that, and this class owns all three:</para>
/// <list type="bullet">
/// <item><b>Metadata</b> — <c>MetadataTitle</c> / <c>MetadataArtist</c> /
/// <c>MetadataArtworkUrl</c> are the toolkit's cross-platform seam; its coverage of Now
/// Playing is partial, so we ALSO write
/// <c>MPNowPlayingInfoCenter</c> ourselves.</item>
/// <item><b>Audio session</b> (iOS) — <c>Playback</c> + <c>SpokenAudio</c>, activated on load.
/// <c>PlayAndRecord</c> would route to the earpiece and degrade AirPlay; <c>SpokenAudio</c> is
/// what makes car systems treat the narration as speech.</item>
/// <item><b>Remote commands</b> (iOS) — lock screen, AirPods, and car-stereo transport are
/// wired straight to this player's Play/Pause/Stop/Seek.</item>
/// </list>
/// <para>The soft hum and the Premium ambient bed deliberately do NONE of this: they mix under
/// narration on the same session but never write Now Playing, so an AirPlay receiver or lock
/// screen always shows the story, never the hum.</para></summary>
public sealed class MediaElementNarrationPlayer : INarrationPlayer
{
    private readonly MediaElementHost _host;
    private StoryRecord? _story;
    private bool _wired;

    public MediaElementNarrationPlayer(MediaElementHost host) => _host = host;

    public event EventHandler? PositionChanged;
    public event EventHandler? Completed;

    public bool CanPlay(StoryRecord story) => !string.IsNullOrEmpty(story.AudioPath);

    public bool IsPlaying => _host.Element?.CurrentState == CommunityToolkit.Maui.Core.MediaElementState.Playing;

    private double _volume = 1.0;

    /// <summary>Live narration volume — drives MediaElement.Volume directly (the Voice mix row).</summary>
    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 1);
            if (_host.Element is { } element) element.Volume = _volume;
        }
    }

    public bool SupportsVolume => true;

    public double PositionSeconds => _host.Element?.Position.TotalSeconds ?? 0;
    public double DurationSeconds
    {
        get
        {
            var elementDuration = _host.Element?.Duration.TotalSeconds ?? 0;
            return elementDuration > 0 ? elementDuration : _story?.DurationSeconds ?? 0;
        }
    }

    public Task LoadAsync(StoryRecord story, bool teaserOnly, CancellationToken ct)
    {
        _story = story;
        var element = _host.Element;
        if (element is null) return Task.CompletedTask;

        if (!_wired)
        {
            element.PositionChanged += (_, _) => PositionChanged?.Invoke(this, EventArgs.Empty);
            element.MediaEnded += (_, _) =>
            {
                PushPlaybackState(playing: false);
                Completed?.Invoke(this, EventArgs.Empty);
            };
            _wired = true;
        }

        var path = teaserOnly && !string.IsNullOrEmpty(story.TeaserPath) ? story.TeaserPath : story.AudioPath;

        // Announce the story to the system BEFORE the source lands, so the first frame the
        // platform publishes to AirPlay / the lock screen / a car head unit is already ours.
        // A teaser is still the same story — same title, same artwork.
        element.MetadataTitle = story.Title;
        element.MetadataArtist = NarratorNameOf(story);
        element.MetadataArtworkUrl = ArtworkUrlOf(story);

        element.Source = MediaSource.FromFile(path);
        element.Volume = _volume;

        ConfigureNativeSession();
        PublishNowPlaying(story, teaserOnly);
        return Task.CompletedTask;
    }

    public Task PlayAsync()
    {
        _host.Element?.Play();
        PushPlaybackState(playing: true);
        return Task.CompletedTask;
    }

    public Task PauseAsync()
    {
        _host.Element?.Pause();
        PushPlaybackState(playing: false);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _host.Element?.Stop();
        PushPlaybackState(playing: false);
        ClearNativeNowPlaying();
        return Task.CompletedTask;
    }

    public async Task SeekAsync(double seconds)
    {
        if (_host.Element is { } element)
            await element.SeekTo(TimeSpan.FromSeconds(seconds), CancellationToken.None);
        // External scrubbers extrapolate from (elapsed, rate) — a seek that does not republish
        // leaves a car's progress bar reading the pre-seek position.
        PushPlaybackState(IsPlaying);
    }

    /// <summary>Narrator persona, shown as the "artist" on every native surface.</summary>
    private static string NarratorNameOf(StoryRecord story) => story.Voice.ToString();

    /// <summary>The story's flattened poster thumbnail, handed to the toolkit as a PLAIN
    /// filesystem path rather than a <c>file://</c> URI: the toolkit reads a local path
    /// directly (its HttpClient branch is for real URLs), and there is no network here to
    /// serve one from — offline-only is a product invariant.
    /// Empty when the story has no art yet (an unposterised resume); the toolkit tolerates
    /// that and simply shows no cover.</summary>
    private static string ArtworkUrlOf(StoryRecord story) =>
        !string.IsNullOrEmpty(story.ThumbnailPath) && File.Exists(story.ThumbnailPath)
            ? story.ThumbnailPath
            : string.Empty;

    // ---------------- platform bridge ----------------
    // iOS needs the audio session, Now Playing and the remote-command wiring done
    // explicitly — the toolkit's metadata properties alone do not reach every surface.

    private void ConfigureNativeSession()
    {
        Platforms.iOS.NativeAudio.ConfigureSession();
    }

    private void PublishNowPlaying(StoryRecord story, bool teaserOnly)
    {
        var duration = teaserOnly ? 0 : story.DurationSeconds;
        Platforms.iOS.NativeAudio.SetNowPlaying(story.Title, NarratorNameOf(story),
            story.ThumbnailPath, duration, 0, 0);
        Platforms.iOS.NativeAudio.RegisterRemoteCommands(
            play: () => _ = PlayAsync(),
            pause: () => _ = PauseAsync(),
            stop: () => _ = StopAsync(),
            seek: seconds => _ = SeekAsync(seconds),
            position: () => PositionSeconds,
            // Headphones out / AirPlay receiver gone: pause rather than switch to the phone
            // speaker beside a sleeping child. The story run, if one is running, is untouched.
            onRouteLost: () => _ = PauseAsync());
        Platforms.iOS.NativeAudio.DumpNowPlaying();
    }

    private void PushPlaybackState(bool playing)
    {
        Platforms.iOS.NativeAudio.UpdatePlaybackState(PositionSeconds, playing ? 1.0 : 0.0);
    }

    private static void ClearNativeNowPlaying()
    {
        Platforms.iOS.NativeAudio.ClearNowPlaying();
    }
}

/// <summary>Live TTS narration — the degraded mode for stories with no audio file. Drives
/// TextToSpeech.SpeakAsync sentence-by-sentence; position derives from the estimated
/// per-sentence timestamps; seek snaps to the nearest sentence boundary.
///
/// <para><b>No native-player integration here, deliberately.</b> This path speaks through
/// <c>AVSpeechSynthesizer</c> live, which is an utterance engine, not
/// media players: there is no seekable item to hand AVPlayer, no timeline an AirPlay receiver
/// or a car head unit can scrub, and no meaningful Now Playing entry (the "duration" is an
/// estimate from a words-per-second model, and a remote pause can only be honoured between
/// sentences). Publishing a lock-screen entry over it would be a lie with a progress bar.
/// So this player clears Now Playing on load and stays out of the way — recorded honestly in
/// <c>docs/known-gaps.md</c>. It disappears entirely once every story has rendered audio on
/// disk — the AVSpeechSynthesizer narration engine's job (MauiProgram.RegisterTtsEngine).</para></summary>
public sealed class LiveTtsNarrationPlayer : INarrationPlayer
{
    private readonly PlatformTtsEngine _tts;

    private List<string> _sentences = [];
    private List<SentenceTimestamp> _timestamps = [];
    private NarratorVoice _voice;
    private int _sentenceIndex;
    private CancellationTokenSource? _speakCts;
    private Task? _speakLoop;
    private IDispatcherTimer? _tick;
    private DateTime _sentenceStartedUtc;
    private bool _isPlaying;

    public LiveTtsNarrationPlayer(PlatformTtsEngine tts) => _tts = tts;

    public event EventHandler? PositionChanged;
    public event EventHandler? Completed;

    public bool CanPlay(StoryRecord story) => true; // universal fallback

    public bool IsPlaying => _isPlaying;
    public double PositionSeconds { get; private set; }
    public double DurationSeconds { get; private set; }

    /// <summary>Platform TTS speaks at system volume; it cannot re-level an utterance in
    /// flight, so the Voice mix row is disabled in live mode (SupportsVolume=false).</summary>
    public double Volume { get; set; } = 1.0;
    public bool SupportsVolume => false;

    public Task LoadAsync(StoryRecord story, bool teaserOnly, CancellationToken ct)
    {
        StopInternal();
        // No seekable media item exists on this path — make sure a previous story's entry
        // isn't left on the lock screen / AirPlay receiver claiming to be this one.
        Platforms.iOS.NativeAudio.ClearNowPlaying();
        _voice = story.Voice;
        _sentences = StoryPipeline.SplitSentences(story.FullText);
        if (teaserOnly) _sentences = _sentences.Take(3).ToList();

        _timestamps = [];
        if (!string.IsNullOrEmpty(story.TimestampsJson))
        {
            try
            {
                _timestamps = JsonSerializer.Deserialize<List<SentenceTimestamp>>(story.TimestampsJson) ?? [];
            }
            catch (JsonException) { _timestamps = []; }
        }
        if (_timestamps.Count != _sentences.Count)
        {
            // (Re-)estimate: words / 2.6 per second, floor 0.8s.
            _timestamps = [];
            double t = 0;
            for (var i = 0; i < _sentences.Count; i++)
            {
                var words = _sentences[i].Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                var duration = Math.Max(0.8, words / PlatformTtsEngine.WordsPerSecond);
                _timestamps.Add(new SentenceTimestamp { Index = i, StartSeconds = t, EndSeconds = t + duration });
                t += duration;
            }
        }
        else if (teaserOnly)
        {
            _timestamps = _timestamps.Take(_sentences.Count).ToList();
        }

        DurationSeconds = _timestamps.Count > 0 ? _timestamps[^1].EndSeconds : 0;
        _sentenceIndex = 0;
        PositionSeconds = 0;
        return Task.CompletedTask;
    }

    public Task PlayAsync()
    {
        if (_isPlaying || _sentences.Count == 0) return Task.CompletedTask;
        if (_sentenceIndex >= _sentences.Count) _sentenceIndex = 0;

        _isPlaying = true;
        _speakCts = new CancellationTokenSource();
        var token = _speakCts.Token;
        _speakLoop = RunSpeakLoopAsync(token);
        StartTick();
        return Task.CompletedTask;
    }

    private async Task RunSpeakLoopAsync(CancellationToken token)
    {
        try
        {
            var options = await _tts.OptionsForAsync(_voice);
            while (!token.IsCancellationRequested && _sentenceIndex < _sentences.Count)
            {
                _sentenceStartedUtc = DateTime.UtcNow;
                PositionSeconds = _timestamps[_sentenceIndex].StartSeconds;
                RaiseOnMainThread(PositionChanged);
                await TextToSpeech.SpeakAsync(_sentences[_sentenceIndex], options, token);
                if (token.IsCancellationRequested) break;
                _sentenceIndex++;
            }

            if (!token.IsCancellationRequested && _sentenceIndex >= _sentences.Count)
            {
                _isPlaying = false;
                PositionSeconds = DurationSeconds;
                StopTick();
                RaiseOnMainThread(PositionChanged);
                RaiseOnMainThread(Completed);
            }
        }
        catch (OperationCanceledException)
        {
            // pause/stop/seek
        }
        catch (Exception)
        {
            _isPlaying = false;
            StopTick();
        }
    }

    public Task PauseAsync()
    {
        _isPlaying = false;
        _speakCts?.Cancel();
        StopTick();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        StopInternal();
        RaiseOnMainThread(PositionChanged);
        return Task.CompletedTask;
    }

    public async Task SeekAsync(double seconds)
    {
        var wasPlaying = _isPlaying;
        _speakCts?.Cancel();
        if (_speakLoop is not null)
        {
            try { await _speakLoop; } catch (OperationCanceledException) { }
        }
        _isPlaying = false;

        // Snap to the nearest sentence boundary.
        var index = _timestamps.FindIndex(t => seconds < t.EndSeconds);
        _sentenceIndex = index < 0 ? Math.Max(0, _sentences.Count - 1) : index;
        PositionSeconds = _timestamps.Count > 0 ? _timestamps[_sentenceIndex].StartSeconds : 0;
        RaiseOnMainThread(PositionChanged);

        if (wasPlaying) await PlayAsync();
    }

    private void StopInternal()
    {
        _speakCts?.Cancel();
        _isPlaying = false;
        _sentenceIndex = 0;
        PositionSeconds = 0;
        StopTick();
    }

    private void StartTick()
    {
        if (Application.Current?.Dispatcher is not { } dispatcher) return;
        _tick ??= dispatcher.CreateTimer();
        _tick.Interval = TimeSpan.FromMilliseconds(300);
        _tick.Tick -= OnTick;
        _tick.Tick += OnTick;
        _tick.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (!_isPlaying || _sentenceIndex >= _timestamps.Count) return;
        var withinSentence = (DateTime.UtcNow - _sentenceStartedUtc).TotalSeconds;
        var stamp = _timestamps[_sentenceIndex];
        PositionSeconds = Math.Min(stamp.StartSeconds + withinSentence, stamp.EndSeconds);
        PositionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void StopTick() => _tick?.Stop();

    private static void RaiseOnMainThread(EventHandler? handler)
    {
        if (handler is null) return;
        MainThread.BeginInvokeOnMainThread(() => handler.Invoke(null, EventArgs.Empty));
    }
}

/// <summary>File-backed player when the story has audio on disk; live TTS otherwise.</summary>
public sealed class NarrationPlayerProvider : INarrationPlayerProvider
{
    private readonly MediaElementNarrationPlayer _file;
    private readonly LiveTtsNarrationPlayer _live;

    public NarrationPlayerProvider(MediaElementNarrationPlayer file, LiveTtsNarrationPlayer live)
    {
        _file = file;
        _live = live;
    }

    public INarrationPlayer GetFor(StoryRecord story) =>
        _file.CanPlay(story) && File.Exists(story.AudioPath) ? _file : _live;
}
