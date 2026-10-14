using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StoryTime.Contracts;
using StoryTime.Core.Abstractions;

namespace StoryTime.Core.ViewModels;

/// <summary>Content filters page — pushed from Parent settings, so it lives INSIDE the
/// biometric-gated flow (no extra gate here). Radio pattern like the narrator picker:
/// two levels, selected row amber. The level only changes how adventurous NEW stories
/// feel; every hard safety layer is identical at both levels, by construction.</summary>
public sealed partial class ContentFiltersViewModel : ObservableObject
{
    private readonly ISettingsStore _settings;
    private readonly INavigator _navigator;
    private readonly IHaptics _haptics;

    public ContentFiltersViewModel(ISettingsStore settings, INavigator navigator, IHaptics haptics)
    {
        _settings = settings;
        _navigator = navigator;
        _haptics = haptics;
        _isGentle = settings.ContentFilter == ContentFilterLevel.Gentle;
    }

    /// <summary>True → Gentle selected; false → Brave-ish selected (two-level radio).</summary>
    [ObservableProperty]
    private bool _isGentle;

    public bool IsBraveish => !IsGentle;

    partial void OnIsGentleChanged(bool value) => OnPropertyChanged(nameof(IsBraveish));

    /// <summary>Parent-facing display name for a level ("Gentle" / "Brave-ish") — shared
    /// with the Settings row caption.</summary>
    public static string DisplayName(ContentFilterLevel level) =>
        level == ContentFilterLevel.Braveish ? "Brave-ish" : "Gentle";

    /// <summary>Re-read the store on page appearance (settings may change elsewhere).</summary>
    public void Activate() => IsGentle = _settings.ContentFilter == ContentFilterLevel.Gentle;

    [RelayCommand]
    private void SelectGentle() => Select(ContentFilterLevel.Gentle);

    [RelayCommand]
    private void SelectBraveish() => Select(ContentFilterLevel.Braveish);

    private void Select(ContentFilterLevel level)
    {
        if (_settings.ContentFilter != level)
        {
            _settings.ContentFilter = level;
            _haptics.Selection();        // picker tick, same as the narrator rows
        }
        IsGentle = level == ContentFilterLevel.Gentle;
    }

    [RelayCommand]
    private Task CloseAsync() => _navigator.GoBackAsync();   // navigator fires Light on back
}
