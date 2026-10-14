using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StoryTime.Contracts;
using StoryTime.Core.Abstractions;

namespace StoryTime.Core.ViewModels;

/// <summary>Parent settings — biometric-gated on every entry (structural, not optional).</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IBiometricGate _biometrics;
    private readonly ISettingsStore _settings;
    private readonly IEntitlements _entitlements;
    private readonly IStoryStore _store;
    private readonly INavigator _navigator;
    private readonly IHaptics _haptics;

    private ChildProfile? _profile;

    public SettingsViewModel(IBiometricGate biometrics, ISettingsStore settings, IEntitlements entitlements,
        IStoryStore store, INavigator navigator, IHaptics haptics)
    {
        _biometrics = biometrics;
        _settings = settings;
        _entitlements = entitlements;
        _store = store;
        _navigator = navigator;
        _haptics = haptics;

        _approvalStep = settings.ApprovalStep;
        _storyReadyNotifications = settings.StoryReadyNotifications;

        ProfileInterests = new ObservableCollection<InterestOption>(
            OnboardingProfileViewModel.AllInterests.Select(i => new InterestOption(i, false)));
    }

    [ObservableProperty]
    private bool _isUnlocked;

    [ObservableProperty]
    private bool _gateFailed;

    [ObservableProperty]
    private string _badgeText = string.Empty;

    [ObservableProperty]
    private string _trialLine = string.Empty;

    [ObservableProperty]
    private bool _approvalStep;

    [ObservableProperty]
    private bool _storyReadyNotifications;

    [ObservableProperty]
    private string _storageText = "…";

    /// <summary>Current content filter level name for the nav row ("Gentle" / "Brave-ish").</summary>
    [ObservableProperty]
    private string _contentFilterName = "Gentle";

    // ---- Inline profile editor (kept deliberately tight) ----

    [ObservableProperty]
    private bool _isProfileEditorVisible;

    [ObservableProperty]
    private string _profileName = string.Empty;

    [ObservableProperty]
    private int _profileAge = 5;

    public ObservableCollection<InterestOption> ProfileInterests { get; }

    public IReadOnlyList<int> Ages { get; } = [3, 4, 5, 6, 7, 8];

    /// <summary>Biometric prompt on every entry. Returns true when the gate opened.</summary>
    public async Task<bool> ActivateAsync(CancellationToken ct)
    {
        IsUnlocked = false;
        GateFailed = false;

        _haptics.Light();                // gate appearing — the door knocks softly
        var passed = await _biometrics.AuthenticateAsync("Open parent settings", ct);
        if (!passed)
        {
            _haptics.Warning();          // gate fail — gentle bump, veil offers "Try again"
            GateFailed = true;
            return false;
        }

        _haptics.Light();                // gate pass
        IsUnlocked = true;
        BadgeText = $"✓ {_biometrics.MechanismName.ToUpperInvariant()}";
        TrialLine = _entitlements.IsPremium
            ? "Premium · unlocked for testing"
            : $"Trial · {_entitlements.TrialNightsLeft} nights left";

        _profile = await _store.GetActiveProfileAsync();
        if (_profile is not null)
        {
            ProfileName = _profile.Name;
            ProfileAge = _profile.Age;
            foreach (var option in ProfileInterests)
                option.IsSelected = _profile.Interests.Contains(option.Name);
        }

        var bytes = await _store.GetStoredBytesAsync();
        StorageText = $"{FormatBytes(bytes)}, on device";
        ContentFilterName = ContentFiltersViewModel.DisplayName(_settings.ContentFilter);
        return true;
    }

    partial void OnApprovalStepChanged(bool value)
    {
        _settings.ApprovalStep = value;
        _haptics.Selection();
    }

    partial void OnStoryReadyNotificationsChanged(bool value)
    {
        _settings.StoryReadyNotifications = value;
        _haptics.Selection();
    }

    [RelayCommand]
    private void ToggleApproval() => ApprovalStep = !ApprovalStep;

    [RelayCommand]
    private void ToggleNotifications() => StoryReadyNotifications = !StoryReadyNotifications;

    /// <summary>UPGRADE → routes to the Premium page (the coming-soon sheet it replaced
    /// lives on as the page's honest price row).</summary>
    [RelayCommand]
    private Task ShowUpgradeAsync()
    {
        _haptics.Light();
        return _navigator.GoToAsync(Routes.Premium);
    }

    /// <summary>Content filters row → pushed page inside the already-gated settings flow.</summary>
    [RelayCommand]
    private Task OpenContentFiltersAsync()
    {
        _haptics.Light();
        return _navigator.GoToAsync(Routes.ContentFilters);
    }

    /// <summary>The Profiles nav row. Fires Light like every other row that opens
    /// something — it was the one tappable in Settings with no haptic at all.</summary>
    [RelayCommand]
    private void ToggleProfileEditor()
    {
        _haptics.Light();
        IsProfileEditorVisible = !IsProfileEditorVisible;
    }

    /// <summary>Picker ticks, same vocabulary as onboarding — the inline profile editor
    /// was silent under the finger before the affordance pass.</summary>
    [RelayCommand]
    private void SelectProfileAge(int age)
    {
        if (age != ProfileAge) _haptics.Selection();
        ProfileAge = age;
    }

    [RelayCommand]
    private void ToggleProfileInterest(InterestOption option)
    {
        _haptics.Selection();
        option.IsSelected = !option.IsSelected;
    }

    [RelayCommand]
    private async Task SaveProfileAsync()
    {
        if (_profile is null) return;
        _haptics.Light();
        _profile.Name = string.IsNullOrWhiteSpace(ProfileName) ? _profile.Name : ProfileName.Trim();
        _profile.Age = Math.Clamp(ProfileAge, 3, 8);
        _profile.Interests = ProfileInterests.Where(i => i.IsSelected).Select(i => i.Name).ToList();
        await _store.SaveProfileAsync(_profile);
        IsProfileEditorVisible = false;
    }

    [RelayCommand]
    private Task CloseAsync() => _navigator.GoBackAsync();

    public static string FormatBytes(long bytes)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return bytes switch
        {
            < 1024 => string.Create(inv, $"{bytes} B"),
            < 1024 * 1024 => string.Create(inv, $"{bytes / 1024.0:0.#} KB"),
            < 1024L * 1024 * 1024 => string.Create(inv, $"{bytes / (1024.0 * 1024):0.#} MB"),
            _ => string.Create(inv, $"{bytes / (1024.0 * 1024 * 1024):0.##} GB"),
        };
    }
}
