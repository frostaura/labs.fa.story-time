using SkiaSharp;
using StoryTime.Contracts;
using StoryTime.Poster.Color;
using StoryTime.Poster.Composition;
using StoryTime.Poster.Core;

namespace StoryTime.Poster.Render;

/// <summary>The sky: a per-pixel backdrop (fixed — it never travels) and the travelling
/// celestial layer (depth 0: halo, Milky Way, aurora, stars, clouds, moon).
///
/// Both large soft fields — the sky ramp and the moon's halo — are computed per pixel in
/// float, encoded through a linear→sRGB LUT and quantised with a triangular-PDF dither of
/// about ±1 LSB applied LAST (Wronski; Blender's dither). A dark night gradient is the
/// worst case for 8-bit banding; this is what keeps it smooth on device.</summary>
internal static class SkyPainter
{
    /// <summary>TPDF dither in [-1,1) LSB from two decorrelated hashes.</summary>
    private static float Dither(int x, int y) =>
        (float)(Hash.Unit(x, y, 0x1234u) + Hash.Unit(x, y, 0xBEEFu) - 1.0);

    public static void PaintBackdrop(SKBitmap bmp, PosterPlan plan, PosterViewport vp)
    {
        var c = plan.Colors;
        var w = vp.Width;
        var h = vp.Height;
        var span = bmp.GetPixelSpan();

        var zen = c.SkyZenith.ToOklab();
        var mid = c.SkyMid.ToOklab();
        var hor = c.SkyHorizon.ToOklab();
        var glow = c.HorizonGlow.ToOklab().ToLinearRgb();
        var horizon = plan.Horizon;
        var underwater = plan.Underwater;
        var sunX = 0.5 + plan.Side * 0.22;
        var dusk = plan.Spec.TimeOfDay == PosterTimeOfDay.Dusk;

        // Per-column horizontal weighting of the afterglow (stronger where the sun went down).
        var colW = new float[w];
        for (var x = 0; x < w; x++)
        {
            var nx = vp.NX(x + 0.5f);
            var d = (nx - sunX) / 0.55;
            colW[x] = (float)(dusk ? 0.55 + 0.45 * Math.Exp(-d * d) : 0.8 + 0.2 * Math.Exp(-d * d));
        }

        for (var y = 0; y < h; y++)
        {
            var ny = vp.NY(y + 0.5f);
            Oklab baseLab;
            double g;
            if (underwater)
            {
                if (hor.L > 0.36) hor = hor with { L = 0.36 };
                // Water column: brighter toward the surface, deepening to the reef floor.
                var t = Math.Clamp(ny / horizon, 0, 1.2);
                baseLab = t < 0.45
                    ? Oklab.Mix(hor, mid, Noise.SmoothStep(0, 0.45, t))
                    : Oklab.Mix(mid, zen, Noise.SmoothStep(0.45, 1.1, t));
                var s = ny / 0.16;
                g = Math.Min(0.12, c.HorizonGlowStrength * 0.15) * Math.Exp(-s * s);
            }
            else
            {
                var t = Math.Clamp(ny / horizon, -0.3, 1.3);
                baseLab = t < 0.55
                    ? Oklab.Mix(zen, mid, Noise.SmoothStep(-0.1, 0.55, t))
                    : Oklab.Mix(mid, hor, Noise.SmoothStep(0.55, 1.0, t));
                var dh = (horizon - ny) / 0.2;
                g = ny > horizon ? c.HorizonGlowStrength : c.HorizonGlowStrength * Math.Exp(-dh * dh);
            }
            var (br, bg, bb) = baseLab.MapToGamut().ToLinearRgb();
            var row = y * w * 4;
            for (var x = 0; x < w; x++)
            {
                var k = (float)(g * colW[x]);
                var r = (float)(br + (glow.R - br) * k);
                var gg = (float)(bg + (glow.G - bg) * k);
                var b = (float)(bb + (glow.B - bb) * k);
                var d = Dither(x, y);
                var i = row + x * 4;
                span[i] = Q(Srgb.EncodeFloat(r) + d);
                span[i + 1] = Q(Srgb.EncodeFloat(gg) + d);
                span[i + 2] = Q(Srgb.EncodeFloat(b) + d);
                span[i + 3] = 255;
            }
        }
    }

