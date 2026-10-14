using SkiaSharp;
using StoryTime.Poster.Color;
using StoryTime.Poster.Composition;
using StoryTime.Poster.Core;

namespace StoryTime.Poster.Render;

/// <summary>Pixel geometry of one render: the visible poster frame plus a bleed margin on
/// every side (the overscan that parallax travel moves into). Normalized plan coordinates
/// map to the visible frame; the bleed is x &lt; 0 / x &gt; 1.</summary>
public readonly record struct PosterViewport(int VisibleWidth, int VisibleHeight, int BleedX, int BleedY)
{
    public int Width => VisibleWidth + 2 * BleedX;
    public int Height => VisibleHeight + 2 * BleedY;
    public float Unit => Math.Min(VisibleWidth, VisibleHeight);
    public float X(double nx) => (float)(BleedX + nx * VisibleWidth);
    public float Y(double ny) => (float)(BleedY + ny * VisibleHeight);
    public float S(double size) => (float)(size * Unit);
    public double NX(float px) => (px - BleedX) / VisibleWidth;
    public double NY(float py) => (py - BleedY) / VisibleHeight;
    /// <summary>Normalized x range covered by the bitmap, bleed included.</summary>
    public double MinNX => -(double)BleedX / VisibleWidth;
    public double MaxNX => 1 + (double)BleedX / VisibleWidth;
}

/// <summary>Shared drawing helpers: colours, glows, rim light, gouache grain.</summary>
internal static class Paint
{
    public static SKColor Sk(Oklch c, double alpha = 1) => c.ToSk((byte)Math.Clamp(Math.Round(alpha * 255), 0, 255));

    public static SKPaint Fill(SKColor color) => new() { Color = color, IsAntialias = true, Style = SKPaintStyle.Fill };

    public static SKPaint Stroke(SKColor color, float width, SKStrokeCap cap = SKStrokeCap.Round) => new()
    {
        Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = width, StrokeCap = cap,
        StrokeJoin = SKStrokeJoin.Round,
    };

    /// <summary>Soft radial glow — a warm window, a lantern, a firefly.</summary>
    public static void Glow(SKCanvas canvas, float cx, float cy, float radius, SKColor color, double strength)
    {
        if (radius <= 0.5f || strength <= 0) return;
        var a = (byte)Math.Clamp(strength * 255, 0, 255);
        using var p = new SKPaint
        {
            IsAntialias = true,
            IsDither = true,
            Shader = SKShader.CreateRadialGradient(new SKPoint(cx, cy), radius,
                [color.WithAlpha(a), color.WithAlpha((byte)(a * 0.38)), color.WithAlpha((byte)(a * 0.1)), color.WithAlpha(0)],
                [0f, 0.25f, 0.6f, 1f], SKShaderTileMode.Clamp),
        };
        canvas.DrawCircle(cx, cy, radius, p);
    }

    /// <summary>Rim light: the sliver of <paramref name="path"/> NOT covered by the same
    /// path shifted away from the light — a moonlit edge, painted inside the silhouette so
    /// it never haloes onto the sky.</summary>
    public static void Rim(SKCanvas canvas, SKPath path, SKColor rim, float dx, float dy)
    {
        if (rim.Alpha == 0 || (Math.Abs(dx) < 0.05f && Math.Abs(dy) < 0.05f)) return;
        using var shifted = new SKPath(path);
        shifted.Offset(dx, dy);
        canvas.Save();
        canvas.ClipPath(path, SKClipOperation.Intersect, true);
        canvas.ClipPath(shifted, SKClipOperation.Difference, true);
        using var p = Fill(rim);
        canvas.DrawRect(path.Bounds, p);
        canvas.Restore();
    }

