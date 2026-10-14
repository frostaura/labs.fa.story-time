using CommunityToolkit.Maui.Core;
using StoryTime.App.Services;
using StoryTime.Core.ViewModels;

namespace StoryTime.App.Views;

public partial class LibraryPage : ContentPage
{
    private readonly LibraryViewModel _vm;

    public LibraryPage(LibraryViewModel vm)
    {
        _vm = vm;
        BindingContext = vm;
        InitializeComponent();
    }

    /// <summary>Row press-down Light — fires at pointer-down, before open/navigate.</summary>
    private void OnPressDownLight(object? sender, TouchStatusChangedEventArgs e)
    {
        if (e.Status == TouchStatus.Started) PressHaptics.Light();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.ActivateAsync();
    }
}
