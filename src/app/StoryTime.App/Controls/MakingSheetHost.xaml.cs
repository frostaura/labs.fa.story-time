using System.ComponentModel;
using CommunityToolkit.Maui.Behaviors;
using CommunityToolkit.Maui.Core;
using StoryTime.App.Services;
using StoryTime.Core.Abstractions;
using StoryTime.Core.ViewModels;

namespace StoryTime.App.Controls;

/// <summary>One instance sits as the top layer of every non-modal page's root Grid
/// (Home, Library, Shelf, Settings). All instances bind to the singleton
/// MakingSheetViewModel, so whichever page the user navigates to shows the same sheet
/// state — that is the whole "the story is made behind the app" illusion.
///
/// Animation discipline (Hearth + gotchas): every transition is ≤300 ms eased, nothing
/// bounces; both loops (ember pulse, progress shimmer) yield unconditionally and are
/// gated on page-visible + app-foregrounded + the state that actually needs them.</summary>
public partial class MakingSheetHost : Grid
{
    private const double CollapseDragThreshold = 110;   // dp of drag-down that commits a collapse

    private readonly MakingSheetViewModel _vm;
    private readonly MakingSheetController _controller;
    private readonly IHaptics _haptics;

    private ContentPage? _page;
    private bool _pageVisible;
    private bool _pulsing;
    private int _transition;      // bumps on every state change; stale animations bail out
    private double _lastDragY;    // last Running pan position (Completed reports zeros — gotcha)
    private bool _dragArmed;      // dismiss-threshold arming Selection fired for this gesture

    public MakingSheetHost()
    {
        var services = IPlatformApplication.Current!.Services;
        _vm = services.GetRequiredService<MakingSheetViewModel>();
        _controller = services.GetRequiredService<MakingSheetController>();
        _haptics = services.GetRequiredService<IHaptics>();

        InitializeComponent();
        BindingContext = _vm;
        ProgressBar.Ticks = MakingSheetViewModel.StageTicks;

        // Drag-down on the grab zone collapses (commit uses the last Running position —
        // MAUI reports TotalY = 0 on Completed, the LengthTrackView lesson).
        var pan = new PanGestureRecognizer();
        pan.PanUpdated += OnGrabPan;
        GrabZone.GestureRecognizers.Add(pan);

        // Grip at DRAG-START = first touch, not first movement (anticipatory rule): the pan's
        // Started only arrives after the slop threshold, so the handle would feel dead for the
        // first few millimetres of a drag. Listen-only TouchBehavior — the tap and pan
        // recognizers above keep owning the collapse (the LengthTrackView trackArea pattern).
        var grip = new TouchBehavior();
        grip.CurrentTouchStatusChanged += (_, e) =>
        {
            if (e.Status == TouchStatus.Started) _haptics.Tick();
        };
        GrabZone.Behaviors.Add(grip);

        // Mini-bar: Light at pointer-down, before the expand animation. Listen-only — the
        // tap/swipe-up/swipe-down recognizers in the XAML must keep working (TouchBehavior
        // has no swipe), and the VM's expand Light then coalesces away.
        MiniBar.Behaviors.Add(PressHaptics.Touch(MiniBar, PressHapticKind.Light, listenOnly: true));

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => { SyncInstant(); UpdateMiniProgress(); };
        MiniProgressTrack.SizeChanged += (_, _) => UpdateMiniProgress();
    }

    /// <summary>Scrim press-down Light — fires at pointer-down, before the collapse.</summary>
    private void OnPressDownLight(object? sender, TouchStatusChangedEventArgs e)
    {
        if (e.Status == TouchStatus.Started) PressHaptics.Light();
    }

    // ---- lifecycle & gating -------------------------------------------------------

    private void OnLoaded(object? sender, EventArgs e)
    {
        _vm.PropertyChanged += OnVmPropertyChanged;
        _controller.AnimationGateChanged += OnAnimationGateChanged;

        // Hook the owning page so we know when this host's page is actually on screen.
        for (Element? p = Parent; p is not null; p = p.Parent)
        {
            if (p is ContentPage page) { _page = page; break; }
        }
        if (_page is not null)
        {
            _page.Appearing += OnPageAppearing;
            _page.Disappearing += OnPageDisappearing;
        }
        _pageVisible = true;   // Loaded ⇒ attached to a window; Appearing/Disappearing refine it
        _vm.IsForegrounded = _controller.IsAppForegrounded;   // catch up on flips missed while unloaded
        ApplySafeAreaPadding();   // Appearing may already have fired before we hooked it
        SyncInstant();
        ReevaluateAnimations();
    }