    private static byte Q(float v) => (byte)Math.Clamp((int)MathF.Round(v), 0, 255);

    /// <summary>Depth 0: everything in the sky that travels.</summary>
    public static void PaintCelestial(SKBitmap bmp, PosterPlan plan, PosterViewport vp)
    {
        if (plan.Moon is { Depth: 0 } moon) PaintHalo(bmp, plan, moon, vp);
        using var canvas = new SKCanvas(bmp);
        if (plan.Underwater) PaintShafts(canvas, plan, vp);
        if (plan.MilkyWay is { } mw) PaintMilkyWay(canvas, plan, mw, vp);
        if (plan.Aurora is { } au) PaintAurora(canvas, plan, au, vp);
        PaintStars(canvas, plan, vp);
        if (plan.ShootingStar is { } ss) PaintShootingStar(canvas, ss, vp);   // behind clouds
        if (plan.Moon is { Depth: 0 } m) PaintMoon(canvas, plan, m, vp);
        foreach (var cloud in plan.Clouds) PaintCloud(canvas, plan, cloud, vp);
    }

    /// <summary>The moon on the water's layer (when a moonglade must stay under it): halo
    /// pixels first, then the disc — before any band of that layer is painted.</summary>
    public static void PaintMoonOnLayer(SKBitmap bmp, PosterPlan plan, PosterViewport vp)
    {
        if (plan.Moon is not { } moon || moon.Depth == 0) return;
        PaintHalo(bmp, plan, moon, vp);
        using var canvas = new SKCanvas(bmp);
        PaintMoon(canvas, plan, moon, vp);
    }

