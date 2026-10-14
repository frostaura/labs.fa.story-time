using StoryTime.Core.Abstractions;
using StoryTime.Core.Services;

#if IOS
using UIKit;
#endif
#if ANDROID
using Android.Content;
using Android.OS;
#endif

namespace StoryTime.App.Services;

/// <summary>IHaptics over the platform feedback engines. iOS: UIFeedbackGenerator family
/// (impact/selection/notification — automatically silent when the user disables System
/// Haptics); Tick is a soft impact at 0.4 intensity, Celebrate a light→medium two-beat
/// ~120 ms apart. Android: predefined VibrationEffects via VibratorManager/Vibrator
/// (CLICK/TICK/HEAVY_CLICK/DOUBLE_CLICK — the OEM haptic engine honours system settings);
/// Tick is EFFECT_TICK, Celebrate a two-pulse soft waveform (never the harsh double-click).
///
/// Coalescing (HapticTickLimiter, Core-tested): Tick ≥60 ms so scrubbing can't buzz; Light,
/// Medium and Selection each carry their own short window so a press-down haptic (the
/// anticipatory rule — see <see cref="PressHaptics"/>) plus the same gesture's command-level
/// haptic can never double-fire. The Press* variants fire at gesture recognition and arm the
/// longer press refractory; Success / Warning / Celebrate are terminal outcomes and are
/// deliberately never coalesced. Every call is fire-and-forget, main-thread-safe and
/// swallow-on-error: haptics must never crash or block the UI. Registered in MauiProgram
/// before AddStoryTimeCore so it wins over Core's NullHaptics.</summary>
public sealed class PlatformHaptics : IHaptics
{
    /// <summary>Continuous-tick coalescing window — one shared limiter, app-wide, so two
    /// simultaneous continuous surfaces can't stack into a buzz either.</summary>
    private readonly HapticTickLimiter _tickLimiter = new();

    private readonly HapticTickLimiter _lightLimiter = new(HapticTickLimiter.LightWindowMs);
    private readonly HapticTickLimiter _mediumLimiter = new(HapticTickLimiter.MediumWindowMs);
    private readonly HapticTickLimiter _selectionLimiter = new(HapticTickLimiter.SelectionWindowMs);

    /// <summary>Debug-only breadcrumb so a QA pass can verify calls fire on the emulator
    /// (where haptics can't be felt): grep logcat / sim console for "TWHaptics". Suppressed
    /// (coalesced) calls log too, tagged "~coalesced", so the dedupe is provable.</summary>
    [System.Diagnostics.Conditional("DEBUG")]
    private static void Trace(string name)
    {
#if ANDROID
        Android.Util.Log.Debug("TWHaptics", name);
#else
        Console.WriteLine($"TWHaptics {name}");
#endif
    }

    // ---- IHaptics (command-level; coalesced against the press-down variants) ----------

    public void Light()
    {
        if (!_lightLimiter.TryPass()) { Trace("Light~coalesced"); return; }
        Fire(Kind.Light);
    }

    public void Selection()
    {
        if (!_selectionLimiter.TryPass()) { Trace("Selection~coalesced"); return; }
        Fire(Kind.Selection);
    }

    public void Medium()
    {
        if (!_mediumLimiter.TryPass()) { Trace("Medium~coalesced"); return; }
        Fire(Kind.Medium);
    }

    public void Tick()
    {
        if (!_tickLimiter.TryPass()) return;   // scrub streams — stay quiet in the log too
        Fire(Kind.Tick);
    }

    // Terminal outcomes — never coalesced.
    public void Success() => Fire(Kind.Success);
    public void Warning() => Fire(Kind.Warning);
    public void Celebrate() => Fire(Kind.Celebrate);

    // ---- press-down (gesture-recognition) variants ------------------------------------
    // Fire at pointer-down / threshold-crossing, BEFORE the command runs; passing arms the
    // long press refractory so the command's own haptic coalesces away (see PressHaptics).

    public void PressLight()
    {
        if (!_lightLimiter.TryPassPress()) { Trace("PressLight~coalesced"); return; }
        Fire(Kind.Light, press: true);
    }

    public void PressMedium()
    {
        if (!_mediumLimiter.TryPassPress()) { Trace("PressMedium~coalesced"); return; }
        Fire(Kind.Medium, press: true);
    }

    public void PressSelection()
    {
        if (!_selectionLimiter.TryPassPress()) { Trace("PressSelection~coalesced"); return; }
        Fire(Kind.Selection, press: true);
    }

    private enum Kind { Light, Selection, Medium, Success, Warning, Tick, Celebrate }

