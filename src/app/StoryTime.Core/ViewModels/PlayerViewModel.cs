using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StoryTime.Contracts;
using StoryTime.Core.Abstractions;
using StoryTime.Core.Audio;
using StoryTime.Core.Services;

namespace StoryTime.Core.ViewModels;

public sealed partial class PlayerViewModel : ObservableObject
{
    /// <summary>Static duck: bed volume = slider × this while narration is speaking, so the
    /// music always sits well under the voice.</summary>
    public const double AmbientDuckWhileNarrating = 0.35;

    /// <summary>Bed factor while narration is paused-but-open. Deliberately NOT the full
    /// slider value: a jump to full volume on pause would be startling at bedtime; a gentle
    /// lift keeps the room warm without waking anyone.</summary>
    public const double AmbientPausedFactor = 0.55;

    private readonly IStoryStore _store;
    private readonly INarrationPlayerProvider _players;
    private readonly IPosterComposer _composer;
    private readonly PlayerLaunchContext _context;
    private readonly INavigator _navigator;
    private readonly IHaptics _haptics;
    private readonly IEntitlements _entitlements;
    private readonly ISettingsStore _settings;
    private readonly IAmbientPlayer _ambient;
    private readonly AmbientTrackProvider _ambientTracks;

    private INarrationPlayer? _player;
    private StoryRecord? _story;
    private bool _ambientStarting;
    private bool _activated;

    public PlayerViewModel(IStoryStore store, INarrationPlayerProvider players, IPosterComposer composer,
        PlayerLaunchContext context, INavigator navigator, IHaptics haptics, IEntitlements entitlements,
        ISettingsStore settings, IAmbientPlayer ambient, AmbientTrackProvider ambientTracks)
    {
        _store = store;
        _players = players;
        _composer = composer;
        _context = context;
        _navigator = navigator;
        _haptics = haptics;
        _entitlements = entitlements;
        _settings = settings;
        _ambient = ambient;
        _ambientTracks = ambientTracks;
    }

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _eyebrow = string.Empty;

    [ObservableProperty]
    private string _recapLine = string.Empty;

    public bool HasRecap => RecapLine.Length > 0;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private double _positionSeconds;

    [ObservableProperty]
    private double _durationSeconds;

    [ObservableProperty]
    private bool _isTeaserOnly;

    [ObservableProperty]
    private bool _isAwaitingApproval;

    [ObservableProperty]
    private string _continueLabel = "Continue the series · +2 min";

    [ObservableProperty]
    private bool _isContinueBusy;

    [ObservableProperty]
    private bool _isSeries;

    /// <summary>True when Premium makes the Music mix row live; false shows the PREMIUM
    /// tag routing to the paygate.</summary>
    [ObservableProperty]
    private bool _isMusicLive;

    public bool IsMusicLocked => !IsMusicLive;

    /// <summary>Ambient bed mix level 0..1 — persisted per app via ISettingsStore.</summary>
    [ObservableProperty]
    private double _musicVolume;

    /// <summary>Narration volume 0..1 — live for file-backed narration, disabled for
    /// live platform TTS (which cannot re-level mid-utterance). Session-scoped.</summary>
    [ObservableProperty]
    private double _voiceVolume = 1.0;

    [ObservableProperty]
    private bool _isVoiceRowEnabled;

    public ResolvedPoster? Poster { get; private set; }

    public double ProgressFraction => DurationSeconds <= 0 ? 0 : Math.Clamp(PositionSeconds / DurationSeconds, 0, 1);

    public string PositionText => Format(PositionSeconds);
    public string DurationText => Format(DurationSeconds);

