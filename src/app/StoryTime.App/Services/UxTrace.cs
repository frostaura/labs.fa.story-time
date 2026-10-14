namespace StoryTime.App.Services;

/// <summary>Debug-only action breadcrumbs, the counterpart of the "TWHaptics" trace: QA
/// proves the anticipatory-haptics ordering on the simulator by checking that a TWHaptics
/// line precedes the matching TWAction line (menu shown, navigation, sheet expand) in the
/// sim console. Compiled out of Release entirely.</summary>
public static class UxTrace
{
    [System.Diagnostics.Conditional("DEBUG")]
    public static void Action(string name)
    {
        Console.WriteLine($"TWAction {name}");
    }
}
