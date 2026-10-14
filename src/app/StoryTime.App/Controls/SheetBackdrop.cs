namespace StoryTime.App.Controls;

/// <summary>The backdrop behind an expanded bottom sheet (founder feedback 2026-10-14): a
/// black dimming scrim over a subtle blur of the page behind it. Fade it with
/// <see cref="VisualElement.Opacity"/> 0 ↔ 1 in step with the sheet's own motion — the dim
/// level and the blur strength are properties of the backdrop, not of the fade.
///
/// Rendering is the iOS handler's (Platforms/iOS/SheetBackdropHandler): a
/// <c>UIVisualEffectView</c> held at a fraction of a full blur, under a black layer at
/// <see cref="DimOpacity"/>. With Reduce Transparency on, the blur is dropped and the dim
/// deepens instead. The backdrop is never an accessibility element: the sheet traps
/// VoiceOver focus (<see cref="SheetAccessibility"/>), and the backdrop's own tap is the
/// visual-only twin of the sheet's labelled close control.</summary>
public sealed class SheetBackdrop : View
{
    /// <summary>Black scrim opacity at full fade-in (0.55–0.6 per the founder brief).</summary>
    public static readonly BindableProperty DimOpacityProperty = BindableProperty.Create(
        nameof(DimOpacity), typeof(double), typeof(SheetBackdrop), 0.58);

    public double DimOpacity
    {
        get => (double)GetValue(DimOpacityProperty);
        set => SetValue(DimOpacityProperty, value);
    }

    /// <summary>Fraction of a full system blur (0 = none, 1 = the full dark blur).</summary>
    public static readonly BindableProperty BlurAmountProperty = BindableProperty.Create(
        nameof(BlurAmount), typeof(double), typeof(SheetBackdrop), 0.22);

    public double BlurAmount
    {
        get => (double)GetValue(BlurAmountProperty);
        set => SetValue(BlurAmountProperty, value);
    }

    public SheetBackdrop()
    {
        Opacity = 0;
        IsVisible = false;
        AutomationProperties.SetIsInAccessibleTree(this, false);
        AutomationProperties.SetExcludedWithChildren(this, true);
    }
}

/// <summary>Focus trapping for an expanded sheet. The host is the page-root sibling that holds
/// backdrop + sheet; while modal, VoiceOver ignores everything beside it (the page, the
/// docked mini-bar) and focus moves into the sheet.</summary>
public static partial class SheetAccessibility
{
    /// <summary>Marks <paramref name="host"/> modal (or not) for assistive technology and,
    /// when it becomes modal, moves focus to <paramref name="focus"/>.</summary>
    public static partial void SetModal(VisualElement host, bool modal, VisualElement? focus);
}
