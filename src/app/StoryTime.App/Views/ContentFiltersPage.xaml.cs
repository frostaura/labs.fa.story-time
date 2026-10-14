using System.ComponentModel;
using CommunityToolkit.Maui.Core;
using StoryTime.App.Services;
using StoryTime.Core.ViewModels;

namespace StoryTime.App.Views;

public partial class ContentFiltersPage : ContentPage
{
    private readonly ContentFiltersViewModel _vm;

    public ContentFiltersPage(ContentFiltersViewModel vm)
    {
        _vm = vm;
        BindingContext = vm;
        InitializeComponent();
        _vm.PropertyChanged += OnVmPropertyChanged;
        ApplySelection();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _vm.Activate();      // re-read the store; no gate here (settings already gated)
    }

    /// <summary>Radio-row press-down Selection — fires at pointer-down, before the
    /// selection commits and the row repaints.</summary>
    private void OnPressDownSelection(object? sender, TouchStatusChangedEventArgs e)
    {
        if (e.Status == TouchStatus.Started) PressHaptics.Selection();
    }

    private static Color Token(string key) => (Color)Application.Current!.Resources[key];

    // Radio-row coloring, same token language as the narrator picker rows.
    private void ApplySelection()
    {
        GentleDot.TextColor = _vm.IsGentle ? Token("Accent") : Token("Dim");
        GentleTitle.TextColor = _vm.IsGentle ? Token("Accent") : Token("Muted");
        BraveishDot.TextColor = _vm.IsBraveish ? Token("Accent") : Token("Dim");
        BraveishTitle.TextColor = _vm.IsBraveish ? Token("Accent") : Token("Muted");
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ContentFiltersViewModel.IsGentle) or nameof(ContentFiltersViewModel.IsBraveish))
            ApplySelection();
    }
}
