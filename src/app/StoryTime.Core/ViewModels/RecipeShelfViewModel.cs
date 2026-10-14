using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StoryTime.Contracts;
using StoryTime.Core.Abstractions;
using StoryTime.Core.Services;

namespace StoryTime.Core.ViewModels;

/// <summary>State behind the "Tonight's recipe" shelf on Home (founder request 2026-07-30:
/// edit interests / narrator / series mode from the generation page, elegantly — a compact
/// slide-up shelf in the weave-sheet visual language, entry point the summary line).
///
/// Live-persistence covenant: every change writes through to the active profile via
/// <see cref="IStoryStore.SaveProfileAsync"/> IMMEDIATELY — no Save button, no dirty state.
/// The shelf edits the SAME ChildProfile instance Home holds (passed into OpenAsync), so
/// Home's summary line recomputes from shared state on <see cref="ProfileChanged"/>.
/// During an active weave the running pipeline holds its own snapshot (HomeViewModel clones
/// the profile at weave start), so edits here affect the NEXT weave only — the eyebrow
/// gains a "for the next story" whisper instead of a warning.</summary>
public sealed partial class RecipeShelfViewModel : ObservableObject
{
    public const string SeriesHint = OnboardingVoiceViewModel.SeriesHint;
    public const string OneShotHint = OnboardingVoiceViewModel.OneShotHint;

    private readonly IStoryStore _store;
    private readonly ITtsEngine _tts;
    private readonly IHaptics _haptics;
    private readonly IWeaveHost _weaveHost;

    private ChildProfile? _profile;

    private readonly IVoiceSamplePlayer _samplePlayer;
    private readonly VoicePreviewRunner _preview;
    private readonly VoiceSampleWarmer _warmer;

    public RecipeShelfViewModel(IStoryStore store, ITtsEngine tts, IHaptics haptics, IWeaveHost weaveHost,
        IVoiceSamplePlayer samplePlayer)
    {
        _store = store;
        _tts = tts;
        _haptics = haptics;
        _weaveHost = weaveHost;
        _samplePlayer = samplePlayer;
        _preview = new VoicePreviewRunner(tts, samplePlayer);
        _warmer = new VoiceSampleWarmer(tts);

        Interests = new ObservableCollection<InterestOption>(
            OnboardingProfileViewModel.AllInterests.Select(i => new InterestOption(i, false)));
        Voices =
        [
            new VoiceOption(NarratorVoice.Ember, "Ember", "warm & unhurried"),
            new VoiceOption(NarratorVoice.Willow, "Willow", "bright & gentle"),
            new VoiceOption(NarratorVoice.Sage, "Sage", "deep & calm"),
        ];
    }

    /// <summary>Raised after every write-through save; Home re-renders its summary line.</summary>
    public event EventHandler? ProfileChanged;

    /// <summary>Drives the App-layer BottomShelfHost (two-way: a drag-down/scrim-tap
    /// dismissal in the control writes false back).</summary>
    [ObservableProperty]
    private bool _isOpen;

    /// <summary>True while a weave runs — the eyebrow whispers "for the next story"
    /// (the running weave already snapshotted its recipe; no warning needed).</summary>
    [ObservableProperty]
    private bool _isWeaving;

    public ObservableCollection<InterestOption> Interests { get; }

    public IReadOnlyList<VoiceOption> Voices { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeHint))]
    private bool _isSeries = true;

    public string ModeHint => IsSeries ? SeriesHint : OneShotHint;

    /// <summary>Loads the shelf from the profile Home holds and slides it up. The same
    /// instance is mutated + saved on every change, so Home stays in sync by reference.</summary>
    public Task OpenAsync(ChildProfile profile)
    {
        _profile = profile;
        foreach (var option in Interests) option.IsSelected = profile.Interests.Contains(option.Name);
        foreach (var voice in Voices) voice.IsSelected = voice.Voice == profile.Voice;
        IsSeries = profile.Mode == StoryMode.Series;
        IsWeaving = _weaveHost.IsWeaving;
        // Cold sample renders read as a dead button — warm the samples the moment the
        // picker is visible so the first ▶ tap plays instantly.
        _warmer.Warm(profile.Voice);
        IsOpen = true;
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void Close()
    {
        if (!IsOpen) return;
        _haptics.Light();
        IsOpen = false;
    }

    [RelayCommand]
    private async Task ToggleInterestAsync(InterestOption option)
    {
        if (option is null || _profile is null) return;
        _haptics.Selection();
        option.IsSelected = !option.IsSelected;
        _profile.Interests = Interests.Where(i => i.IsSelected).Select(i => i.Name).ToList();
        await SaveAsync();
    }

    [RelayCommand]
    private async Task SelectVoiceAsync(VoiceOption option)
    {
        if (option is null || _profile is null) return;
        if (!option.IsSelected) _haptics.Selection();
        foreach (var voice in Voices) voice.IsSelected = ReferenceEquals(voice, option);
        if (_profile.Voice == option.Voice) return;   // re-tap of the current voice: no write
        _profile.Voice = option.Voice;
        await SaveAsync();
    }

    /// <summary>▶ ring — selects AND previews the voice (onboarding pattern; a failed
    /// sample must never block the shelf, but it is no longer silent — see
    /// <see cref="VoicePreviewRunner"/>).
    ///
    /// <para>AllowConcurrentExecutions, as in onboarding: the default async command reports
    /// CanExecute=false for the whole render+utterance, which made every tap after the first
    /// a no-op at the command layer. Repeat taps now cancel and replace instead.</para></summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task PlaySampleAsync(VoiceOption option)
    {
        if (option is null) return;
        await SelectVoiceAsync(option);
        await _preview.RunAsync(option);
    }

    [RelayCommand]
    private async Task SetSeriesAsync(bool isSeries)
    {
        if (_profile is null) return;
        if (isSeries == IsSeries) return;
        _haptics.Selection();
        IsSeries = isSeries;
        _profile.Mode = isSeries ? StoryMode.Series : StoryMode.OneShot;
        await SaveAsync();
    }

    private async Task SaveAsync()
    {
        if (_profile is null) return;
        await _store.SaveProfileAsync(_profile);
        ProfileChanged?.Invoke(this, EventArgs.Empty);
    }
}
