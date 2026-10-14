namespace StoryTime.Core.Services;

/// <summary>Coalesces haptic calls so one gesture can never double-thump. Two entry points:
///
/// <para><see cref="TryPass"/> — command-level haptics (nav Light, VM Selection, story-start
/// Medium…). A call only passes when the currently-armed window has elapsed since the last
/// call that passed. The default window is <see cref="MinIntervalMs"/>.</para>
///
/// <para><see cref="TryPassPress"/> — press-down (gesture-recognition) haptics. The
/// anticipatory-haptics rule (docs/design/mockup-spec.md) fires feedback at pointer-down /
/// threshold-crossing, BEFORE the command runs; the command's own haptic then lands a press
/// duration later (~100–300 ms) and must coalesce away. A press call passes whenever it is
/// outside the SHORT default window (two press sources overlapping on the same tap stay one
/// thump), and on passing it arms the LONGER <paramref name="refractoryMs"/> window so the
/// follow-up command haptic is swallowed even on a slow tap. A later press always re-opens
/// the gate after the short window — a new finger is a new gesture and always deserves its
/// feedback — while a passed command call re-arms only the short window.</para>
///
/// Lives in Core (not the platform layer) purely so the coalescing windows are unit-testable
/// with a fake clock; PlatformHaptics owns the app-wide instances. The default clock is
/// <see cref="Environment.TickCount64"/> — monotonic and immune to wall-clock changes.
/// Not thread-safe by design: haptics fire on the main thread.</summary>
public sealed class HapticTickLimiter
{
    public const long DefaultMinIntervalMs = 60;

    /// <summary>Light window: a nav Light (ShellNavigator) followed ~instantly by a
    /// page-open Light must feel like ONE tap, never a double-thump.</summary>
    public const long LightWindowMs = 90;

    /// <summary>Medium window — same job as the Light window for actions of weight
    /// (press-down Medium on the Make CTA vs. the VM's story-start Medium).</summary>
    public const long MediumWindowMs = 90;

    /// <summary>Selection window — picker rows fire press-down Selection and then the
    /// VM's change-committed Selection; only one may buzz.</summary>
    public const long SelectionWindowMs = 60;

    /// <summary>How long a press-down haptic suppresses the follow-up command haptic.
    /// Covers a slow, deliberate tap (press → release → command dispatch) without being
    /// long enough to eat the NEXT tap's command haptic in a fast repeat.</summary>
    public const long PressRefractoryMs = 350;

    private readonly Func<long> _nowMs;
    private long _lastPassedMs;
    private long _armedWindowMs;
    private bool _hasPassed;

    public HapticTickLimiter(long minIntervalMs = DefaultMinIntervalMs, Func<long>? nowMs = null)
    {
        MinIntervalMs = minIntervalMs;
        _armedWindowMs = minIntervalMs;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
    }

    public long MinIntervalMs { get; }

    /// <summary>True when this command-level call may fire; false when it falls inside the
    /// window armed by the previous passed call (the caller drops it silently). A passing
    /// call re-arms the default window.</summary>
    public bool TryPass()
    {
        var now = _nowMs();
        if (_hasPassed && now - _lastPassedMs < _armedWindowMs) return false;
        Pass(now, MinIntervalMs);
        return true;
    }

    /// <summary>True when this press-down (gesture-recognition) call may fire. Checked only
    /// against the SHORT default window — a press is a new gesture, so the long refractory
    /// armed by a previous press never silences it — and on passing it arms
    /// <paramref name="refractoryMs"/> so the same gesture's command haptic coalesces away.</summary>
    public bool TryPassPress(long refractoryMs = PressRefractoryMs)
    {
        var now = _nowMs();
        if (_hasPassed && now - _lastPassedMs < MinIntervalMs) return false;
        Pass(now, refractoryMs);
        return true;
    }

    private void Pass(long now, long armWindowMs)
    {
        _hasPassed = true;
        _lastPassedMs = now;
        _armedWindowMs = armWindowMs;
    }
}