    private void Fire(Kind kind, bool press = false)
    {
        Trace(press ? $"{kind}@press" : kind.ToString());
        Run(() => Perform(kind));
    }

    private static void Run(Action action)
    {
        try
        {
            if (MainThread.IsMainThread) action();
            else MainThread.BeginInvokeOnMainThread(() => { try { action(); } catch { /* never crash */ } });
        }
        catch
        {
            // Haptics are decoration — never let them take the app down.
        }
    }

#if IOS
    private static void Perform(Kind kind)
    {
        switch (kind)
        {
            case Kind.Light:
            {
                using var gen = new UIImpactFeedbackGenerator(UIImpactFeedbackStyle.Light);
                gen.Prepare();
                gen.ImpactOccurred();
                break;
            }
            case Kind.Selection:
            {
                using var gen = new UISelectionFeedbackGenerator();
                gen.Prepare();
                gen.SelectionChanged();
                break;
            }
            case Kind.Medium:
            {
                using var gen = new UIImpactFeedbackGenerator(UIImpactFeedbackStyle.Medium);
                gen.Prepare();
                gen.ImpactOccurred();
                break;
            }
            case Kind.Success:
            {
                using var gen = new UINotificationFeedbackGenerator();
                gen.Prepare();
                gen.NotificationOccurred(UINotificationFeedbackType.Success);
                break;
            }
            case Kind.Warning:
            {
                using var gen = new UINotificationFeedbackGenerator();
                gen.Prepare();
                gen.NotificationOccurred(UINotificationFeedbackType.Warning);
                break;
            }
            case Kind.Tick:
            {
                // The lightest possible tap: a soft impact dialled down to 0.4 intensity.
                using var gen = new UIImpactFeedbackGenerator(UIImpactFeedbackStyle.Soft);
                gen.Prepare();
                gen.ImpactOccurred(0.4f);
                break;
            }
            case Kind.Celebrate:
            {
                // Gentle two-beat: light now, medium ~120 ms later — a quiet "ta-da", not a thud.
                using (var first = new UIImpactFeedbackGenerator(UIImpactFeedbackStyle.Light))
                {
                    first.Prepare();
                    first.ImpactOccurred();
                }
                var second = new UIImpactFeedbackGenerator(UIImpactFeedbackStyle.Medium);
                second.Prepare();
                _ = Task.Delay(120).ContinueWith(_ => MainThread.BeginInvokeOnMainThread(() =>
                {
                    try { second.ImpactOccurred(0.8f); }
                    catch { /* never crash */ }
                    finally { second.Dispose(); }
                }));
                break;
            }
        }
    }
#elif ANDROID
    private static void Perform(Kind kind)
    {
        try
        {
            // Predefined effects exist from API 29; below that stay silent rather than buzz crudely.
            if (!OperatingSystem.IsAndroidVersionAtLeast(29)) return;

            var context = Platform.AppContext;
            Vibrator? vibrator;
            if (OperatingSystem.IsAndroidVersionAtLeast(31))
            {
                var manager = context.GetSystemService(Context.VibratorManagerService) as VibratorManager;
                vibrator = manager?.DefaultVibrator;
            }
            else
            {
#pragma warning disable CA1422 // VibratorService is the pre-31 path by design
                vibrator = context.GetSystemService(Context.VibratorService) as Vibrator;
#pragma warning restore CA1422
            }
            if (vibrator is null || !vibrator.HasVibrator) return;

            if (kind == Kind.Celebrate)
            {
                // Gentle two-beat: two short soft pulses ~110 ms apart — deliberately NOT
                // the OS EFFECT_DOUBLE_CLICK, which lands far too hard for bedtime.
                var effect = vibrator.HasAmplitudeControl
                    ? VibrationEffect.CreateWaveform([0, 18, 110, 26], [0, 90, 0, 160], -1)
                    : VibrationEffect.CreateWaveform([0, 15, 110, 22], -1);
                if (effect is not null) vibrator.Vibrate(effect);
                return;
            }

            var predefined = kind switch
            {
                Kind.Selection or Kind.Tick => VibrationEffect.EffectTick,
                Kind.Medium or Kind.Warning => VibrationEffect.EffectHeavyClick,
                Kind.Success => VibrationEffect.EffectDoubleClick,
                _ => VibrationEffect.EffectClick,
            };
            vibrator.Vibrate(VibrationEffect.CreatePredefined(predefined));
        }
        catch
        {
            // Haptics are decoration — never let them take the app down.
        }
    }
#else
    private static void Perform(Kind kind) { _ = kind; }
#endif
}
