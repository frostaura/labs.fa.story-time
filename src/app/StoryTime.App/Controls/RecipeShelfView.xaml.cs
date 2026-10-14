using CommunityToolkit.Maui.Core;
using StoryTime.App.Services;

namespace StoryTime.App.Controls;

/// <summary>Content of the "Tonight's recipe" shelf (hosted by BottomShelfHost on Home).
/// Bindings live in RecipeShelfView.xaml; the only code here is the anticipatory press-down
/// haptics. The commands stay on the existing TapGestureRecognizers because a Behavior
/// inside a DataTemplate cannot reach the ancestor RecipeShelfViewModel — so these
/// TouchBehaviors are listen-only and exist purely to answer the finger at pointer-down,
/// before the chip flips / the row repaints / the voice sample loads.</summary>
public partial class RecipeShelfView : ContentView
{
    public RecipeShelfView()
    {
        InitializeComponent();
    }

    /// <summary>Pickers — interest chips, narrator rows, the series/one-shot pair.</summary>
    private void OnPressDownSelection(object? sender, TouchStatusChangedEventArgs e)
    {
        if (e.Status == TouchStatus.Started) PressHaptics.Selection();
    }

    /// <summary>Actions — the ▶ narrator sample ring.</summary>
    private void OnPressDownLight(object? sender, TouchStatusChangedEventArgs e)
    {
        if (e.Status == TouchStatus.Started) PressHaptics.Light();
    }
}
