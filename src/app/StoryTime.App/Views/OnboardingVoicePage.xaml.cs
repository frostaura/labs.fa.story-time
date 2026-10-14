using System.ComponentModel;
using Microsoft.Maui.Controls.Shapes;
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
        BuildVoiceList();
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

    private void BuildVoiceList()
    {
        foreach (var option in _vm.Voices)
        {
            var dot = new Label
            {
                Text = "●",
                FontSize = 10,
                VerticalOptions = LayoutOptions.Center,
                Margin = new Thickness(0, 0, 12, 0),
            };
            var title = new Label
            {
                Text = option.DisplayLine,
                FontFamily = "OutfitRegular",
                FontSize = 17,
                VerticalOptions = LayoutOptions.Center,
            };

            // ▶ ring — plays a live voice sample.
            var playGlyph = new Label
            {
                Text = "▶︎",
                FontSize = 11,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                Margin = new Thickness(2, 0, 0, 0),
                InputTransparent = true,   // the 48dp wrapper owns the gesture, not the glyph
            };
            // ▶ PREVIEW RING (founder bug 2026-07-31: "does nothing, no haptic"). The visible
            // ring stays 40dp but is INPUT-TRANSPARENT and sits inside a 48dp transparent
            // target that owns the gesture: 40dp was under the 44dp minimum, and a Border
            // with no recognizer of its own swallows any touch that lands on it.
            var ring = new Border
            {
                StrokeThickness = 1,
                StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(999) },
                WidthRequest = 40,
                HeightRequest = 40,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                Content = playGlyph,
                BackgroundColor = Colors.Transparent,
                InputTransparent = true,   // (the glyph inside is marked the same, above)
            };
            var ringTarget = new Grid
            {
                WidthRequest = 48,
                HeightRequest = 48,
                BackgroundColor = Colors.Transparent,
                HorizontalOptions = LayoutOptions.End,
                VerticalOptions = LayoutOptions.Center,
                Children = { ring },
            };
            var captured = option;
            // The ring is an ACTION (it starts audio), not a picker: Light at POINTER-DOWN,
            // before the sample renders — the anticipatory rule's whole point is masking
            // exactly that wait. Its absence is what proved the gesture never arrived.
            ringTarget.Behaviors.Add(PressHaptics.Touch(ringTarget, PressHapticKind.Light,
                () => _vm.PlaySampleCommand.Execute(captured)));

            var row = new Grid
            {
                HeightRequest = 58,
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto),
                },
            };
            row.Add(dot, 0, 0);
            row.Add(title, 1, 0);
            row.Add(ringTarget, 2, 0);

            // Selection target covers the dot column too (the ring keeps its own touch).
            // Radio row ⇒ Selection at POINTER-DOWN; the VM's commit Selection coalesces.
            title.Behaviors.Add(PressHaptics.Touch(title, PressHapticKind.Selection,
                () => _vm.SelectVoiceCommand.Execute(captured)));
            dot.Behaviors.Add(PressHaptics.Touch(dot, PressHapticKind.Selection,
                () => _vm.SelectVoiceCommand.Execute(captured)));

            void Apply()
            {
                var accent = captured.IsSelected;
                dot.TextColor = accent ? Token("Accent") : Token("Dim");
                title.TextColor = accent ? Token("Accent") : Token("Muted");
                // The sample ring is a real control; Dim was too faint to read as one.
                playGlyph.TextColor = accent ? Token("Accent") : Token("Muted");
                // Rendering state: amber + dimmed. A neural sample takes seconds, so the tap
                // must be visibly acknowledged before any audio exists.
                var previewing = captured.IsPreviewing;
                ring.Stroke = new SolidColorBrush(accent || previewing ? Token("Accent") : Token("Border"));
                ring.Opacity = previewing ? 0.45 : 1.0;
            }
            captured.PropertyChanged += (_, _) => Apply();
            Apply();

            VoiceList.Children.Add(row);
            VoiceList.Children.Add(new BoxView());
        }
    }

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
