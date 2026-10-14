using StoryTime.Core.Abstractions;

namespace StoryTime.App.Services;

/// <summary>Platform looping ambient-bed player that mixes WITH the narration player.
///
/// iOS: AVAudioPlayer (NumberOfLoops = −1) on the app's shared audio session — the
/// MediaElement toolkit configures the Playback category for background narration and we
/// deliberately do not touch the session, so both players coexist and mix natively.
/// Android: Android.Media.MediaPlayer (Looping = true) with USAGE_MEDIA attributes; it mixes
/// with MediaElement's ExoPlayer inside the same app without audio-focus arbitration.
///
/// All starts fade in and all stops fade out over ~1.5 s; SetVolume ramps rather than steps.
/// StopAsync returns immediately (the fade completes in the background) so closing the
/// player sheet never waits on the music.</summary>
public sealed class AmbientPlayer : IAmbientPlayer
{
    private const double FadeSeconds = 1.5;
    private const int RampIntervalMs = 50;

    private readonly object _gate = new();
    private int _generation;
    private bool _isPlaying;
    private bool _rampRunning;
    private bool _releaseAtZero;
    private double _current;
    private double _target;
    private string? _currentPath;

    public bool IsPlaying
    {
        get { lock (_gate) return _isPlaying; }
    }

    public Task StartAsync(string wavPath, double volume, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_isPlaying && wavPath == _currentPath)
            {
                _target = Math.Clamp(volume, 0, 1);
                _releaseAtZero = false;
                StartRampLocked();
                return Task.CompletedTask;
            }

            ReleasePlatformPlayer();
            _generation++;
            try
            {
                CreateAndPlayPlatformPlayer(wavPath);
            }
            catch (Exception)
            {
                // A bed that fails to start stays silent — never disturb narration.
                return Task.CompletedTask;
            }
            _isPlaying = true;
            _currentPath = wavPath;
            _current = 0;
            _target = Math.Clamp(volume, 0, 1);
            _releaseAtZero = false;
            StartRampLocked();
        }
        return Task.CompletedTask;
    }

    public void SetVolume(double volume)
    {
        lock (_gate)
        {
            if (!_isPlaying) return;
            _target = Math.Clamp(volume, 0, 1);
            _releaseAtZero = false;
            StartRampLocked();
        }
    }

    public Task StopAsync()
    {
        lock (_gate)
        {
            if (!_isPlaying) return Task.CompletedTask;
            _isPlaying = false;   // logically stopped now; audio fades out in the background
            _currentPath = null;
            _target = 0;
            _releaseAtZero = true;
            StartRampLocked();
        }
        return Task.CompletedTask;
    }

    // ---------------- volume ramp ----------------

    private void StartRampLocked()
    {
        if (_rampRunning) return;
        _rampRunning = true;
        var generation = _generation;
        _ = Task.Run(() => RampLoopAsync(generation));
    }

    private async Task RampLoopAsync(int generation)
    {
        const double step = RampIntervalMs / 1000.0 / FadeSeconds; // full-scale in FadeSeconds
        while (true)
        {
            double applied;
            bool reached, release;
            lock (_gate)
            {
                if (generation != _generation) { _rampRunning = false; return; }
                _current = _current < _target
                    ? Math.Min(_target, _current + step)
                    : Math.Max(_target, _current - step);
                applied = _current;
                reached = Math.Abs(_current - _target) < 1e-9;
                release = reached && _releaseAtZero && _target <= 0;
            }

            ApplyPlatformVolume(applied);

            if (reached)
            {
                lock (_gate)
                {
                    if (generation != _generation) { _rampRunning = false; return; }
                    if (release)
                    {
                        ReleasePlatformPlayer();
                        _releaseAtZero = false;
                    }
                    // A new target may have landed while we were applying — keep ramping.
                    if (Math.Abs(_current - _target) < 1e-9)
                    {
                        _rampRunning = false;
                        return;
                    }
                }
            }

            await Task.Delay(RampIntervalMs);
        }
    }

    // ---------------- platform players ----------------

#if ANDROID
    private Android.Media.MediaPlayer? _mediaPlayer;

    private void CreateAndPlayPlatformPlayer(string wavPath)
    {
        var player = new Android.Media.MediaPlayer();
        player.SetAudioAttributes(new Android.Media.AudioAttributes.Builder()
            .SetUsage(Android.Media.AudioUsageKind.Media)!
            .SetContentType(Android.Media.AudioContentType.Music)!
            .Build()!);
        player.SetDataSource(wavPath);
        player.Prepare();               // local wav — effectively instant
        player.Looping = true;
        player.SetVolume(0, 0);
        player.Start();
        _mediaPlayer = player;
    }

    private void ApplyPlatformVolume(double volume)
    {
        try
        {
            var v = (float)volume;
            _mediaPlayer?.SetVolume(v, v);
        }
        catch (Java.Lang.IllegalStateException) { /* released underneath the ramp */ }
    }

    private void ReleasePlatformPlayer()
    {
        var player = _mediaPlayer;
        _mediaPlayer = null;
        if (player is null) return;
        try
        {
            if (player.IsPlaying) player.Stop();
        }
        catch (Java.Lang.IllegalStateException) { }
        player.Release();
        player.Dispose();
    }
#elif IOS
    private AVFoundation.AVAudioPlayer? _audioPlayer;

    private void CreateAndPlayPlatformPlayer(string wavPath)
    {
        var player = AVFoundation.AVAudioPlayer.FromUrl(Foundation.NSUrl.FromFilename(wavPath), out var error);
        if (player is null || error is not null)
            throw new InvalidOperationException($"AVAudioPlayer failed: {error?.LocalizedDescription}");
        player.NumberOfLoops = -1;      // seamless infinite loop
        player.Volume = 0;
        player.PrepareToPlay();
        player.Play();
        _audioPlayer = player;
    }

    private void ApplyPlatformVolume(double volume) =>
        _audioPlayer?.SetVolume((float)volume, 0);

    private void ReleasePlatformPlayer()
    {
        var player = _audioPlayer;
        _audioPlayer = null;
        if (player is null) return;
        player.Stop();
        player.Dispose();
    }
#else
    private void CreateAndPlayPlatformPlayer(string wavPath) { }
    private void ApplyPlatformVolume(double volume) { }
    private void ReleasePlatformPlayer() { }
#endif
}
