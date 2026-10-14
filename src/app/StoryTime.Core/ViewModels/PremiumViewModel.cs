using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StoryTime.Core.Abstractions;

namespace StoryTime.Core.ViewModels;

/// <summary>The Premium page — an honest paygate. No billing exists until the M4 decision,
/// so the price row says so plainly; the only "purchase" is a Debug-build-only local
/// testing unlock (ISettingsStore.DebugPremiumOverride, honoured by StubEntitlements).</summary>
public sealed partial class PremiumViewModel : ObservableObject
{
    private readonly ISettingsStore _settings;
    private readonly INavigator _navigator;
    private readonly IHaptics _haptics;

    public PremiumViewModel(ISettingsStore settings, INavigator navigator, IHaptics haptics)
    {
        _settings = settings;
        _navigator = navigator;
        _haptics = haptics;
        _isUnlockedForTesting = settings.DebugPremiumOverride;
    }

    /// <summary>The quiet testing row renders only in Debug builds.</summary>
    public static bool IsDebugBuild =>
#if DEBUG
        true;
#else
        false;
#endif

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TestingRowText))]
    private bool _isUnlockedForTesting;

    public string TestingRowText => IsUnlockedForTesting
        ? "Premium · unlocked for testing — tap to relock"
        : "unlock for testing";

    public void Activate()
    {
        _haptics.Light();   // paygate page opening
        IsUnlockedForTesting = _settings.DebugPremiumOverride;
    }

    [RelayCommand]
    private void ToggleTestingUnlock()
    {
        if (!IsDebugBuild) return;
        _settings.DebugPremiumOverride = !_settings.DebugPremiumOverride;
        IsUnlockedForTesting = _settings.DebugPremiumOverride;
        if (IsUnlockedForTesting) _haptics.Celebrate();   // premium unlocked — two-beat
        else _haptics.Selection();
    }

    [RelayCommand]
    private Task CloseAsync() => _navigator.GoBackAsync();
}
