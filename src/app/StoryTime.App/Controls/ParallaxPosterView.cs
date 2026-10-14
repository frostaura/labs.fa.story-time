using SkiaSharp;
using StoryTime.App.Graphics;
using StoryTime.Core.Abstractions;

namespace StoryTime.App.Controls;

/// <summary>4-layer parallax poster: a window into the scene. Each layer is rasterized ONCE
/// (SkiaSharp → PNG → Image) at the view size PLUS a bleed margin equal to the deepest
/// layer's travel, then translated on BOTH axes by low-pass-filtered accelerometer tilt —
/// the phone's attitude relative to a fixed neutral hold — clamped to each layer's
/// ParallaxTravel ([12,20,30,40]pt by depth). Roll moves it horizontally, pitch vertically, a
/// diagonal tilt diagonally; every layer moves, the foreground 3.3× as far as the stars.
///
/// The stack is five images, not four: the depth-0 sky GRADIENT is split out and pinned as a
/// fixed backdrop so the depth-0 stars and moon travel AGAINST it rather than with it, and a
/// fixed (non-translating) vignette overlay on top grounds the whole thing.
///
/// **The accelerometer is the ONLY motion source on any device that has one** (founder
/// mandate, 2026-07-31). The figure-eight idle drift exists solely so a sensorless host
/// (iOS simulator, emulator without sensor injection) is not a frozen rectangle; it is
/// gated behind "sensor absent OR proven silent for 2 s", it is torn down the instant a
/// real reading arrives, and once a reading has ever arrived it can never start again for
/// the life of the view. If both loops could ever run at once the drift would overwrite the
/// sensor's offsets on its next tick (last-writer-wins) and the poster would wander instead
/// of tracking the phone — that arbitration is the reason for _sensorEverDelivered.
///
/// Lifecycle is belt-and-braces by design: Loaded/Unloaded does NOT fire reliably for views
/// inside Shell modal pages on iOS, so motion also starts on handler attach and stops on
/// handler detach, and the view follows Window Resumed/Stopped so tilt pauses while the app
/// is backgrounded. All motion stops when the view leaves the tree.</summary>
public sealed class ParallaxPosterView : ContentView
{
    /// <summary>Filter constant on the tilt vector. 0.18 at SensorSpeed.Game (~50 Hz) is a
    /// ~80 ms time constant: smooth enough to kill hand jitter, fast enough that the poster
    /// tracks the wrist rather than trailing it.</summary>
    private const double Alpha = 0.18;

    /// <summary>Neutral hold attitude, in g. MAUI normalizes BOTH platforms to the Android
    /// convention: the reading is a unit vector pointing along the device's world-up axis, so
    /// a phone held bolt upright in portrait reads (0, +1, 0) and one lying flat face-up
    /// reads (0, 0, +1). (iOS CoreMotion's raw sign is the opposite; Essentials flips it.
    /// Getting this backwards is exactly what pinned the vertical channel — see the note on
    /// TiltGain.) Roll (X) is absolutely referenced: 0 g is "screen level left-to-right".
    /// Pitch (Y) is referenced to +0.75 g ≈ 41° back from vertical — a natural bedtime
    /// reading angle — so a phone in the hand sits near the CENTRE of the vertical range
    /// instead of against its clamp.</summary>
    private const double NeutralX = 0.0;
    private const double NeutralY = 0.75;

    /// <summary>Full travel at 1/2.0 = 0.5 g of deviation from neutral (~30° of tilt) on
    /// each axis independently, so a diagonal tilt produces a proportional diagonal offset.
    ///
    /// This gain, and the neutral above, ARE the bug the founder reported twice. The original
    /// implementation had neither: it fed raw gravity straight in at unity gain, so (a) the
    /// vertical channel sat at 0.80–0.90 of full deflection permanently and a hand-sized
    /// pitch change moved it ~2 px, and (b) a hand-sized roll of ±0.15 g moved the foreground
    /// ~3.6 px and the sky ~0.6 px on a 226 pt poster — invisible. The Android-emulator QA
    /// that "proved" the earlier fix injected ±0.7 g on both axes at once, an attitude a hand
    /// never reaches, which is why it measured a healthy 89 px and looked correct.</summary>
    private const double TiltGain = 2.0;

