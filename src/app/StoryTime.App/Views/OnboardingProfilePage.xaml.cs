using System.ComponentModel;
using StoryTime.App.Services;
using StoryTime.Core.ViewModels;

namespace StoryTime.App.Views;

public partial class OnboardingProfilePage : ContentPage
{
    private readonly OnboardingProfileViewModel _vm;
    private readonly Dictionary<int, (Label Numeral, BoxView Underline)> _ageViews = [];

    public OnboardingProfilePage(OnboardingProfileViewModel vm)
    {
        _vm = vm;
        BindingContext = vm;
        InitializeComponent();
        BuildAgeRow();
        BuildInterestList();
        _vm.PropertyChanged += OnVmPropertyChanged;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        // Design: nickname empty-but-focused on first show.
        if (string.IsNullOrEmpty(_vm.Nickname))
            Dispatcher.Dispatch(() => NicknameEntry.Focus());
    }

    private static Color Token(string key) => (Color)Application.Current!.Resources[key];

    private void BuildAgeRow()
    {
        foreach (var age in _vm.Ages)
        {
            var numeral = new Label
            {
                Text = age.ToString(),
                FontFamily = "OutfitLight",
                FontSize = 22,
                TextColor = Token("Dim"),
                HorizontalTextAlignment = TextAlignment.Center,
            };
            var underline = new BoxView
            {
                HeightRequest = 2,
                WidthRequest = 22,
                Color = Colors.Transparent,
                HorizontalOptions = LayoutOptions.Center,
            };
            var cell = new VerticalStackLayout { Spacing = 6, Children = { numeral, underline } };
            var captured = age;
            // Age numeral = a picker cell: Selection at POINTER-DOWN (anticipatory rule),
            // before the command repaints the numerals and underline.
            cell.Behaviors.Add(PressHaptics.Touch(cell, PressHapticKind.Selection,
                () => _vm.SelectAgeCommand.Execute(captured)));
            AgeRow.Children.Add(cell);
            _ageViews[age] = (numeral, underline);
        }
        ApplyAgeSelection();
    }

    private void ApplyAgeSelection()
    {
        foreach (var (age, (numeral, underline)) in _ageViews)
        {
            var selected = age == _vm.SelectedAge;
            numeral.TextColor = selected ? Token("Accent") : Token("Dim");
            numeral.FontFamily = selected ? "OutfitRegular" : "OutfitLight";
            underline.Color = selected ? Token("Accent") : Colors.Transparent;
        }
    }

    private void BuildInterestList()
    {
        foreach (var option in _vm.Interests)
        {
            var name = new Label
            {
                Text = option.Name,
                FontFamily = "OutfitRegular",
                FontSize = 17,
                VerticalOptions = LayoutOptions.Center,
            };
            var marker = new Label
            {
                FontFamily = "OutfitRegular",
                FontSize = 17,
                HorizontalOptions = LayoutOptions.End,
                VerticalOptions = LayoutOptions.Center,
            };
            var row = new Grid { HeightRequest = 56, BackgroundColor = Colors.Transparent, Children = { name, marker } };
            var captured = option;
            // Interest toggle = a picker: Selection at POINTER-DOWN, before the ✓/+ flips.
            row.Behaviors.Add(PressHaptics.Touch(row, PressHapticKind.Selection,
                () => _vm.ToggleInterestCommand.Execute(captured)));

            void Apply()
            {
                // Idle marker lifted Dim → Muted (affordance pass 2026-07-31): Dim is never
                // the sole signal that a control is live. The picker language is unchanged.
                name.TextColor = captured.IsSelected ? Token("Accent") : Token("Muted");
                marker.Text = captured.IsSelected ? "✓" : "+";
                marker.TextColor = captured.IsSelected ? Token("Accent") : Token("Muted");
            }
            captured.PropertyChanged += (_, _) => Apply();
            Apply();

            InterestList.Children.Add(row);
            InterestList.Children.Add(new BoxView()); // hairline divider (implicit style)
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OnboardingProfileViewModel.SelectedAge))
            ApplyAgeSelection();
    }
}
