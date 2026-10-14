using SkiaSharp;
using StoryTime.Poster.Color;
using StoryTime.Poster.Composition;
using StoryTime.Poster.Core;

namespace StoryTime.Poster.Render;

/// <summary>Silhouette bands (ridges, dunes, cloud banks, roofs) and the water plane.
/// Each band is sampled across the FULL bitmap width, bleed included, and filled to the
/// bitmap's bottom edge — so no tilt, however far, can ever reveal its end.</summary>
internal static class TerrainPainter
{
    public static Oklch BandColor(PosterPlan plan, TerrainBand band)
    {
        var c = plan.Colors;
        var g = c.Ground(band.Tone);
        return band.Style switch
        {
            TerrainStyle.Sand => g with { L = g.L + 0.035, C = g.C * 0.9 + 0.012, H = ScenePalette.HueLerp(g.H, 55, 0.35) },
            TerrainStyle.Cloud => Oklab.Mix(c.CloudLit.ToOklab(), c.CloudShade.ToOklab(), 0.3 + band.Tone * 0.4).ToOklch()
                with { L = Math.Max(c.CloudShade.L + 0.06, 0.5 - band.Tone * 0.1) },
            TerrainStyle.Reef => g with { H = ScenePalette.HueLerp(g.H, 200, 0.4), C = g.C * 1.1 },
            TerrainStyle.Canopy => Oklab.Mix(c.Foliage.ToOklab(), c.Haze.ToOklab(), (1 - band.Tone) * 0.7).ToOklch(),
            _ => g,
        };
    }

    public static SKPath BandPath(TerrainBand band, PosterViewport vp, float bottomPad = 4)
    {
        var path = new SKPath();
        var step = Math.Max(1f, vp.Unit / 360f);
        var first = true;
        for (float px = -2; px <= vp.Width + 2; px += step)
        {
            var y = vp.Y(band.Profile.Y(vp.NX(px)));
            if (first) { path.MoveTo(px, y); first = false; }
            else path.LineTo(px, y);
        }
        path.LineTo(vp.Width + 2, vp.Height + bottomPad);
        path.LineTo(-2, vp.Height + bottomPad);
        path.Close();
        return path;
    }

    public static void PaintBand(SKCanvas canvas, PosterPlan plan, TerrainBand band, PosterViewport vp)
    {
        if (band.Profile.Kind == TerrainKind.Rooftops) { PaintRoofs(canvas, plan, band, vp); return; }

        var c = plan.Colors;
        var color = BandColor(plan, band);
        using var path = BandPath(band, vp);
        var crest = vp.Y(band.Profile.Base - (band.Profile.Amp > 0 ? band.Profile.Amp : 0.06));
        var foot = Math.Min(vp.Height, vp.Y(band.Profile.Base + 0.12));

        // Body: lit top → grounded foot (snow keeps its moonlit face).
        var top = band.Style == TerrainStyle.Snow ? color.Lighter(0.05) : color.Lighter(0.025);
        var bottom = color.Lighter(band.Style == TerrainStyle.Cloud ? -0.05 : -0.03);
        using (var fill = new SKPaint
               {
                   IsAntialias = true,
                   IsDither = true,
                   Shader = SKShader.CreateLinearGradient(new SKPoint(0, crest), new SKPoint(0, foot),
                       [Paint.Sk(top), Paint.Sk(color), Paint.Sk(bottom)], [0f, 0.45f, 1f], SKShaderTileMode.Clamp),
               })
        {
            if (band.Style == TerrainStyle.Cloud)
                fill.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, Math.Max(0.5f, vp.Unit * 0.0025f));
            canvas.DrawPath(path, fill);
        }

        if (band.SnowCaps) PaintSnowCaps(canvas, plan, band, vp, path);

        // Rim light along the crest, brightest on slopes facing the moon.
        if (band.Rim && c.RimStrength > 0)
        {
            var lx = plan.Moon is { } m ? m.X : 0.5;
            var rimW = Math.Max(1f, vp.Unit * (band.Style == TerrainStyle.Cloud ? 0.007f : 0.0035f));
            var near = band.Tone;
            var alpha = c.RimStrength * (band.Style == TerrainStyle.Cloud ? 0.75 : 0.35 + 0.35 * near);
            using var shifted = new SKPath(path);
            shifted.Offset(0, rimW);
            canvas.Save();
            canvas.ClipPath(path, SKClipOperation.Intersect, true);
            canvas.ClipPath(shifted, SKClipOperation.Difference, true);
            using var rim = new SKPaint
            {
                IsAntialias = true,
                Shader = SKShader.CreateLinearGradient(new SKPoint(vp.X(lx - 0.6), 0), new SKPoint(vp.X(lx + 0.6), 0),
                    [Paint.Sk(c.Rim, alpha * 0.35), Paint.Sk(c.Rim, alpha), Paint.Sk(c.Rim, alpha * 0.35)], SKShaderTileMode.Clamp),
            };
            canvas.DrawRect(0, 0, vp.Width, vp.Height, rim);
            canvas.Restore();
        }

