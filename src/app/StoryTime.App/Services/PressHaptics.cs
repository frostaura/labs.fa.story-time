using CommunityToolkit.Maui.Behaviors;
using CommunityToolkit.Maui.Core;
using StoryTime.Core.Abstractions;

namespace StoryTime.App.Services;

/// <summary>Which press-down haptic a control fires the instant the finger lands.</summary>
public enum PressHapticKind
{
    None,
    /// <summary>Default for buttons, pills, rows, tiles — anything that opens or navigates.</summary>
    Light,
    /// <summary>The big actions: the weave CTA, the final onboarding CTA.</summary>
    Medium,
    /// <summary>Pickers and toggles — chips, radio rows, switches.</summary>
    Selection,
}

/// <summary>The anticipatory-haptics rule (docs/design/mockup-spec.md): a haptic fires at
/// GESTURE RECOGNITION — pointer-down for taps, threshold-crossing for holds and drags —
/// always BEFORE any await, navigation, animation, menu population or data load, so the
/// feedback masks whatever latency follows. The later command-level haptic then coalesces
/// away inside PlatformHaptics (press-down arms the press refractory).
///
/// Two ways in:
/// <para>• XAML, app-wide: the attached <see cref="OnPressedProperty"/> is set by the shared
/// Button styles (Styles.xaml) so every Button/pill/CTA fires Light on the platform Pressed
/// event (pointer-down) with no per-page markup. Per-control override:
/// <c>services:PressHaptics.OnPressed="Medium"</c>. Button.Pressed is never raised for a
/// disabled button, so IsEnabled is respected by construction.</para>
/// <para>• Code / TouchBehavior sites: call <see cref="Light"/> / <see cref="Medium"/> /
/// <see cref="Selection"/> from a TouchStatus.Started handler (rows, tiles, cards, grips).</para>
///
/// Resolves IHaptics lazily from the app service provider (the service-locator pattern the
/// Controls already use); the press variants exist on PlatformHaptics (concrete) because
/// IHaptics is a frozen Core seam.</summary>
public static class PressHaptics
{
    private static IHaptics? _haptics;

    private static IHaptics? Resolve() => _haptics ??= IPlatformApplication.Current?.Services.GetService<IHaptics>();

    public static void Light()
    {
        if (Resolve() is PlatformHaptics platform) platform.PressLight();
        else Resolve()?.Light();
    }

    public static void Medium()
    {
        if (Resolve() is PlatformHaptics platform) platform.PressMedium();
        else Resolve()?.Medium();
    }

    public static void Selection()
    {
        if (Resolve() is PlatformHaptics platform) platform.PressSelection();
        else Resolve()?.Selection();
    }

    public static void Fire(PressHapticKind kind)
    {
        switch (kind)
        {
            case PressHapticKind.Light: Light(); break;
            case PressHapticKind.Medium: Medium(); break;
            case PressHapticKind.Selection: Selection(); break;
        }
    }

    // ---- code-built touch sites --------------------------------------------------------

    /// <summary>A TouchBehavior for controls built in C# rather than XAML (the onboarding
    /// pickers, the voice rows): fires <paramref name="kind"/> at POINTER-DOWN and runs
    /// <paramref name="onTapped"/> when the touch completes — exactly where the replaced
    /// TapGestureRecognizer used to fire. Both halves are gated on
    /// <paramref name="owner"/>.IsEnabled, so a disabled control never answers the finger.
    ///
    /// <para><paramref name="listenOnly"/> keeps the existing recognizers in charge of the
    /// command (used where a pan or swipe must survive) and only adds the press haptic.</para></summary>
    public static TouchBehavior Touch(VisualElement owner, PressHapticKind kind,
                                      Action? onTapped = null, bool listenOnly = false)
    {
        var behavior = new TouchBehavior();
        if (!listenOnly && onTapped is not null)
        {
            behavior.Command = new Command(() => { if (owner.IsEnabled) onTapped(); });
        }
        behavior.CurrentTouchStatusChanged += (_, e) =>
        {
            if (e.Status == TouchStatus.Started && owner.IsEnabled) Fire(kind);
        };
        return behavior;
    }

    // ---- attached property: Button press-down haptics via the shared styles -----------

    public static readonly BindableProperty OnPressedProperty = BindableProperty.CreateAttached(
        "OnPressed", typeof(PressHapticKind), typeof(PressHaptics), PressHapticKind.None,
        propertyChanged: OnOnPressedChanged);

    public static PressHapticKind GetOnPressed(BindableObject bindable) =>
        (PressHapticKind)bindable.GetValue(OnPressedProperty);

    public static void SetOnPressed(BindableObject bindable, PressHapticKind value) =>
        bindable.SetValue(OnPressedProperty, value);

    private static void OnOnPressedChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is not Button button) return;
        button.Pressed -= OnButtonPressed;   // idempotent when a style re-applies
        if ((PressHapticKind)newValue != PressHapticKind.None) button.Pressed += OnButtonPressed;
    }

    private static void OnButtonPressed(object? sender, EventArgs e)
    {
        // Pressed is not raised for disabled buttons, but belt-and-braces: a disabled
        // control must never answer the finger.
        if (sender is not Button button || !button.IsEnabled) return;
        Fire(GetOnPressed(button));
    }
}