    /// <summary>Silhouette with an inner vertical tone ramp (lit top → grounded base) and a
    /// rim light. The base of every painted shape is the anchor of a gouache look: flat
    /// colour, but never dead-flat.</summary>
    public static void Silhouette(SKCanvas canvas, SKPath path, Oklch body, Oklch rim, double rimAlpha,
        float rimDx, float rimDy, double topLift = 0.02, double baseDrop = 0.025)
    {
        using var simple = path.Simplify();
        if (simple is not null) path = simple;   // one outline: no seams where parts overlap
        var b = path.Bounds;
        if (b.IsEmpty) return;
        using var p = new SKPaint
        {
            IsAntialias = true,
            IsDither = true,
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, b.Top), new SKPoint(0, b.Bottom),
                [Sk(body.Lighter(topLift)), Sk(body), Sk(body.Lighter(-baseDrop))], [0f, 0.45f, 1f], SKShaderTileMode.Clamp),
        };
        canvas.DrawPath(path, p);
        if (rimAlpha > 0) Rim(canvas, path, Sk(rim, rimAlpha), rimDx, rimDy);
    }

    // ---------------- gouache grain ----------------

    private const int GrainSize = 384;

    /// <summary>One deterministic grain tile: fine per-pixel tooth (±) plus soft low-
    /// frequency blotches, stored as premultiplied white (lighten) or black (darken).</summary>
    private static readonly Lazy<SKImage> GrainTile = new(() =>
    {
        using var bmp = new SKBitmap(new SKImageInfo(GrainSize, GrainSize, SKColorType.Rgba8888, SKAlphaType.Premul));
        var span = bmp.GetPixelSpan();
        for (var y = 0; y < GrainSize; y++)
        for (var x = 0; x < GrainSize; x++)
        {
            // Tileable blotches: sample the periodic lattice (wrap by GrainSize/48 cells).
            var blotch = TileableValue(x / 48.0, y / 48.0, GrainSize / 48) * 2 - 1;
            var fine = Hash.Unit(x, y, 0xA5A5u) * 2 - 1;
            var v = blotch * 0.7 + fine * 0.3;
            var a = (byte)Math.Clamp(Math.Abs(v) * 255, 0, 255);
            // Lightening specks at half strength: on the darkest ground they read as dust.
            if (v > 0) a = (byte)(a / 2);
            var c = v > 0 ? a : (byte)0;
            var i = (y * GrainSize + x) * 4;
            span[i] = c; span[i + 1] = c; span[i + 2] = c; span[i + 3] = a;
        }
        return SKImage.FromBitmap(bmp);
    });

    private static double TileableValue(double x, double y, int period)
    {
        var ix = (int)Math.Floor(x);
        var iy = (int)Math.Floor(y);
        var fx = x - ix;
        var fy = y - iy;
        double H(int a, int b) => Hash.Unit(((a % period) + period) % period, ((b % period) + period) % period, 0x5EEDu);
        var ux = fx * fx * (3 - 2 * fx);
        var uy = fy * fy * (3 - 2 * fy);
        var v00 = H(ix, iy); var v10 = H(ix + 1, iy); var v01 = H(ix, iy + 1); var v11 = H(ix + 1, iy + 1);
        return v00 + (v10 - v00) * ux + (v01 - v00) * uy + (v00 - v10 - v01 + v11) * ux * uy;
    }

    /// <summary>Lays the grain over whatever is already painted on the canvas (SrcATop
    /// keeps the destination alpha: sky stays sky, silhouettes gain tooth).</summary>
    public static void Grain(SKCanvas canvas, int width, int height, double strength, int offsetSeed)
    {
        if (strength <= 0) return;
        var ox = offsetSeed % GrainSize;
        var oy = (offsetSeed / 7) % GrainSize;
        using var shader = GrainTile.Value.ToShader(SKShaderTileMode.Repeat, SKShaderTileMode.Repeat,
            SKMatrix.CreateTranslation(ox, oy));
        using var p = new SKPaint
        {
            Shader = shader,
            BlendMode = SKBlendMode.SrcATop,
            Color = SKColors.Black.WithAlpha((byte)Math.Clamp(strength * 255, 0, 255)),
        };
        canvas.DrawRect(0, 0, width, height, p);
    }
}
