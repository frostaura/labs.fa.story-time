using CommunityToolkit.Maui.Behaviors;
using CommunityToolkit.Maui.Core;
using Microsoft.Maui.Controls.Shapes;
using StoryTime.Contracts;
using StoryTime.Core.Abstractions;
using StoryTime.Core.ViewModels;

namespace StoryTime.App.Controls;

/// <summary>The 5 / 10 / 15 length track: a hairline with three detents and an amber thumb.
/// Tap commits the nearest detent immediately. Drag tracks the finger continuously (thumb
/// and label highlight follow, with a selection-haptic tick at each detent crossing) and
/// commits the NEAREST detent on release. "15 · Premium" is gated by the page's view-model
/// (the control raises the request; the VM decides — a rejected request snaps the thumb
/// back to the accepted value).
/// Implementation note: commit uses the last Running position, never the Completed event's
/// deltas — MAUI reports TotalX = 0 on GestureStatus.Completed, which is exactly the bug
/// that made a 10→5 drag pop back to 10 (founder-reported, 2026-07-30).</summary>
public sealed class LengthTrackView : ContentView
{
    public static readonly BindableProperty SelectedLengthProperty = BindableProperty.Create(
        nameof(SelectedLength), typeof(StoryLength), typeof(LengthTrackView), StoryLength.Ten,
        BindingMode.TwoWay, propertyChanged: (bindable, _, _) => ((LengthTrackView)bindable).ApplySelection());

    public StoryLength SelectedLength
    {
        get => (StoryLength)GetValue(SelectedLengthProperty);
        set => SetValue(SelectedLengthProperty, value);
    }

    /// <summary>Raised when the user picks a detent; the handler applies gating
    /// (SelectedLength only changes if the VM accepts).</summary>
    public event EventHandler<StoryLength>? LengthRequested;

    private readonly BoxView _track;
    private readonly Ellipse _thumb;
    private readonly Label[] _labels = new Label[3];
    private readonly Grid _root;

    private bool _dragging;
    private double _panStartX;       // thumb X at drag start (device-independent units)
    private double _lastDragX;       // last known finger-tracked thumb X during the drag
    private int _lastDetentIndex;    // for selection ticks at detent crossings

    private IHaptics? _haptics;
    private IHaptics? Haptics => _haptics ??= IPlatformApplication.Current?.Services.GetService<IHaptics>();

    private static Color TokenColor(string key) => (Color)Application.Current!.Resources[key];

    public LengthTrackView()
    {
        _track = new BoxView { HeightRequest = 1, Color = TokenColor("Hairline"), VerticalOptions = LayoutOptions.Center };

        _thumb = new Ellipse
        {
            WidthRequest = 18,
            HeightRequest = 18,
            Fill = new SolidColorBrush(TokenColor("Accent")),
            HorizontalOptions = LayoutOptions.Start,
            VerticalOptions = LayoutOptions.Center,
        };

        var trackArea = new Grid { HeightRequest = 44 };
        trackArea.Children.Add(_track);
        trackArea.Children.Add(_thumb);

        var labelRow = new Grid { ColumnSpacing = 0 };
        for (var i = 0; i < 3; i++)
        {
            labelRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var label = new Label
            {
                FontFamily = "OutfitRegular",
                FontSize = 13,
                TextColor = TokenColor("Dim"),
                HorizontalOptions = i == 0 ? LayoutOptions.Start : i == 1 ? LayoutOptions.Center : LayoutOptions.End,
                Text = i switch { 0 => "5", 1 => "10", _ => "15 · Premium" },
            };
            _labels[i] = label;
            labelRow.Add(label, i);
        }

        _root = new Grid { RowSpacing = 0 };
        _root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        _root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        _root.Add(trackArea, 0, 0);
        _root.Add(labelRow, 0, 1);
        Content = _root;

        // Grip at POINTER-DOWN (anticipatory rule): the finger lands on the track → Tick
        // immediately, before the pan recognizer's slop or any commit. TouchBehavior gives
        // the true touch-down; the pan's Started (post-slop) no longer ticks.
        var touch = new TouchBehavior();
        touch.CurrentTouchStatusChanged += (_, e) =>
        {
            if (e.Status == TouchStatus.Started) Haptics?.Tick();
        };
        trackArea.Behaviors.Add(touch);

        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, e) =>
        {
            if (_dragging) return;
            var point = e.GetPosition(trackArea);
            if (point is not null) Commit(point.Value.X);
        };
        trackArea.GestureRecognizers.Add(tap);

        var pan = new PanGestureRecognizer();
        pan.PanUpdated += (_, e) =>
        {
            switch (e.StatusType)
            {
                case GestureStatus.Started:
                    _dragging = true;
                    _panStartX = XForDetent(LengthDetents.IndexOf(SelectedLength));
                    _lastDragX = _panStartX;
                    _lastDetentIndex = LengthDetents.IndexOf(SelectedLength);
                    // Grip already ticked at pointer-down (TouchBehavior above) — pan
                    // Started fires post-slop, which is first MOVEMENT, not first touch.
                    break;

                case GestureStatus.Running:
                    if (UsableWidth <= 0) break;
                    // Thumb + label highlight follow the finger continuously.
                    _lastDragX = Math.Clamp(_panStartX + e.TotalX, 0, UsableWidth);
                    _thumb.TranslationX = _lastDragX;
                    var index = LengthDetents.NearestIndex(_lastDragX / UsableWidth);
                    if (index != _lastDetentIndex)
                    {
                        _lastDetentIndex = index;
                        HighlightLabels(index);
                        Haptics?.Selection();   // detent-crossing tick
                    }
                    break;

                case GestureStatus.Completed:
                case GestureStatus.Canceled:
                    // Deltas are 0 in the Completed event — commit the last Running
                    // position, then snap to whatever the VM actually accepted.
                    _dragging = false;
                    Commit(_lastDragX);
                    Haptics?.Light();   // drag commit — the thumb settles
                    break;
            }
        };
        trackArea.GestureRecognizers.Add(pan);

        SizeChanged += (_, _) => ApplySelection();
    }

    private double UsableWidth => Math.Max(0, Width - 18);

    private double XForDetent(int index) => index <= 0 ? 0 : UsableWidth * index / 2.0;

    /// <summary>Snap to the nearest detent for X and commit it through the VM gate. The
    /// explicit ApplySelection afterwards covers the rejected case (15 without Premium):
    /// SelectedLength stays put, so no property change fires, and the thumb must be
    /// walked back to the accepted detent by hand.</summary>
    private void Commit(double x)
    {
        if (UsableWidth <= 0) return;
        var length = LengthDetents.Nearest(Math.Clamp(x / UsableWidth, 0, 1));
        LengthRequested?.Invoke(this, length);
        ApplySelection();
    }

    private void ApplySelection()
    {
        if (_dragging) return;   // never fight the finger mid-drag
        var index = LengthDetents.IndexOf(SelectedLength);
        _thumb.TranslationX = XForDetent(index);
        HighlightLabels(index);
    }

    private void HighlightLabels(int index)
    {
        for (var i = 0; i < 3; i++)
        {
            _labels[i].TextColor = i == index ? TokenColor("Accent") : TokenColor("Dim");
            _labels[i].FontFamily = i == index ? "OutfitMedium" : "OutfitRegular";
        }
    }
}
