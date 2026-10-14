using StoryTime.Core.Abstractions;
using StoryTime.Core.ViewModels;

namespace StoryTime.App.Services;

/// <summary>INavigator over Shell, always on the UI thread.
/// Note: absolute Shell.GoToAsync("//home") from inside a pushed stack never completes on
/// this MAUI version (observed on Android, 10.0.60) — going home is implemented as
/// pop-all-modals + PopToRootAsync instead.</summary>
public sealed class ShellNavigator(IHaptics haptics) : INavigator
{
    // Every command-driven navigation answers the finger (founder: the All/back buttons
    // lacked haptics). Since the anticipatory pass (2026-07-31) buttons and rows fire a
    // press-down Light BEFORE this runs, so this Light normally coalesces away in
    // PlatformHaptics; it stays as the safety net for navigations no press haptic
    // preceded (programmatic routes, gesture recognizers without press tracking).
    public Task GoToAsync(string route)
    {
        haptics.Light();
        UxTrace.Action($"Nav {route}");
        return MainThread.InvokeOnMainThreadAsync(async () =>
        {
            if (route == Routes.Home)
            {
                await GoHomeAsync();
                return;
            }
            await Shell.Current.GoToAsync(route);
        });
    }

    public Task GoBackAsync()
    {
        haptics.Light();
        UxTrace.Action("Nav back");
        return MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var nav = Shell.Current.Navigation;
            if (nav.ModalStack.Count > 0)
            {
                await nav.PopModalAsync();
                return;
            }
            await Shell.Current.GoToAsync("..");
        });
    }

    private static async Task GoHomeAsync()
    {
        var nav = Shell.Current.Navigation;
        while (nav.ModalStack.Count > 0)
            await nav.PopModalAsync(animated: false);
        while (nav.NavigationStack.Count > 1)
        {
            await nav.PopAsync(animated: false);
        }
    }
}
