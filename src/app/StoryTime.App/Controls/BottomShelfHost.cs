using System.Windows.Input;
using CommunityToolkit.Maui.Behaviors;
using CommunityToolkit.Maui.Core;
using Microsoft.Maui.Controls.Shapes;
using StoryTime.App.Services;
using StoryTime.Core.Abstractions;

namespace StoryTime.App.Controls;

/// <summary>Lightweight reusable slide-up shelf: scrim + drag handle + content-sized
/// bottom sheet, in exactly the making sheet's visual language (BgPhone panel, Hairline
/// stroke, 20-radius top corners, ~55% BgPhone scrim, ≤300 ms eased motion, drag-down ≥110dp
/// or scrim/handle tap to dismiss — the MakingSheetHost pattern, extracted for shelves that
/// are content-sized rather than near-full-height and driven by a simple IsOpen flag).
///
/// Layering: place this as the LAST child of a page's root Grid — above MakingSheetHost —
/// so an open shelf slides up OVER the docked making mini-bar (the bar dims under the scrim
/// and is inert while the shelf is open; it is exactly where it was on dismiss).
///
/// Dismissal goes through <see cref="CloseCommand"/> when one is bound (so the owning VM
/// keeps a single close path — haptics included); otherwise IsOpen is written back false.</summary>
[ContentProperty(nameof(ShelfContent))]
public sealed class BottomShelfHost : Grid
{
    private const double DismissDragThreshold = 110;   // dp of drag-down that commits a dismiss (making sheet value)

    public static readonly BindableProperty IsOpenProperty = BindableProperty.Create(
        nameof(IsOpen), typeof(bool), typeof(BottomShelfHost), false, BindingMode.TwoWay,
        propertyChanged: (bindable, _, _) => ((BottomShelfHost)bindable).OnIsOpenChanged());

    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public static readonly BindableProperty CloseCommandProperty = BindableProperty.Create(
        nameof(CloseCommand), typeof(ICommand), typeof(BottomShelfHost));

    /// <summary>Single close path for scrim tap, handle tap and drag-down dismissal.</summary>
    public ICommand? CloseCommand
    {
        get => (ICommand?)GetValue(CloseCommandProperty);
        set => SetValue(CloseCommandProperty, value);
    }

    public static readonly BindableProperty ShelfContentProperty = BindableProperty.Create(
        nameof(ShelfContent), typeof(View), typeof(BottomShelfHost),
        propertyChanged: (bindable, _, newValue) => ((BottomShelfHost)bindable).OnContentChanged((View?)newValue));

    public View? ShelfContent
    {
        get => (View?)GetValue(ShelfContentProperty);
        set => SetValue(ShelfContentProperty, value);
    }

    private readonly Grid _scrim;
    private readonly Border _sheet;
    private readonly Grid _sheetGrid;

    private int _transition;      // bumps on every open/close; stale animations bail out
    private double _lastDragY;    // last Running pan position (Completed reports zeros — gotcha)
    private bool _dragArmed;      // dismiss-threshold arming Selection fired for this gesture

    private IHaptics? _haptics;
    private IHaptics? Haptics => _haptics ??= IPlatformApplication.Current?.Services.GetService<IHaptics>();

    private static Color Token(string key) => (Color)Application.Current!.Resources[key];

