using Microsoft.Maui.Controls.Shapes;
using StoryTime.Core.Abstractions;

namespace StoryTime.App.Controls;

/// <summary>Hearth mix-row slider: the same hairline track the Player mock shows, made
/// draggable. A full-width hairline, an accent fill from the left, and a small accent dot at
/// the fill's end. Tap sets the value; drag tracks the finger continuously (Value updates
/// live so volume follows in real time). Gesture handling adapts LengthTrackView's pattern —
/// the drag commits from the last Running position, never the Completed event's deltas,
/// because MAUI reports TotalX = 0 on GestureStatus.Completed (founder-reported bug class,
/// 2026-07-30). Disabled state dims to 0.4 opacity and ignores gestures.</summary>
public sealed class MixTrackView : ContentView
{
    public static readonly BindableProperty ValueProperty = BindableProperty.Create(
        nameof(Value), typeof(double), typeof(MixTrackView), 0.0, BindingMode.TwoWay,
        coerceValue: (_, v) => Math.Clamp((double)v, 0.0, 1.0),
        propertyChanged: (bindable, _, _) => ((MixTrackView)bindable).ApplyValue());

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    private const double ThumbSize = 10;

    private readonly BoxView _fill;
    private readonly Ellipse _thumb;
    private bool _dragging;
    private double _panStartX;
    private double _lastDragX;

    // Drag haptics: a Tick at each 25% step boundary and on arriving at either end
    // (0 / 100) — coalesced app-wide by PlatformHaptics so a fast swipe can't buzz.
    private int _lastQuarterStep = -1;
    private bool _wasAtLowEnd;
    private bool _wasAtHighEnd;

    private IHaptics? _haptics;
    private IHaptics? Haptics => _haptics ??= IPlatformApplication.Current?.Services.GetService<IHaptics>();

    private static Color TokenColor(string key) => (Color)Application.Current!.Resources[key];

    public MixTrackView()
    {
        var track = new BoxView { HeightRequest = 1, Color = TokenColor("Hairline"), VerticalOptions = LayoutOptions.Center };
        _fill = new BoxView
        {
            HeightRequest = 1,
            Color = TokenColor("Accent"),
            WidthRequest = 0,
            HorizontalOptions = LayoutOptions.Start,
            VerticalOptions = LayoutOptions.Center,
        };
        _thumb = new Ellipse
        {
            WidthRequest = ThumbSize,
            HeightRequest = ThumbSize,
            Fill = new SolidColorBrush(TokenColor("Accent")),
            HorizontalOptions = LayoutOptions.Start,
            VerticalOptions = LayoutOptions.Center,
        };

        var area = new Grid { HeightRequest = 44 };
        area.Children.Add(track);
        area.Children.Add(_fill);
        area.Children.Add(_thumb);
        Content = area;

        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, e) =>
        {
            if (!IsEnabled || _dragging) return;
            var point = e.GetPosition(area);
            if (point is not null) SetFromX(point.Value.X);
        };
        area.GestureRecognizers.Add(tap);

        // Grip at POINTER-DOWN, not at the pan's post-slop Started and not at the first 25%
        // boundary (anticipatory rule, LengthTrackView pattern). Silent when disabled — the
        // Voice row is inert in live-TTS mode and must not answer the finger.
        var grip = new CommunityToolkit.Maui.Behaviors.TouchBehavior();
        grip.CurrentTouchStatusChanged += (_, e) =>
        {
            if (e.Status == CommunityToolkit.Maui.Core.TouchStatus.Started && IsEnabled) Haptics?.Tick();
        };
        area.Behaviors.Add(grip);

        var pan = new PanGestureRecognizer();
        pan.PanUpdated += (_, e) =>
        {
            if (!IsEnabled) return;
            switch (e.StatusType)
            {
                case GestureStatus.Started:
                    _dragging = true;
                    _panStartX = Value * UsableWidth;
                    _lastDragX = _panStartX;
                    _lastQuarterStep = QuarterStep(Value);
                    _wasAtLowEnd = Value <= LowEnd;
                    _wasAtHighEnd = Value >= HighEnd;
                    break;

                case GestureStatus.Running:
                    if (UsableWidth <= 0) break;
                    _lastDragX = Math.Clamp(_panStartX + e.TotalX, 0, UsableWidth);
                    SetFromX(_lastDragX);   // live: volume follows the finger
                    TickOnStepOrEnd();
                    break;

                case GestureStatus.Completed:
                case GestureStatus.Canceled:
                    // TotalX is 0 here — the last Running position is already committed.
                    _dragging = false;
                    SetFromX(_lastDragX);
                    break;
            }
        };
        area.GestureRecognizers.Add(pan);

        SizeChanged += (_, _) => ApplyValue();
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsEnabled)) Opacity = IsEnabled ? 1.0 : 0.4;
        };
    }

    private double UsableWidth => Math.Max(0, Width - ThumbSize);

    private const double LowEnd = 0.0005;
    private const double HighEnd = 0.9995;

    /// <summary>0..4 — which 25% band the value sits in; a change means a boundary crossed.</summary>
    private static int QuarterStep(double value) => (int)Math.Floor(value / 0.25 + 1e-9);

    private void TickOnStepOrEnd()
    {
        var tick = false;
        var step = QuarterStep(Value);
        if (step != _lastQuarterStep)
        {
            _lastQuarterStep = step;
            tick = true;
        }
        var low = Value <= LowEnd;
        var high = Value >= HighEnd;
        if ((low && !_wasAtLowEnd) || (high && !_wasAtHighEnd)) tick = true;
        _wasAtLowEnd = low;
        _wasAtHighEnd = high;
        if (tick) Haptics?.Tick();
    }

    private void SetFromX(double x)
    {
        if (UsableWidth <= 0) return;
        Value = Math.Clamp(x / UsableWidth, 0, 1);
    }

    private void ApplyValue()
    {
        if (UsableWidth <= 0) return;
        var x = Value * UsableWidth;
        _fill.WidthRequest = Math.Max(0, x + ThumbSize / 2);
        _thumb.TranslationX = x;
    }
}
