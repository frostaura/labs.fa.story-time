using CommunityToolkit.Maui.Behaviors;
using CommunityToolkit.Maui.Core;
using Microsoft.Maui.Controls.Shapes;
using StoryTime.App.Services;

namespace StoryTime.App.Controls;

/// <summary>Hearth 44×26 switch: amber when on, hairline capsule when off, sliding knob.
/// (Per-instance shapes — style-shared Shape instances crash Android.)</summary>
public sealed class ToggleSwitchView : ContentView
{
    public static readonly BindableProperty IsOnProperty = BindableProperty.Create(
        nameof(IsOn), typeof(bool), typeof(ToggleSwitchView), false, BindingMode.TwoWay,
        propertyChanged: (bindable, _, _) => ((ToggleSwitchView)bindable).Render(animate: true));

    public bool IsOn
    {
        get => (bool)GetValue(IsOnProperty);
        set => SetValue(IsOnProperty, value);
    }

    public event EventHandler? Toggled;

    private readonly Border _capsule;
    private readonly Ellipse _knob;

    private static Color TokenColor(string key) => (Color)Application.Current!.Resources[key];

    public ToggleSwitchView()
    {
        // The capsule stays the mockup's 44×26; the CONTROL is 64×44 of transparent
        // touch area around it (founder affordance pass 2026-07-31 — a 26dp-tall tap
        // target is not a target). Nothing visual changes.
        WidthRequest = 64;
        HeightRequest = 44;
        // Explicit Transparent (not null) — an unpainted ContentViewGroup swallows
        // nothing on Android, so the padding around the capsule would be dead.
        BackgroundColor = Colors.Transparent;

        _knob = new Ellipse
        {
            WidthRequest = 20,
            HeightRequest = 20,
            Margin = new Thickness(3, 0),
            HorizontalOptions = LayoutOptions.Start,
            VerticalOptions = LayoutOptions.Center,
            Fill = new SolidColorBrush(TokenColor("Cream")),
        };

        _capsule = new Border
        {
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(13) },
            Content = new Grid { Children = { _knob } },
            Padding = 0,
            WidthRequest = 44,
            HeightRequest = 26,
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.Center,
        };

        Content = _capsule;

        // TouchBehavior (not a bare tap): Selection at PRESS-DOWN (anticipatory rule) —
        // the VM's change-committed Selection then coalesces away — and the toggle flips
        // on touch-completed, exactly where the tap used to fire.
        var touch = new TouchBehavior
        {
            Command = new Command(() =>
            {
                if (!IsEnabled) return;
                IsOn = !IsOn;
                Toggled?.Invoke(this, EventArgs.Empty);
            }),
        };
        touch.CurrentTouchStatusChanged += (_, e) =>
        {
            if (e.Status == TouchStatus.Started && IsEnabled) PressHaptics.Selection();
        };
        Behaviors.Add(touch);

        Loaded += (_, _) => Render(animate: false);
    }

    private void Render(bool animate)
    {
        var on = IsOn;
        _capsule.BackgroundColor = on ? TokenColor("Accent") : Colors.Transparent;
        _capsule.Stroke = new SolidColorBrush(on ? TokenColor("Accent") : TokenColor("Hairline"));
        _knob.Fill = new SolidColorBrush(on ? TokenColor("InkOnAccent") : TokenColor("Dim"));

        var targetX = on ? 18.0 : 0.0;
        if (animate)
            _knob.TranslateTo(targetX, 0, 160, Easing.CubicOut);
        else
            _knob.TranslationX = targetX;
    }
}
