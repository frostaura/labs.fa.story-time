using System.ComponentModel;
using CommunityToolkit.Maui.Behaviors;
using CommunityToolkit.Maui.Core;
using StoryTime.App.Services;
using StoryTime.Core.ViewModels;

namespace StoryTime.App.Views;

public partial class SettingsPage : ContentPage
{
    private readonly SettingsViewModel _vm;
    private readonly Dictionary<int, Label> _editorAges = [];
    private bool _editorBuilt;

    public SettingsPage(SettingsViewModel vm)
    {
        _vm = vm;
        BindingContext = vm;
        InitializeComponent();
        _vm.PropertyChanged += OnVmPropertyChanged;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        BuildEditorRows();
        await _vm.ActivateAsync(CancellationToken.None);   // biometric gate — every entry
    }

    private async void OnRetryClicked(object? sender, EventArgs e) =>
        await _vm.ActivateAsync(CancellationToken.None);

    private static Color Token(string key) => (Color)Application.Current!.Resources[key];

    /// <summary>Nav-row press-down Light (anticipatory rule) — pointer-down, before the
    /// row's command runs.</summary>
    private void OnPressDownLight(object? sender, TouchStatusChangedEventArgs e)
    {
        if (e.Status == TouchStatus.Started) PressHaptics.Light();
    }

    /// <summary>Picker press-down: Selection at pointer-down; the VM's change-committed
    /// Selection coalesces away. TouchBehavior (command set directly in code, so the
    /// no-BindingContext gotcha cannot bite) replaces the old bare TapGestureRecognizer.</summary>
    private static TouchBehavior PressSelectionTouch(Action execute)
    {
        var touch = new TouchBehavior { Command = new Command(execute) };
        touch.CurrentTouchStatusChanged += (_, e) =>
        {
            if (e.Status == TouchStatus.Started) PressHaptics.Selection();
        };
        return touch;
    }

    private void BuildEditorRows()
    {
        if (_editorBuilt) return;
        _editorBuilt = true;

        foreach (var age in _vm.Ages)
        {
            var numeral = new Label
            {
                Text = age.ToString(),
                FontFamily = "OutfitLight",
                FontSize = 20,
                TextColor = Token("Dim"),
            };
            var captured = age;
            numeral.Behaviors.Add(PressSelectionTouch(() => _vm.SelectProfileAgeCommand.Execute(captured)));
            EditorAgeRow.Children.Add(numeral);
            _editorAges[age] = numeral;
        }
        ApplyEditorAge();

        foreach (var option in _vm.ProfileInterests)
        {
            var chip = new Label
            {
                Text = option.Name,
                FontFamily = "OutfitRegular",
                FontSize = 14,
                Padding = new Thickness(0, 6, 18, 6),
            };
            var captured = option;
            chip.Behaviors.Add(PressSelectionTouch(() => _vm.ToggleProfileInterestCommand.Execute(captured)));

            void Apply() => chip.TextColor = captured.IsSelected ? Token("Accent") : Token("Faint");
            captured.PropertyChanged += (_, _) => Apply();
            Apply();
            EditorInterestWrap.Children.Add(chip);
        }
    }

    private void ApplyEditorAge()
    {
        foreach (var (age, numeral) in _editorAges)
            numeral.TextColor = age == _vm.ProfileAge ? Token("Accent") : Token("Dim");
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.ProfileAge))
            ApplyEditorAge();
    }
}
