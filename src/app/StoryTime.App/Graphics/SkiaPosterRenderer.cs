using SkiaSharp;
using StoryTime.Contracts;
using StoryTime.Core.Abstractions;

namespace StoryTime.App.Graphics;

/// <summary>Rasterizes resolved poster layers with SkiaSharp — flat night-illustration
/// craft (Alto's-Odyssey-style silhouettes): gradient skies, soft radial glows, cubic-curve
/// silhouettes, seeded scatters with size/alpha jitter, and low-alpha water reflections.
/// Each layer renders ONCE; parallax is translation of the prerendered images, never
/// per-frame Skia, so all the glow/gradient cost is paid one time.
///
/// Anchor conventions (the composer guarantees zones): celestial/atmo shapes center on
/// (x,y); Hills/Mountain/ForestRow crest at y and fill to the bottom edge; Water's
/// waterline is y; every grounded motif (flora, structures, creatures) anchors its BASE
/// at y so it visibly sits on a ground line and can never float.</summary>
public static class SkiaPosterRenderer
{
    /// <summary>Renders one layer to an image. Depth-0 carries the sky gradient; other
    /// layers are transparent except their elements.
    ///
    /// The two flags exist for the parallax view, which splits depth 0 in half: the sky
    /// GRADIENT is rendered alone as a fixed backdrop (a smooth ramp looks identical whether
    /// it translates or not, and holding it still guarantees no edge can ever be revealed),
    /// while the stars and moon on that same layer are rendered alone and DO travel — which
    /// is what actually makes a night sky read as deep.</summary>
    public static SKImage RenderLayer(ResolvedPosterLayer layer, int widthPx, int heightPx,
        bool includeBackground = true, bool includeElements = true)
    {
        var info = new SKImageInfo(widthPx, heightPx, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        if (includeBackground) DrawLayerBackground(canvas, layer, widthPx, heightPx);

        if (includeElements)
            foreach (var element in layer.Elements)
                DrawElement(canvas, element, widthPx, heightPx);

        return surface.Snapshot();
    }

    /// <summary>Flattens all four layers into one square image (thumbnails).</summary>
    public static SKImage RenderFlattened(ResolvedPoster poster, int sizePx)
    {
        var info = new SKImageInfo(sizePx, sizePx, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColor.Parse(poster.BaseHex));

        foreach (var layer in poster.Layers.OrderBy(l => l.Depth))
        {
            DrawLayerBackground(canvas, layer, sizePx, sizePx);
            foreach (var element in layer.Elements)
                DrawElement(canvas, element, sizePx, sizePx);
        }
        DrawVignette(canvas, sizePx, sizePx);
        return surface.Snapshot();
    }

    /// <summary>Renders ONLY the corner vignette to a transparent image — the parallax view
    /// stacks it as a fixed (non-translating) top layer so the grounding shadow never slides
    /// with the art. Flattened thumbnails get the same vignette baked in.</summary>
    public static SKImage RenderVignetteOverlay(int widthPx, int heightPx)
    {
        var info = new SKImageInfo(widthPx, heightPx, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        surface.Canvas.Clear(SKColors.Transparent);
        DrawVignette(surface.Canvas, widthPx, heightPx);
        return surface.Snapshot();
    }

    /// <summary>Gentle grounding vignette: corners darken to ~6% alpha, the center stays
    /// untouched. Radial falloff starts well outside the focal zone so it reads as depth,
    /// not as a dirty frame.</summary>
    private static void DrawVignette(SKCanvas canvas, int w, int h)
    {
        var cx = w * 0.5f;
        var cy = h * 0.52f;
        var radius = MathF.Sqrt(cx * cx + cy * cy); // reaches the far corners exactly
        using var vignette = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateRadialGradient(
                new SKPoint(cx, cy), radius,
                [SKColors.Black.WithAlpha(0), SKColors.Black.WithAlpha(0), SKColors.Black.WithAlpha(15)],
                [0f, 0.62f, 1f], SKShaderTileMode.Clamp),
        };
        canvas.DrawRect(new SKRect(0, 0, w, h), vignette);
    }

    /// <summary>Tiny tiled ±1-LSB luminance noise, lazily built once — layered over sky
    /// gradients at very low alpha it breaks up 8-bit gradient banding without ever reading
    /// as film grain (the amplitude is below one perceptual step).</summary>
    private static readonly Lazy<SKBitmap> DitherTile = new(() =>
    {
        const int size = 128;
        var bmp = new SKBitmap(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                // Deterministic 2D hash (Wang-style) — same tile on every platform/run.
                var n = (uint)(x * 374761393 + y * 668265263);
                n = (n ^ (n >> 13)) * 1274126177u;
                n ^= n >> 16;
                // Half the pixels nudge up (white), half nudge down (black), alpha ≈ 1.6%.
                bmp.SetPixel(x, y, (n & 1) == 0
                    ? new SKColor(255, 255, 255, 3)
                    : new SKColor(0, 0, 0, 3));
            }
        }
        bmp.SetImmutable();
        return bmp;
    });

    private static void DrawLayerBackground(SKCanvas canvas, ResolvedPosterLayer layer, int w, int h)
    {
        if (layer.BackgroundTopHex is not null && layer.BackgroundBottomHex is not null)
        {
            using var sky = new SKPaint
            {
                Shader = SKShader.CreateLinearGradient(
                    new SKPoint(0, 0), new SKPoint(0, h),
                    [SKColor.Parse(layer.BackgroundTopHex), SKColor.Parse(layer.BackgroundHex), SKColor.Parse(layer.BackgroundBottomHex)],
                    [0f, 0.55f, 1f], SKShaderTileMode.Clamp),
            };
            canvas.DrawRect(new SKRect(0, 0, w, h), sky);

            // Anti-banding dither over the gradient only (see DitherTile).
            using var dither = new SKPaint
            {
                Shader = DitherTile.Value.ToShader(SKShaderTileMode.Repeat, SKShaderTileMode.Repeat),
            };
            canvas.DrawRect(new SKRect(0, 0, w, h), dither);
        }
        else if (layer.BackgroundHex != "transparent")
        {
            canvas.Clear(SKColor.Parse(layer.BackgroundHex));
        }
    }

    private static void DrawElement(SKCanvas canvas, ResolvedPosterElement element, int w, int h)
    {
        var cx = (float)(element.X * w);
        var cy = (float)(element.Y * h);
        var unit = Math.Min(w, h);
        var s = (float)(element.Scale * unit);
        var alpha = (byte)Math.Clamp(element.Alpha * 255, 0, 255);
        var color = SKColor.Parse(element.ColorHex).WithAlpha(alpha);
        var secondary = element.SecondaryColorHex is null
            ? (SKColor?)null
            : SKColor.Parse(element.SecondaryColorHex).WithAlpha(alpha);
        var glowColor = SKColor.Parse(element.GlowColorHex ?? element.ColorHex);

        var save = canvas.Save();
        if (element.FlipX && element.Shape is not PosterShape.Moon and not PosterShape.Crescent)
        {
            canvas.Scale(-1, 1, cx, cy);
        }

        using var paint = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Fill };

