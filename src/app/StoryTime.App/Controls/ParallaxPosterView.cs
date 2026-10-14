using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;
using StoryTime.App.Graphics;
using StoryTime.App.Services;
using StoryTime.Contracts;
using StoryTime.Poster;
using StoryTime.Poster.Composition;
using StoryTime.Poster.Render;

namespace StoryTime.App.Controls;

/// <summary>A window into the story's scene: the procedural engine's layers, cached as
/// bitmaps, translated by device tilt. Parallax is compositor work only — each layer is an
/// Image whose TranslationX/Y follows the shared <see cref="TiltMotion"/> hub, scaled by
/// that layer's travel; Skia never runs per frame for the layers.
///
/// The stack, back to front: the sky backdrop (FIXED — a ramp that never moves can never
/// reveal an edge, and it gives the stars something to travel against), the four
/// travelling layers (each rendered with a bleed equal to the deepest travel and cropped to
/// its painted content), the ambient canvas (twinkle, fireflies, glints, snow — the only
/// per-frame drawing, a few dozen soft dots on a 30 fps clock, player only), and a fixed
/// vignette.
///
/// Cards and shelf tiles use <see cref="MotionScale"/> &lt; 1 (less travel, smaller bleed,
/// less memory) and <see cref="AnimateAmbient"/> = false (particles baked at t = 0).
/// Under Reduce Motion nothing moves: the hub stays off and the ambient canvas draws one
/// still frame.</summary>
public sealed class ParallaxPosterView : ContentView
{
    public static readonly BindableProperty PosterProperty = BindableProperty.Create(
        nameof(Poster), typeof(PosterSpec), typeof(ParallaxPosterView),
        propertyChanged: (b, _, _) => ((ParallaxPosterView)b).Invalidate(force: true));

    public PosterSpec? Poster
    {
        get => (PosterSpec?)GetValue(PosterProperty);
        set => SetValue(PosterProperty, value);
    }

    public static readonly BindableProperty CornerRadiusProperty = BindableProperty.Create(
        nameof(CornerRadius), typeof(double), typeof(ParallaxPosterView), 20.0,
        propertyChanged: (b, _, n) => ((ParallaxPosterView)b)._shape.CornerRadius = new CornerRadius((double)n));

    public double CornerRadius
    {
        get => (double)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    /// <summary>Fraction of the engine's full travel (1 = player, ~0.5 = cards).</summary>
    public static readonly BindableProperty MotionScaleProperty = BindableProperty.Create(
        nameof(MotionScale), typeof(double), typeof(ParallaxPosterView), 1.0,
        propertyChanged: (b, _, _) => ((ParallaxPosterView)b).Invalidate(force: true));

    public double MotionScale
    {
        get => (double)GetValue(MotionScaleProperty);
        set => SetValue(MotionScaleProperty, value);
    }

    public static readonly BindableProperty AnimateAmbientProperty = BindableProperty.Create(
        nameof(AnimateAmbient), typeof(bool), typeof(ParallaxPosterView), true,
        propertyChanged: (b, _, _) => ((ParallaxPosterView)b).Invalidate(force: true));

    public bool AnimateAmbient
    {
        get => (bool)GetValue(AnimateAmbientProperty);
        set => SetValue(AnimateAmbientProperty, value);
    }

    private readonly Grid _stack = new() { IsClippedToBounds = true };
    private readonly Microsoft.Maui.Controls.Shapes.RoundRectangle _shape = new() { CornerRadius = new CornerRadius(20) };
    private readonly List<(Image View, double TravelPt)> _layers = [];
    private readonly List<(SKCanvasView View, int Depth)> _ambientViews = [];
    private RenderedPoster? _rendered;
    private string? _renderedKey;
    private int _renderGeneration;
    private bool _subscribed;
    private IDispatcherTimer? _ambientClock;
    private DateTime _ambientStart = DateTime.UtcNow;
    private Window? _observedWindow;

    public ParallaxPosterView()
    {
        Content = new Border
        {
            StrokeThickness = 0,
            StrokeShape = _shape,
            Content = _stack,
            BackgroundColor = Colors.Transparent,
        };
        Loaded += (_, _) => Start();
        Unloaded += (_, _) => Stop();
        SizeChanged += (_, _) => Invalidate(force: false);
    }

    /// <summary>Loaded is unreliable inside Shell modal pages on iOS, so handler attach
    /// also starts motion (both paths are idempotent).</summary>
    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        if (Handler is null) Stop(); else Start();
    }

    // ------------------------------------------------------------------ rendering

    private void Invalidate(bool force)
    {
        var spec = Poster;
        if (spec is null || Width <= 0 || Height <= 0) return;
        var density = DeviceDisplay.MainDisplayInfo.Density is > 0 and var d ? d : 3.0;
        // Cards render at 2x: they are small and many; the player gets the full density.
        if (MotionScale < 0.9) density = Math.Min(density, 2.0);
        var wPx = (int)Math.Round(Width * density);
        var hPx = (int)Math.Round(Height * density);
        var bake = !AnimateAmbient || TiltMotion.ReduceMotion;
        var key = PosterLayerCache.KeyFor(spec, wPx, hPx, MotionScale, bake);
        if (!force && key == _renderedKey) return;
        if (key == _renderedKey && _rendered is not null) return;
        _renderedKey = key;
        var generation = ++_renderGeneration;
        _ = RenderAsync(spec, wPx, hPx, density, bake, generation);
    }