    private void OnUnloaded(object? sender, EventArgs e)
    {
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _controller.AnimationGateChanged -= OnAnimationGateChanged;
        if (_page is not null)
        {
            _page.Appearing -= OnPageAppearing;
            _page.Disappearing -= OnPageDisappearing;
            _page = null;
        }
        _pageVisible = false;
        ReevaluateAnimations();
    }

    private void OnPageAppearing(object? sender, EventArgs e)
    {
        _pageVisible = true;
        ApplySafeAreaPadding();
        SyncInstant();   // pick up state changes that happened while another page was up
        ReevaluateAnimations();
    }

    /// <summary>iOS pages bleed edge-to-edge, so the docked mini-bar would sink half
    /// under the home indicator without this: height grows by the bottom inset and the
    /// content pads up by the same amount (spec: "height ~64 + safe area").</summary>
    private void ApplySafeAreaPadding()
    {
        if (_page is null) return;
        var inset = Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific.Page
            .SafeAreaInsets(_page.On<Microsoft.Maui.Controls.PlatformConfiguration.iOS>());
        MiniBar.HeightRequest = 64 + inset.Bottom;
        MiniBar.Padding = new Thickness(0, 0, 0, inset.Bottom);
    }

    private void OnPageDisappearing(object? sender, EventArgs e)
    {
        _pageVisible = false;
        ReevaluateAnimations();
    }