    private static readonly TimeSpan SensorWatchdog = TimeSpan.FromSeconds(2);

    public static readonly BindableProperty PosterProperty = BindableProperty.Create(
        nameof(Poster), typeof(ResolvedPoster), typeof(ParallaxPosterView),
        propertyChanged: OnPosterChanged);

    private static void OnPosterChanged(BindableObject bindable, object? oldValue, object? newValue)
    {
        var view = (ParallaxPosterView)bindable;
        var layers = (newValue as ResolvedPoster)?.Layers.Count ?? -1;
        ParallaxLog.Write($"poster changed → layers={layers} (w={view.Width:0.#} h={view.Height:0.#})");
        view._renderedForWidth = -1;   // a new poster always re-renders, even at the same width
        view.Invalidate();
    }

    public ResolvedPoster? Poster
    {
        get => (ResolvedPoster?)GetValue(PosterProperty);
        set => SetValue(PosterProperty, value);
    }

    public static readonly BindableProperty CornerRadiusProperty = BindableProperty.Create(
        nameof(CornerRadius), typeof(double), typeof(ParallaxPosterView), 20.0);

    public double CornerRadius
    {
        get => (double)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    private readonly Grid _stack = new();
    private readonly List<(Image View, double Travel)> _layers = [];
    private double _filteredX;
    private double _filteredY;
    private bool _running;                // some motion source is active (sensor or drift)
    private bool _sensorRunning;
    private bool _sensorDelivered;        // a reading arrived since this Start (watchdog evidence)
    private bool _sensorEverDelivered;    // a reading arrived at ANY point → drift is dead forever
    private bool _driftRunning;
    private IDispatcherTimer? _driftTimer;
    private IDispatcherTimer? _watchdogTimer;
    private double _driftPhase;
    private int _renderedForWidth = -1;
    private Window? _observedWindow;
    private long _readingCount;
    private DateTime _sensorStartedUtc;

    public ParallaxPosterView()
    {
        Content = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = new CornerRadius(20) },
            Content = _stack,
            BackgroundColor = Colors.Transparent,
        };
        Loaded += (_, _) => Start("Loaded");
        Unloaded += (_, _) => Stop("Unloaded");
        SizeChanged += (_, _) =>
        {
            ParallaxLog.Write($"size changed → {Width:0.#}x{Height:0.#}");
            Invalidate();
        };
        ParallaxLog.Write("ctor");
    }

    /// <summary>Loaded is unreliable inside Shell modal pages (iOS); handler attach is the
    /// dependable "I am on screen now" signal, so motion starts here too (Start is
    /// idempotent — whichever event fires first wins, the second is a no-op).</summary>
    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        if (Handler is null) Stop("HandlerDetached");
        else Start("HandlerAttached");
    }

    private void Invalidate()
    {
        if (Poster is null || Width <= 0 || Height <= 0) return;
        var widthKey = (int)Width;
        if (_renderedForWidth == widthKey && _layers.Count > 0) return;
        _renderedForWidth = widthKey;
        RenderLayers();
    }

    private void RenderLayers()
    {
        var poster = Poster;
        if (poster is null) return;

        _stack.Children.Clear();
        _layers.Clear();

        var ordered = poster.Layers.OrderBy(l => l.Depth).ToList();
        // Bleed = the deepest travel, so even the fastest layer never exposes an edge at
        // full deflection. A uniform bleed (rather than per-layer) keeps every layer in the
        // same coordinate space — horizons and waterlines must line up across depths.
        var bleed = ordered.Count > 0 ? ordered.Max(l => l.ParallaxTravel) : 0;

        var density = DeviceDisplay.MainDisplayInfo.Density is > 0 and var d ? d : 2.0;
        var layerWidth = Width + bleed * 2;
        var layerHeight = Height + bleed * 2;
        var pxWidth = (int)(layerWidth * density);
        var pxHeight = (int)(layerHeight * density);
        if (pxWidth <= 0 || pxHeight <= 0) return;

        // The sky GRADIENT is split off depth 0 and pinned. A smooth vertical ramp looks
        // identical translating or still, so pinning it costs nothing visually and buys two
        // things: it can never expose an edge, and it gives the depth-0 stars and moon
        // something to travel AGAINST. Without the split, the whole back plate moved as one
        // and the sky read as dead — the founder's "just the front layers get parallax".
        var skyLayer = ordered.FirstOrDefault(l => l.BackgroundTopHex is not null || l.BackgroundHex != "transparent");
        if (skyLayer is not null)
        {
            using var sky = SkiaPosterRenderer.RenderLayer(skyLayer, pxWidth, pxHeight,
                includeBackground: true, includeElements: false);
            _stack.Children.Add(LayerImage(sky, bleed));   // NOT added to _layers: fixed backdrop
        }

        foreach (var layer in ordered)
        {
            using var image = SkiaPosterRenderer.RenderLayer(layer, pxWidth, pxHeight,
                includeBackground: false, includeElements: true);
            var view = LayerImage(image, bleed);
            _stack.Children.Add(view);
            _layers.Add((view, layer.ParallaxTravel));
        }

        // Fixed vignette on top: rendered at the VIEW size (not bled) so the corner shading
        // hugs the visible frame and never slides with the tilting layers.
        var vw = (int)(Width * density);
        var vh = (int)(Height * density);
        if (vw > 0 && vh > 0)
        {
            using var vignette = SkiaPosterRenderer.RenderVignetteOverlay(vw, vh);
            _stack.Children.Add(LayerImage(vignette, 0));
        }

        ApplyOffsets();
        ParallaxLog.Write($"rendered fixedSky={skyLayer is not null} + {_layers.Count} travelling layers + vignette · " +
                          $"view={Width:0.#}x{Height:0.#} " +
                          $"bleed={bleed:0.#}pt layer={layerWidth:0.#}x{layerHeight:0.#}pt " +
                          $"image={pxWidth}x{pxHeight}px density={density:0.##} " +
                          $"travels=[{string.Join(",", _layers.Select(l => l.Travel))}]");
        ParallaxLog.WhenIdle(this, LogLayerFrames);
    }

    /// <summary>Post-layout evidence that the bleed survived arrangement. MAUI clamps a
    /// non-Fill child to its container, which would silently erase the overscan and let the
    /// poster's background show through at full tilt — Fill + negative margin is the shape
    /// that does NOT get clamped, and this proves it on the actual device.</summary>
    private void LogLayerFrames()
    {
        for (var i = 0; i < _layers.Count; i++)
        {
            var v = _layers[i].View;
            ParallaxLog.Write($"layer[{i}] travel={_layers[i].Travel} frame=({v.X:0.#},{v.Y:0.#}) " +
                              $"{v.Width:0.#}x{v.Height:0.#} tx={v.TranslationX:0.##} ty={v.TranslationY:0.##}");
        }
    }

    /// <summary>Fill + a negative margin equal to the bleed. Deliberately NOT
    /// WidthRequest/Center: MAUI's arrange path clamps a centered child's frame to
    /// min(container, desired), which silently discards any overscan; the Fill path
    /// inflates by the (negative) margin instead and keeps it.</summary>
    private static Image LayerImage(SKImage image, double bleed)
    {
        using var data = image.Encode(SKEncodedImageFormat.Png, 92);
        var bytes = data.ToArray();
        return new Image
        {
            Source = ImageSource.FromStream(() => new MemoryStream(bytes)),
            Aspect = Aspect.Fill,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
            Margin = new Thickness(-bleed),
            InputTransparent = true,
        };
    }

    // ---------------- motion lifecycle ----------------

    private void Start(string reason)
    {
        Invalidate();
        ObserveWindow();
        if (_running) { ParallaxLog.Write($"start ({reason}) — already running, no-op"); return; }
        _running = true;

        _sensorDelivered = false;
        _readingCount = 0;
        bool supported;
        try { supported = Accelerometer.Default.IsSupported; }
        catch (Exception ex) { supported = false; ParallaxLog.Write($"IsSupported threw {ex.GetType().Name}: {ex.Message}"); }
        ParallaxLog.Write($"start ({reason}) · supported={supported} monitoring=" +
                          $"{SafeMonitoring()} everDelivered={_sensorEverDelivered}");

        try
        {
            if (supported)
            {
                Accelerometer.Default.ReadingChanged += OnReading;
                if (!Accelerometer.Default.IsMonitoring)
                    Accelerometer.Default.Start(SensorSpeed.Game);
                _sensorRunning = true;
                _sensorStartedUtc = DateTime.UtcNow;
                StartWatchdog();
                ParallaxLog.Write($"accelerometer started · monitoring={SafeMonitoring()}");
                return;
            }
            ParallaxLog.Write("accelerometer NOT supported → drift fallback");
        }
        catch (Exception ex)
        {
            ParallaxLog.Write($"accelerometer failed ({ex.GetType().Name}: {ex.Message}) → drift fallback");
            try { Accelerometer.Default.ReadingChanged -= OnReading; } catch { /* best effort */ }
            _sensorRunning = false;
        }
        StartDrift("sensor unavailable");
    }

    private static string SafeMonitoring()
    {
        try { return Accelerometer.Default.IsMonitoring.ToString(); }
        catch (Exception ex) { return $"?({ex.GetType().Name})"; }
    }

    private void Stop(string reason)
    {
        if (!_running && !_sensorRunning && !_driftRunning) return;
        ParallaxLog.Write($"stop ({reason}) · readings={_readingCount}");
        _running = false;
        try
        {
            if (_sensorRunning)
            {
                Accelerometer.Default.ReadingChanged -= OnReading;
                if (Accelerometer.Default.IsMonitoring) Accelerometer.Default.Stop();
                _sensorRunning = false;
            }
        }
        catch (Exception) { /* sensor teardown is best-effort */ }
        StopDrift("stopped");
        _watchdogTimer?.Stop();
    }

    /// <summary>Pauses motion while the app is backgrounded and resumes with it. The
    /// Window property only becomes non-null once attached, so this is (re)hooked from
    /// Start, which runs on both Loaded and handler attach.</summary>
    private void ObserveWindow()
    {
        var window = Window;
        if (ReferenceEquals(window, _observedWindow)) return;
        if (_observedWindow is not null)
        {
            _observedWindow.Stopped -= OnWindowStopped;
            _observedWindow.Resumed -= OnWindowResumed;
            _observedWindow.Destroying -= OnWindowStopped;
        }
        _observedWindow = window;
        if (window is not null)
        {
            window.Stopped += OnWindowStopped;
            window.Resumed += OnWindowResumed;
            window.Destroying += OnWindowStopped;
        }
    }

    private void OnWindowStopped(object? sender, EventArgs e) => Stop("WindowStopped");

    private void OnWindowResumed(object? sender, EventArgs e)
    {
        // Only resume if we are still in the visual tree (the page may have closed).
        if (Handler is not null) Start("WindowResumed");
    }

    // ---------------- sensor path (the only motion source on real hardware) ----------------

    private void OnReading(object? sender, AccelerometerChangedEventArgs e)
    {
        var raw = e.Reading.Acceleration;
        _readingCount++;

        if (!_sensorDelivered)
        {
            _sensorDelivered = true;
            var first = !_sensorEverDelivered;
            _sensorEverDelivered = true;
            ParallaxLog.Write($"FIRST reading after {(DateTime.UtcNow - _sensorStartedUtc).TotalMilliseconds:0}ms " +
                              $"raw=({raw.X:0.###},{raw.Y:0.###},{raw.Z:0.###}) coldStart={first}");
            _watchdogTimer?.Stop();
            // A real reading outranks any fallback, permanently. Without this the drift
            // timer would keep overwriting the sensor's offsets on its next tick.
            if (_driftRunning) MainThread.BeginInvokeOnMainThread(() => StopDrift("real reading arrived"));
        }

        // Tilt relative to the neutral hold attitude, scaled so a comfortable wrist tilt
        // reaches full travel. Gravity components approximate device attitude directly.
        var tiltX = Math.Clamp((raw.X - NeutralX) * TiltGain, -1, 1);
        var tiltY = Math.Clamp((raw.Y - NeutralY) * TiltGain, -1, 1);
        _filteredX += (tiltX - _filteredX) * Alpha;
        _filteredY += (tiltY - _filteredY) * Alpha;

        TrackRange(raw.X, raw.Y);
        if (_readingCount % 50 == 0)
            ParallaxLog.Write($"r#{_readingCount} raw=({raw.X:0.###},{raw.Y:0.###},{raw.Z:0.###}) " +
                              $"tilt=({tiltX:0.###},{tiltY:0.###}) filt=({_filteredX:0.###},{_filteredY:0.###}) " +
                              $"drift={_driftRunning} {OffsetSummary()}");
        if (_readingCount % 250 == 0) FlushRange();

        MainThread.BeginInvokeOnMainThread(ApplyOffsets);
    }

    /// <summary>A started sensor that never delivers (some emulators, TCC-odd sims) would
    /// leave the poster frozen — after 2 s of silence, hand over to the drift loop. On real
    /// hardware this never fires; the first reading lands in tens of milliseconds.</summary>
    private void StartWatchdog()
    {
        _watchdogTimer ??= Dispatcher.CreateTimer();
        _watchdogTimer.Interval = SensorWatchdog;
        _watchdogTimer.IsRepeating = false;
        _watchdogTimer.Tick -= OnWatchdogTick;
        _watchdogTimer.Tick += OnWatchdogTick;
        _watchdogTimer.Start();
    }

    private void OnWatchdogTick(object? sender, EventArgs e)
    {
        if (!_running || _sensorDelivered) return;
        if (_sensorEverDelivered)
        {
            // This device HAS a working accelerometer; a momentary gap is not a reason to
            // start autonomous motion. Wait for it.
            ParallaxLog.Write("watchdog: silent, but sensor has delivered before → NOT starting drift");
            return;
        }
        ParallaxLog.Write($"watchdog: sensor silent after 2s (monitoring={SafeMonitoring()}) → drift fallback");
        try
        {
            Accelerometer.Default.ReadingChanged -= OnReading;
            if (Accelerometer.Default.IsMonitoring) Accelerometer.Default.Stop();
        }
        catch (Exception) { /* best effort */ }
        _sensorRunning = false;
        StartDrift("sensor silent");
    }

    // ---------------- drift path (sensorless hosts ONLY) ----------------

    private void StartDrift(string reason)
    {
        if (_sensorEverDelivered)
        {
            ParallaxLog.Write($"drift suppressed ({reason}) — this device has a live accelerometer");
            return;
        }
        if (_driftRunning) return;
        _driftTimer ??= Dispatcher.CreateTimer();
        _driftTimer.Interval = TimeSpan.FromMilliseconds(50);
        _driftTimer.Tick -= OnDriftTick;
        _driftTimer.Tick += OnDriftTick;
        _driftTimer.Start();
        _driftRunning = true;
        ParallaxLog.Write($"idle drift STARTED ({reason})");
    }

    private void StopDrift(string reason)
    {
        _driftTimer?.Stop();
        if (!_driftRunning) return;
        _driftRunning = false;
        ParallaxLog.Write($"idle drift STOPPED ({reason})");
    }

    private void OnDriftTick(object? sender, EventArgs e)
    {
        // Slow figure-eight idle drift (full cycle ≈ 16 s) so a sensorless host still shows
        // depth — a ken-burns whisper, never a bounce. The dispatcher timer yields to the UI
        // loop by construction (no hot loop; see gotchas).
        if (_sensorEverDelivered) { StopDrift("sensor took over"); return; }
        _driftPhase += 0.02;
        _filteredX = Math.Sin(_driftPhase) * 0.45;
        _filteredY = Math.Sin(_driftPhase * 0.7 + 1.3) * 0.35;
        ApplyOffsets();
    }

    /// <summary>Both axes, every layer, every reading — the offset is a 2-D vector, so a
    /// diagonal tilt gives a diagonal shift. Content moves OPPOSITE the edge you tip away:
    /// drop the right edge (raw X goes negative) and the scene slides left; tip the top edge
    /// away (raw Y falls toward 0) and the scene slides down, revealing the sky. Depth scales
    /// the whole vector, so the foreground swings ~6.3× the sky and the poster reads as a
    /// window rather than a sliding picture.</summary>
    private void ApplyOffsets()
    {
        foreach (var (view, travel) in _layers)
        {
            view.TranslationX = Math.Clamp(_filteredX * travel, -travel, travel);
            view.TranslationY = Math.Clamp(-_filteredY * travel, -travel, travel);
        }
    }

    // ---------------- tilt-response evidence ----------------
    // A rolling min/max over each ~5 s window, logged as one line. Instantaneous samples
    // prove the maths; the SPAN proves the poster actually answered a physical tilt, which
    // is the only thing that settles "is the parallax working on the phone".

    private double _rMinX = double.MaxValue, _rMaxX = double.MinValue;
    private double _rMinY = double.MaxValue, _rMaxY = double.MinValue;

    private void TrackRange(double x, double y)
    {
        _rMinX = Math.Min(_rMinX, x); _rMaxX = Math.Max(_rMaxX, x);
        _rMinY = Math.Min(_rMinY, y); _rMaxY = Math.Max(_rMaxY, y);
    }

    private void FlushRange()
    {
        if (_rMinX > _rMaxX || _layers.Count == 0) return;
        var front = _layers[^1].Travel;
        var back = _layers[0].Travel;
        var spanX = Math.Min(Math.Abs(_rMaxX - _rMinX) * TiltGain, 2);
        var spanY = Math.Min(Math.Abs(_rMaxY - _rMinY) * TiltGain, 2);
        ParallaxLog.Write($"RANGE(5s) rawX=[{_rMinX:0.###}..{_rMaxX:0.###}] rawY=[{_rMinY:0.###}..{_rMaxY:0.###}] " +
                          $"→ front swing x={spanX * front:0.#}pt y={spanY * front:0.#}pt · " +
                          $"sky swing x={spanX * back:0.#}pt y={spanY * back:0.#}pt");
        _rMinX = double.MaxValue; _rMaxX = double.MinValue;
        _rMinY = double.MaxValue; _rMaxY = double.MinValue;
    }

    private string OffsetSummary() =>
        _layers.Count == 0
            ? "layers=0 (NOTHING TO MOVE)"
            : $"tx=[{string.Join(",", _layers.Select(l => l.View.TranslationX.ToString("0.#")))}] " +
              $"ty=[{string.Join(",", _layers.Select(l => l.View.TranslationY.ToString("0.#")))}]";
}