        // Soft contact shadow grounds standing motifs onto their crest line. The offset
        // matches each silhouette's visual footprint (the fox's mass sits left of anchor).
        var (shadowHalf, shadowOffset) = element.Shape switch
        {
            PosterShape.FoxSilhouette => (0.44f, -0.06f),
            PosterShape.Rabbit => (0.36f, -0.03f),
            PosterShape.DeerSilhouette => (0.42f, -0.02f),
            PosterShape.Tent => (0.66f, 0f),
            PosterShape.WindowGlow => (0.58f, 0f),
            PosterShape.RoundTree => (0.30f, 0f),
            PosterShape.Lantern => (0.14f, 0f),
            _ => (0f, 0f),
        };
        if (shadowHalf > 0)
        {
            var sx = cx + s * shadowOffset;
            using var shadow = new SKPaint
            {
                Color = SKColors.Black.WithAlpha((byte)(38 * element.Alpha)),
                IsAntialias = true,
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, MathF.Max(1f, s * 0.035f)),
            };
            canvas.DrawOval(new SKRect(sx - s * shadowHalf, cy - s * 0.02f, sx + s * shadowHalf, cy + s * 0.028f), shadow);
        }

        switch (element.Shape)
        {
            case PosterShape.Moon when element.FlipY:
            case PosterShape.Crescent when element.FlipY:
                DrawMoonglade(canvas, cx, cy, s, color, element.Glow, h);
                break;

            case PosterShape.Moon:
                if (element.Glow > 0) DrawGlow(canvas, cx, cy, s * (1.1f + (float)element.Glow * 1.3f), glowColor, element.Glow * 0.55);
                canvas.DrawCircle(cx, cy, s * 0.5f, paint);
                break;

            case PosterShape.Crescent:
                if (element.Glow > 0) DrawGlow(canvas, cx, cy, s * (1.0f + (float)element.Glow * 1.2f), glowColor, element.Glow * 0.5);
                DrawCrescent(canvas, cx, cy, s, paint, element.FlipX);
                break;

            case PosterShape.Stars:
                DrawStar(canvas, cx, cy, s, unit, paint);
                break;

            case PosterShape.Snowfall:
                using (var snow = new SKPaint
                {
                    Color = color, IsAntialias = true,
                    MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, Math.Max(0.6f, s * 0.35f)),
                })
                {
                    canvas.DrawCircle(cx, cy, MathF.Max(1.4f, s * 0.55f), snow);
                }
                break;

            case PosterShape.FireflyScatter:
                DrawGlow(canvas, cx, cy, MathF.Max(3.5f, s * 4.5f), glowColor, 0.55 * element.Alpha);
                canvas.DrawCircle(cx, cy, MathF.Max(1.1f, s * 0.6f), paint);
                break;

            case PosterShape.FlowerScatter:
                DrawFlower(canvas, cx, cy, MathF.Max(1.6f, s), paint);
                break;

            case PosterShape.Cloud:
                DrawCloud(canvas, cx, cy, s, paint);
                break;

            case PosterShape.MistBand:
                DrawMistBand(canvas, cx, cy, s, w, unit, color);
                break;

            case PosterShape.Aurora:
                DrawAurora(canvas, cx, cy, s, w, h, color);
                break;

            case PosterShape.ShootingStar:
                DrawShootingStar(canvas, cx, cy, s, color);
                break;

            case PosterShape.Hills:
                DrawHill(canvas, cx, cy, s, w, h, paint);
                break;

            case PosterShape.Mountain:
                DrawMountain(canvas, cx, cy, s, w, h, paint, secondary);
                break;

            case PosterShape.ForestRow:
                DrawForestRow(canvas, cx, cy, s, w, h, paint);
                break;

            case PosterShape.Path:
                DrawPath(canvas, cx, cy, s, w, h, paint);
                break;

            case PosterShape.Water:
                DrawWater(canvas, cx, cy, s, w, h, color, secondary);
                break;

            case PosterShape.Reeds:
                DrawReeds(canvas, cx, cy, s, unit, paint);
                break;

            case PosterShape.Pine:
                DrawPineCluster(canvas, cx, cy, s, element.Density, paint);
                break;

            case PosterShape.RoundTree:
                DrawRoundTree(canvas, cx, cy, s, paint);
                break;

            case PosterShape.FoxSilhouette:
                DrawFox(canvas, cx, cy, s, paint);
                break;

            case PosterShape.Rabbit:
                DrawRabbit(canvas, cx, cy, s, paint);
                break;

            case PosterShape.Owl:
                DrawOwl(canvas, cx, cy, s, paint, secondary);
                break;

            case PosterShape.BirdFlock:
                DrawBirdFlock(canvas, cx, cy, s, paint);
                break;

            case PosterShape.DeerSilhouette:
                DrawDeer(canvas, cx, cy, s, paint);
                break;

            case PosterShape.WhaleTail:
                DrawWhaleTail(canvas, cx, cy, s, paint);
                break;

            case PosterShape.Lighthouse:
                DrawLighthouse(canvas, cx, cy, s, w, paint, secondary, element.Glow, glowColor);
                break;

            case PosterShape.Boat:
                DrawBoat(canvas, cx, cy, s, paint);
                break;

            case PosterShape.Sailboat:
                DrawSailboat(canvas, cx, cy, s, paint, secondary);
                break;

            case PosterShape.WindowGlow:
                DrawCottageWindow(canvas, cx, cy, s, paint, secondary, element.Glow, glowColor);
                break;

            case PosterShape.Lantern:
                DrawLantern(canvas, cx, cy, s, paint, secondary, element.Glow, glowColor);
                break;

            case PosterShape.Tent:
                DrawTent(canvas, cx, cy, s, paint, secondary, element.Glow, glowColor);
                break;
        }

        canvas.RestoreToCount(save);
    }

    /// <summary>Soft radial glow disc — blurred, low alpha, additive feel.</summary>
    private static void DrawGlow(SKCanvas canvas, float cx, float cy, float radius, SKColor color, double strength)
    {
        if (strength <= 0 || radius <= 0) return;
        using var glow = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateRadialGradient(
                new SKPoint(cx, cy), radius,
                [color.WithAlpha((byte)Math.Clamp(strength * 150, 0, 255)), color.WithAlpha(0)],
                null, SKShaderTileMode.Clamp),
        };
        canvas.DrawCircle(cx, cy, radius, glow);
    }

    // ---------------- celestial ----------------

    /// <summary>Dot star; the composer marks a few as bright (larger scale) — those get a
    /// tiny four-point sparkle so the sky reads hand-finished, not uniform.</summary>
    private static void DrawStar(SKCanvas canvas, float cx, float cy, float s, int unit, SKPaint paint)
    {
        var r = MathF.Max(0.8f, MathF.Min(s * 0.10f, unit * 0.009f));
        canvas.DrawCircle(cx, cy, r, paint);
        if (s < unit * 0.055f) return; // only the bright ones sparkle
        var ray = r * 3.2f;
        using var sparkle = new SKPath();
        sparkle.MoveTo(cx, cy - ray);
        sparkle.LineTo(cx + r * 0.5f, cy);
        sparkle.LineTo(cx, cy + ray);
        sparkle.LineTo(cx - r * 0.5f, cy);
        sparkle.Close();
        sparkle.MoveTo(cx - ray, cy);
        sparkle.LineTo(cx, cy - r * 0.5f);
        sparkle.LineTo(cx + ray, cy);
        sparkle.LineTo(cx, cy + r * 0.5f);
        sparkle.Close();
        using var soft = new SKPaint { Color = paint.Color.WithAlpha((byte)(paint.Color.Alpha * 0.7)), IsAntialias = true };
        canvas.DrawPath(sparkle, soft);
    }

    private static void DrawCrescent(SKCanvas canvas, float cx, float cy, float s, SKPaint paint, bool flip)
    {
        var r = s * 0.5f;
        var dir = flip ? -1f : 1f;
        var biteCx = cx + dir * r * 0.42f;
        var biteCy = cy - r * 0.18f;
        var biteR = r * 0.88f;
        using var disc = new SKPath();
        disc.AddCircle(cx, cy, r);
        using var bite = new SKPath();
        bite.AddCircle(biteCx, biteCy, biteR);
        using var crescent = disc.Op(bite, SKPathOp.Difference);
        if (crescent is null) return;
        canvas.DrawPath(crescent, paint);

        // Soft inner shadow hugging the bite edge (the terminator): a narrow radial band
        // just outside the bite circle, clipped to the crescent, fading before it reaches
        // the moonlit outer limb — the crescent reads rounded instead of a flat sticker.
        var save = canvas.Save();
        canvas.ClipPath(crescent, antialias: true);
        var shadeR = biteR * 1.35f;
        var tBite = biteR / shadeR; // ≈0.74 — where the crescent's inner face begins
        using var shade = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateRadialGradient(
                new SKPoint(biteCx, biteCy), shadeR,
                [SKColors.Transparent, SKColors.Black.WithAlpha((byte)(42 * paint.Color.Alpha / 255)), SKColors.Transparent],
                [tBite - 0.02f, tBite + 0.03f, tBite + 0.20f], SKShaderTileMode.Clamp),
        };
        canvas.DrawCircle(biteCx, biteCy, shadeR, shade);
        canvas.RestoreToCount(save);
    }

    /// <summary>Moonglade: the moon's reflection as one continuous soft column of light
    /// narrowing and fading down from the waterline, plus two shimmer flecks — never a
    /// hard mirrored disc, never a stack of visible blobs.</summary>
    private static void DrawMoonglade(SKCanvas canvas, float cx, float cy, float s, SKColor color, double glow, int h)
    {
        var len = MathF.Min(s * 1.35f, h - cy);
        if (len <= 2) return;
        using (var column = new SKPaint
        {
            IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, s * 0.09f),
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(cx, cy), new SKPoint(cx, cy + len),
                [color, color.WithAlpha((byte)(color.Alpha * 0.4)), color.WithAlpha(0)],
                [0f, 0.45f, 1f], SKShaderTileMode.Clamp),
        })
        using (var trapezoid = new SKPath())
        {
            trapezoid.MoveTo(cx - s * 0.40f, cy);
            trapezoid.LineTo(cx + s * 0.40f, cy);
            trapezoid.LineTo(cx + s * 0.10f, cy + len);
            trapezoid.LineTo(cx - s * 0.10f, cy + len);
            trapezoid.Close();
            canvas.DrawPath(trapezoid, column);
        }
        using var fleck = new SKPaint
        {
            Color = color.WithAlpha((byte)(color.Alpha * 0.8)),
            IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, s * 0.02f),
        };
        Pill(canvas, cx - s * 0.10f, cy + len * 0.28f, s * 0.16f, MathF.Max(1f, s * 0.022f), fleck);
        Pill(canvas, cx + s * 0.08f, cy + len * 0.55f, s * 0.11f, MathF.Max(1f, s * 0.018f), fleck);
    }

    /// <summary>A delicate wish-streak: a short, slim tail arcing up-left from a small soft
    /// head. The tail fades early (alpha midpoint) and carries a whisper of blur so it reads
    /// as a meteor's afterglow, never a hard rod; the head is a faintly haloed spark, not a
    /// heavy dot. Composer keeps the whole streak clear of the moon and admits it rarely.</summary>
    private static void DrawShootingStar(SKCanvas canvas, float cx, float cy, float s, SKColor color)
    {
        var tail = s * 2.1f;
        var ex = cx - tail;
        var ey = cy - tail * 0.30f;
        using var stroke = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = MathF.Max(1f, s * 0.045f),
            StrokeCap = SKStrokeCap.Round,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, MathF.Max(0.4f, s * 0.02f)),
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(cx, cy), new SKPoint(ex, ey),
                [color.WithAlpha((byte)(color.Alpha * 0.85)), color.WithAlpha((byte)(color.Alpha * 0.30)), color.WithAlpha(0)],
                [0f, 0.45f, 1f], SKShaderTileMode.Clamp),
        };
        using var path = new SKPath();
        path.MoveTo(cx, cy);
        path.QuadTo(cx - tail * 0.5f, cy - tail * 0.24f, ex, ey);
        canvas.DrawPath(path, stroke);
        DrawGlow(canvas, cx, cy, MathF.Max(3f, s * 0.30f), color, 0.35);
        using var head = new SKPaint { Color = color.WithAlpha((byte)(color.Alpha * 0.95)), IsAntialias = true };
        canvas.DrawCircle(cx, cy, MathF.Max(1.1f, s * 0.06f), head);
    }

    // ---------------- atmosphere ----------------

    /// <summary>Organic cloud: five overlapping puffs with a flattened base — reads soft
    /// at 44px and at 512px.</summary>
    private static void DrawCloud(SKCanvas canvas, float cx, float cy, float s, SKPaint paint)
    {
        canvas.DrawCircle(cx - s * 0.52f, cy + s * 0.10f, s * 0.22f, paint);
        canvas.DrawCircle(cx - s * 0.22f, cy - s * 0.06f, s * 0.30f, paint);
        canvas.DrawCircle(cx + s * 0.10f, cy - s * 0.14f, s * 0.34f, paint);
        canvas.DrawCircle(cx + s * 0.40f, cy - s * 0.02f, s * 0.26f, paint);
        canvas.DrawCircle(cx + s * 0.58f, cy + s * 0.12f, s * 0.18f, paint);
        canvas.DrawRoundRect(new SKRect(cx - s * 0.6f, cy + s * 0.02f, cx + s * 0.62f, cy + s * 0.28f), s * 0.14f, s * 0.14f, paint);
    }

    /// <summary>A breath of mist: two soft lens shapes whose light fades out toward BOTH
    /// horizontal ends (radial-stretched gradients) — never a hard-edged bar.</summary>
    private static void DrawMistBand(SKCanvas canvas, float cx, float cy, float s, int w, int unit, SKColor color)
    {
        void Lens(float x, float y, float rx, float ry, double alpha)
        {
            using var mist = new SKPaint { IsAntialias = true };
            canvas.Save();
            canvas.Translate(x, y);
            canvas.Scale(rx / ry, 1);
            mist.Shader = SKShader.CreateRadialGradient(
                new SKPoint(0, 0), ry * 3f,
                [color.WithAlpha((byte)(color.Alpha * alpha)), color.WithAlpha(0)],
                null, SKShaderTileMode.Clamp);
            canvas.DrawRect(new SKRect(-ry * 3.2f, -ry * 3.2f, ry * 3.2f, ry * 3.2f), mist);
            canvas.Restore();
        }
        var halfW = Math.Min(s * 0.8f * w / unit, w * 0.42f);
        var t = unit * 0.020f;
        Lens(cx, cy, halfW, t, 0.34);
        Lens(cx + halfW * 0.45f, cy + t * 2.4f, halfW * 0.6f, t * 0.8f, 0.26);
        Lens(cx - halfW * 0.5f, cy - t * 2.0f, halfW * 0.5f, t * 0.7f, 0.20);
    }

    /// <summary>Aurora: three translucent curtain ribbons drifting down from the top,
    /// each fading out at its lower hem.</summary>
    private static void DrawAurora(SKCanvas canvas, float cx, float cy, float s, int w, int h, SKColor color)
    {
        // Four curtain ribbons falling from above the canvas top, each with its own lean
        // and sway, fading out toward its lower hem — the classic curtain read.
        var height = s * 1.5f;
        var top = -h * 0.03f;
        Span<(float off, float lean, float widthF, float hemF, float alphaF)> ribbons =
        [
            (-0.34f, 0.10f, 0.095f, 0.80f, 1.0f),
            (-0.08f, 0.02f, 0.120f, 1.00f, 0.85f),
            (0.20f, -0.07f, 0.085f, 0.70f, 0.75f),
            (0.42f, -0.14f, 0.065f, 0.55f, 0.55f),
        ];
        foreach (var (offF, leanF, widthF, hemF, alphaF) in ribbons)
        {
            var x0 = cx + offF * s;
            var lean = leanF * s * 2.2f;
            var half = widthF * s;
            var bottom = top + height * hemF;
            using var ribbon = new SKPath();
            ribbon.MoveTo(x0 - half, top);
            ribbon.CubicTo(x0 - half + lean * 0.6f, top + height * 0.35f, x0 - half + lean, top + height * 0.6f, x0 - half + lean * 1.6f, bottom);
            ribbon.LineTo(x0 + half + lean * 1.6f, bottom);
            ribbon.CubicTo(x0 + half + lean, top + height * 0.6f, x0 + half + lean * 0.6f, top + height * 0.35f, x0 + half, top);
            ribbon.Close();
            using var fill = new SKPaint
            {
                IsAntialias = true,
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, half * 0.45f),
                Shader = SKShader.CreateLinearGradient(
                    new SKPoint(x0, top), new SKPoint(x0 + lean * 1.6f, bottom),
                    [color.WithAlpha((byte)(105 * alphaF)), color.WithAlpha((byte)(60 * alphaF)), color.WithAlpha(0)],
                    [0f, 0.55f, 1f], SKShaderTileMode.Clamp),
            };
            canvas.DrawPath(ribbon, fill);
        }
    }

    // ---------------- terrain ----------------

    /// <summary>A hill is a large soft ellipse whose crest is at cy and whose mass overflows
    /// the bottom edge — grounded by construction. The radius is kept tight enough that the
    /// crest CURVES visibly instead of flattening into a featureless horizon band.</summary>
    private static void DrawHill(SKCanvas canvas, float cx, float cy, float s, int w, int h, SKPaint paint)
    {
        var rx = s * 0.62f * Math.Min(w, h);
        var bottom = h + (h - cy) * 1.1f + Math.Min(w, h) * 0.16f;
        canvas.DrawOval(new SKRect(cx - rx, cy, cx + rx, bottom), paint);
        // A companion shoulder keeps the far side from cutting off in a hard arc mid-canvas.
        canvas.DrawOval(new SKRect(cx - rx * 1.7f, cy + (bottom - cy) * 0.18f, cx + rx * 1.7f, bottom + (bottom - cy) * 0.1f), paint);
    }

    /// <summary>Mountain ridge in the flat-illustration idiom: crisp angular twin peaks
    /// (main at cy, lower shoulder to the right), straight slopes with the slightest
    /// concave ease, filled to the bottom edge; a faint angular snow cap on the main peak.</summary>
    private static void DrawMountain(SKCanvas canvas, float cx, float cy, float s, int w, int h, SKPaint paint, SKColor? capColor)
    {
        var unit = Math.Min(w, h);
        var half = s * 1.35f;
        var baseY = h + unit * 0.04f;
        var rise = baseY - cy;
        var p2x = cx + half * 0.52f;          // shoulder peak
        var p2y = cy + rise * 0.26f;
        var saddleX = cx + half * 0.27f;
        var saddleY = cy + rise * 0.30f;
        using var ridge = new SKPath();
        ridge.MoveTo(cx - half, baseY);
        ridge.QuadTo(cx - half * 0.45f, baseY - rise * 0.62f, cx, cy);              // long left slope
        ridge.QuadTo(cx + half * 0.16f, cy + rise * 0.17f, saddleX, saddleY);       // down to the saddle
        ridge.QuadTo(saddleX + half * 0.12f, saddleY - rise * 0.075f, p2x, p2y);    // up the shoulder
        ridge.QuadTo(cx + half * 0.78f, p2y + (baseY - p2y) * 0.55f, cx + half, baseY); // right slope
        ridge.Close();
        canvas.DrawPath(ridge, paint);

        if (capColor is null) return;
        // Snow cap: a small angular collar hanging just below the main peak.
        var capY = cy + rise * 0.13f;
        using var cap = new SKPath();
        cap.MoveTo(cx, cy);
        cap.LineTo(cx + half * 0.085f, capY - rise * 0.02f);
        cap.LineTo(cx + half * 0.045f, capY - rise * 0.055f);
        cap.LineTo(cx + half * 0.005f, capY);
        cap.LineTo(cx - half * 0.045f, capY - rise * 0.06f);
        cap.LineTo(cx - half * 0.075f, capY - rise * 0.015f);
        cap.Close();
        using var capPaint = new SKPaint { Color = capColor.Value.WithAlpha((byte)(capColor.Value.Alpha * 0.75)), IsAntialias = true };
        canvas.DrawPath(cap, capPaint);
    }

    /// <summary>Treeline band: alternating rounded and pointed treetops with seeded height
    /// jitter, filled to the bottom edge. Scale ≥0.9 spans the full width.</summary>
    private static void DrawForestRow(SKCanvas canvas, float cx, float cy, float s, int w, int h, SKPaint paint)
    {
        var unit = Math.Min(w, h);
        var half = s >= unit * 0.85f ? w : s * 1.4f;
        var left = MathF.Max(-unit * 0.02f, cx - half);
        var right = MathF.Min(w + unit * 0.02f, cx + half);
        var baseY = h + unit * 0.02f;
        var step = unit * 0.075f;

        using var band = new SKPath();
        band.MoveTo(left, baseY);
        band.LineTo(left, cy + step * 0.8f);
        var x = left;
        var i = 0;
        while (x < right)
        {
            // Deterministic per-tree jitter from the tree index (no RNG state needed).
            var jitter = ((i * 2654435761u) >> 16 & 1023) / 1023f;
            var treeW = step * (0.85f + jitter * 0.5f);
            var treeH = step * (1.5f + jitter * 1.5f);
            var apex = cy - treeH * 0.75f + step * 0.4f;
            var mid = x + treeW * 0.5f;
            if ((i & 1) == 0)
            {
                band.LineTo(x, cy + step * 0.5f);
                band.LineTo(mid, apex);                       // pointed pine top
                band.LineTo(x + treeW, cy + step * 0.5f);
            }
            else
            {
                band.LineTo(x, cy + step * 0.55f);
                band.CubicTo(x + treeW * 0.1f, apex, x + treeW * 0.9f, apex, x + treeW, cy + step * 0.55f); // round top
            }
            x += treeW;
            i++;
        }
        band.LineTo(right, cy + step * 0.8f);
        band.LineTo(right, baseY);
        band.Close();
        canvas.DrawPath(band, paint);
    }

    /// <summary>A winding path from the bottom edge narrowing toward (x, y) — a clean
    /// S-curving ribbon, slightly lighter than the ground, crisp edges (flat-illustration
    /// idiom: shapes, not airbrush).</summary>
    private static void DrawPath(SKCanvas canvas, float cx, float cy, float s, int w, int h, SKPaint paint)
    {
        var footHalf = s * 0.28f;
        var footX = cx + (cx < w / 2f ? w * 0.10f : -w * 0.10f);
        var tipHalf = MathF.Max(1.5f, footHalf * 0.09f);
        var drop = h - cy;
        using var path = new SKPath();
        path.MoveTo(footX - footHalf, h);
        path.CubicTo(footX - footHalf * 0.25f, h - drop * 0.30f,
                     cx - footHalf * 0.85f, cy + drop * 0.55f,
                     cx - tipHalf * 5f, cy + drop * 0.18f);
        path.QuadTo(cx - tipHalf * 2f, cy + drop * 0.05f, cx - tipHalf, cy);
        path.LineTo(cx + tipHalf, cy);
        path.QuadTo(cx + tipHalf * 3f, cy + drop * 0.10f, cx + tipHalf * 7f, cy + drop * 0.22f);
        path.CubicTo(cx + footHalf * 0.9f, cy + drop * 0.60f,
                     footX + footHalf * 0.30f, h - drop * 0.28f,
                     footX + footHalf, h);
        path.Close();
        canvas.DrawPath(path, paint);
    }

    /// <summary>Calm water: gradient band deepening downward, a faint waterline highlight,
    /// and 2–3 lighter shimmer pills.</summary>
    private static void DrawWater(SKCanvas canvas, float cx, float cy, float s, int w, int h, SKColor color, SKColor? waveColor)
    {
        var deep = new SKColor(
            (byte)(color.Red * 0.72), (byte)(color.Green * 0.72), (byte)(color.Blue * 0.72), color.Alpha);
        using (var band = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, cy), new SKPoint(0, h),
                [color, deep], null, SKShaderTileMode.Clamp),
        })
        using (var path = new SKPath())
        {
            path.MoveTo(0, cy + s * 0.04f);
            path.QuadTo(w * 0.5f, cy - s * 0.04f, w, cy + s * 0.04f);
            path.LineTo(w, h);
            path.LineTo(0, h);
            path.Close();
            canvas.DrawPath(path, band);
        }

        if (waveColor is null) return;
        using var line = new SKPaint
        {
            Color = waveColor.Value.WithAlpha((byte)(waveColor.Value.Alpha * 0.55)),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = MathF.Max(1f, Math.Min(w, h) * 0.0035f),
        };
        using (var crest = new SKPath())
        {
            crest.MoveTo(0, cy + s * 0.04f);
            crest.QuadTo(w * 0.5f, cy - s * 0.04f, w, cy + s * 0.04f);
            canvas.DrawPath(crest, line);
        }

        using var wave = new SKPaint
        {
            Color = waveColor.Value.WithAlpha((byte)(waveColor.Value.Alpha * 0.5)),
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
        };
        var t = Math.Min(w, h) * 0.006f;
        Pill(canvas, w * 0.28f, cy + (h - cy) * 0.30f, w * 0.13f, t, wave);
        Pill(canvas, w * 0.62f, cy + (h - cy) * 0.52f, w * 0.10f, t, wave);
        Pill(canvas, w * 0.42f, cy + (h - cy) * 0.74f, w * 0.08f, t, wave);
    }

    private static void Pill(SKCanvas canvas, float cx, float cy, float rx, float ry, SKPaint paint) =>
        canvas.DrawRoundRect(new SKRect(cx - rx, cy - ry, cx + rx, cy + ry), ry, ry, paint);

    // ---------------- flora ----------------

    /// <summary>1–3 pines, base-anchored at cy. Density adds the smaller companions.</summary>
    private static void DrawPineCluster(SKCanvas canvas, float cx, float cy, float s, double density, SKPaint paint)
    {
        DrawPine(canvas, cx, cy, s, paint);
        if (density > 0.34) DrawPine(canvas, cx - s * 1.15f, cy, s * 0.66f, paint);
        if (density > 0.67) DrawPine(canvas, cx + s * 1.05f, cy, s * 0.55f, paint);
    }

    /// <summary>One pine: three tiers with gently concave skirts + a slight trunk, base at cy.</summary>
    private static void DrawPine(SKCanvas canvas, float cx, float cy, float s, SKPaint paint)
    {
        var height = s * 1.5f;
        canvas.DrawRect(new SKRect(cx - s * 0.05f, cy - s * 0.16f, cx + s * 0.05f, cy), paint);
        for (var i = 0; i < 3; i++)
        {
            var baseY = cy - s * 0.12f - i * height * 0.24f;
            var tipY = baseY - height * 0.42f;
            var half = s * (0.52f - i * 0.13f);
            using var tier = new SKPath();
            tier.MoveTo(cx, tipY);
            tier.QuadTo(cx + half * 0.55f, baseY - height * 0.10f, cx + half, baseY);
            tier.QuadTo(cx + half * 0.4f, baseY + height * 0.015f, cx, baseY + height * 0.02f);
            tier.QuadTo(cx - half * 0.4f, baseY + height * 0.015f, cx - half, baseY);
            tier.QuadTo(cx - half * 0.55f, baseY - height * 0.10f, cx, tipY);
            tier.Close();
            canvas.DrawPath(tier, paint);
        }
    }

    /// <summary>Deciduous tree: tapered trunk with a low branch, canopy of overlapping
    /// blobs — slightly asymmetric so it reads organic.</summary>
    private static void DrawRoundTree(SKCanvas canvas, float cx, float cy, float s, SKPaint paint)
    {
        using (var trunk = new SKPath())
        {
            trunk.MoveTo(cx - s * 0.07f, cy);
            trunk.CubicTo(cx - s * 0.045f, cy - s * 0.32f, cx - s * 0.035f, cy - s * 0.5f, cx - s * 0.02f, cy - s * 0.62f);
            trunk.LineTo(cx + s * 0.03f, cy - s * 0.62f);
            trunk.CubicTo(cx + s * 0.04f, cy - s * 0.45f, cx + s * 0.05f, cy - s * 0.3f, cx + s * 0.08f, cy);
            trunk.Close();
            canvas.DrawPath(trunk, paint);
        }
        // Low branch reaching left.
        using (var branch = new SKPath())
        {
            branch.MoveTo(cx - s * 0.04f, cy - s * 0.42f);
            branch.QuadTo(cx - s * 0.22f, cy - s * 0.50f, cx - s * 0.34f, cy - s * 0.60f);
            branch.LineTo(cx - s * 0.32f, cy - s * 0.64f);
            branch.QuadTo(cx - s * 0.18f, cy - s * 0.55f, cx - s * 0.02f, cy - s * 0.48f);
            branch.Close();
            canvas.DrawPath(branch, paint);
        }
        canvas.DrawCircle(cx - s * 0.30f, cy - s * 0.72f, s * 0.20f, paint);
        canvas.DrawCircle(cx - s * 0.05f, cy - s * 0.88f, s * 0.28f, paint);
        canvas.DrawCircle(cx + s * 0.26f, cy - s * 0.74f, s * 0.22f, paint);
        canvas.DrawCircle(cx + s * 0.10f, cy - s * 0.66f, s * 0.24f, paint);
        canvas.DrawCircle(cx - s * 0.16f, cy - s * 0.62f, s * 0.18f, paint);
    }

    /// <summary>A clump of 7 lean, quad-curved reed stems, three with cattail heads.</summary>
    private static void DrawReeds(SKCanvas canvas, float cx, float cy, float s, int unit, SKPaint paint)
    {
        using var stem = new SKPaint
        {
            Color = paint.Color,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = MathF.Max(1f, unit * 0.004f),
            StrokeCap = SKStrokeCap.Round,
        };
        for (var i = 0; i < 7; i++)
        {
            var f = i / 6f;
            var x = cx + (f - 0.5f) * s * 1.3f;
            var height = s * (0.85f + ((i * 37) % 5) * 0.14f);
            var lean = (f - 0.5f) * s * 0.5f + s * 0.08f;
            var topX = x + lean;
            var topY = cy - height;
            using var path = new SKPath();
            path.MoveTo(x, cy);
            path.QuadTo(x + lean * 0.2f, cy - height * 0.6f, topX, topY);
            canvas.DrawPath(path, stem);
            if (i % 2 == 0 && i < 6)
            {
                var ry = height * 0.14f;
                canvas.DrawRoundRect(new SKRect(topX - ry * 0.32f, topY - ry, topX + ry * 0.32f, topY + ry), ry * 0.32f, ry * 0.32f, paint);
            }
        }
    }

    /// <summary>Tiny blossom: overlapping petals merge into one soft clover blob — reads as
    /// a pale dot at 44px, a flower at 512px, never a crisp plus-sign sparkle.</summary>
    private static void DrawFlower(SKCanvas canvas, float cx, float cy, float s, SKPaint paint)
    {
        var r = s * 0.34f;
        using var soft = new SKPaint
        {
            Color = paint.Color.WithAlpha((byte)(paint.Color.Alpha * 0.85)),
            IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, MathF.Max(0.4f, r * 0.35f)),
        };
        canvas.DrawCircle(cx - r, cy, r, soft);
        canvas.DrawCircle(cx + r, cy, r, soft);
        canvas.DrawCircle(cx, cy - r, r, soft);
        canvas.DrawCircle(cx, cy + r, r, soft);
        canvas.DrawCircle(cx, cy, r * 0.9f, soft);
    }

    // ---------------- creatures ----------------

    /// <summary>Sitting fox in side profile, base at cy: plump teardrop body, chest curve,
    /// one pointed snout, two tall close-set ears, and the signature large tail curling up
    /// around the body. Single silhouette color. (The quality bar for every creature.)</summary>
    private static void DrawFox(SKCanvas canvas, float cx, float cy, float s, SKPaint paint)
    {
        using (var body = new SKPath())
        {
            body.MoveTo(cx - s * 0.34f, cy);
            body.CubicTo(cx - s * 0.46f, cy - s * 0.30f, cx - s * 0.28f, cy - s * 0.56f,
                         cx - s * 0.10f, cy - s * 0.64f);
            body.CubicTo(cx - s * 0.02f, cy - s * 0.68f, cx + s * 0.06f, cy - s * 0.66f,
                         cx + s * 0.10f, cy - s * 0.60f);
            body.CubicTo(cx + s * 0.20f, cy - s * 0.44f, cx + s * 0.26f, cy - s * 0.22f,
                         cx + s * 0.26f, cy);
            body.Close();
            canvas.DrawPath(body, paint);
        }

        var hx = cx + s * 0.02f;
        var hy = cy - s * 0.72f;
        canvas.DrawCircle(hx, hy, s * 0.17f, paint);
        using (var snout = new SKPath())
        {
            snout.MoveTo(hx + s * 0.06f, hy - s * 0.10f);
            snout.LineTo(hx + s * 0.30f, hy + s * 0.045f);
            snout.LineTo(hx + s * 0.05f, hy + s * 0.13f);
            snout.Close();
            canvas.DrawPath(snout, paint);
        }

        using (var ear = new SKPath())
        {
            ear.MoveTo(hx - s * 0.15f, hy - s * 0.06f);
            ear.LineTo(hx - s * 0.115f, hy - s * 0.38f);
            ear.LineTo(hx - s * 0.005f, hy - s * 0.12f);
            ear.Close();
            canvas.DrawPath(ear, paint);
        }
        using (var ear2 = new SKPath())
        {
            ear2.MoveTo(hx - s * 0.015f, hy - s * 0.125f);
            ear2.LineTo(hx + s * 0.05f, hy - s * 0.36f);
            ear2.LineTo(hx + s * 0.135f, hy - s * 0.085f);
            ear2.Close();
            canvas.DrawPath(ear2, paint);
        }

        using (var tail = new SKPath())
        {
            tail.MoveTo(cx - s * 0.30f, cy);
            tail.CubicTo(cx - s * 0.72f, cy - s * 0.02f, cx - s * 0.78f, cy - s * 0.46f,
                         cx - s * 0.46f, cy - s * 0.62f);
            tail.CubicTo(cx - s * 0.38f, cy - s * 0.66f, cx - s * 0.30f, cy - s * 0.64f,
                         cx - s * 0.28f, cy - s * 0.58f);
            tail.CubicTo(cx - s * 0.52f, cy - s * 0.50f, cx - s * 0.54f, cy - s * 0.16f,
                         cx - s * 0.16f, cy - s * 0.10f);
            tail.Close();
            canvas.DrawPath(tail, paint);
        }
    }

    /// <summary>Sitting rabbit, alert, base at cy: rounded haunch, upright chest, small
    /// head with two long ears (one straight, one tipped), cotton tail.</summary>
    private static void DrawRabbit(SKCanvas canvas, float cx, float cy, float s, SKPaint paint)
    {
        // Haunch + body in one flowing path (facing right).
        using (var body = new SKPath())
        {
            body.MoveTo(cx - s * 0.30f, cy);
            body.CubicTo(cx - s * 0.44f, cy - s * 0.26f, cx - s * 0.30f, cy - s * 0.50f, cx - s * 0.10f, cy - s * 0.52f);
            body.CubicTo(cx - s * 0.02f, cy - s * 0.56f, cx + s * 0.07f, cy - s * 0.62f, cx + s * 0.10f, cy - s * 0.60f);
            body.CubicTo(cx + s * 0.17f, cy - s * 0.44f, cx + s * 0.20f, cy - s * 0.20f, cx + s * 0.18f, cy);
            body.Close();
            canvas.DrawPath(body, paint);
        }
        // Head, slightly forward.
        var hx = cx + s * 0.075f;
        var hy = cy - s * 0.64f;
        canvas.DrawOval(new SKRect(hx - s * 0.13f, hy - s * 0.115f, hx + s * 0.155f, hy + s * 0.115f), paint);
        // Straight ear.
        using (var ear = new SKPath())
        {
            ear.MoveTo(hx - s * 0.075f, hy - s * 0.02f);
            ear.CubicTo(hx - s * 0.135f, hy - s * 0.34f, hx - s * 0.075f, hy - s * 0.52f, hx - s * 0.035f, hy - s * 0.52f);
            ear.CubicTo(hx - s * 0.005f, hy - s * 0.50f, hx + s * 0.015f, hy - s * 0.30f, hx + s * 0.015f, hy - s * 0.045f);
            ear.Close();
            canvas.DrawPath(ear, paint);
        }
        // Tipped ear, folded slightly back.
        using (var ear2 = new SKPath())
        {
            ear2.MoveTo(hx + s * 0.035f, hy - s * 0.045f);
            ear2.CubicTo(hx + s * 0.06f, hy - s * 0.30f, hx + s * 0.13f, hy - s * 0.435f, hx + s * 0.175f, hy - s * 0.40f);
            ear2.CubicTo(hx + s * 0.185f, hy - s * 0.36f, hx + s * 0.145f, hy - s * 0.22f, hx + s * 0.095f, hy - s * 0.015f);
            ear2.Close();
            canvas.DrawPath(ear2, paint);
        }
        // Cotton tail + front paw hint.
        canvas.DrawCircle(cx - s * 0.315f, cy - s * 0.115f, s * 0.075f, paint);
        canvas.DrawOval(new SKRect(cx + s * 0.06f, cy - s * 0.05f, cx + s * 0.22f, cy), paint);
    }

    /// <summary>Owl perched on a branch stub: plump pear body, shallow ear tufts, folded
    /// wing line, two small moonlit eyes when a secondary color is given.</summary>
    private static void DrawOwl(SKCanvas canvas, float cx, float cy, float s, SKPaint paint, SKColor? eyeColor)
    {
        // Branch stub.
        using (var branch = new SKPaint
        {
            Color = paint.Color, IsAntialias = true, Style = SKPaintStyle.Stroke,
            StrokeWidth = s * 0.055f, StrokeCap = SKStrokeCap.Round,
        })
        using (var line = new SKPath())
        {
            line.MoveTo(cx - s * 0.52f, cy + s * 0.02f);
            line.QuadTo(cx, cy - s * 0.03f, cx + s * 0.5f, cy + s * 0.045f);
            canvas.DrawPath(line, branch);
        }
        // Body: pear silhouette with ear tufts in one path, base at cy.
        using (var body = new SKPath())
        {
            body.MoveTo(cx - s * 0.26f, cy);
            body.CubicTo(cx - s * 0.34f, cy - s * 0.30f, cx - s * 0.30f, cy - s * 0.62f, cx - s * 0.185f, cy - s * 0.745f);
            body.LineTo(cx - s * 0.235f, cy - s * 0.92f);                                   // left tuft
            body.CubicTo(cx - s * 0.12f, cy - s * 0.85f, cx + s * 0.12f, cy - s * 0.85f, cx + s * 0.235f, cy - s * 0.92f);
            body.LineTo(cx + s * 0.185f, cy - s * 0.745f);                                  // right tuft
            body.CubicTo(cx + s * 0.30f, cy - s * 0.62f, cx + s * 0.34f, cy - s * 0.30f, cx + s * 0.26f, cy);
            body.Close();
            canvas.DrawPath(body, paint);
        }
        if (eyeColor is not null)
        {
            using var eye = new SKPaint { Color = eyeColor.Value.WithAlpha((byte)(eyeColor.Value.Alpha * 0.9)), IsAntialias = true };
            canvas.DrawCircle(cx - s * 0.095f, cy - s * 0.66f, s * 0.038f, eye);
            canvas.DrawCircle(cx + s * 0.095f, cy - s * 0.66f, s * 0.038f, eye);
        }
    }

    /// <summary>Five to seven distant birds — shallow double-arc glyphs with size jitter,
    /// drifting in a loose diagonal.</summary>
    private static void DrawBirdFlock(SKCanvas canvas, float cx, float cy, float s, SKPaint paint)
    {
        using var stroke = new SKPaint
        {
            Color = paint.Color,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = MathF.Max(1f, s * 0.035f),
            StrokeCap = SKStrokeCap.Round,
        };
        Span<(float dx, float dy, float size)> flock =
        [
            (0f, 0f, 1.0f), (-0.55f, -0.28f, 0.75f), (0.5f, -0.42f, 0.8f),
            (-1.05f, -0.62f, 0.55f), (1.0f, -0.15f, 0.6f), (0.25f, -0.85f, 0.5f), (-0.35f, -1.0f, 0.45f),
        ];
        foreach (var (dx, dy, size) in flock)
        {
            var bx = cx + dx * s * 0.8f;
            var by = cy + dy * s * 0.8f;
            var wing = s * 0.16f * size;
            using var bird = new SKPath();
            bird.MoveTo(bx - wing, by);
            bird.QuadTo(bx - wing * 0.5f, by - wing * 0.75f, bx, by);
            bird.QuadTo(bx + wing * 0.5f, by - wing * 0.75f, bx + wing, by);
            canvas.DrawPath(bird, stroke);
        }
    }

    /// <summary>Standing deer in side profile, base at cy: arched neck, small head with two
    /// ears, slender branching antlers, tapered legs, a lifted tail.</summary>
    private static void DrawDeer(SKCanvas canvas, float cx, float cy, float s, SKPaint paint)
    {
        var legTop = cy - s * 0.42f;
        // Legs first (behind the body): slim tapered strokes.
        using (var leg = new SKPaint
        {
            Color = paint.Color, IsAntialias = true, Style = SKPaintStyle.Stroke,
            StrokeWidth = s * 0.045f, StrokeCap = SKStrokeCap.Round,
        })
        {
            canvas.DrawLine(cx - s * 0.30f, legTop, cx - s * 0.335f, cy, leg);
            canvas.DrawLine(cx - s * 0.20f, legTop, cx - s * 0.165f, cy, leg);
            canvas.DrawLine(cx + s * 0.18f, legTop, cx + s * 0.145f, cy, leg);
            canvas.DrawLine(cx + s * 0.28f, legTop, cx + s * 0.315f, cy, leg);
        }
        // Body + neck + head in one flowing silhouette (facing right).
        using (var body = new SKPath())
        {
            body.MoveTo(cx - s * 0.36f, cy - s * 0.40f);                                     // rump
            body.CubicTo(cx - s * 0.42f, cy - s * 0.52f, cx - s * 0.30f, cy - s * 0.60f, cx - s * 0.12f, cy - s * 0.585f); // back
            body.CubicTo(cx + s * 0.02f, cy - s * 0.575f, cx + s * 0.14f, cy - s * 0.565f, cx + s * 0.22f, cy - s * 0.60f); // shoulder
            body.CubicTo(cx + s * 0.30f, cy - s * 0.70f, cx + s * 0.335f, cy - s * 0.80f, cx + s * 0.36f, cy - s * 0.875f); // neck front
            body.LineTo(cx + s * 0.475f, cy - s * 0.915f);                                   // muzzle top
            body.LineTo(cx + s * 0.475f, cy - s * 0.875f);                                   // nose
            body.CubicTo(cx + s * 0.44f, cy - s * 0.845f, cx + s * 0.42f, cy - s * 0.85f, cx + s * 0.40f, cy - s * 0.835f); // chin
            body.CubicTo(cx + s * 0.36f, cy - s * 0.70f, cx + s * 0.33f, cy - s * 0.56f, cx + s * 0.30f, cy - s * 0.44f);  // throat→chest
            body.CubicTo(cx + s * 0.20f, cy - s * 0.36f, cx - s * 0.22f, cy - s * 0.36f, cx - s * 0.36f, cy - s * 0.40f);  // belly
            body.Close();
            canvas.DrawPath(body, paint);
        }
        // Ears + antlers, thin and calm.
        using (var thin = new SKPaint
        {
            Color = paint.Color, IsAntialias = true, Style = SKPaintStyle.Stroke,
            StrokeWidth = s * 0.028f, StrokeCap = SKStrokeCap.Round,
        })
        {
            var ax = cx + s * 0.375f;
            var ay = cy - s * 0.925f;
            using (var beam = new SKPath())
            {
                beam.MoveTo(ax, ay);
                beam.CubicTo(ax - s * 0.04f, ay - s * 0.14f, ax - s * 0.10f, ay - s * 0.20f, ax - s * 0.145f, ay - s * 0.235f);
                beam.MoveTo(ax - s * 0.045f, ay - s * 0.125f);
                beam.LineTo(ax + s * 0.02f, ay - s * 0.20f);
                beam.MoveTo(ax - s * 0.10f, ay - s * 0.19f);
                beam.LineTo(ax - s * 0.075f, ay - s * 0.27f);
                canvas.DrawPath(beam, thin);
            }
            using (var beam2 = new SKPath())
            {
                beam2.MoveTo(ax - s * 0.04f, ay + s * 0.01f);
                beam2.CubicTo(ax - s * 0.10f, ay - s * 0.10f, ax - s * 0.17f, ay - s * 0.145f, ax - s * 0.22f, ay - s * 0.16f);
                beam2.MoveTo(ax - s * 0.115f, ay - s * 0.095f);
                beam2.LineTo(ax - s * 0.10f, ay - s * 0.185f);
                canvas.DrawPath(beam2, thin);
            }
        }
        using (var ear = new SKPath())
        {
            ear.MoveTo(cx + s * 0.33f, cy - s * 0.87f);
            ear.LineTo(cx + s * 0.24f, cy - s * 0.95f);
            ear.LineTo(cx + s * 0.325f, cy - s * 0.925f);
            ear.Close();
            canvas.DrawPath(ear, paint);
        }
        // Tail flick.
        using (var tail = new SKPath())
        {
            tail.MoveTo(cx - s * 0.36f, cy - s * 0.50f);
            tail.QuadTo(cx - s * 0.45f, cy - s * 0.545f, cx - s * 0.435f, cy - s * 0.435f);
            tail.QuadTo(cx - s * 0.40f, cy - s * 0.455f, cx - s * 0.355f, cy - s * 0.43f);
            tail.Close();
            canvas.DrawPath(tail, paint);
        }
    }

    /// <summary>Whale flukes rising from the waterline at cy — the classic silhouette: a
    /// stout tapering trunk flaring into two wide flukes that sweep up and OUT to pointed
    /// tips, with a clean notch between them. Nothing below the line.</summary>
    private static void DrawWhaleTail(SKCanvas canvas, float cx, float cy, float s, SKPaint paint)
    {
        var tipY = cy - s * 0.72f;   // fluke tips are the highest points
        using var tail = new SKPath();
        tail.MoveTo(cx - s * 0.16f, cy);                                                                     // trunk base left
        tail.CubicTo(cx - s * 0.13f, cy - s * 0.22f, cx - s * 0.10f, cy - s * 0.34f, cx - s * 0.09f, cy - s * 0.42f); // taper up
        tail.CubicTo(cx - s * 0.20f, cy - s * 0.48f, cx - s * 0.40f, cy - s * 0.58f, cx - s * 0.50f, tipY + s * 0.04f); // out to left tip
        tail.QuadTo(cx - s * 0.505f, tipY, cx - s * 0.475f, tipY + s * 0.005f);                              // pointed-soft tip
        tail.CubicTo(cx - s * 0.33f, cy - s * 0.62f, cx - s * 0.12f, cy - s * 0.52f, cx, cy - s * 0.50f);    // fluke underside to notch
        tail.CubicTo(cx + s * 0.12f, cy - s * 0.52f, cx + s * 0.33f, cy - s * 0.62f, cx + s * 0.475f, tipY + s * 0.005f);
        tail.QuadTo(cx + s * 0.505f, tipY, cx + s * 0.50f, tipY + s * 0.04f);
        tail.CubicTo(cx + s * 0.40f, cy - s * 0.58f, cx + s * 0.20f, cy - s * 0.48f, cx + s * 0.09f, cy - s * 0.42f);
        tail.CubicTo(cx + s * 0.10f, cy - s * 0.34f, cx + s * 0.13f, cy - s * 0.22f, cx + s * 0.16f, cy);    // trunk base right
        tail.Close();
        canvas.DrawPath(tail, paint);
        // Two soft ripples where the trunk meets the water.
        using var ripple = new SKPaint { Color = paint.Color.WithAlpha((byte)(paint.Color.Alpha * 0.35)), IsAntialias = true };
        Pill(canvas, cx, cy, s * 0.30f, MathF.Max(1f, s * 0.022f), ripple);
        Pill(canvas, cx + s * 0.12f, cy + s * 0.05f, s * 0.18f, MathF.Max(1f, s * 0.018f), ripple);
    }

    // ---------------- structures ----------------

    /// <summary>Slim tapered tower + lamp room + cap, base at cy, with two hoop stripes,
    /// a warm lamp glow, and (when it owns the accent) a soft beam wedge.</summary>
    private static void DrawLighthouse(SKCanvas canvas, float cx, float cy, float s, int w,
        SKPaint paint, SKColor? beamColor, double glow, SKColor glowColor)
    {
        var height = s * 1.6f;
        using (var tower = new SKPath())
        {
            tower.MoveTo(cx - s * 0.13f, cy);
            tower.LineTo(cx - s * 0.075f, cy - height * 0.78f);
            tower.LineTo(cx + s * 0.075f, cy - height * 0.78f);
            tower.LineTo(cx + s * 0.13f, cy);
            tower.Close();
            canvas.DrawPath(tower, paint);
        }
        // Hoop stripes — subtle darker bands.
        using (var stripe = new SKPaint { Color = paint.Color.WithAlpha((byte)(paint.Color.Alpha * 0.35)), IsAntialias = true, BlendMode = SKBlendMode.Multiply })
        {
            for (var i = 0; i < 2; i++)
            {
                var f = 0.30f + i * 0.28f;
                var yTop = cy - height * 0.78f * f;
                var half = s * (0.075f + (0.13f - 0.075f) * f);
                canvas.DrawRect(new SKRect(cx - half, yTop - height * 0.035f, cx + half, yTop + height * 0.035f), stripe);
            }
        }
        var lampY = cy - height * 0.78f;
        canvas.DrawRect(new SKRect(cx - s * 0.095f, lampY - s * 0.16f, cx + s * 0.095f, lampY), paint);
        using (var cap = new SKPath())
        {
            cap.MoveTo(cx, lampY - s * 0.30f);
            cap.LineTo(cx + s * 0.115f, lampY - s * 0.155f);
            cap.LineTo(cx - s * 0.115f, lampY - s * 0.155f);
            cap.Close();
            canvas.DrawPath(cap, paint);
        }
        var lampCy = lampY - s * 0.08f;
        if (glow > 0) DrawGlow(canvas, cx, lampCy, s * 0.55f, glowColor, glow);
        if (beamColor is null) return;
        var toward = cx < w / 2f ? 1 : -1;
        var farX = cx + toward * s * 2.1f;
        using var beam = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(cx, lampCy), new SKPoint(farX, lampCy),
                [beamColor.Value.WithAlpha(125), beamColor.Value.WithAlpha(0)],
                null, SKShaderTileMode.Clamp),
        };
        using var beamPath = new SKPath();
        beamPath.MoveTo(cx + toward * s * 0.09f, lampCy);
        beamPath.LineTo(farX, lampCy - s * 0.40f);
        beamPath.LineTo(farX, lampCy + s * 0.26f);
        beamPath.Close();
        canvas.DrawPath(beamPath, beam);
    }

    /// <summary>Small hull arc + thin mast + pennant, waterline at cy.</summary>
    private static void DrawBoat(SKCanvas canvas, float cx, float cy, float s, SKPaint paint)
    {
        using (var hull = new SKPath())
        {
            hull.MoveTo(cx - s * 0.55f, cy - s * 0.06f);
            hull.LineTo(cx + s * 0.55f, cy - s * 0.06f);
            hull.QuadTo(cx + s * 0.32f, cy + s * 0.30f, cx, cy + s * 0.32f);
            hull.QuadTo(cx - s * 0.32f, cy + s * 0.30f, cx - s * 0.55f, cy - s * 0.06f);
            hull.Close();
            canvas.DrawPath(hull, paint);
        }
        canvas.DrawRect(new SKRect(cx - s * 0.025f, cy - s * 0.72f, cx + s * 0.025f, cy - s * 0.06f), paint);
        using var flag = new SKPath();
        flag.MoveTo(cx + s * 0.025f, cy - s * 0.72f);
        flag.LineTo(cx + s * 0.24f, cy - s * 0.63f);
        flag.LineTo(cx + s * 0.025f, cy - s * 0.54f);
        flag.Close();
        canvas.DrawPath(flag, paint);
    }

    /// <summary>Sailboat: hull arc, mast, a full curved mainsail and a smaller jib —
    /// the sails take the secondary (pale) color, waterline at cy.</summary>
    private static void DrawSailboat(SKCanvas canvas, float cx, float cy, float s, SKPaint paint, SKColor? sailColor)
    {
        using (var hull = new SKPath())
        {
            hull.MoveTo(cx - s * 0.5f, cy - s * 0.05f);
            hull.LineTo(cx + s * 0.5f, cy - s * 0.05f);
            hull.QuadTo(cx + s * 0.30f, cy + s * 0.22f, cx, cy + s * 0.24f);
            hull.QuadTo(cx - s * 0.30f, cy + s * 0.22f, cx - s * 0.5f, cy - s * 0.05f);
            hull.Close();
            canvas.DrawPath(hull, paint);
        }
        var mastTop = cy - s * 1.18f;
        canvas.DrawRect(new SKRect(cx - s * 0.02f, mastTop, cx + s * 0.02f, cy - s * 0.05f), paint);
        using var sail = new SKPaint { Color = sailColor ?? paint.Color, IsAntialias = true };
        using (var main = new SKPath())
        {
            main.MoveTo(cx - s * 0.055f, mastTop + s * 0.06f);
            main.CubicTo(cx - s * 0.16f, cy - s * 0.75f, cx - s * 0.32f, cy - s * 0.38f, cx - s * 0.44f, cy - s * 0.13f);
            main.LineTo(cx - s * 0.055f, cy - s * 0.13f);
            main.Close();
            canvas.DrawPath(main, sail);
        }
        using (var jib = new SKPath())
        {
            jib.MoveTo(cx + s * 0.05f, mastTop + s * 0.16f);
            jib.CubicTo(cx + s * 0.17f, cy - s * 0.62f, cx + s * 0.28f, cy - s * 0.34f, cx + s * 0.38f, cy - s * 0.13f);
            jib.LineTo(cx + s * 0.05f, cy - s * 0.13f);
            jib.Close();
            canvas.DrawPath(jib, sail);
        }
    }

    /// <summary>A warm-lit cottage: dark gable silhouette anchored at cy, chimney, and a
    /// softly glowing window — never a floating rectangle.</summary>
    private static void DrawCottageWindow(SKCanvas canvas, float cx, float cy, float s,
        SKPaint windowPaint, SKColor? cottageColor, double glow, SKColor glowColor)
    {
        var bodyHalf = s * 0.52f;
        var bodyTop = cy - s * 0.52f;
        var ridgeY = bodyTop - s * 0.34f;

        if (cottageColor is not null)
        {
            using var cottage = new SKPaint { Color = cottageColor.Value, IsAntialias = true, Style = SKPaintStyle.Fill };
            using var shape = new SKPath();
            shape.MoveTo(cx - bodyHalf, cy);
            shape.LineTo(cx - bodyHalf, bodyTop);
            shape.LineTo(cx - bodyHalf - s * 0.07f, bodyTop);          // eave overhang
            shape.LineTo(cx, ridgeY);
            shape.LineTo(cx + bodyHalf + s * 0.07f, bodyTop);
            shape.LineTo(cx + bodyHalf, bodyTop);
            shape.LineTo(cx + bodyHalf, cy);
            shape.Close();
            canvas.DrawPath(shape, cottage);
            canvas.DrawRect(new SKRect(cx + s * 0.24f, ridgeY + s * 0.02f, cx + s * 0.34f, bodyTop - s * 0.02f), cottage);
        }

        var wx = cx - s * 0.02f;
        var wy = cy - s * 0.26f;
        var wrx = s * 0.115f;
        var wry = s * 0.15f;
        if (glow > 0) DrawGlow(canvas, wx, wy, s * (0.35f + (float)glow * 0.35f), glowColor, glow * 0.8);
        canvas.DrawRoundRect(new SKRect(wx - wrx, wy - wry, wx + wrx, wy + wry), s * 0.045f, s * 0.045f, windowPaint);
        // Four-pane muntin cut in the cottage tone keeps it storybook.
        if (cottageColor is not null)
        {
            using var muntin = new SKPaint { Color = cottageColor.Value, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = MathF.Max(1f, s * 0.018f) };
            canvas.DrawLine(wx, wy - wry, wx, wy + wry, muntin);
            canvas.DrawLine(wx - wrx, wy, wx + wrx, wy, muntin);
        }
    }

    /// <summary>A hanging lantern on a gently curved post, base at cy: dark post + arm,
    /// warm glowing pane in a small frame.</summary>
    private static void DrawLantern(SKCanvas canvas, float cx, float cy, float s, SKPaint postPaint,
        SKColor? paneColor, double glow, SKColor glowColor)
    {
        var height = s * 1.7f;
        var topY = cy - height;
        using (var post = new SKPath())
        {
            post.MoveTo(cx - s * 0.045f, cy);
            post.LineTo(cx - s * 0.03f, topY);
            post.LineTo(cx + s * 0.03f, topY);
            post.LineTo(cx + s * 0.045f, cy);
            post.Close();
            canvas.DrawPath(post, postPaint);
        }
        // Curved arm reaching right.
        using (var arm = new SKPaint
        {
            Color = postPaint.Color, IsAntialias = true, Style = SKPaintStyle.Stroke,
            StrokeWidth = s * 0.05f, StrokeCap = SKStrokeCap.Round,
        })
        using (var curve = new SKPath())
        {
            curve.MoveTo(cx, topY);
            curve.QuadTo(cx + s * 0.28f, topY - s * 0.02f, cx + s * 0.34f, topY + s * 0.10f);
            canvas.DrawPath(curve, arm);
        }
        // Lantern body hanging from the arm tip.
        var lx = cx + s * 0.34f;
        var ly = topY + s * 0.13f;   // hook point
        var lw = s * 0.135f;
        var lh = s * 0.19f;
        var frameTop = ly + s * 0.03f;
        if (glow > 0) DrawGlow(canvas, lx, frameTop + lh * 0.55f, s * (0.3f + (float)glow * 0.3f), glowColor, glow * 0.85);
        using (var pane = new SKPaint { Color = paneColor ?? glowColor, IsAntialias = true })
        {
            canvas.DrawRoundRect(new SKRect(lx - lw * 0.72f, frameTop + lh * 0.12f, lx + lw * 0.72f, frameTop + lh * 1.0f), s * 0.025f, s * 0.025f, pane);
        }
        using (var frame = new SKPaint
        {
            Color = postPaint.Color, IsAntialias = true, Style = SKPaintStyle.Stroke,
            StrokeWidth = MathF.Max(1f, s * 0.028f),
        })
        {
            canvas.DrawRoundRect(new SKRect(lx - lw * 0.72f, frameTop + lh * 0.12f, lx + lw * 0.72f, frameTop + lh * 1.0f), s * 0.025f, s * 0.025f, frame);
        }
        using (var cap = new SKPath())
        {
            cap.MoveTo(lx, frameTop - lh * 0.18f);
            cap.LineTo(lx + lw * 0.9f, frameTop + lh * 0.14f);
            cap.LineTo(lx - lw * 0.9f, frameTop + lh * 0.14f);
            cap.Close();
            canvas.DrawPath(cap, postPaint);
        }
    }

    /// <summary>A softly sagging A-frame tent, base at cy, with a warm glowing door flap.</summary>
    private static void DrawTent(SKCanvas canvas, float cx, float cy, float s, SKPaint paint,
        SKColor? doorColor, double glow, SKColor glowColor)
    {
        var half = s * 0.62f;
        var apexY = cy - s * 0.78f;
        using (var body = new SKPath())
        {
            body.MoveTo(cx - half, cy);
            body.CubicTo(cx - half * 0.62f, cy - s * 0.30f, cx - half * 0.28f, cy - s * 0.62f, cx - s * 0.015f, apexY);
            body.LineTo(cx + s * 0.015f, apexY);
            body.CubicTo(cx + half * 0.28f, cy - s * 0.62f, cx + half * 0.62f, cy - s * 0.30f, cx + half, cy);
            body.Close();
            canvas.DrawPath(body, paint);
        }
        // Ridge pole tip.
        using (var pole = new SKPaint
        {
            Color = paint.Color, IsAntialias = true, Style = SKPaintStyle.Stroke,
            StrokeWidth = MathF.Max(1f, s * 0.03f), StrokeCap = SKStrokeCap.Round,
        })
        {
            canvas.DrawLine(cx, apexY, cx + s * 0.09f, apexY - s * 0.14f, pole);
        }
        // Door flap: inner triangle, warm-lit.
        var doorHalf = half * 0.30f;
        var doorTop = cy - s * 0.46f;
        if (glow > 0) DrawGlow(canvas, cx, cy - s * 0.18f, s * (0.32f + (float)glow * 0.3f), glowColor, glow * 0.7);
        using (var door = new SKPaint { Color = doorColor ?? glowColor, IsAntialias = true })
        using (var flap = new SKPath())
        {
            flap.MoveTo(cx, doorTop);
            flap.CubicTo(cx + doorHalf * 0.75f, cy - s * 0.28f, cx + doorHalf, cy - s * 0.08f, cx + doorHalf * 0.9f, cy);
            flap.LineTo(cx - doorHalf * 0.9f, cy);
            flap.CubicTo(cx - doorHalf, cy - s * 0.08f, cx - doorHalf * 0.75f, cy - s * 0.28f, cx, doorTop);
            flap.Close();
            canvas.DrawPath(flap, door);
        }
    }
}

/// <summary>IPosterRasterizer over SkiaPosterRenderer — flattened PNG thumbnails saved
/// beside the story.</summary>
public sealed class SkiaPosterRasterizer : IPosterRasterizer
{
    private readonly IPosterComposer _composer;

    public SkiaPosterRasterizer(IPosterComposer composer) => _composer = composer;

    public Task<string> FlattenToPngAsync(PosterSpec spec, string outputPath, int sizePx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var resolved = _composer.Resolve(spec);
        using var image = SkiaPosterRenderer.RenderFlattened(resolved, sizePx);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using (var stream = File.Create(outputPath))
        {
            data.SaveTo(stream);
        }
        return Task.FromResult(outputPath);
    }
}
