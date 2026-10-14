using System.Diagnostics;
using SkiaSharp;
using StoryTime.Contracts;
using StoryTime.Poster.Composition;
using StoryTime.Poster.Render;
using StoryTime.Poster.Spec;

namespace StoryTime.Poster;

/// <summary>One rendered parallax layer: the bitmap cropped to its painted content, and
/// where that crop sits inside the full (bleed-inclusive) layer canvas.</summary>
public sealed class PosterLayerImage(int depth, SKImage image, SKRectI bounds, float travel) : IDisposable
{
    public int Depth { get; } = depth;
    public SKImage Image { get; } = image;
    /// <summary>Crop rectangle in full-canvas pixels (bleed included).</summary>
    public SKRectI Bounds { get; } = bounds;
    /// <summary>Max parallax offset in pixels at full tilt.</summary>
    public float Travel { get; } = travel;
    public long Bytes => (long)Image.Width * Image.Height * 4;
    public void Dispose() => Image.Dispose();
}

/// <summary>A poster rendered for parallax: a FIXED backdrop (the sky ramp — it never
/// moves, so it can never reveal an edge), four travelling layers back→front, a fixed
/// vignette, and the ambient particles the view animates.</summary>
public sealed class PosterLayerSet : IDisposable
{
    public required PosterPlan Plan { get; init; }
    public required PosterViewport Viewport { get; init; }
    public required SKImage Backdrop { get; init; }
    public required IReadOnlyList<PosterLayerImage> Layers { get; init; }
    public required SKImage Vignette { get; init; }
    public required TimeSpan RenderTime { get; init; }
    /// <summary>True when the ambient particles were painted into their layers at t = 0
    /// (cards, Reduce Motion): nothing is left to animate.</summary>
    public bool AmbientBaked { get; init; }

    public long Bytes => (long)Backdrop.Width * Backdrop.Height * 4 + Layers.Sum(l => l.Bytes)
                         + (long)Vignette.Width * Vignette.Height * 4;

    public void Dispose()
    {
        Backdrop.Dispose();
        foreach (var l in Layers) l.Dispose();
        Vignette.Dispose();
    }
}

/// <summary>The procedural parallax poster engine. Deterministic: (spec, size) → the same
/// pixels on every platform. Pure SkiaSharp raster — it renders headless on the Mac for
/// tests and on device for the app, and it is called once per poster per size (the view
/// caches the result; parallax is compositor translation, never per-frame Skia).</summary>
public static class PosterEngine
{
    public const int ArtVersion = PosterEngineInfo.ArtVersion;

    /// <summary>Full-tilt travel per depth as a fraction of the poster's shorter side.
    /// Calibrated on a physical iPhone against a 226 pt player poster (founder evidence,
    /// 2026-07-31): [12, 20, 30, 40] pt. Below these the back layers read as static.</summary>
    public static readonly double[] TravelFraction = [12 / 226.0, 20 / 226.0, 30 / 226.0, 40 / 226.0];

    public static PosterPlan Plan(PosterSpec spec) => ScenePlanner.Plan(spec);

    /// <summary>Bleed in pixels so the fastest layer never exposes an edge at full tilt.</summary>
    public static int BleedFor(int visibleW, int visibleH, double travelScale) =>
        travelScale <= 0 ? 0 : (int)Math.Ceiling(TravelFraction[^1] * Math.Min(visibleW, visibleH) * travelScale) + 2;

    public static PosterLayerSet RenderLayers(PosterSpec spec, int visibleW, int visibleH, double travelScale = 1.0,
        bool bakeAmbient = false) =>
        RenderLayers(Plan(spec), visibleW, visibleH, travelScale, bakeAmbient);