    private void OnAnimationGateChanged(object? sender, EventArgs e) =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            // Mirror the app-foreground gate into the VM so story-driven haptics (stage
            // ticks, story-ready Celebrate) stay silent while backgrounded. Every loaded
            // host writes the same value — idempotent by construction.
            _vm.IsForegrounded = _controller.IsAppForegrounded;
            ReevaluateAnimations();
        });

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MakingSheetViewModel.State):
                if (_pageVisible) _ = AnimateToStateAsync();
                else SyncInstant();
                ReevaluateAnimations();
                break;
            case nameof(MakingSheetViewModel.Percent):
                UpdateMiniProgress();
                break;
            case nameof(MakingSheetViewModel.IsDone):
            case nameof(MakingSheetViewModel.IsFailed):
                UpdateMiniProgress();
                ReevaluateAnimations();
                break;
        }
    }

    private void ReevaluateAnimations()
    {
        var alive = _pageVisible && _controller.IsAppForegrounded;
        var making = !_vm.IsDone && !_vm.IsFailed;

        // Shimmer/eased fill: only inside the visible expanded sheet, mid-story.
        ProgressBar.Animate = alive && _vm.State == MakingSheetState.Expanded && making;

        // Ember pulse: only on the visible mini-bar, mid-story (done shows a calm dot).
        if (alive && _vm.State == MakingSheetState.Collapsed && making) _ = PulseEmberAsync();
    }

    // ---- state presentation -------------------------------------------------------

    private double OffscreenY => Height > 0 ? Height : 2000;

    /// <summary>Set the final visual state with no animation (page re-appearing, first
    /// layout, background state changes).</summary>
    private void SyncInstant()
    {
        _transition++;
        this.CancelAnimationsSafely(SheetPanel, Scrim, MiniBar);
        switch (_vm.State)
        {
            case MakingSheetState.Expanded:
                Scrim.IsVisible = true;
                Scrim.Opacity = 0.55;
                SheetPanel.IsVisible = true;
                SheetPanel.TranslationY = 0;
                MiniBar.IsVisible = false;
                break;
            case MakingSheetState.Collapsed:
                Scrim.IsVisible = false;
                Scrim.Opacity = 0;
                SheetPanel.IsVisible = false;
                SheetPanel.TranslationY = OffscreenY;
                MiniBar.IsVisible = true;
                MiniBar.TranslationY = 0;
                UpdateMiniProgress();
                break;
            default:
                Scrim.IsVisible = false;
                Scrim.Opacity = 0;
                SheetPanel.IsVisible = false;
                SheetPanel.TranslationY = OffscreenY;
                MiniBar.IsVisible = false;
                break;
        }
    }

    private async Task AnimateToStateAsync()
    {
        var token = ++_transition;
        this.CancelAnimationsSafely(SheetPanel, Scrim, MiniBar);
        switch (_vm.State)
        {
            case MakingSheetState.Expanded:
            {
                if (MiniBar.IsVisible)
                {
                    await MiniBar.TranslateToAsync(0, 90, 160, Easing.CubicIn);
                    if (token != _transition) return;
                    MiniBar.IsVisible = false;
                    MiniBar.TranslationY = 0;
                }
                Scrim.IsVisible = true;
                SheetPanel.IsVisible = true;
                if (SheetPanel.TranslationY <= 0) SheetPanel.TranslationY = OffscreenY;
                _ = Scrim.FadeToAsync(0.55, 240, Easing.CubicOut);
                await SheetPanel.TranslateToAsync(0, 0, 290, Easing.CubicOut);
                break;
            }
            case MakingSheetState.Collapsed:
            {
                if (SheetPanel.IsVisible)
                {
                    _ = Scrim.FadeToAsync(0, 220, Easing.CubicOut);
                    await SheetPanel.TranslateToAsync(0, OffscreenY, 260, Easing.CubicIn);
                    if (token != _transition) return;
                    SheetPanel.IsVisible = false;
                    Scrim.IsVisible = false;
                }
                UpdateMiniProgress();
                MiniBar.TranslationY = 90;
                MiniBar.IsVisible = true;
                await MiniBar.TranslateToAsync(0, 0, 230, Easing.CubicOut);
                break;
            }
            default:   // Hidden — slide out whatever is on screen
            {
                if (SheetPanel.IsVisible)
                {
                    _ = Scrim.FadeToAsync(0, 220, Easing.CubicOut);
                    await SheetPanel.TranslateToAsync(0, OffscreenY, 260, Easing.CubicIn);
                    if (token != _transition) return;
                    SheetPanel.IsVisible = false;
                    Scrim.IsVisible = false;
                }
                if (MiniBar.IsVisible)
                {
                    await MiniBar.TranslateToAsync(0, 90, 200, Easing.CubicIn);
                    if (token != _transition) return;
                    MiniBar.IsVisible = false;
                    MiniBar.TranslationY = 0;
                }
                break;
            }
        }
    }

    // ---- gestures -----------------------------------------------------------------

    private void OnGrabPan(object? sender, PanUpdatedEventArgs e)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _lastDragY = 0;
                _dragArmed = false;
                break;
            case GestureStatus.Running:
                _lastDragY = Math.Max(0, e.TotalY);
                SheetPanel.TranslationY = _lastDragY;
                // Arming feedback, once per gesture: the drag just crossed the point where
                // releasing will commit the collapse.
                if (!_dragArmed && _lastDragY > CollapseDragThreshold)
                {
                    _dragArmed = true;
                    _haptics.Selection();
                }
                break;
            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                if (_lastDragY > CollapseDragThreshold)
                {
                    _vm.CollapseCommand.Execute(null);   // AnimateToState finishes the slide
                }
                else
                {
                    _ = SheetPanel.TranslateToAsync(0, 0, 200, Easing.CubicOut);   // settle back, no bounce
                }
                _lastDragY = 0;
                break;
        }
    }

    // ---- mini-bar bits ------------------------------------------------------------

    private void UpdateMiniProgress()
    {
        if (MiniProgressTrack.Width <= 0) return;
        var fraction = _vm.IsDone ? 1.0 : Math.Clamp(_vm.ProgressFraction, 0, 1);
        MiniProgressFill.WidthRequest = fraction * MiniProgressTrack.Width;
    }

    /// <summary>Gentle 1.2 s ember pulse. Yield-floor pattern from memory/gotchas.md:
    /// FadeTo completes synchronously on a detached view, so every iteration is forced
    /// to yield regardless — this loop can never spin the UI thread.</summary>
    private async Task PulseEmberAsync()
    {
        if (_pulsing) return;
        _pulsing = true;
        try
        {
            while (_pageVisible && _controller.IsAppForegrounded
                   && _vm.State == MakingSheetState.Collapsed && !_vm.IsDone && !_vm.IsFailed)
            {
                var startedMs = Environment.TickCount64;
                await EmberDot.FadeToAsync(0.35, 600, Easing.SinInOut);
                await EmberDot.FadeToAsync(1.0, 600, Easing.SinInOut);
                if (Environment.TickCount64 - startedMs < 600) await Task.Delay(600);
            }
        }
        finally
        {
            EmberDot.Opacity = 1.0;
            _pulsing = false;
        }
    }
}

file static class MakingSheetHostExtensions
{
    public static void CancelAnimationsSafely(this Grid _, params VisualElement[] elements)
    {
        foreach (var element in elements) Microsoft.Maui.Controls.ViewExtensions.CancelAnimations(element);
    }
}
