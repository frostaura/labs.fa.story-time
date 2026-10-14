namespace StoryTime.App.Services;

/// <summary>One shared tilt source for every parallax poster on screen (player, Home cards,
/// Kid Shelf tiles). The accelerometer is a process-wide singleton: per-view start/stop
/// would let the first card that scrolls away stop the sensor under every other poster, so
/// the hub reference-counts subscribers, filters ONCE, and broadcasts the filtered tilt on
/// the main thread.
///
/// Calibration (unchanged from the founder-validated player tuning, 2026-07-31): tilt is
/// measured against a neutral bedtime reading hold (roll 0 g, pitch +0.75 g ≈ 41° back from
/// upright), full travel at 0.5 g of deviation (TiltGain 2), and a low-pass α = 0.18 at
/// SensorSpeed.Game (~80 ms time constant: no hand jitter, no lag behind the wrist).
///
/// The accelerometer is the ONLY motion source on hardware that has one. A slow
/// figure-eight drift runs solely on sensorless hosts (the simulator) or when a started
/// sensor stays silent for 2 s, and it dies for good the moment a real reading arrives.
///
/// Reduce Motion: parallax is exactly the motion iOS removes under Reduce Motion, so the
/// hub never starts and every poster holds still at its centred composition.</summary>
public static class TiltMotion
{
    private const double Alpha = 0.18;
    private const double NeutralX = 0.0;
    private const double NeutralY = 0.75;
    private const double TiltGain = 2.0;
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(2);

    private static int _subscribers;
    private static bool _sensorRunning;
    private static bool _sensorDelivered;
    private static bool _sensorEverDelivered;
    private static IDispatcherTimer? _driftTimer;
    private static IDispatcherTimer? _watchdogTimer;
    private static double _driftPhase;
    private static bool _pendingPublish;

    /// <summary>Filtered tilt in [-1, 1] per axis; +X slides content right, +Y down.</summary>
    public static double X { get; private set; }
    public static double Y { get; private set; }

    /// <summary>Raised on the main thread whenever the filtered tilt moves.</summary>
    public static event Action? Changed;

    public static bool ReduceMotion
    {
        get
        {
            try { return UIKit.UIAccessibility.IsReduceMotionEnabled; }
            catch (Exception) { return false; }
        }
    }

    public static void Subscribe()
    {
        _subscribers++;
        if (_subscribers == 1) Start();
    }

    public static void Unsubscribe()
    {
        if (_subscribers == 0) return;
        _subscribers--;
        if (_subscribers == 0) Stop();
    }

    private static void Start()
    {
        X = Y = 0;
        if (ReduceMotion) { Publish(); return; }
        _sensorDelivered = false;
        try
        {
            if (Accelerometer.Default.IsSupported)
            {
                Accelerometer.Default.ReadingChanged += OnReading;
                if (!Accelerometer.Default.IsMonitoring) Accelerometer.Default.Start(SensorSpeed.Game);
                _sensorRunning = true;
                StartWatchdog();
                return;
            }
        }
        catch (Exception)
        {
            try { Accelerometer.Default.ReadingChanged -= OnReading; } catch (Exception) { /* best effort */ }
            _sensorRunning = false;
        }
        StartDrift();
    }

    private static void Stop()
    {
        try
        {
            if (_sensorRunning)
            {
                Accelerometer.Default.ReadingChanged -= OnReading;
                if (Accelerometer.Default.IsMonitoring) Accelerometer.Default.Stop();
            }
        }
        catch (Exception) { /* sensor teardown is best-effort */ }
        _sensorRunning = false;
        _driftTimer?.Stop();
        _watchdogTimer?.Stop();
    }

    private static void OnReading(object? sender, AccelerometerChangedEventArgs e)
    {
        var raw = e.Reading.Acceleration;
        if (!_sensorDelivered)
        {
            _sensorDelivered = true;
            _sensorEverDelivered = true;
            MainThread.BeginInvokeOnMainThread(() =>
            {
                _watchdogTimer?.Stop();
                _driftTimer?.Stop();
            });
        }
        var tiltX = Math.Clamp((raw.X - NeutralX) * TiltGain, -1, 1);
        var tiltY = Math.Clamp((raw.Y - NeutralY) * TiltGain, -1, 1);
        X += (tiltX - X) * Alpha;
        Y += (-tiltY - Y) * Alpha;
        Publish();
    }

    /// <summary>Coalesces readings: at most one pending main-thread hop at a time.</summary>
    private static void Publish()
    {
        if (_pendingPublish) return;
        _pendingPublish = true;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _pendingPublish = false;
            Changed?.Invoke();
        });
    }

    private static void StartWatchdog()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        _watchdogTimer ??= dispatcher.CreateTimer();
        _watchdogTimer.Interval = Watchdog;
        _watchdogTimer.IsRepeating = false;
        _watchdogTimer.Tick -= OnWatchdog;
        _watchdogTimer.Tick += OnWatchdog;
        _watchdogTimer.Start();
    }

    private static void OnWatchdog(object? sender, EventArgs e)
    {
        if (_subscribers == 0 || _sensorDelivered || _sensorEverDelivered) return;
        try
        {
            Accelerometer.Default.ReadingChanged -= OnReading;
            if (Accelerometer.Default.IsMonitoring) Accelerometer.Default.Stop();
        }
        catch (Exception) { /* best effort */ }
        _sensorRunning = false;
        StartDrift();
    }

    private static void StartDrift()
    {
        if (_sensorEverDelivered) return;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        _driftTimer ??= dispatcher.CreateTimer();
        _driftTimer.Interval = TimeSpan.FromMilliseconds(50);
        _driftTimer.Tick -= OnDrift;
        _driftTimer.Tick += OnDrift;
        _driftTimer.Start();
    }

    private static void OnDrift(object? sender, EventArgs e)
    {
        if (_sensorEverDelivered || _subscribers == 0) { _driftTimer?.Stop(); return; }
        // ~16 s figure-eight: a ken-burns whisper for sensorless hosts, never a bounce.
        _driftPhase += 0.02;
        X = Math.Sin(_driftPhase) * 0.45;
        Y = Math.Sin(_driftPhase * 0.7 + 1.3) * 0.35;
        Changed?.Invoke();
    }
}