    public static PosterLayerSet RenderLayers(PosterPlan plan, int visibleW, int visibleH, double travelScale = 1.0,
        bool bakeAmbient = false)
    {
        if (visibleW <= 0 || visibleH <= 0) throw new ArgumentOutOfRangeException(nameof(visibleW));
        var sw = Stopwatch.StartNew();
        var bleed = BleedFor(visibleW, visibleH, travelScale);
        var vp = new PosterViewport(visibleW, visibleH, bleed, bleed);
        var info = new SKImageInfo(vp.Width, vp.Height, SKColorType.Rgba8888, SKAlphaType.Premul);

        SKImage backdrop;
        using (var bmp = new SKBitmap(info))
        {
            SkyPainter.PaintBackdrop(bmp, plan, vp);
            bmp.SetImmutable();
            backdrop = SKImage.FromBitmap(bmp);
        }

        var unit = Math.Min(visibleW, visibleH);
        var layers = new List<PosterLayerImage>(4);
        for (var depth = 0; depth < 4; depth++)
        {
            using var bmp = new SKBitmap(info);
            bmp.Erase(SKColors.Transparent);
            if (depth == 0)
            {
                SkyPainter.PaintCelestial(bmp, plan, vp);
            }
            else
            {
                if (plan.Moon is { } moon && moon.Depth == depth) SkyPainter.PaintMoonOnLayer(bmp, plan, vp);
                using var canvas = new SKCanvas(bmp);
                foreach (var band in plan.Bands.Where(b => b.Depth == depth))
                    TerrainPainter.PaintBand(canvas, plan, band, vp);
                if (plan.Water is { } water && water.Depth == depth)
                    TerrainPainter.PaintWater(canvas, plan, water, vp,
                        plan.Bands.Where(b => b.Depth == depth && b.Profile.Kind != TerrainKind.Flat).ToList());
                foreach (var p in plan.Placements.Where(p => p.Depth == depth))
                    PropPainter.Paint_(canvas, plan, p, vp);
                Paint.Grain(canvas, vp.Width, vp.Height, 0.036 - depth * 0.005, depth * 977 + plan.Spec.Seed);
            }
            if (bakeAmbient && plan.Ambient.Any(a => a.Depth == depth))
            {
                using var canvas = new SKCanvas(bmp);
                AmbientPainter.Draw(canvas, plan.Ambient.Where(a => a.Depth == depth).ToList(), vp, 0, []);
            }
            var bounds = ContentBounds(bmp);
            var travel = (float)(TravelFraction[depth] * unit * travelScale);
            SKImage image;
            if (bounds.IsEmpty)
            {
                bounds = new SKRectI(0, 0, 1, 1);
                using var tiny = new SKBitmap(new SKImageInfo(1, 1, SKColorType.Rgba8888, SKAlphaType.Premul));
                tiny.Erase(SKColors.Transparent);
                image = SKImage.FromBitmap(tiny);
            }
            else
            {
                // Copy the cropped pixels out (a subset would alias the bitmap, which dies here).
                using var pixmap = bmp.PeekPixels();
                using var sub = pixmap.ExtractSubset(bounds);
                image = SKImage.FromPixelCopy(sub);
            }
            layers.Add(new PosterLayerImage(depth, image, bounds, travel));
        }

        var vignette = RenderVignette(visibleW, visibleH);
        return new PosterLayerSet
        {
            Plan = plan,
            Viewport = vp,
            Backdrop = backdrop,
            Layers = layers,
            Vignette = vignette,
            RenderTime = sw.Elapsed,
            AmbientBaked = bakeAmbient,
        };
    }