    /// <summary>Two-term halo: a tight bloom plus a long Lorentzian tail, written straight
    /// into the (still empty) layer as premultiplied, dithered pixels.</summary>
    private static void PaintHalo(SKBitmap bmp, PosterPlan plan, MoonPlan moon, PosterViewport vp)
    {
        var c = plan.Colors.MoonGlow.ToSk();
        var cx = vp.X(moon.X);
        var cy = vp.Y(moon.Y);
        var rr = vp.S(moon.R);
        var strength = moon.Glow * (1 - 0.55 * moon.Phase);
        var reach = rr * 9;
        var x0 = Math.Max(0, (int)(cx - reach));
        var x1 = Math.Min(vp.Width - 1, (int)(cx + reach));
        var y0 = Math.Max(0, (int)(cy - reach));
        var y1 = Math.Min(vp.Height - 1, (int)(cy + reach));
        var span = bmp.GetPixelSpan();
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                var dx = (x + 0.5f - cx) / rr;
                var dy = (y + 0.5f - cy) / rr;
                var r2 = dx * dx + dy * dy;
                var a = strength * (0.32 * Math.Exp(-r2 / 0.9) + 0.13 / (1 + r2 / 14.0));
                a *= 1 - Noise.SmoothStep(64, 81, r2); // fade to exactly 0 at the box edge
                var a8 = (float)(a * 255) + Dither(x, y) * 0.9f;
                if (a8 <= 0.5f) continue;
                var av = Q(a8);
                var i = (y * vp.Width + x) * 4;
                span[i] = (byte)(c.Red * av / 255);
                span[i + 1] = (byte)(c.Green * av / 255);
                span[i + 2] = (byte)(c.Blue * av / 255);
                span[i + 3] = av;
            }
        }
    }

    private static void PaintMoon(SKCanvas canvas, PosterPlan plan, MoonPlan moon, PosterViewport vp)
    {
        var cx = vp.X(moon.X);
        var cy = vp.Y(moon.Y);
        var r = vp.S(moon.R);
        var cream = plan.Colors.Moon;

        using var disc = new SKPath();
        disc.AddCircle(cx, cy, r);
        SKPath lit;
        if (moon.Phase < 0.02)
        {
            lit = new SKPath(disc);
        }
        else
        {
            // Bite: an offset disc; LitFromRight puts the shadow on the left.
            var d = 2 * r * (float)(1 - moon.Phase);
            // The lit limb faces the sun: at dusk and twilight the sun is below the horizon,
            // so the crescent opens downward toward it; at night it lies on its side.
            var low = plan.Spec.TimeOfDay is PosterTimeOfDay.Dusk or PosterTimeOfDay.Twilight;
            var dir = moon.LitFromRight ? -1f : 1f;
            var (bx, by) = low ? (dir * 0.55f, -0.84f) : (dir, -0.04f);
            using var bite = new SKPath();
            bite.AddCircle(cx + bx * d, cy + by * d, r * 1.02f);
            lit = disc.Op(bite, SKPathOp.Difference) ?? new SKPath(disc);

        }

        using (lit)
        {
            var hx = cx + (moon.LitFromRight ? r * 0.25f : -r * 0.25f);
            using var body = new SKPaint
            {
                IsAntialias = true,
                IsDither = true,
                Shader = SKShader.CreateRadialGradient(new SKPoint(hx, cy - r * 0.2f), r * 1.25f,
                    [Paint.Sk(cream.Lighter(0.02)), Paint.Sk(cream), Paint.Sk(cream.Lighter(-0.07))],
                    [0f, 0.6f, 1f], SKShaderTileMode.Clamp),
            };
            canvas.DrawPath(lit, body);

            // Maria: three faint soft patches, only where lit.
            canvas.Save();
            canvas.ClipPath(lit, SKClipOperation.Intersect, true);
            var mr = new Rng((ulong)(plan.Spec.Seed ^ 0x6D6F6F6E));
            using var maria = new SKPaint
            {
                IsAntialias = true,
                Color = Paint.Sk(cream.Lighter(-0.16).ScaleC(1.5), 0.10),
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, Math.Max(0.6f, r * 0.12f)),
            };
            for (var i = 0; i < 3; i++)
                canvas.DrawOval(cx + (float)mr.Jitter(0.45) * r, cy + (float)mr.Jitter(0.4) * r,
                    r * (float)mr.Range(0.18, 0.32), r * (float)mr.Range(0.14, 0.26), maria);
            canvas.Restore();
        }
    }

    private static void PaintStars(SKCanvas canvas, PosterPlan plan, PosterViewport vp)
    {
        var twinkling = new HashSet<(double, double)>(plan.Ambient.Where(a => a.Kind == AmbientKind.Twinkle).Select(a => (a.X, a.Y)));
        var tints = new[]
        {
            ScenePalette.Cream,
            new Oklch(0.92, 0.035, 250),
            new Oklch(0.92, 0.045, 70),
        };
        using var dot = new SKPaint { IsAntialias = true };
        foreach (var s in plan.Stars)
        {
            if (twinkling.Contains((s.X, s.Y))) continue; // drawn live by the ambient pass
            var x = vp.X(s.X);
            var y = vp.Y(s.Y);
            if (x < -4 || x > vp.Width + 4 || y < -4 || y > vp.Height + 4) continue;
            DrawStar(canvas, x, y, Math.Max(0.55f, vp.S(s.Size)), Paint.Sk(tints[s.Tint]), s.Alpha, s.Sparkle);
        }
    }

    /// <summary>A star: core dot, a soft glow for the bright ones, and a thin four-point
    /// sparkle for the very brightest. Shared with the ambient twinkle pass.</summary>
    public static void DrawStar(SKCanvas canvas, float x, float y, float radius, SKColor color, double alpha, bool sparkle)
    {
        if (alpha <= 0.01) return;
        if (alpha > 0.6)
            Paint.Glow(canvas, x, y, radius * 4.5f, color, alpha * 0.22);
        using var core = Paint.Fill(color.WithAlpha((byte)Math.Clamp(alpha * 255, 0, 255)));
        canvas.DrawCircle(x, y, radius, core);
        if (!sparkle) return;
        var len = radius * 5.5f;
        using var ray = new SKPaint
        {
            IsAntialias = true,
            StrokeWidth = Math.Max(0.6f, radius * 0.45f),
            Style = SKPaintStyle.Stroke,
            StrokeCap = SKStrokeCap.Round,
        };
        ray.Shader = SKShader.CreateLinearGradient(new SKPoint(x - len, y), new SKPoint(x + len, y),
            [color.WithAlpha(0), color.WithAlpha((byte)(alpha * 150)), color.WithAlpha(0)], SKShaderTileMode.Clamp);
        canvas.DrawLine(x - len, y, x + len, y, ray);
        ray.Shader = SKShader.CreateLinearGradient(new SKPoint(x, y - len), new SKPoint(x, y + len),
            [color.WithAlpha(0), color.WithAlpha((byte)(alpha * 150)), color.WithAlpha(0)], SKShaderTileMode.Clamp);
        canvas.DrawLine(x, y - len, x, y + len, ray);
    }

    private static void PaintMilkyWay(SKCanvas canvas, PosterPlan plan, MilkyWayPlan mw, PosterViewport vp)
    {
        // Half resolution, then scaled up with smooth sampling: it is a soft field.
        var hw = Math.Max(2, vp.Width / 2);
        var hh = Math.Max(2, vp.Height / 2);
        using var bmp = new SKBitmap(new SKImageInfo(hw, hh, SKColorType.Rgba8888, SKAlphaType.Premul));
        var span = bmp.GetPixelSpan();
        var col = Oklab.Mix(ScenePalette.Cream.ToOklab(), plan.Colors.SkyMid.ToOklab(), 0.45).ToSk();
        var rad = mw.AngleDeg * Math.PI / 180;
        var (ux, uy) = (Math.Cos(rad), Math.Sin(rad));
        for (var y = 0; y < hh; y++)
        {
            var ny = vp.NY(y * 2 + 1);
            var ext = Noise.SmoothStep(plan.Horizon - 0.02, plan.Horizon - 0.22, ny);
            for (var x = 0; x < hw; x++)
            {
                var nx = vp.NX(x * 2 + 1);
                var px = nx - mw.CenterX;
                var py = ny - mw.CenterY;
                var along = px * ux + py * uy;
                var across = -px * uy + py * ux;
                var g = Math.Exp(-(across * across) / (mw.Width * mw.Width) * 3);
                if (g < 0.01) continue;
                var n = Noise.Fbm2(along * 5 + 11, across * 9, mw.Seed, 4);
                var lane = 1 - 0.55 * Math.Exp(-Math.Pow((across + 0.01 * Math.Sin(along * 7)) / (mw.Width * 0.18), 2));
                var a = g * (0.25 + 0.75 * n * n) * lane * ext * 0.20;
                if (TitleSafeFade(plan, nx, ny) is var tf && tf < 1) a *= tf;
                var av = (byte)Math.Clamp(a * 255 + Dither(x, y) * 0.8, 0, 255);
                if (av == 0) continue;
                var i = (y * hw + x) * 4;
                span[i] = (byte)(col.Red * av / 255);
                span[i + 1] = (byte)(col.Green * av / 255);
                span[i + 2] = (byte)(col.Blue * av / 255);
                span[i + 3] = av;
            }
        }
        using var img = SKImage.FromBitmap(bmp);
        using var p = new SKPaint { IsAntialias = true };
        canvas.DrawImage(img, new SKRect(0, 0, vp.Width, vp.Height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), p);
    }

    /// <summary>Soft fields dim inside the title band so the title always wins.</summary>
    private static double TitleSafeFade(PosterPlan plan, double nx, double ny)
    {
        var t = plan.TitleSafe;
        if (ny > t.Bottom + 0.06) return 1;
        var vertical = Noise.SmoothStep(t.Bottom + 0.06, t.Bottom - 0.02, ny);
        var horizontal = Noise.SmoothStep(t.Left - 0.08, t.Left + 0.06, nx) * (1 - Noise.SmoothStep(t.Right - 0.06, t.Right + 0.08, nx));
        return 1 - 0.9 * vertical * horizontal;
    }

    private static void PaintAurora(SKCanvas canvas, PosterPlan plan, AuroraPlan au, PosterViewport vp)
    {
        var green = new Oklch(0.80, 0.12 * plan.Colors.Chroma, 162);
        var violet = new Oklch(0.62, 0.10, 305);
        using var layer = new SKPaint
        {
            ImageFilter = SKImageFilter.CreateBlur(vp.Unit * 0.006f, vp.Unit * 0.012f),
        };
        canvas.SaveLayer(layer);
        var step = Math.Max(1f, vp.Unit / 260f);
        using var p = new SKPaint { IsAntialias = true, IsDither = true };
        for (var px = vp.X(au.X0 - 0.05); px < vp.X(au.X1 + 0.05); px += step)
        {
            var nx = vp.NX(px);
            var env = Noise.SmoothStep(au.X0 - 0.05, au.X0 + 0.12, nx) * (1 - Noise.SmoothStep(au.X1 - 0.12, au.X1 + 0.05, nx));
            var n = 0.5 + 0.5 * Noise.Fbm1(nx * 3.1, au.Seed, 3);
            var curtain = 0.5 + 0.5 * Noise.Fbm1(nx * 14, au.Seed + 5, 2);
            var intensity = env * (0.35 + 0.65 * n) * (0.6 + 0.4 * curtain);
            if (intensity < 0.02) continue;
            var by = vp.Y(au.Base + 0.025 * Math.Sin(nx * 5.5 + au.Seed % 7) + 0.02 * Noise.Fbm1(nx * 2, au.Seed + 9, 2));
            var height = vp.S(au.Height * (0.55 + 0.45 * n));
            p.Shader = SKShader.CreateLinearGradient(new SKPoint(0, by), new SKPoint(0, by - height),
                [Paint.Sk(green, 0.30 * intensity), Paint.Sk(green, 0.14 * intensity), Paint.Sk(violet, 0.05 * intensity), Paint.Sk(violet, 0)],
                [0f, 0.25f, 0.7f, 1f], SKShaderTileMode.Clamp);
            canvas.DrawRect(px, by - height, step + 0.6f, height + vp.S(0.006), p);
            p.Shader?.Dispose();
        }
        canvas.Restore();
    }

    private static void PaintCloud(SKCanvas canvas, PosterPlan plan, CloudPlan cloud, PosterViewport vp)
    {
        var r = new Rng(cloud.Seed);
        var cx = vp.X(cloud.X);
        var baseY = vp.Y(cloud.Y);
        var w = vp.S(cloud.Width);
        var h = vp.S(cloud.Height);
        using var path = new SKPath();
        var n = 6 + r.Int(4);
        for (var i = 0; i < n; i++)
        {
            var u = (i + 0.5f) / n;
            var bulge = 1 - MathF.Pow(MathF.Abs(2 * u - 1), 1.6f) * 0.55f;
            var rad = h * bulge * (float)r.Range(0.55, 0.85);
            var x = cx - w / 2 + u * w + (float)r.Jitter(w * 0.03);
            path.AddCircle(x, baseY - rad * 0.55f, rad);
        }
        // A second, smaller top row: the cumulus "heads".
        for (var i = 0; i < 3; i++)
        {
            var x = cx + (float)r.Jitter(w * 0.22);
            var rad = h * (float)r.Range(0.4, 0.62);
            path.AddCircle(x, baseY - h * 0.75f, rad);
        }
        // One outline (lobes unioned) so overlaps never build up opacity or show seams; the
        // underside is a soft, slightly bowed curve, not a ruler line.
        using var merged = path.Simplify() ?? new SKPath(path);
        using var under = new SKPath();
        under.MoveTo(cx - w, baseY - h * 0.1f);
        under.QuadTo(cx, baseY + h * 0.22f, cx + w, baseY - h * 0.1f);
        under.LineTo(cx + w, baseY - h * 3);
        under.LineTo(cx - w, baseY - h * 3);
        under.Close();
        using var shape = merged.Op(under, SKPathOp.Intersect) ?? new SKPath(merged);
        path.Reset();
        path.AddPath(shape);
        canvas.Save();
        var lit = plan.Colors.CloudLit;
        var shade = plan.Colors.CloudShade;
        using var fill = new SKPaint
        {
            IsAntialias = true,
            IsDither = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, Math.Max(0.5f, h * 0.05f)),
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, baseY - h * 1.4f), new SKPoint(0, baseY),
                [Paint.Sk(lit), Paint.Sk(Oklab.Mix(lit.ToOklab(), shade.ToOklab(), 0.6).ToOklch()), Paint.Sk(shade)],
                [0f, 0.55f, 1f], SKShaderTileMode.Clamp),
        };
        canvas.DrawPath(path, fill);
        var lx = plan.Moon is { } m ? Math.Sign(m.X - cloud.X) : 0;
        Paint.Rim(canvas, path, Paint.Sk(ScenePalette.Cream, 0.1 * plan.Colors.RimStrength + 0.04), -lx * h * 0.10f, h * 0.12f);
        canvas.Restore();
    }

    private static void PaintShootingStar(SKCanvas canvas, ShootingStarPlan ss, PosterViewport vp)
    {
        var hx = vp.X(ss.X);
        var hy = vp.Y(ss.Y);
        var len = vp.S(ss.Length);
        var rad = ss.AngleDeg * Math.PI / 180;
        var tx = hx + (float)Math.Cos(rad) * len;
        var ty = hy - (float)Math.Sin(rad) * len * 0.45f;
        var cream = Paint.Sk(ScenePalette.Cream);
        using var streak = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeCap = SKStrokeCap.Round,
            StrokeWidth = Math.Max(0.8f, vp.Unit * 0.0035f),
            Shader = SKShader.CreateLinearGradient(new SKPoint(tx, ty), new SKPoint(hx, hy),
                [cream.WithAlpha(0), cream.WithAlpha(70), cream.WithAlpha(210)], [0f, 0.6f, 1f], SKShaderTileMode.Clamp),
        };
        canvas.DrawLine(tx, ty, hx, hy, streak);
        Paint.Glow(canvas, hx, hy, vp.Unit * 0.016f, cream, 0.55);
        using var head = Paint.Fill(cream);
        canvas.DrawCircle(hx, hy, Math.Max(0.8f, vp.Unit * 0.0035f), head);
    }

    /// <summary>Underwater: soft light shafts slanting down from the surface.</summary>
    private static void PaintShafts(SKCanvas canvas, PosterPlan plan, PosterViewport vp)
    {
        var r = new Rng((ulong)(uint)plan.Spec.Seed ^ 0x5348414654UL);
        var color = Oklab.Mix(ScenePalette.Cream.ToOklab(), plan.Colors.SkyHorizon.ToOklab(), 0.35).ToOklch();
        var n = 4 + r.Int(3);
        var slant = plan.Side * 0.12;
        for (var i = 0; i < n; i++)
        {
            var x = r.Range(-0.05, 1.05);
            var w0 = r.Range(0.02, 0.05);
            var w1 = w0 * r.Range(2.5, 4);
            var len = r.Range(0.5, 0.8);
            using var path = new SKPath();
            path.MoveTo(vp.X(x - w0), vp.Y(-0.05));
            path.LineTo(vp.X(x + w0), vp.Y(-0.05));
            path.LineTo(vp.X(x + slant * len + w1), vp.Y(len));
            path.LineTo(vp.X(x + slant * len - w1), vp.Y(len));
            path.Close();
            using var p = new SKPaint
            {
                IsAntialias = true,
                IsDither = true,
                Shader = SKShader.CreateLinearGradient(new SKPoint(0, vp.Y(0)), new SKPoint(0, vp.Y(len)),
                    [Paint.Sk(color, r.Range(0.07, 0.12)), Paint.Sk(color, 0)], SKShaderTileMode.Clamp),
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, vp.Unit * 0.01f),
            };
            canvas.DrawPath(path, p);
        }
    }
}