        // Valley fog: the band's foot dissolves into the haze (aerial perspective).
        if (band.Fog > 0)
        {
            var fogTop = vp.Y(band.Profile.Base - 0.04);
            var fogBottom = vp.Y(band.Profile.Base + 0.10);
            canvas.Save();
            canvas.ClipPath(path, SKClipOperation.Intersect, true);
            using var fog = new SKPaint
            {
                IsDither = true,
                Shader = SKShader.CreateLinearGradient(new SKPoint(0, fogTop), new SKPoint(0, fogBottom),
                    [Paint.Sk(c.Haze, 0), Paint.Sk(c.Haze, band.Fog * 0.75), Paint.Sk(c.Haze, band.Fog)], [0f, 0.55f, 1f], SKShaderTileMode.Clamp),
            };
            canvas.DrawRect(0, fogTop, vp.Width, vp.Height - fogTop, fog);
            canvas.Restore();
        }
    }

    private static void PaintSnowCaps(SKCanvas canvas, PosterPlan plan, TerrainBand band, PosterViewport vp, SKPath bandPath)
    {
        var p = band.Profile;
        var snowLine = p.Base - p.Amp * 0.66;
        using var cap = new SKPath();
        var step = Math.Max(1f, vp.Unit / 360f);
        var pts = new List<SKPoint>();
        for (float px = -2; px <= vp.Width + 2; px += step)
        {
            var nx = vp.NX(px);
            var y = p.Y(nx);
            var depth = Math.Min(0.045, Math.Max(0, snowLine - y) * 0.45) + 0.004 * (0.5 + 0.5 * Noise.Fbm1(nx * 40, p.Seed + 3, 2));
            if (y > snowLine) depth = 0;
            pts.Add(new SKPoint(px, vp.Y(y + depth)));
        }
        cap.MoveTo(-2, vp.Y(p.Y(vp.NX(-2))));
        for (float px = -2; px <= vp.Width + 2; px += step) cap.LineTo(px, vp.Y(p.Y(vp.NX(px))));
        for (var i = pts.Count - 1; i >= 0; i--) cap.LineTo(pts[i]);
        cap.Close();
        var snow = Oklab.Mix(plan.Colors.Snow.ToOklab(), plan.Colors.Haze.ToOklab(), (1 - band.Tone) * 0.55).ToOklch();
        canvas.Save();
        canvas.ClipPath(bandPath, SKClipOperation.Intersect, true);
        using var paint = Paint.Fill(Paint.Sk(snow, 0.9));
        canvas.DrawPath(cap, paint);
        canvas.Restore();
    }

    private static void PaintRoofs(SKCanvas canvas, PosterPlan plan, TerrainBand band, PosterViewport vp)
    {
        var c = plan.Colors;
        var color = BandColor(plan, band);
        var bottom = vp.Height + 4f;
        var windowColor = c.Window;
        var unlit = color.Lighter(0.05);
        // One continuous body under every roofline: no seam can open between buildings.
        if (band.Profile.Buildings.Count > 0)
        {
            var baseTop = vp.Y(band.Profile.Buildings.Max(b => b.WallTop) + 0.02);
            using var body = Paint.Fill(Paint.Sk(color.Lighter(-0.01)));
            canvas.DrawRect(-2, baseTop, vp.Width + 4, bottom - baseTop, body);
        }
        foreach (var b in band.Profile.Buildings)
        {
            var x0 = vp.X(b.X0);
            var x1 = vp.X(b.X1);
            if (x1 < -4 || x0 > vp.Width + 4) continue;
            var wall = vp.Y(b.WallTop);
            using var path = new SKPath();
            path.MoveTo(x0, bottom);
            path.LineTo(x0, wall);
            switch (b.Roof)
            {
                case RoofKind.Flat:
                    path.LineTo(x1, wall);
                    break;
                case RoofKind.Dome:
                    path.ArcTo(new SKRect(x0, wall - vp.S(b.RoofHeight), x1, wall + vp.S(b.RoofHeight)), 180, 180, false);
                    break;
                default:
                    var overhang = (x1 - x0) * 0.06f;
                    path.LineTo(x0 - overhang, wall);
                    path.LineTo((x0 + x1) / 2, vp.Y(b.WallTop - b.RoofHeight));
                    path.LineTo(x1 + overhang, wall);
                    path.LineTo(x1, wall);
                    break;
            }
            path.LineTo(x1, bottom);
            path.Close();
            if (b.Chimney && b.Roof != RoofKind.Steep)
            {
                var cx = vp.X(b.X0 + (b.X1 - b.X0) * b.ChimneyX);
                var cw = (x1 - x0) * 0.12f;
                var cy = vp.Y(b.RoofY(b.X0 + (b.X1 - b.X0) * b.ChimneyX));
                path.AddRect(new SKRect(cx - cw / 2, cy - vp.S(0.035), cx + cw / 2, cy + 2));
            }
            var r = new Rng(b.Seed);
            var tone = color.Lighter(r.Jitter(0.012));
            Paint.Silhouette(canvas, path, tone, c.Rim, band.Rim ? c.RimStrength * 0.5 : 0, 0, Math.Max(1f, vp.Unit * 0.003f), 0.01, 0.0);

            // Windows: a few warm, most dark; tiny on the far skyline.
            var ww = Math.Max(1.2f, (x1 - x0) * 0.16f);
            var wh = ww * 1.3f;
            for (var i = 0; i < b.Windows; i++)
            {
                var wx = x0 + (x1 - x0) * (i + 1f) / (b.Windows + 1) - ww / 2;
                var wy = wall + vp.S(0.018) + (float)r.Range(0, vp.S(0.02));
                if (wy + wh > bottom) continue;
                var lit = r.Chance(band.Tone < 0.3 ? 0.18 : 0.32);
                if (lit)
                {
                    Paint.Glow(canvas, wx + ww / 2, wy + wh / 2, ww * 2.6f, Paint.Sk(c.WindowGlow), 0.35);
                    using var wp = Paint.Fill(Paint.Sk(windowColor));
                    canvas.DrawRoundRect(wx, wy, ww, wh, ww * 0.15f, ww * 0.15f, wp);
                }
                else if (band.Tone > 0.3)
                {
                    using var wp = Paint.Fill(Paint.Sk(unlit, 0.7));
                    canvas.DrawRoundRect(wx, wy, ww, wh, ww * 0.15f, ww * 0.15f, wp);
                }
            }
        }
    }

    /// <summary>The water plane: depth ramp, the horizon's reflected light, mirrored
    /// reflections of everything standing on the far shore (broken into horizontal
    /// ripples), and the moonglade — a column of glints under the moon that widens toward
    /// the viewer (glitter-path geometry: calm water, a path about as wide as the moon at
    /// the horizon).</summary>
    public static void PaintWater(SKCanvas canvas, PosterPlan plan, WaterPlan water, PosterViewport vp,
        IReadOnlyList<TerrainBand> reflected)
    {
        var c = plan.Colors;
        var line = vp.Y(water.Line);
        var bottom = vp.Height + 2f;
        var stops = new SKColor[5];
        var pos = new float[5];
        for (var i = 0; i < 5; i++)
        {
            var t = i / 4f;
            pos[i] = t;
            stops[i] = Oklab.Mix(c.WaterTop.ToOklab(), c.WaterDeep.ToOklab(), Math.Pow(t, 0.7)).ToSk();
        }
        using (var p = new SKPaint
               {
                   IsDither = true,
                   Shader = SKShader.CreateLinearGradient(new SKPoint(0, line), new SKPoint(0, bottom), stops, pos, SKShaderTileMode.Clamp),
               })
            canvas.DrawRect(-2, line, vp.Width + 4, bottom - line, p);

        // Reflections of the far shore, mirrored about the waterline, softened and broken.
        using (var reflection = new SKPaint())
        {
            canvas.SaveLayer(reflection);
            canvas.ClipRect(new SKRect(-2, line, vp.Width + 2, bottom));
            foreach (var band in reflected)
            {
                using var path = new SKPath();
                var step = Math.Max(1f, vp.Unit / 300f);
                path.MoveTo(-2, line);
                for (float px = -2; px <= vp.Width + 2; px += step)
                {
                    var y = vp.Y(band.Profile.Y(vp.NX(px)));
                    path.LineTo(px, line + Math.Max(0, (line - y) * 0.92f));
                }
                path.LineTo(vp.Width + 2, line);
                path.Close();
                var col = Oklab.Mix(BandColor(plan, band).ToOklab(), c.WaterDeep.ToOklab(), 0.4).ToOklch().Lighter(-0.03);
                using var rp = Paint.Fill(Paint.Sk(col, 0.62));
                canvas.DrawPath(path, rp);
            }
            // Ripples: thin horizontal gaps, denser and longer toward the viewer.
            var rr = new Rng((ulong)(uint)plan.Spec.Seed ^ 0x52495050UL);
            using var gap = new SKPaint { BlendMode = SKBlendMode.DstOut, IsAntialias = true, Color = SKColors.Black.WithAlpha(150) };
            var y0 = line + 1.5f;
            var gapStep = Math.Max(1.5f, vp.Unit * 0.006f);
            while (y0 < bottom)
            {
                var depthT = (y0 - line) / Math.Max(1, bottom - line);
                var count = 3 + rr.Int(4);
                for (var k = 0; k < count; k++)
                {
                    var gx = (float)rr.Range(-0.1, 1.1) * vp.Width;
                    var gl = vp.Unit * (float)rr.Range(0.04, 0.16) * (1 + depthT);
                    canvas.DrawRect(gx, y0, gl, Math.Max(0.7f, gapStep * 0.28f * (1 + depthT)), gap);
                }
                y0 += gapStep * (1 + depthT * 1.6f);
            }
            canvas.Restore();
        }

        // A faint bright seam where the sky meets the sea.
        using (var seam = new SKPaint
               {
                   IsDither = true,
                   Shader = SKShader.CreateLinearGradient(new SKPoint(0, line), new SKPoint(0, line + vp.S(0.03)),
                       [Paint.Sk(c.SkyHorizon.Lighter(0.04), 0.5), Paint.Sk(c.SkyHorizon, 0)], SKShaderTileMode.Clamp),
               })
            canvas.DrawRect(-2, line, vp.Width + 4, vp.S(0.03), seam);

        if (water.Glade && plan.Moon is { } moon) PaintGlade(canvas, plan, moon, line, bottom, vp);
    }

    private static void PaintGlade(SKCanvas canvas, PosterPlan plan, MoonPlan moon, float line, float bottom, PosterViewport vp)
    {
        var r = new Rng((ulong)(uint)plan.Spec.Seed ^ 0x474C414445UL);
        var mx = vp.X(moon.X);
        var mr = vp.S(moon.R);
        var strength = 0.85 * (1 - 0.5 * moon.Phase) * Math.Clamp(moon.Glow, 0.4, 1);
        var cream = Paint.Sk(ScenePalette.Cream);

        // Soft column of light under the moon.
        using (var col = new SKPaint
               {
                   IsDither = true,
                   MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, mr * 0.8f),
                   Shader = SKShader.CreateLinearGradient(new SKPoint(0, line), new SKPoint(0, bottom),
                       [cream.WithAlpha((byte)(46 * strength)), cream.WithAlpha((byte)(14 * strength))], SKShaderTileMode.Clamp),
               })
        {
            using var wedge = new SKPath();
            wedge.MoveTo(mx - mr * 0.6f, line);
            wedge.LineTo(mx + mr * 0.6f, line);
            wedge.LineTo(mx + mr * 2.6f, bottom);
            wedge.LineTo(mx - mr * 2.6f, bottom);
            wedge.Close();
            canvas.DrawPath(wedge, col);
        }

        // Glints: short horizontal dashes, perspective-spaced.
        using var dash = new SKPaint { IsAntialias = true };
        var rows = 26;
        for (var i = 0; i < rows; i++)
        {
            var t = MathF.Pow((i + (float)r.Next()) / rows, 1.5f);
            var y = line + 2 + t * (bottom - line);
            var spread = mr * (0.5f + 2.4f * t);
            var n = 1 + r.Int(3);
            for (var k = 0; k < n; k++)
            {
                var x = mx + (float)r.Bell() * spread;
                var len = mr * (0.25f + 1.1f * t) * (float)r.Range(0.5, 1.2);
                var th = Math.Max(0.7f, vp.Unit * (0.0018f + 0.004f * t));
                var a = strength * (0.75 - 0.45 * t) * r.Range(0.5, 1);
                dash.Color = cream.WithAlpha((byte)Math.Clamp(a * 255, 0, 255));
                canvas.DrawRoundRect(x - len / 2, y - th / 2, len, th, th / 2, th / 2, dash);
            }
        }
    }
}