/// <summary>Device-side diagnostics for the parallax stack. Every line is prefixed
/// <c>TWParallax:</c> so a device console capture can be grepped straight out of
/// <c>devicectl device process launch --console</c>. DEBUG-only by construction — the
/// Conditional attribute strips every call site from Release.</summary>
internal static class ParallaxLog
{
    private static readonly object Gate = new();
    private static string? _path;

    /// <summary>Third capture channel, for when the device console cannot be attached (the
    /// phone was locked at launch, or the process was started detached): the same lines land
    /// in the app container's Documents folder, pullable with
    /// <c>devicectl device copy from --domain-type appDataContainer</c>.</summary>
    private static string? Path_
    {
        get
        {
            if (_path is not null) return _path;
            try
            {
                var docs = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(FileSystem.AppDataDirectory, "..", "Documents"));
                Directory.CreateDirectory(docs);
                _path = System.IO.Path.Combine(docs, "tw-parallax.log");
                File.AppendAllText(_path, $"--- session {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---{Environment.NewLine}");
                Console.WriteLine($"TWParallax: log file → {_path}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"TWParallax: log file unavailable ({ex.GetType().Name})");
                _path = string.Empty;
            }
            return _path;
        }
    }

    [System.Diagnostics.Conditional("DEBUG")]
    public static void Write(string message)
    {
        var line = $"TWParallax: [{DateTime.Now:HH:mm:ss.fff}] {message}";
        Console.WriteLine(line);
        // Console/stdout is the primary capture; os_log is the belt-and-braces one for the
        // case where the console cannot be attached (locked phone, detached launch).
        System.Diagnostics.Debug.WriteLine(line);
        lock (Gate)
        {
            var path = Path_;
            if (string.IsNullOrEmpty(path)) return;
            try { File.AppendAllText(path, line + Environment.NewLine); }
            catch (Exception) { /* diagnostics must never break the poster */ }
        }
    }

    /// <summary>Runs an action after the next layout settles — used to read back the
    /// arranged frames of the layer views.</summary>
    [System.Diagnostics.Conditional("DEBUG")]
    public static void WhenIdle(VisualElement owner, Action action)
    {
        var timer = owner.Dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(400);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => action();
        timer.Start();
    }
}
