using CommunityToolkit.Maui.Core;
using StoryTime.App.Services;
using StoryTime.Core.Abstractions;
using StoryTime.Core.Services;
using StoryTime.Core.ViewModels;

namespace StoryTime.App.Views;

public partial class ShelfPage : ContentPage
{
    /// <summary>Must match LockTouch.LongPressDuration in the XAML (2000 ms).</summary>
    private const long HoldDurationMs = 2000;

    private readonly ShelfViewModel _vm;
    private readonly KidModeService _kidMode;
    private readonly IHaptics _haptics;

    private long _holdStartedMs;
    private CancellationTokenSource? _holdCts;

    public ShelfPage(ShelfViewModel vm, KidModeService kidMode, IHaptics haptics)
    {
        _vm = vm;
        _kidMode = kidMode;
        _haptics = haptics;
        BindingContext = vm;
        InitializeComponent();
        // The lock "listens" through the whole 2-second hold: Light at touch-down (grip),
        // escalating Ticks at 0.5 / 1.0 / 1.5 s (progress feel), then the VM's Success on
        // unlock — or a subtle Warning ("not yet") when the finger lifts early.
        LockTouch.CurrentTouchStatusChanged += (_, e) =>
        {
            if (e.Status == TouchStatus.Started)
            {
                _haptics.Light();
                _holdStartedMs = Environment.TickCount64;
                _holdCts?.Cancel();
                var cts = _holdCts = new CancellationTokenSource();
                _ = EscalateHoldAsync(cts.Token);
            }
            else
            {
                _holdCts?.Cancel();
                _holdCts = null;
                var held = Environment.TickCount64 - _holdStartedMs;
                if (_holdStartedMs > 0 && held < HoldDurationMs) _haptics.Warning();
                _holdStartedMs = 0;
            }
        };
    }

    /// <summary>Tile press-down Light — fires at pointer-down, before the replay opens.</summary>
    private void OnPressDownLight(object? sender, TouchStatusChangedEventArgs e)
    {
        if (e.Status == TouchStatus.Started) PressHaptics.Light();
    }

    private async Task EscalateHoldAsync(CancellationToken ct)
    {
        try
        {
            for (var beat = 0; beat < 3; beat++)
            {
                await Task.Delay(500, ct);
                _haptics.Tick();
            }
        }
        catch (TaskCanceledException)
        {
            // Finger lifted — the touch handler already answered with the Warning.
        }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.ActivateAsync();
    }

    /// <summary>Kid mode blocks back navigation — only the 2-second lock-pill hold exits.
    /// (The page is modal, so iOS offers no swipe-back; this keeps any routed back request
    /// locked too.)</summary>
    protected override bool OnBackButtonPressed() => _kidMode.IsKidMode;
}
