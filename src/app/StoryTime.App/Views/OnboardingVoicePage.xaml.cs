using System.ComponentModel;
using StoryTime.App.Services;
using StoryTime.Core.ViewModels;

namespace StoryTime.App.Views;

public partial class OnboardingVoicePage : ContentPage
{
    private readonly OnboardingVoiceViewModel _vm;

    public OnboardingVoicePage(OnboardingVoiceViewModel vm)
    {
        _vm = vm;
        BindingContext = vm;
        InitializeComponent();
        // Series / one-shot picker pair: Selection at POINTER-DOWN, mode flips on release.
        SeriesLabel.Behaviors.Add(PressHaptics.Touch(SeriesLabel, PressHapticKind.Selection,
            () => _vm.SetSeriesCommand.Execute(true)));
        OneShotLabel.Behaviors.Add(PressHaptics.Touch(OneShotLabel, PressHapticKind.Selection,
            () => _vm.SetSeriesCommand.Execute(false)));
        ApplyMode();
        LengthTrack.LengthRequested += (_, length) => _vm.SelectLengthCommand.Execute(length);
        _vm.PropertyChanged += OnVmPropertyChanged;
    }

    private static Color Token(string key) => (Color)Application.Current!.Resources[key];

    /// <summary>Premium-hint press-down Light — fires before the paygate is pushed.</summary>
    private void OnPressDownLight(object? sender, CommunityToolkit.Maui.Core.TouchStatusChangedEventArgs e)
    {
        if (e.Status == CommunityToolkit.Maui.Core.TouchStatus.Started) PressHaptics.Light();
    }

    private void ApplyMode()
    {
        var series = _vm.IsSeries;
        // Both labels are tappable, so the idle one is Muted rather than Faint.
        SeriesLabel.TextColor = series ? Token("Accent") : Token("Muted");
        OneShotLabel.TextColor = series ? Token("Muted") : Token("Accent");
        SeriesUnderline.Color = series ? Token("Accent") : Colors.Transparent;
        OneShotUnderline.Color = series ? Colors.Transparent : Token("Accent");
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OnboardingVoiceViewModel.IsSeries))
            ApplyMode();
    }
}
