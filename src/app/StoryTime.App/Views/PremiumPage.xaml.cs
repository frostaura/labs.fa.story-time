using CommunityToolkit.Maui.Core;
using StoryTime.App.Services;
using StoryTime.Core.ViewModels;

namespace StoryTime.App.Views;

/// <summary>The Premium page (founder-requested addition beyond the mockup — recorded in
/// docs/design/mockup-spec.md): one on-device, one-off App Store purchase with Buy and
/// Restore. Reached from Settings UPGRADE →, the 15-minute slider gate upsell, and the
/// Player Music · PREMIUM row. Debug builds add a quiet local testing unlock.</summary>
public partial class PremiumPage : ContentPage
{
    private readonly PremiumViewModel _vm;

    public PremiumPage(PremiumViewModel vm)
    {
        _vm = vm;
        BindingContext = vm;
        InitializeComponent();
        TestingRow.IsVisible = PremiumViewModel.IsDebugBuild;
    }

    /// <summary>Press-down Light for the drag handle and the Debug unlock row — fires at
    /// pointer-down, ahead of the close animation / the unlock's terminal Celebrate.</summary>
    private void OnPressDownLight(object? sender, TouchStatusChangedEventArgs e)
    {
        if (e.Status == TouchStatus.Started) PressHaptics.Light();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            await _vm.ActivateAsync();
        }
        catch (Exception)
        {
            // The view-model already maps every store failure to honest copy; a page
            // event must never take the app down.
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _vm.Deactivate();
    }
}