    public BottomShelfHost()
    {
        InputTransparent = true;             // the host grid never blocks the page…
        CascadeInputTransparent = false;     // …only its visible children catch touches.

        // A Grid, not a BoxView: the app's implicit BoxView style is the 1dp hairline
        // divider (HeightRequest=1), which silently collapses a code-created scrim to an
        // untappable line. A Grid has no implicit style and fills the host.
        _scrim = new Grid { BackgroundColor = Token("BgPhone"), Opacity = 0, IsVisible = false };
        var scrimTap = new TapGestureRecognizer();
        scrimTap.Tapped += (_, _) => RequestClose();
        _scrim.GestureRecognizers.Add(scrimTap);
        // Light at pointer-down, before the dismiss animation (anticipatory rule).
        _scrim.Behaviors.Add(PressHaptics.Touch(_scrim, PressHapticKind.Light, listenOnly: true));

        // Grab zone: drag handle (making sheet/Player pattern) — tap or drag-down dismisses.
        var grabZone = new Grid { HeightRequest = 34, BackgroundColor = Colors.Transparent };
        grabZone.Children.Add(new BoxView
        {
            WidthRequest = 44,
            HeightRequest = 4,
            CornerRadius = 2,
            Color = Token("Border"),
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
        });
        var handleTap = new TapGestureRecognizer();
        handleTap.Tapped += (_, _) => RequestClose();
        grabZone.GestureRecognizers.Add(handleTap);
        var pan = new PanGestureRecognizer();
        pan.PanUpdated += OnGrabPan;
        grabZone.GestureRecognizers.Add(pan);
        // Grip at DRAG-START = first touch, not first movement: the pan's Started only
        // arrives after the slop threshold (making sheet / LengthTrackView pattern).
        var grip = new TouchBehavior();
        grip.CurrentTouchStatusChanged += (_, e) =>
        {
            if (e.Status == TouchStatus.Started) Haptics?.Tick();
        };
        grabZone.Behaviors.Add(grip);

        _sheetGrid = new Grid
        {
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto) },
            Padding = new Thickness(32, 10, 32, 24),
        };
        _sheetGrid.Add(grabZone, 0, 0);

        _sheet = new Border
        {
            IsVisible = false,
            BackgroundColor = Token("BgPhone"),
            Stroke = new SolidColorBrush(Token("Hairline")),
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(20, 20, 0, 0) },
            VerticalOptions = LayoutOptions.End,
            Content = _sheetGrid,
        };

        Children.Add(_scrim);
        Children.Add(_sheet);

        Loaded += (_, _) => ApplySafeAreaPadding();
    }

    private void OnContentChanged(View? content)
    {
        // Row 1 holds the provided content; the grab zone stays row 0.
        var stale = _sheetGrid.Children.OfType<View>().FirstOrDefault(v => Grid.GetRow(v) == 1);
        if (stale is not null) _sheetGrid.Children.Remove(stale);
        if (content is not null) _sheetGrid.Add(content, 0, 1);
    }

    /// <summary>iOS pages bleed edge-to-edge; pad the sheet's content above the home
    /// indicator (same treatment as the making mini-bar).</summary>
    private void ApplySafeAreaPadding()
    {
        for (Element? p = Parent; p is not null; p = p.Parent)
        {
            if (p is ContentPage page)
            {
                var inset = Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific.Page
                    .SafeAreaInsets(page.On<Microsoft.Maui.Controls.PlatformConfiguration.iOS>());
                _sheetGrid.Padding = new Thickness(32, 10, 32, 24 + inset.Bottom);
                break;
            }
        }
    }

    private double OffscreenY => _sheet.Height > 0 ? _sheet.Height : (Height > 0 ? Height : 2000);

    private void RequestClose()
    {
        if (!IsOpen) return;
        if (CloseCommand is { } command && command.CanExecute(null)) command.Execute(null);
        else IsOpen = false;
    }

    private void OnIsOpenChanged() => _ = AnimateAsync();

    private async Task AnimateAsync()
    {
        var token = ++_transition;
        Microsoft.Maui.Controls.ViewExtensions.CancelAnimations(_sheet);
        Microsoft.Maui.Controls.ViewExtensions.CancelAnimations(_scrim);
        if (IsOpen)
        {
            _scrim.IsVisible = true;
            _sheet.IsVisible = true;
            // First open: the sheet has never measured — give layout a beat, then slide
            // from its own height (content-sized, unlike the near-full making sheet).
            if (_sheet.Height <= 0)
            {
                _sheet.TranslationY = OffscreenY;
                await Task.Delay(16);
                if (token != _transition) return;
            }
            if (_sheet.TranslationY <= 0) _sheet.TranslationY = OffscreenY;
            _ = _scrim.FadeToAsync(0.55, 240, Easing.CubicOut);
            await _sheet.TranslateToAsync(0, 0, 290, Easing.CubicOut);
        }
        else
        {
            if (!_sheet.IsVisible) return;
            _ = _scrim.FadeToAsync(0, 220, Easing.CubicOut);
            await _sheet.TranslateToAsync(0, OffscreenY, 260, Easing.CubicIn);
            if (token != _transition) return;
            _sheet.IsVisible = false;
            _scrim.IsVisible = false;
        }
    }

    private void OnGrabPan(object? sender, PanUpdatedEventArgs e)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _lastDragY = 0;
                _dragArmed = false;
                break;
            case GestureStatus.Running:
                _lastDragY = Math.Max(0, e.TotalY);
                _sheet.TranslationY = _lastDragY;
                // Arming feedback, once per gesture (making sheet pattern).
                if (!_dragArmed && _lastDragY > DismissDragThreshold)
                {
                    _dragArmed = true;
                    Haptics?.Selection();
                }
                break;
            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                if (_lastDragY > DismissDragThreshold)
                {
                    RequestClose();   // AnimateAsync finishes the slide from here
                }
                else
                {
                    _ = _sheet.TranslateToAsync(0, 0, 200, Easing.CubicOut);   // settle back, no bounce
                }
                _lastDragY = 0;
                break;
        }
    }
}
