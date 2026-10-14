using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StoryTime.Contracts;
using StoryTime.Core.Abstractions;
using StoryTime.Core.Services;

namespace StoryTime.Core.ViewModels;

/// <summary>State behind the "Tonight's recipe" shelf on Home (founder request 2026-07-30:
/// edit interests / series mode from the generation page, elegantly — a compact
/// slide-up shelf in the making sheet visual language, entry point the summary line).
///
/// Live-persistence covenant: every change writes through to the active profile via
/// <see cref="IStoryStore.SaveProfileAsync"/> IMMEDIATELY — no Save button, no dirty state.
/// The shelf edits the SAME ChildProfile instance Home holds (passed into OpenAsync), so
/// Home's summary line recomputes from shared state on <see cref="ProfileChanged"/>.
/// During an active story run the running pipeline holds its own snapshot (HomeViewModel clones
/// the profile at story start), so edits here affect the NEXT story run only — the eyebrow
/// gains a "for the next story" whisper instead of a warning.</summary>
public sealed partial class RecipeShelfViewModel : ObservableObject
{
    public const string SeriesHint = OnboardingVoiceViewModel.SeriesHint;
    public const string OneShotHint = OnboardingVoiceViewModel.OneShotHint;

    private readonly IStoryStore _store;
    private readonly IHaptics _haptics;
    private readonly IStoryMakingHost _makingHost;

    private ChildProfile? _profile;

    /// <summary>No narrator picker: one storyteller (Serena) reads every story (founder
    /// decision 2026-10-14).</summary>
    public RecipeShelfViewModel(IStoryStore store, IHaptics haptics, IStoryMakingHost makingHost)
    {
        _store = store;
        _haptics = haptics;
        _makingHost = makingHost;

        Interests = new ObservableCollection<InterestOption>(
            OnboardingProfileViewModel.AllInterests.Select(i => new InterestOption(i, false)));
    }

    /// <summary>Raised after every write-through save; Home re-renders its summary line.</summary>
    public event EventHandler? ProfileChanged;

    /// <summary>Drives the App-layer BottomShelfHost (two-way: a drag-down/scrim-tap
    /// dismissal in the control writes false back).</summary>
    [ObservableProperty]
    private bool _isOpen;

    /// <summary>True while a story run is in flight — the eyebrow whispers "for the next story"
    /// (the running story already snapshotted its recipe; no warning needed).</summary>
    [ObservableProperty]
    private bool _isMaking;

    public ObservableCollection<InterestOption> Interests { get; }

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
        IsSeries = profile.Mode == StoryMode.Series;
        IsMaking = _makingHost.IsMaking;
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