    public async Task ActivateAsync()
    {
        _haptics.Light();   // the player modal opening under the finger
        _story = await _store.GetStoryAsync(_context.StoryId);
        if (_story is null)
        {
            // No launch context (QA-route cold open): fall back to the newest playable story
            // instead of an empty player. In-app navigation always sets the context first.
            var profile = await _store.GetActiveProfileAsync();
            if (profile is null) return;
            _story = (await _store.GetStoriesAsync(profile.Id))
                .FirstOrDefault(s => s.State is StoryState.Ready or StoryState.AwaitingApproval);
            if (_story is null) return;
        }

        Title = _story.Title;
        IsSeries = _story.Mode == StoryMode.Series;
        RecapLine = _story.RecapLine;
        OnPropertyChanged(nameof(HasRecap));
        IsAwaitingApproval = _story.State == StoryState.AwaitingApproval;
        IsTeaserOnly = _context.TeaserOnly && IsAwaitingApproval;

        Eyebrow = _story.Mode == StoryMode.Series && _story.EpisodeNumber > 0
            ? $"EPISODE {_story.EpisodeNumber} · 4 LAYERS · TILTS WITH YOUR PHONE"
            : "ONE-SHOT · 4 LAYERS · TILTS WITH YOUR PHONE";

        if (!string.IsNullOrEmpty(_story.PosterSpecJson))
        {
            var spec = JsonSerializer.Deserialize<PosterSpec>(_story.PosterSpecJson);
            if (spec is not null)
            {
                Poster = _composer.Resolve(spec);
                OnPropertyChanged(nameof(Poster));
            }
        }

        _player = _players.GetFor(_story);
        _player.PositionChanged += OnPositionChanged;
        _player.Completed += OnCompleted;
        await _player.LoadAsync(_story, IsTeaserOnly, CancellationToken.None);
        DurationSeconds = _player.DurationSeconds > 0 ? _player.DurationSeconds : _story.DurationSeconds;
        OnPropertyChanged(nameof(DurationText));

        // Mix rows: Music is live for Premium; Voice is live where the backend can re-level.
        IsMusicLive = _entitlements.IsPremium;
        MusicVolume = Math.Clamp(_settings.MusicVolume, 0, 1);
        IsVoiceRowEnabled = _player.SupportsVolume;
        VoiceVolume = _player.SupportsVolume ? Math.Clamp(_player.Volume, 0, 1) : 1.0;
        _activated = true;
    }

    public async Task DeactivateAsync()
    {
        _activated = false;
        await _ambient.StopAsync();   // fades out; safe when never started
        if (_player is null) return;
        _player.PositionChanged -= OnPositionChanged;
        _player.Completed -= OnCompleted;
        await _player.StopAsync();
        _player = null;
    }

    [RelayCommand]
    private async Task PlayPauseAsync()
    {
        if (_player is null) return;
        _haptics.Light();
        if (_player.IsPlaying)
        {
            await _player.PauseAsync();
            IsPlaying = false;
            // Bed keeps breathing while paused-but-open, gently lifted (see AmbientPausedFactor).
            await SyncAmbientAsync(allowStart: false);
        }
        else
        {
            await _player.PlayAsync();
            IsPlaying = true;
            await SyncAmbientAsync(allowStart: true);
        }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (_player is null) return;
        _haptics.Light();
        await _player.StopAsync();
        IsPlaying = false;
        PositionSeconds = 0;
        NotifyPosition();
        await _ambient.StopAsync();
    }

    [RelayCommand]
    private async Task ReplayAsync()
    {
        if (_player is null) return;
        _haptics.Light();
        await _player.SeekAsync(0);
        await _player.PlayAsync();
        IsPlaying = true;
        await SyncAmbientAsync(allowStart: true);
    }

    [RelayCommand]
    private async Task SeekToFractionAsync(double fraction)
    {
        if (_player is null || DurationSeconds <= 0) return;
        _haptics.Selection();   // seek commit (tap or scrub release; scrub steps Tick in the view)
        await _player.SeekAsync(Math.Clamp(fraction, 0, 1) * DurationSeconds);
        PositionSeconds = _player.PositionSeconds;
        NotifyPosition();
    }