    /// <summary>The visible frame with each layer shifted by tilt (−1..1 on each axis) times
    /// its travel — exactly what the parallax view composites — plus the ambient pass at
    /// time <paramref name="ambientTime"/>.</summary>
    public static SKImage Composite(PosterLayerSet set, double tiltX = 0, double tiltY = 0, double ambientTime = 0,
        bool includeAmbient = true)
    {
        var vp = set.Viewport;
        var info = new SKImageInfo(vp.VisibleWidth, vp.VisibleHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var c = surface.Canvas;
        c.Clear(SKColors.Black);
        c.DrawImage(set.Backdrop, -vp.BleedX, -vp.BleedY);
        Span<SKPoint> offsets = stackalloc SKPoint[4];
        foreach (var layer in set.Layers)
        {
            var ox = (float)(Math.Clamp(tiltX, -1, 1) * layer.Travel);
            var oy = (float)(Math.Clamp(tiltY, -1, 1) * layer.Travel);
            offsets[layer.Depth] = new SKPoint(ox, oy);
        }
        foreach (var layer in set.Layers)
        {
            var o = offsets[layer.Depth];
            c.DrawImage(layer.Image, layer.Bounds.Left - vp.BleedX + o.X, layer.Bounds.Top - vp.BleedY + o.Y);
            // Each layer's particles right after it, so a twinkling star can never sit on a
            // mountain and a firefly behind a near leaf stays behind it.
            if (!includeAmbient || set.AmbientBaked) continue;
            var own = set.Plan.Ambient.Where(a => a.Depth == layer.Depth).ToList();
            if (own.Count == 0) continue;
            c.Save();
            c.Translate(-vp.BleedX, -vp.BleedY);
            AmbientPainter.Draw(c, own, vp, ambientTime, offsets);
            c.Restore();
        }
        c.DrawImage(set.Vignette, 0, 0);
        return surface.Snapshot();
    }

    /// <summary>A flat, still poster (thumbnails, share images, Reduce Motion).</summary>
    public static SKImage RenderFlattened(PosterSpec spec, int width, int height)
    {
        using var set = RenderLayers(Plan(spec), width, height, travelScale: 0, bakeAmbient: true);
        return Composite(set);
    }

    public static SKImage RenderVignette(int w, int h)
    {
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var c = surface.Canvas;
        c.Clear(SKColors.Transparent);
        var cx = w * 0.5f;
        var cy = h * 0.52f;
        var radius = MathF.Sqrt(cx * cx + cy * cy);
        using var p = new SKPaint
        {
            IsAntialias = true,
            IsDither = true,
            Shader = SKShader.CreateRadialGradient(new SKPoint(cx, cy), radius,
                [SKColors.Black.WithAlpha(0), SKColors.Black.WithAlpha(0), SKColors.Black.WithAlpha(22)],
                [0f, 0.6f, 1f], SKShaderTileMode.Clamp),
        };
        c.DrawRect(0, 0, w, h, p);
        // A quiet grade into the Hearth page (#201813) across the bottom 9%, so every
        // poster, cool or warm, settles into the warm UI instead of sitting on it.
        using var foot = new SKPaint
        {
            IsDither = true,
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, h * 0.91f), new SKPoint(0, h),
                [new SKColor(0x20, 0x18, 0x13, 0), new SKColor(0x20, 0x18, 0x13, 70)], SKShaderTileMode.Clamp),
        };
        c.DrawRect(0, h * 0.91f, w, h * 0.09f, foot);
        return surface.Snapshot();
    }

    /// <summary>Encodes an image as PNG (fast zlib level: layer caches favour speed).</summary>
    public static byte[] EncodePng(SKImage image, bool fast = true)
    {
        using var pixmap = image.PeekPixels();
        if (pixmap is not null)
        {
            using var data = pixmap.Encode(new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, fast ? 1 : 6));
            if (data is not null) return data.ToArray();
        }
        using var fallback = image.Encode(SKEncodedImageFormat.Png, 100);
        return fallback.ToArray();
    }

    private static SKRectI ContentBounds(SKBitmap bmp)
    {
        var span = bmp.GetPixelSpan();
        int w = bmp.Width, h = bmp.Height;
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (var y = 0; y < h; y++)
        {
            var row = y * w * 4;
            var rowHit = false;
            for (var x = 0; x < w; x++)
            {
                if (span[row + x * 4 + 3] == 0) continue;
                rowHit = true;
                if (x < minX) minX = x;
                break;
            }
            if (!rowHit) continue;
            for (var x = w - 1; x >= 0; x--)
            {
                if (span[row + x * 4 + 3] == 0) continue;
                if (x > maxX) maxX = x;
                break;
            }
            if (y < minY) minY = y;
            maxY = y;
        }
        return maxX < 0 ? SKRectI.Empty : new SKRectI(minX, minY, maxX + 1, maxY + 1);
    }
}
