using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StoryTime.Contracts;
using StoryTime.Core.Abstractions;
using StoryTime.Core.Services;

namespace StoryTime.Core.ViewModels;

public sealed partial class InterestOption : ObservableObject
{
    public InterestOption(string name, bool selected)
    {
        Name = name;
        _isSelected = selected;
    }

    public string Name { get; }

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>Onboarding 1 of 3 — "Who's listening tonight?"</summary>
public sealed partial class OnboardingProfileViewModel : ObservableObject
{
    public static readonly string[] AllInterests =
        ["Foxes", "Ocean", "Magic", "Space", "Forest", "Clouds", "Stars", "Music", "Trains", "Gardens"];

    private readonly OnboardingState _state;
    private readonly INavigator _navigator;
    private readonly IHaptics _haptics;

    public OnboardingProfileViewModel(OnboardingState state, INavigator navigator, IHaptics haptics)
    {
        _state = state;
        _navigator = navigator;
        _haptics = haptics;
        _nickname = state.Profile.Name;
        _selectedAge = state.Profile.Age;
        Interests = new ObservableCollection<InterestOption>(
            AllInterests.Select(i => new InterestOption(i, state.Profile.Interests.Contains(i))));
    }

    [ObservableProperty]
    private string _nickname = string.Empty;

    [ObservableProperty]
    private int _selectedAge = 5;

    public ObservableCollection<InterestOption> Interests { get; }

    public IReadOnlyList<int> Ages { get; } = [3, 4, 5, 6, 7, 8];

    [RelayCommand]
    private void SelectAge(int age)
    {
        if (age != SelectedAge) _haptics.Selection();
        SelectedAge = age;
    }

    [RelayCommand]
    private void ToggleInterest(InterestOption option)
    {
        _haptics.Selection();
        option.IsSelected = !option.IsSelected;
    }

    [RelayCommand]
    private async Task ContinueAsync()
    {
        _haptics.Light();
        _state.Profile.Name = string.IsNullOrWhiteSpace(Nickname) ? "Little One" : Nickname.Trim();
        _state.Profile.Age = Math.Clamp(SelectedAge, 3, 8);
        _state.Profile.Interests = Interests.Where(i => i.IsSelected).Select(i => i.Name).ToList();
        await _navigator.GoToAsync(Routes.OnboardingVoice);
    }
}

/// <summary>Onboarding 2 of 3 — length and series mode. (The narrator picker is gone: one
/// storyteller, Serena, reads every story — founder decision 2026-10-14.)</summary>
public sealed partial class OnboardingVoiceViewModel : ObservableObject
{
    public const string SeriesHint = "One gentle world, two minutes at a time. A Story Bible remembers every name.";
    public const string OneShotHint = "A brand-new story every night.";

    private readonly OnboardingState _state;
    private readonly INavigator _navigator;
    private readonly IEntitlements _entitlements;
    private readonly IHaptics _haptics;
    private readonly IStorytellerVoices? _storytellerVoices;

    public OnboardingVoiceViewModel(OnboardingState state, INavigator navigator, IEntitlements entitlements,
        IHaptics haptics, IStorytellerVoices? storytellerVoices = null)
    {
        _storytellerVoices = storytellerVoices;
        _state = state;
        _navigator = navigator;
        _entitlements = entitlements;
        _haptics = haptics;
        _selectedLength = state.Profile.Length;
        _isSeries = state.Profile.Mode == StoryMode.Series;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LengthMinutes))]
    private StoryLength _selectedLength = StoryLength.Ten;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeHint))]
    private bool _isSeries = true;

    [ObservableProperty]
    private bool _showPremiumHint;

    public int LengthMinutes => (int)SelectedLength;

    public string ModeHint => IsSeries ? SeriesHint : OneShotHint;

    /// <summary>Returns true when the length was accepted (15 is Premium-gated).</summary>
    [RelayCommand]
    private void SelectLength(StoryLength length)
    {
        if (!_entitlements.CanUseLength(length))
        {
            _haptics.Warning();          // premium-gate bump
            ShowPremiumHint = true;
            return;
        }
        if (length != SelectedLength) _haptics.Selection();
        ShowPremiumHint = false;
        SelectedLength = length;
    }

    /// <summary>The gate upsell (premium hint) routes to the Premium page on tap.</summary>
    [RelayCommand]
    private Task OpenPremiumAsync()
    {
        _haptics.Light();
        return _navigator.GoToAsync(Routes.Premium);
    }

    [RelayCommand]
    private void SetSeries(bool isSeries)
    {
        if (isSeries != IsSeries) _haptics.Selection();
        IsSeries = isSeries;
    }

    [RelayCommand]
    private async Task ContinueAsync()
    {
        _haptics.Light();
        _state.Profile.Length = SelectedLength;
        _state.Profile.Mode = IsSeries ? StoryMode.Series : StoryMode.OneShot;
        // Founder rule 2026-10-14: no finishing onboarding on a compact voice — the
        // storyteller-voice step blocks until a premium/enhanced voice is installed.
        if (_storytellerVoices is { } voices)
        {
            voices.Refresh();
            if (!voices.HasStorytellerVoice)
            {
                await _navigator.GoToAsync($"{Routes.StorytellerVoice}?purpose=onboarding");
                return;
            }
        }
        await _navigator.GoToAsync(Routes.OnboardingRules);
    }

    [RelayCommand]
    private Task BackAsync() => _navigator.GoBackAsync();
}

/// <summary>Onboarding 3 of 3 — "Three things, then bed." Completing it creates the profile
/// and the ready-made welcome story.</summary>
public sealed partial class OnboardingRulesViewModel : ObservableObject
{
    private readonly OnboardingState _state;
    private readonly INavigator _navigator;
    private readonly IStoryStore _store;
    private readonly ISettingsStore _settings;
    private readonly IClock _clock;
    private readonly WelcomeStoryFactory _welcomeFactory;
    private readonly IHaptics _haptics;

    public OnboardingRulesViewModel(OnboardingState state, INavigator navigator, IStoryStore store,
        ISettingsStore settings, IClock clock, WelcomeStoryFactory welcomeFactory, IHaptics haptics)
    {
        _state = state;
        _navigator = navigator;
        _store = store;
        _settings = settings;
        _clock = clock;
        _welcomeFactory = welcomeFactory;
        _haptics = haptics;
    }

    [ObservableProperty]
    private bool _isBusy;

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsBusy) return;
        _haptics.Medium();   // "Start my 7 free nights" — crossing the threshold into the app
        IsBusy = true;
        try
        {
            await _store.SaveProfileAsync(_state.Profile);
            _settings.FirstRunUtc ??= _clock.UtcNow;
            _settings.OnboardingComplete = true;
            await _welcomeFactory.CreateAsync(_state.Profile, CancellationToken.None);
            await _navigator.GoToAsync(Routes.Home);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task BackAsync() => _navigator.GoBackAsync();
}
