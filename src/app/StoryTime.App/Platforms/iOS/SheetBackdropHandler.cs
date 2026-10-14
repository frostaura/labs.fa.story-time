using Foundation;
using Microsoft.Maui.Handlers;
using StoryTime.App.Controls;
using UIKit;

namespace StoryTime.App.Platforms.iOS;

/// <summary>iOS rendering of <see cref="SheetBackdrop"/>: a partial blur under a black dim.
/// UIKit has no "blur radius" API, so the blur is a <c>UIViewPropertyAnimator</c> that
/// animates a <c>UIVisualEffectView</c>'s effect from none to a dark blur and is paused at
/// <see cref="SheetBackdrop.BlurAmount"/>. The animator is rebuilt whenever the view joins a
/// window or the app returns to the foreground (a paused animator does not survive either
/// reliably) and is always stopped before release. Cost: one GPU blur pass, only while the
/// backdrop is visible; the effect view is removed from the hierarchy when hidden.</summary>
public sealed class SheetBackdropHandler : ViewHandler<SheetBackdrop, SheetBackdropView>
{
    public static readonly IPropertyMapper<SheetBackdrop, SheetBackdropHandler> PropertyMapper =
        new PropertyMapper<SheetBackdrop, SheetBackdropHandler>(ViewMapper)
        {
            [nameof(SheetBackdrop.DimOpacity)] = (h, v) => h.PlatformView.Configure(v.DimOpacity, v.BlurAmount),
            [nameof(SheetBackdrop.BlurAmount)] = (h, v) => h.PlatformView.Configure(v.DimOpacity, v.BlurAmount),
            [nameof(IView.Visibility)] = MapVisibility,
        };

    public SheetBackdropHandler() : base(PropertyMapper)
    {
    }

    protected override SheetBackdropView CreatePlatformView() => new();

    protected override void ConnectHandler(SheetBackdropView platformView)
    {
        base.ConnectHandler(platformView);
        platformView.Configure(VirtualView.DimOpacity, VirtualView.BlurAmount);
    }

    protected override void DisconnectHandler(SheetBackdropView platformView)
    {
        platformView.Teardown();
        base.DisconnectHandler(platformView);
    }

    private static void MapVisibility(SheetBackdropHandler handler, SheetBackdrop view)
    {
        ViewMapper.UpdateProperty(handler, view, nameof(IView.Visibility));
        handler.PlatformView.SetActive(view.IsVisible);
    }
}

/// <summary>The native backdrop view. Never an accessibility element.</summary>
public sealed class SheetBackdropView : UIView
{
    private const double ReducedTransparencyDim = 0.72;

    private readonly UIVisualEffectView _blur = new() { UserInteractionEnabled = false };
    private readonly UIView _dim = new() { BackgroundColor = UIColor.Black, UserInteractionEnabled = false };
    private UIViewPropertyAnimator? _animator;
    private NSObject? _foregroundObserver;
    private NSObject? _transparencyObserver;
    private double _dimOpacity = 0.58;
    private double _blurAmount = 0.22;
    private bool _active;

    public SheetBackdropView()
    {
        BackgroundColor = UIColor.Clear;
        IsAccessibilityElement = false;
        AccessibilityElementsHidden = true;
        _blur.Frame = Bounds;
        _blur.AutoresizingMask = UIViewAutoresizing.FlexibleDimensions;
        _dim.Frame = Bounds;
        _dim.AutoresizingMask = UIViewAutoresizing.FlexibleDimensions;
        AddSubview(_dim);

        _foregroundObserver = NSNotificationCenter.DefaultCenter.AddObserver(
            UIApplication.WillEnterForegroundNotification, _ => Rebuild());
        _transparencyObserver = NSNotificationCenter.DefaultCenter.AddObserver(
            UIView.ReduceTransparencyStatusDidChangeNotification, _ => Rebuild());
    }

    public void Configure(double dimOpacity, double blurAmount)
    {
        _dimOpacity = Math.Clamp(dimOpacity, 0, 1);
        _blurAmount = Math.Clamp(blurAmount, 0, 1);
        Rebuild();
    }

    /// <summary>Only a visible backdrop keeps a blur in the hierarchy.</summary>
    public void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        Rebuild();
    }

    public override void MovedToWindow()
    {
        base.MovedToWindow();
        Rebuild();
    }

    private void Rebuild()
    {
        StopAnimator();
        var reduce = UIAccessibility.IsReduceTransparencyEnabled;
        _dim.Alpha = (nfloat)(reduce ? Math.Max(_dimOpacity, ReducedTransparencyDim) : _dimOpacity);

        var wantBlur = _active && Window is not null && !reduce && _blurAmount > 0;
        if (!wantBlur)
        {
            _blur.RemoveFromSuperview();
            _blur.Effect = null;
            return;
        }

        if (_blur.Superview is null) InsertSubview(_blur, 0);
        _blur.Effect = null;
        _animator = new UIViewPropertyAnimator(1, UIViewAnimationCurve.Linear,
            () => _blur.Effect = UIBlurEffect.FromStyle(UIBlurEffectStyle.Dark));
        _animator.FractionComplete = (nfloat)_blurAmount;   // pauses the animator at that fraction
    }

    private void StopAnimator()
    {
        if (_animator is null) return;
        // A paused animator must be stopped before release; stopping "without finishing"
        // leaves it inactive and the effect wherever it was, which Rebuild then resets.
        if (_animator.State == UIViewAnimatingState.Active) _animator.StopAnimation(true);
        _animator.Dispose();
        _animator = null;
    }

    public void Teardown()
    {
        StopAnimator();
        _blur.RemoveFromSuperview();
        if (_foregroundObserver is not null) NSNotificationCenter.DefaultCenter.RemoveObserver(_foregroundObserver);
        if (_transparencyObserver is not null) NSNotificationCenter.DefaultCenter.RemoveObserver(_transparencyObserver);
        _foregroundObserver = null;
        _transparencyObserver = null;
    }
}