    private async Task RenderAsync(PosterSpec spec, int wPx, int hPx, double density, bool bake, int generation)
    {
        RenderedPoster rendered;
        try
        {
            rendered = await PosterLayerCache.GetAsync(spec, wPx, hPx, MotionScale, bake);
        }
        catch (Exception)
        {
            return; // the flattened thumbnail underneath remains — never a broken poster
        }
        if (generation != _renderGeneration) return;
        _rendered = rendered;
        Build(rendered, density);
    }

    private void Build(RenderedPoster r, double density)
    {
        _stack.Children.Clear();
        _layers.Clear();
        var bleedPt = r.BleedPx / density;

        _stack.Children.Add(PngImage(r.Backdrop, new Thickness(-bleedPt)));
        var fullW = r.VisibleWidthPx + 2 * r.BleedPx;
        var fullH = r.VisibleHeightPx + 2 * r.BleedPx;
        _ambientViews.Clear();
        var animated = !r.AmbientBaked;
        foreach (var layer in r.Layers.OrderBy(l => l.Depth))
        {
            var margin = new Thickness(
                -bleedPt + layer.Left / density,
                -bleedPt + layer.Top / density,
                -bleedPt + (fullW - layer.Right) / density,
                -bleedPt + (fullH - layer.Bottom) / density);
            var view = PngImage(layer.Png, margin);
            _stack.Children.Add(view);
            _layers.Add((view, layer.TravelPx / density));
            // Each depth's particles sit directly above their own layer, so a twinkling star
            // stays behind the mountains and a firefly behind the near leaves.
            var depth = layer.Depth;
            if (animated && r.Plan.Ambient.Any(a => a.Depth == depth))
            {
                var canvasView = new SKCanvasView { InputTransparent = true };
                canvasView.PaintSurface += (s, e) => OnPaintAmbient(e, depth);
                _stack.Children.Add(canvasView);
                _ambientViews.Add((canvasView, depth));
            }
        }
        _stack.Children.Add(PngImage(r.Vignette, new Thickness(0)));
        ApplyOffsets();
        UpdateAmbientClock();
    }

    /// <summary>Fill + a negative margin: the arrange path that keeps the overscan (a
    /// centred child is clamped to its container and would silently lose the bleed).</summary>
    private static Image PngImage(byte[] png, Thickness margin) => new()
    {
        Source = ImageSource.FromStream(() => new MemoryStream(png)),
        Aspect = Aspect.Fill,
        HorizontalOptions = LayoutOptions.Fill,
        VerticalOptions = LayoutOptions.Fill,
        Margin = margin,
        InputTransparent = true,
    };

    private void OnPaintAmbient(SKPaintSurfaceEventArgs e, int depth)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        var r = _rendered;
        if (r is null || Width <= 0) return;
        var scale = (float)(e.Info.Width / Width);
        var vp = new PosterViewport(e.Info.Width, e.Info.Height, 0, 0);
        Span<SKPoint> offsets = stackalloc SKPoint[4];
        for (var i = 0; i < _layers.Count && i < 4; i++)
            offsets[i] = new SKPoint((float)(_layers[i].View.TranslationX * scale), (float)(_layers[i].View.TranslationY * scale));
        var t = AnimateAmbient && !TiltMotion.ReduceMotion ? (DateTime.UtcNow - _ambientStart).TotalSeconds : 0;
        AmbientPainter.Draw(canvas, r.Plan.Ambient.Where(a => a.Depth == depth).ToList(), vp, t, offsets);
    }

    // ------------------------------------------------------------------ motion

    private void Start()
    {
        Invalidate(force: false);
        ObserveWindow();
        if (_subscribed) return;
        _subscribed = true;
        TiltMotion.Changed += OnTilt;
        TiltMotion.Subscribe();
        UpdateAmbientClock();
    }

    private void Stop()
    {
        if (!_subscribed) return;
        _subscribed = false;
        TiltMotion.Changed -= OnTilt;
        TiltMotion.Unsubscribe();
        _ambientClock?.Stop();
    }

    private void ObserveWindow()
    {
        var window = Window;
        if (ReferenceEquals(window, _observedWindow)) return;
        if (_observedWindow is not null)
        {
            _observedWindow.Stopped -= OnWindowStopped;
            _observedWindow.Resumed -= OnWindowResumed;
        }
        _observedWindow = window;
        if (window is not null)
        {
            window.Stopped += OnWindowStopped;
            window.Resumed += OnWindowResumed;
        }
    }

    private void OnWindowStopped(object? sender, EventArgs e) => Stop();

    private void OnWindowResumed(object? sender, EventArgs e)
    {
        if (Handler is not null) Start();
    }

    private void OnTilt()
    {
        ApplyOffsets();
        InvalidateAmbient();
    }

    private void InvalidateAmbient()
    {
        foreach (var (view, _) in _ambientViews) view.InvalidateSurface();
    }

    private void ApplyOffsets()
    {
        var x = TiltMotion.X;
        var y = TiltMotion.Y;
        foreach (var (view, travel) in _layers)
        {
            view.TranslationX = Math.Clamp(x, -1, 1) * travel;
            view.TranslationY = Math.Clamp(y, -1, 1) * travel;
        }
    }

    private void UpdateAmbientClock()
    {
        var animate = _subscribed && _ambientViews.Count > 0 && AnimateAmbient && !TiltMotion.ReduceMotion;
        if (!animate)
        {
            _ambientClock?.Stop();
            InvalidateAmbient();
            return;
        }
        if (_ambientClock is null)
        {
            _ambientClock = Dispatcher.CreateTimer();
            _ambientClock.Interval = TimeSpan.FromMilliseconds(33);
            _ambientClock.Tick += (_, _) => InvalidateAmbient();
        }
        _ambientClock.Start();
    }
}
