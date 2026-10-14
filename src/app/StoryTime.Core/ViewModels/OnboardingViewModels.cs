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

public sealed partial class VoiceOption : ObservableObject
{
    public VoiceOption(NarratorVoice voice, string title, string blurb)
    {
        Voice = voice;
        Title = title;
        Blurb = blurb;
    }

    public NarratorVoice Voice { get; }
    public string Title { get; }
    public string Blurb { get; }
    public string DisplayLine => $"{Title} — {Blurb}";

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>True from the instant the ▶ ring is tapped until the sample has been handed
    /// to the player (or has failed). A rendered sample is not instant, so the ring must show
    /// SOMETHING immediately or the button reads as dead — the exact founder-reported bug
    /// of 2026-07-31.</summary>
    [ObservableProperty]
    private bool _isPreviewing;
}

/// <summary>Why this exists: both ▶-preview call sites used to `catch (Exception) { }`, so a
/// broken preview was indistinguishable from a dead button — the preview was reported fixed
/// twice while still failing silently. Every step of the chain now leaves a breadcrumb.
/// Greppable as `TWVoice` in the iOS simulator / device console. Failures stay non-fatal: a bad sample must never block
/// onboarding or the shelf.</summary>
internal static class VoicePreviewTrace
{
    public static void Log(string message) => Console.WriteLine($"TWVoice {message}");

    public static void Failed(string stage, Exception ex) =>
        Console.WriteLine($"TWVoice FAILED at {stage}: {ex.GetType().Name}: {ex.Message}");
}

/// <summary>The ▶-preview chain, shared by the onboarding narrator list and the Home
/// "Tonight's recipe" shelf so the two can never drift apart again.
///
/// <para>Cancel-and-replace: a second tap cancels the sample still in flight rather than
/// queueing behind it or being refused. <see cref="VoiceOption.IsPreviewing"/> flips true
/// synchronously on entry so the ring can acknowledge the tap before the render (which is
/// seconds on a file-rendering engine) produces anything.</para></summary>
internal sealed class VoicePreviewRunner(ITtsEngine tts, IVoiceSamplePlayer player)
{
    private CancellationTokenSource? _cts;

    public async Task RunAsync(VoiceOption option)
    {
        var previous = _cts;
        var current = new CancellationTokenSource();
        _cts = current;
        try { previous?.Cancel(); previous?.Dispose(); }
        catch (ObjectDisposedException) { /* already retired */ }

        option.IsPreviewing = true;
        VoicePreviewTrace.Log($"preview requested voice={option.Voice}");
        try
        {
            var path = await tts.RenderVoiceSampleAsync(option.Voice, current.Token).ConfigureAwait(false);
            if (current.Token.IsCancellationRequested)
            {
                VoicePreviewTrace.Log($"preview superseded voice={option.Voice}");
                return;
            }
            // An empty path is NOT a failure: the platform-TTS fallback speaks live and has
            // no file to hand over. Only a real file goes to the one-shot player.
            VoicePreviewTrace.Log(string.IsNullOrEmpty(path)
                ? $"rendered voice={option.Voice} (live-TTS path, spoken directly, no file)"
                : $"rendered voice={option.Voice} file={path}");
            if (!string.IsNullOrEmpty(path))
            {
                await player.PlayAsync(path, current.Token).ConfigureAwait(false);
                VoicePreviewTrace.Log($"playback handed off voice={option.Voice}");
            }
        }
        catch (OperationCanceledException)
        {
            VoicePreviewTrace.Log($"preview cancelled voice={option.Voice}");
        }
        catch (Exception ex)
        {
            // Still non-fatal — but no longer invisible.
            VoicePreviewTrace.Failed($"preview voice={option.Voice}", ex);
        }
        finally
        {
            option.IsPreviewing = false;
        }
    }
}

/// <summary>Onboarding 2 of 3 — narrator, length, series mode.</summary>
public sealed partial class OnboardingVoiceViewModel : ObservableObject
{
    public const string SeriesHint = "One gentle world, two minutes at a time. A Story Bible remembers every name.";
    public const string OneShotHint = "A brand-new story every night.";

    private readonly OnboardingState _state;
    private readonly INavigator _navigator;
    private readonly ITtsEngine _tts;
    private readonly IEntitlements _entitlements;
    private readonly IHaptics _haptics;

    private readonly IVoiceSamplePlayer _samplePlayer;
    private readonly VoicePreviewRunner _preview;

    public OnboardingVoiceViewModel(OnboardingState state, INavigator navigator, ITtsEngine tts, IEntitlements entitlements,
        IHaptics haptics, IVoiceSamplePlayer samplePlayer)
    {
        _state = state;
        _navigator = navigator;
        _tts = tts;
        _entitlements = entitlements;
        _haptics = haptics;
        _samplePlayer = samplePlayer;
        _preview = new VoicePreviewRunner(tts, samplePlayer);
        // The narrator list is on screen the moment this VM exists — warm the samples now.
        new VoiceSampleWarmer(tts).Warm(state.Profile.Voice);

        Voices =
        [
            new VoiceOption(NarratorVoice.Ember, "Ember", "warm & unhurried"),
            new VoiceOption(NarratorVoice.Willow, "Willow", "bright & gentle"),
            new VoiceOption(NarratorVoice.Sage, "Sage", "deep & calm"),
        ];
        foreach (var voice in Voices) voice.IsSelected = voice.Voice == state.Profile.Voice;
        _selectedLength = state.Profile.Length;
        _isSeries = state.Profile.Mode == StoryMode.Series;
    }

    public IReadOnlyList<VoiceOption> Voices { get; }

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

    [RelayCommand]
    private void SelectVoice(VoiceOption option)
    {
        if (!option.IsSelected) _haptics.Selection();
        foreach (var voice in Voices) voice.IsSelected = ReferenceEquals(voice, option);
    }

    /// <summary>▶ ring — select the voice AND preview it.
    ///
    /// <para>AllowConcurrentExecutions is REQUIRED, not incidental. The default
    /// [RelayCommand] async command reports CanExecute=false for its whole duration, and a
    /// preview lasts as long as the render plus the utterance (seconds on the platform-TTS
    /// path, which awaits the full SpeakAsync). With the default, every tap after the first
    /// was silently refused by the command itself — a genuinely dead button. Concurrency is
    /// allowed here and the PREVIOUS preview is cancelled instead, so a repeat tap always
    /// does something.</para></summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task PlaySampleAsync(VoiceOption option)
    {
        if (option is null) return;
        SelectVoice(option);
        await _preview.RunAsync(option);
    }

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
        _state.Profile.Voice = Voices.First(v => v.IsSelected).Voice;
        _state.Profile.Length = SelectedLength;
        _state.Profile.Mode = IsSeries ? StoryMode.Series : StoryMode.OneShot;
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
