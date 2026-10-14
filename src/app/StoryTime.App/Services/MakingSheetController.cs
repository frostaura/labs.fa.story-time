using StoryTime.Core.ViewModels;

namespace StoryTime.App.Services;

/// <summary>App-side coordinator for the making sheet. The shared state itself lives in the
/// singleton <see cref="MakingSheetViewModel"/> (Core); this controller adds what only the
/// App layer knows: whether the app is foregrounded (window Resumed/Stopped, wired in
/// App.CreateWindow) so every host can pause its animations when backgrounded — battery
/// care for the ember pulse and the progress-bar shimmer.</summary>
public sealed class MakingSheetController
{
    public MakingSheetController(MakingSheetViewModel sheet) => Sheet = sheet;

    /// <summary>The one shared sheet state every page's MakingSheetHost binds to.</summary>
    public MakingSheetViewModel Sheet { get; }

    public bool IsAppForegrounded { get; private set; } = true;

    /// <summary>Raised on the UI thread when the foreground gate flips; hosts re-evaluate
    /// their animation loops.</summary>
    public event EventHandler? AnimationGateChanged;

    public void OnAppBackgrounded() => SetForegrounded(false);
    public void OnAppForegrounded() => SetForegrounded(true);

    private void SetForegrounded(bool value)
    {
        if (IsAppForegrounded == value) return;
        IsAppForegrounded = value;
        AnimationGateChanged?.Invoke(this, EventArgs.Empty);
    }
}