    /// <summary>Parent approval: the teaser passed muster; the story unlocks for the shelf.</summary>
    [RelayCommand]
    private async Task ApproveAsync()
    {
        if (_story is null) return;
        _haptics.Celebrate();   // approval unlock — "Sounds lovely" deserves the two-beat
        _story.State = StoryState.Ready;
        await _store.SaveStoryAsync(_story);
        IsAwaitingApproval = false;
        IsTeaserOnly = false;
        if (_player is not null)
        {
            await _player.LoadAsync(_story, teaserOnly: false, CancellationToken.None);
            DurationSeconds = _player.DurationSeconds > 0 ? _player.DurationSeconds : _story.DurationSeconds;
            PositionSeconds = 0;
            NotifyPosition();
        }
    }

    /// <summary>Design-state stub (reported in known-gaps): shows the pressed state, then
    /// returns to idle. Real coda weaving lands with the model engines.</summary>
    [RelayCommand]
    private async Task ContinueSeriesAsync()
    {
        if (IsContinueBusy) return;
        IsContinueBusy = true;
        ContinueLabel = "Making 2 more minutes… ✓";
        await Task.Delay(2200);
        ContinueLabel = "Continue the series · +2 min";
        IsContinueBusy = false;
    }

    [RelayCommand]
    private Task CloseAsync() => _navigator.GoBackAsync();

    /// <summary>The Music · PREMIUM mix row routes to the Premium page.</summary>
    [RelayCommand]
    private Task OpenPremiumAsync()
    {
        _haptics.Light();
        return _navigator.GoToAsync(Routes.Premium);
    }

    private void OnPositionChanged(object? sender, EventArgs e)
    {
        if (_player is null) return;
        PositionSeconds = _player.PositionSeconds;
        if (_player.DurationSeconds > 0) DurationSeconds = _player.DurationSeconds;
        NotifyPosition();
    }

    private void OnCompleted(object? sender, EventArgs e)
    {
        IsPlaying = false;
        PositionSeconds = DurationSeconds;
        NotifyPosition();
        // The story ended gently; let the bed fade away with it.
        _ = _ambient.StopAsync();
    }

    // ---------------- ambient bed (Premium music) ----------------

    partial void OnIsMusicLiveChanged(bool value) => OnPropertyChanged(nameof(IsMusicLocked));

    partial void OnMusicVolumeChanged(double value)
    {
        if (!_activated) return;
        _settings.MusicVolume = Math.Clamp(value, 0, 1);
        // Live retarget while dragging; dragging up from silence also auditions the bed
        // even before play is pressed (paused-level volume).
        _ = SyncAmbientAsync(allowStart: true);
    }

    partial void OnVoiceVolumeChanged(double value)
    {
        if (!_activated || _player is null || !_player.SupportsVolume) return;
        _player.Volume = Math.Clamp(value, 0, 1);
    }

    /// <summary>Duck policy: bed = slider × 0.35 while narrating, slider × 0.55 while
    /// paused-but-open; fades out on stop/completed/close. Never throws — the bed must
    /// never break narration.</summary>
    private async Task SyncAmbientAsync(bool allowStart)
    {
        if (!IsMusicLive || _story is null) return;
        var target = AmbientTarget();
        if (_ambient.IsPlaying)
        {
            if (MusicVolume < 0.005) { await _ambient.StopAsync(); return; }
            _ambient.SetVolume(target);
            return;
        }
        if (!allowStart || MusicVolume < 0.005 || _ambientStarting) return;

        _ambientStarting = true;
        try
        {
            var path = await _ambientTracks.GetOrRenderAsync(_story, CancellationToken.None);
            if (_activated && IsMusicLive && MusicVolume >= 0.005)
                await _ambient.StartAsync(path, AmbientTarget(), CancellationToken.None);
        }
        catch (Exception)
        {
            // Composition/playback trouble stays silent — narration is the product.
        }
        finally
        {
            _ambientStarting = false;
        }
    }

    private double AmbientTarget() =>
        Math.Clamp(MusicVolume, 0, 1) * (IsPlaying ? AmbientDuckWhileNarrating : AmbientPausedFactor);

    private void NotifyPosition()
    {
        OnPropertyChanged(nameof(ProgressFraction));
        OnPropertyChanged(nameof(PositionText));
        OnPropertyChanged(nameof(DurationText));
    }

    private static string Format(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }
}
