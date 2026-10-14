using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace StoryTime.App.Controls;

/// <summary>The woven-thread progress bar inside the expanded weave sheet: a hairline
/// track with faint stage tick marks, an amber fill with a soft glow head, and a slow
/// low-contrast shimmer drifting along the FILLED portion only (a whisper, not a spinner).
///
/// Battery care: one time-driven loop that always yields (Task.Delay every frame — the
/// animation-loop ANR trap from memory/gotchas.md cannot occur), ~30 fps, and it runs only
/// while <see cref="Animate"/> is true — the host gates that on sheet-expanded + weaving +
/// page visible + app foregrounded. When idle the bar is a single static draw.</summary>
public sealed class WeaveProgressBar : SKCanvasView
{
    private const double ShimmerPeriodMs = 2800;   // slow drift, spec: 2.5–3 s
    private const float BarHeightDp = 26;

    public static readonly BindableProperty ProgressProperty = BindableProperty.Create(
        nameof(Progress), typeof(double), typeof(WeaveProgressBar), 0.0,
        propertyChanged: (b, _, _) => ((WeaveProgressBar)b).OnProgressChanged());

    /// <summary>Target fill 0..1; the drawn fill eases toward it so stage jumps glide.</summary>
    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    /// <summary>Stage-boundary fractions (0..1) drawn as faint ticks on the track.</summary>
    public IReadOnlyList<double> Ticks { get; set; } = [];

    private bool _animate;
    /// <summary>Gate for the shimmer/eased-fill loop. Set false whenever the bar is not
    /// actually on screen weaving — the loop stops and the bar freezes at a static frame.</summary>
    public bool Animate
    {
        get => _animate;
        set
        {
            if (_animate == value) return;
            _animate = value;
            if (_animate) StartLoop();
            else InvalidateSurface();   // final static frame (also snaps eased fill)
        }
    }

    private double _displayProgress;
    private bool _looping;

    public WeaveProgressBar()
    {
        HeightRequest = BarHeightDp;
        IgnorePixelScaling = false;
        PaintSurface += OnPaintSurface;
    }

    private void OnProgressChanged()
    {
        if (!_animate)
        {
            _displayProgress = Progress;   // no loop running — snap and draw once
            InvalidateSurface();
        }
    }

    private void StartLoop()
    {
        if (_looping) return;
        _looping = true;
        _ = LoopAsync();
    }

    private async Task LoopAsync()
    {
        try
        {
            while (_animate && Handler is not null)
            {
                // Ease the drawn fill toward the reported percent (glides over stage jumps).
                var target = Progress;
                _displayProgress += (target - _displayProgress) * 0.14;
                if (Math.Abs(target - _displayProgress) < 0.0015) _displayProgress = target;

                InvalidateSurface();
                await Task.Delay(33);   // unconditional yield every frame — never a tight loop
            }
        }
        finally
        {
            _looping = false;
            _displayProgress = Progress;
        }
    }

    private static SKColor Token(string key)
    {
        var c = (Color)Application.Current!.Resources[key];
        return new SKColor((byte)(c.Red * 255), (byte)(c.Green * 255), (byte)(c.Blue * 255));
    }

    private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        var w = e.Info.Width;
        var h = e.Info.Height;
        if (w <= 0 || h <= 0) return;
        var scale = Width > 0 ? (float)(w / Width) : 3f;
        var midY = h / 2f;
        var inset = 2f * scale;                    // keep the glow head inside the canvas
        var trackW = w - inset * 2;

        var hairline = Token("Hairline");
        var border = Token("Border");
        var accent = Token("Accent");
        var accentHover = Token("AccentHover");

        // Track — hairline.
        using var trackPaint = new SKPaint
        {
            Color = hairline, StrokeWidth = 1.2f * scale, IsAntialias = true,
            StrokeCap = SKStrokeCap.Round, Style = SKPaintStyle.Stroke,
        };
        canvas.DrawLine(inset, midY, inset + trackW, midY, trackPaint);

        // Faint stage ticks at the pipeline's stage-weight boundaries.
        using var tickPaint = new SKPaint
        {
            Color = border.WithAlpha(150), StrokeWidth = 1f * scale, IsAntialias = true,
        };
        var tickHalf = 3.2f * scale;
        foreach (var t in Ticks)
        {
            var x = inset + (float)(t * trackW);
            canvas.DrawLine(x, midY - tickHalf, x, midY + tickHalf, tickPaint);
        }

        var fillW = (float)(Math.Clamp(_displayProgress, 0, 1) * trackW);
        if (fillW <= 0.5f) return;
        var headX = inset + fillW;

        // Amber fill.
        using var fillPaint = new SKPaint
        {
            Color = accent, StrokeWidth = 2.4f * scale, IsAntialias = true,
            StrokeCap = SKStrokeCap.Round, Style = SKPaintStyle.Stroke,
        };
        canvas.DrawLine(inset, midY, headX, midY, fillPaint);

        // Shimmer — drifts along the filled portion only, low contrast.
        if (_animate && fillW > 8f * scale)
        {
            var band = 46f * scale;
            var phase = (Environment.TickCount64 % (long)ShimmerPeriodMs) / ShimmerPeriodMs;
            var cx = inset - band + (float)(phase * (fillW + band * 2));
            using var shimmer = new SKPaint
            {
                StrokeWidth = 2.4f * scale, IsAntialias = true, StrokeCap = SKStrokeCap.Round,
                Style = SKPaintStyle.Stroke,
                Shader = SKShader.CreateLinearGradient(
                    new SKPoint(cx - band / 2, midY), new SKPoint(cx + band / 2, midY),
                    [accentHover.WithAlpha(0), accentHover.WithAlpha(120), accentHover.WithAlpha(0)],
                    [0f, 0.5f, 1f], SKShaderTileMode.Clamp),
            };
            canvas.Save();
            canvas.ClipRect(new SKRect(inset, 0, headX, h));
            canvas.DrawLine(inset, midY, headX, midY, shimmer);
            canvas.Restore();
        }

        // Soft glow head — a small radial halo + a brighter leading dot.
        using var glow = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateRadialGradient(
                new SKPoint(headX, midY), 9f * scale,
                [accent.WithAlpha(110), accent.WithAlpha(0)], [0f, 1f], SKShaderTileMode.Clamp),
        };
        canvas.DrawCircle(headX, midY, 9f * scale, glow);
        using var headDot = new SKPaint { Color = accentHover, IsAntialias = true };
        canvas.DrawCircle(headX, midY, 2.6f * scale, headDot);
    }
}
