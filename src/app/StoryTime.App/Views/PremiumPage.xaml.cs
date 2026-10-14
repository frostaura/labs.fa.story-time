using CommunityToolkit.Maui.Core;
using StoryTime.App.Services;
using StoryTime.Core.ViewModels;

namespace StoryTime.App.Views;

/// <summary>The honest paygate (founder-requested addition beyond the mockup — recorded in
/// docs/design/mockup-spec.md). Reached from Settings UPGRADE →, the 15-minute slider gate
/// upsell, and the Player Music · PREMIUM row. No billing exists until M4; the only unlock
/// is the Debug-only testing row.</summary>
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

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _vm.Activate();
    }
}
