using System.ComponentModel;
using CommunityToolkit.Maui.Behaviors;
using CommunityToolkit.Maui.Core;
using StoryTime.App.Services;
using StoryTime.Core.Abstractions;
using StoryTime.Core.ViewModels;

namespace StoryTime.App.Views;

public partial class PlayerPage : ContentPage
{
    /// <summary>Drag-down distance on the handle that arms (Tick) and, on release, commits
    /// the dismiss — mirrors the weave sheet's grab-zone pattern at modal scale.</summary>
    private const double DismissDragThreshold = 70;

    private readonly PlayerViewModel _vm;
    private readonly MediaElementHost _mediaHost;
    private readonly IHaptics _haptics;

    private bool _scrubbing;
    private double _scrubStartX;
    private double _scrubFraction;
    private int _scrubStep = -1;      // 5% steps (0..20) for the scrub Tick
    private double _handleDragY;
    private bool _handleArmed;

    public PlayerPage(PlayerViewModel vm, MediaElementHost mediaHost, IHaptics haptics)
    {
        _vm = vm;
        _mediaHost = mediaHost;
        _haptics = haptics;
        BindingContext = vm;
        InitializeComponent();

        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, e) =>
        {
            if (_scrubbing) return;
            var point = e.GetPosition(ProgressArea);
            if (point is not null && ProgressArea.Width > 0)
                _vm.SeekToFractionCommand.Execute(point.Value.X / ProgressArea.Width);
        };
        ProgressArea.GestureRecognizers.Add(tap);

        // Scrub drag: the thumb follows the finger with a Tick per 5% step (coalesced in
        // PlatformHaptics); the seek itself commits once on release (VM adds Selection).
        // Last-Running-position discipline per the LengthTrackView TotalX=0 gotcha.
        var scrub = new PanGestureRecognizer();
        scrub.PanUpdated += OnProgressPan;
        ProgressArea.GestureRecognizers.Add(scrub);

        // Scrub grip at POINTER-DOWN (anticipatory rule): the pan's Started arrives only
        // after the slop threshold and the first step Tick can be a whole 5% away, so the
        // track would feel dead the moment the finger lands. Listen-only — the tap and pan
        // above keep owning the seek (the LengthTrackView trackArea pattern).
        var scrubGrip = new TouchBehavior();
        scrubGrip.CurrentTouchStatusChanged += (_, e) =>
        {
            if (e.Status == TouchStatus.Started) _haptics.Tick();
        };
        ProgressArea.Behaviors.Add(scrubGrip);

        // Drag handle: pan past the threshold arms with a Tick; releasing past it closes.
        var handlePan = new PanGestureRecognizer();
        handlePan.PanUpdated += OnHandlePan;
        DragHandle.GestureRecognizers.Add(handlePan);
        // Light the instant the finger lands on the handle, before the tap/swipe/drag
        // resolves. Listen-only: the tap + swipe-down recognizers in the XAML own the close.
        DragHandle.Behaviors.Add(PressHaptics.Touch(DragHandle, PressHapticKind.Light, listenOnly: true));

        _vm.PropertyChanged += OnVmPropertyChanged;
    }

    /// <summary>Press-down Light for the play/pause disc and the locked Music mix row —
    /// fires at pointer-down, before the transport flips or the paygate pushes.</summary>
    private void OnPressDownLight(object? sender, TouchStatusChangedEventArgs e)
    {
        if (e.Status == TouchStatus.Started) PressHaptics.Light();
    }

    private void OnProgressPan(object? sender, PanUpdatedEventArgs e)
    {
        if (ProgressArea.Width <= 0) return;
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _scrubbing = true;
                _scrubFraction = _vm.ProgressFraction;
                _scrubStartX = _scrubFraction * ProgressArea.Width;
                _scrubStep = (int)(_scrubFraction * 20);
                break;
            case GestureStatus.Running:
                if (!_scrubbing) return;
                var x = Math.Clamp(_scrubStartX + e.TotalX, 0, ProgressArea.Width);
                _scrubFraction = x / ProgressArea.Width;
                var step = (int)(_scrubFraction * 20);
                if (step != _scrubStep)
                {
                    _scrubStep = step;
                    _haptics.Tick();   // 5% step crossed under the finger
                }
                UpdateProgressVisual();
                break;
            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                if (!_scrubbing) return;
                _scrubbing = false;
                _vm.SeekToFractionCommand.Execute(_scrubFraction);
                break;
        }
    }

    private void OnHandlePan(object? sender, PanUpdatedEventArgs e)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _handleDragY = 0;
                _handleArmed = false;
                break;
            case GestureStatus.Running:
                _handleDragY = Math.Max(0, e.TotalY);
                if (!_handleArmed && _handleDragY > DismissDragThreshold)
                {
                    _handleArmed = true;
                    _haptics.Tick();   // dismiss armed — release now to close
                }
                break;
            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                if (_handleDragY > DismissDragThreshold) _vm.CloseCommand.Execute(null);
                _handleDragY = 0;
                break;
        }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _mediaHost.Element = Media;   // file-backed player drives this element
        await _vm.ActivateAsync();
        UpdateProgressVisual();
    }

    protected override async void OnDisappearing()
    {
        base.OnDisappearing();
        await _vm.DeactivateAsync();
        if (ReferenceEquals(_mediaHost.Element, Media)) _mediaHost.Element = null;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerViewModel.IsPlaying):
                // U+FE0E keeps ▶ in text presentation — iOS renders the bare glyph as colour emoji.
                PlayGlyph.Text = _vm.IsPlaying ? "❚❚" : "▶︎";
                break;
            case nameof(PlayerViewModel.ProgressFraction):
                UpdateProgressVisual();
                break;
        }
    }

    private void UpdateProgressVisual()
    {
        if (ProgressArea.Width <= 0) return;
        // While scrubbing the finger owns the thumb; live PositionChanged updates resume
        // after the release commits the seek.
        var fraction = _scrubbing ? _scrubFraction : _vm.ProgressFraction;
        var x = fraction * (ProgressArea.Width - 14);
        ProgressFill.WidthRequest = Math.Max(0, x + 7);
        ProgressThumb.TranslationX = x;
    }
}
