using SkiaSharp;
using StoryTime.Poster.Color;
using StoryTime.Poster.Composition;
using StoryTime.Poster.Core;

namespace StoryTime.Poster.Render;

/// <summary>Foreground framing and the warm-light props. The framing clusters are the
/// picture-book repoussoir: a darker, richly shaped corner of leaves, flowers, rocks or
/// snowy boughs that the eye looks past, which is what puts the hero in a place.</summary>
internal static partial class PropPainter
{
    /// <summary>Framing kits, one per scene family (the placement's Variant).</summary>
    public enum FrameKit { Meadow, Forest, Snow, Shore, Beach, Desert, Reef, Cloud, Roof, Jungle }

    /// <summary>A framing cluster anchored at the frame's bottom edge; +u points into the
    /// picture (the planner flips right-edge clusters).</summary>
    private static void FrameCluster(K k)
    {
        // The anchor sits on the frame edge; the kit is composed a little inward of it so
        // its tallest mass lands inside the picture, not in the bleed.
        var f = new Frame(k.X + k.S * 0.25f, k.Y, k.S);
        var r = k.R;
        var kit = (FrameKit)(k.P.Variant & 15);
        if (k.P.Variant >= 16)
        {
            // The hero-side cluster: compressed toward the edge so it rises without reaching in.
            k.C.Save();
            k.C.Scale(0.8f, 1, k.X, k.Y);
            FrameKitBody(k, f, r, kit);
            k.C.Restore();
            return;
        }
        FrameKitBody(k, f, r, kit);
    }

    private static void FrameKitBody(K k, Frame f, Rng r, FrameKit kit)
    {
        var c = k.Colors;
        var leafBase = kit switch
        {
            FrameKit.Snow => c.Foliage with { L = c.LandNear.L - 0.02, C = 0.05, H = 175 },
            FrameKit.Desert => c.Foliage with { H = 135, C = 0.06, L = c.LandNear.L - 0.02 },
            FrameKit.Reef => new Oklch(c.LandNear.L, 0.06, 170),
            _ => c.Foliage with { L = c.LandNear.L - 0.05 },
        };
        leafBase = leafBase with { L = Math.Clamp(leafBase.L - 0.06, 0.11, 0.24), C = Math.Min(0.09, leafBase.C + 0.015) };
        var rock = Oklab.Mix(c.LandNear.ToOklab(), new Oklch(0.32, 0.015, 60).ToOklab(), 0.5).ToOklch() with { L = Math.Max(0.16, c.LandNear.L - 0.02) };

        switch (kit)
        {
            case FrameKit.Cloud:
            {
                using var puffs = new SKPath();
                for (var i = 0; i < 6; i++)
                {
                    var u = -0.5 + i * 0.22 + r.Jitter(0.04);
                    var rad = 0.4 - i * 0.045 + r.Range(0, 0.06);
                    puffs.AddCircle(f.U(u), f.V(rad * 0.6 + (i < 2 ? 0.2 : 0.0)), f.L(Math.Max(0.08, rad)));
                }
                using var merged = puffs.Simplify() ?? new SKPath(puffs);
                var shade = Oklab.Mix(c.CloudShade.ToOklab(), c.CloudLit.ToOklab(), 0.25).ToOklch();
                Paint.Silhouette(k.C, merged, shade with { L = Math.Max(0.3, shade.L - 0.04) }, ScenePalette.Cream, 0.45, k.RimDx, k.RimDy * 1.5f, 0.06, 0.04);
                return;
            }
            case FrameKit.Roof:
            {
                // A window box on the near ledge: planter, trailing leaves and a few blooms.
                using var box = new SKPath();
                box.AddRoundRect(new SKRect(f.U(-0.6), f.V(0.3), f.U(0.62), f.V(0.0)), f.L(0.03), f.L(0.03));
                var wood = k.Colors.Grade(new Oklch(0.5, 0.07, 45));
                Coat(k, box, wood with { L = Math.Min(wood.L, 0.42) }, 0.8, 0.6);
                Foliage(k, f, r, leafBase, -0.55, 0.55, 0.28, 0.62, 9);
                Blooms(k, f, r, 5, -0.4, 0.5, 0.45, 0.7);
                return;
            }
            case FrameKit.Reef:
            {
                Kelps(k, f, r, leafBase, 3);
                var coral = k.Colors.Grade(new Oklch(0.66, 0.1, 28));
                using var clump = new SKPath();
                for (var i = 0; i < 7; i++)
                    clump.AddCircle(f.U(-0.4 + i * 0.16 + r.Jitter(0.03)), f.V(0.12 + r.Range(0, 0.18) * (1 - i / 8.0)), f.L(r.Range(0.1, 0.16)));
                using var mc = clump.Simplify() ?? new SKPath(clump);
                Coat(k, mc, coral, 0.8);
                using var polyps = new SKPath();
                for (var i = 0; i < 12; i++) polyps.AddCircle(f.U(r.Range(-0.45, 0.6)), f.V(r.Range(0.05, 0.36)), f.L(0.018));
                Mark(k, mc, polyps, coral.Lighter(0.12), 0.8, 0.01);
                return;
            }
        }

        // Rocks sit at the back of shore, beach and desert corners.
        if (kit is FrameKit.Shore or FrameKit.Beach or FrameKit.Desert)
        {
            foreach (var (u, w, h) in new[] { (-0.25, 0.6, 0.66), (0.32, 0.34, 0.32) })
            {
                using var stone = Blob(f, (u - w, 0.0), (u - w * 0.9, h * 0.6), (u - w * 0.3, h), (u + w * 0.4, h * 0.9), (u + w, h * 0.3), (u + w * 0.9, 0.0));
                Coat(k, stone, rock.Lighter(r.Jitter(0.02)), 0.7);
            }
        }

        switch (kit)
        {
            case FrameKit.Meadow:
                Foliage(k, f, r, leafBase, -0.7, 0.55, 0.0, 0.7, 16);
                Blades(k, f, r, leafBase.Lighter(0.02), 9, -0.3, 0.6, 0.95);
                Blooms(k, f, r, 5, -0.4, 0.45, 0.45, 0.85);
                break;
            case FrameKit.Forest:
                Foliage(k, f, r, leafBase, -0.7, 0.5, 0.0, 0.62, 14);
                Fronds(k, f, r, leafBase.Lighter(0.03), 5, 0.95);
                Toadstools(k, f, r);
                break;
            case FrameKit.Snow:
                Foliage(k, f, r, leafBase, -0.7, 0.5, 0.0, 0.55, 10);
                Boughs(k, f, r, leafBase);
                break;
            case FrameKit.Shore:
                Foliage(k, f, r, leafBase, -0.7, 0.55, 0.0, 0.36, 8);
                Blades(k, f, r, leafBase, 7, -0.1, 0.55, 0.9);
                Cattails(k, f, r, leafBase);
                break;
            case FrameKit.Beach:
            {
                Foliage(k, f, r, leafBase, -0.7, 0.5, 0.0, 0.34, 7);
                using var frond = Frond(f.U(-0.1), f.V(0.18), -0.45f, k.S * 1.05f, k.S * 0.14f, r);
                Coat(k, frond, leafBase, 0.6, 0.6);
                using var frond2 = Frond(f.U(-0.2), f.V(0.12), -0.9f, k.S * 0.8f, k.S * 0.12f, r);
                Coat(k, frond2, leafBase.Lighter(0.03), 0.6, 0.6);
                using var shell = Blob(f, (0.45, 0.02), (0.5, 0.12), (0.6, 0.12), (0.64, 0.02));
                Coat(k, shell, k.Colors.Grade(new Oklch(0.82, 0.06, 40)), 0.6);
                break;
            }
            case FrameKit.Desert:
                using (var dune = Blob(f, (-0.8, -0.05), (-0.6, 0.3), (0.1, 0.3), (0.7, -0.05)))
                    Coat(k, dune, k.Colors.LandNear.Lighter(-0.03), 0.3, 0.6);
                Rosette(k, f, r, leafBase.Lighter(0.04));
                Blooms(k, f, r, 3, -0.2, 0.3, 0.42, 0.58);
                break;
            case FrameKit.Jungle:
                Foliage(k, f, r, leafBase, -0.7, 0.55, 0.0, 0.66, 14);
                Fronds(k, f, r, leafBase.Lighter(0.02), 4, 1.0);
                Blooms(k, f, r, 3, -0.2, 0.4, 0.4, 0.7);
                break;
        }
    }

    /// <summary>A leafy mound: overlapping soft leaves along a rising contour, lighter
    /// toward the light.</summary>
    private static void Foliage(K k, Frame f, Rng r, Oklch col, double u0, double u1, double vBase, double vTop, int leaves)
    {
        using var mound = Blob(f, (u0 - 0.1, vBase - 0.05), (u0, vTop * 0.9), ((u0 + u1) / 2 - 0.15, vTop), (u1 - 0.1, vTop * 0.55), (u1 + 0.05, vBase - 0.05));
        Coat(k, mound, col, 0.4, 0.8);
        for (var i = 0; i < leaves; i++)
        {
            var t = (i + r.Next()) / leaves;
            var u = u0 + (u1 - u0) * t;
            var v = vBase + (vTop - vBase) * (1 - Math.Pow(Math.Abs(t - 0.35) / 0.75, 1.6)) + r.Jitter(0.05);
            var ang = r.Range(-2.6, -0.5);
            var len = r.Range(0.16, 0.26);
            using var leaf = Leaf(f.U(u), f.V(v - 0.06), f.U(u + Math.Cos(ang) * len), f.V(v - 0.06 - Math.Sin(ang) * len), f.L(0.055), f.L(r.Jitter(0.02)));
            Coat(k, leaf, col.Lighter(r.Range(0.02, 0.1)).ScaleC(1.15), 0.5, 0.6);
        }
    }

    private static void Blades(K k, Frame f, Rng r, Oklch col, int n, double u0, double u1, double height)
    {
        using var path = new SKPath();
        for (var i = 0; i < n; i++)
        {
            var bu = r.Range(u0, u1);
            var h = height * r.Range(0.55, 1.0) * (1 - Math.Max(0, bu - 0.2) * 0.6);
            var lean = r.Jitter(0.18);
            using var blade = Leaf(f.U(bu), f.V(-0.02), f.U(bu + lean), f.V(h), f.L(0.022), f.L(lean * 0.12));
            path.AddPath(blade);
        }
        Coat(k, path, col, 0.3, 0.5);
    }

    /// <summary>A few flowers on fine stems in the palette's soft accents.</summary>
    private static void Blooms(K k, Frame f, Rng r, int n, double u0, double u1, double v0, double v1)
    {
        var hues = new[] { k.Colors.Accent2, new Oklch(0.86, 0.04, 85), new Oklch(0.74, 0.09, 350), k.Colors.Accent };
        for (var i = 0; i < n; i++)
        {
            var u = r.Range(u0, u1);
            var v = r.Range(v0, v1);
            using var stem = Paint.Stroke(Paint.Sk(k.Colors.Foliage with { L = 0.28 }, 0.9), Math.Max(0.6f, f.L(0.014)));
            k.C.DrawLine(f.U(u), f.V(v), f.U(u + r.Jitter(0.04)), f.V(v * 0.4), stem);
            var col = k.Colors.Grade(hues[r.Int(hues.Length)]);
            using var petals = new SKPath();
            var pr = r.Range(0.035, 0.05);
            for (var p = 0; p < 5; p++)
            {
                var a = p * Math.PI * 2 / 5 + r.Next();
                petals.AddCircle(f.U(u + Math.Cos(a) * pr), f.V(v + Math.Sin(a) * pr), f.L(pr * 0.85));
            }
            using var merged = petals.Simplify() ?? new SKPath(petals);
            Coat(k, merged, col, 0.5, 0.5);
            using var center = Paint.Fill(Paint.Sk(ScenePalette.AmberSoft, 0.95));
            k.C.DrawCircle(f.U(u), f.V(v), Math.Max(0.5f, f.L(pr * 0.45)), center);
        }
    }

    private static void Fronds(K k, Frame f, Rng r, Oklch col, int n, double height)
    {
        for (var i = 0; i < n; i++)
        {
            var ang = Math.PI * (0.62 - i * 0.22 / Math.Max(1, n - 1)) + r.Jitter(0.08);
            var len = height * r.Range(0.75, 1.0);
            var bu = r.Range(-0.25, 0.1);
            using var frond = new SKPath();
            var steps = 9;
            for (var j = 1; j <= steps; j++)
            {
                var t = j / (double)steps;
                var cu = bu + Math.Cos(ang) * len * t + 0.12 * t * t;
                var cv = Math.Sin(ang) * len * t - 0.15 * t * t;
                var w = 0.07 * (1 - t * 0.7);
                using var l1 = Leaf(f.U(cu), f.V(cv), f.U(cu + w), f.V(cv + w * 1.2), f.L(0.02), 0);
                using var l2 = Leaf(f.U(cu), f.V(cv), f.U(cu - w * 1.1), f.V(cv - w * 0.4), f.L(0.02), 0);
                frond.AddPath(l1);
                frond.AddPath(l2);
            }
            using var rib = new SKPath();
            rib.MoveTo(f.P(bu, 0));
            rib.QuadTo(f.P(bu + Math.Cos(ang) * len * 0.5, Math.Sin(ang) * len * 0.5), f.P(bu + Math.Cos(ang) * len + 0.12, Math.Sin(ang) * len - 0.15));
            using (var st = Paint.Stroke(SKColors.White, f.L(0.016))) AddStroke(frond, rib, st);
            Coat(k, frond, col.Lighter(r.Jitter(0.02)), 0.4, 0.5);
        }
    }

    private static void Toadstools(K k, Frame f, Rng r)
    {
        var cap = k.Colors.Grade(new Oklch(0.6, 0.13, 30));
        var stemCol = k.Colors.Grade(new Oklch(0.86, 0.03, 80));
        foreach (var (u, sz) in new[] { (0.42, 0.22), (0.58, 0.15) })
        {
            using var stem = Blob(f, (u - sz * 0.18, 0.0), (u - sz * 0.13, sz * 0.6), (u + sz * 0.13, sz * 0.6), (u + sz * 0.18, 0.0));
            Coat(k, stem, stemCol, 0.6);
            using var top = Blob(f, (u - sz * 0.5, sz * 0.52), (u - sz * 0.3, sz * 0.92), (u + sz * 0.3, sz * 0.92), (u + sz * 0.5, sz * 0.52));
            Coat(k, top, cap, 0.7);
            using var dots = new SKPath();
            for (var i = 0; i < 4; i++) dots.AddCircle(f.U(u + (i - 1.5) * sz * 0.2), f.V(sz * (0.7 + (i % 2) * 0.1)), f.L(sz * 0.05));
            Mark(k, top, dots, stemCol, 0.9, 0.01);
        }
    }

    private static void Boughs(K k, Frame f, Rng r, Oklch col)
    {
        // A snow-laden young fir at the edge, a drift and a sprig of red berries.
        Pine(k with { X = f.U(-0.05), Ink = col, P = k.P with { Variant = 2 } });
        using var drift = Blob(f, (-0.7, -0.05), (-0.55, 0.16), (-0.1, 0.22), (0.4, 0.14), (0.8, -0.05));
        Coat(k, drift, k.Colors.Snow.Lighter(0.04), 0.3, 0.6);
        using var sprig = Paint.Stroke(Paint.Sk(col.Lighter(0.04)), Math.Max(0.7f, f.L(0.014)));
        k.C.DrawLine(f.U(0.25), f.V(0.12), f.U(0.4), f.V(0.34), sprig);
        for (var i = 0; i < 3; i++)
        {
            using var leaf = Leaf(f.U(0.3 + i * 0.04), f.V(0.2 + i * 0.05), f.U(0.4 + i * 0.05), f.V(0.24 + i * 0.07), f.L(0.03), 0);
            Coat(k, leaf, col.Lighter(0.08), 0.4, 0.4);
        }
        using var berries = Paint.Fill(Paint.Sk(k.Colors.Grade(new Oklch(0.58, 0.16, 25))));
        for (var i = 0; i < 4; i++) k.C.DrawCircle(f.U(0.36 + r.Jitter(0.03)), f.V(0.3 + r.Jitter(0.03)), f.L(0.024), berries);
    }

    private static void Cattails(K k, Frame f, Rng r, Oklch col)
    {
        var head = k.Colors.Grade(new Oklch(0.42, 0.06, 45));
        for (var i = 0; i < 4; i++)
        {
            var u = r.Range(-0.15, 0.35);
            var h = r.Range(0.7, 1.05);
            using var stem = Paint.Stroke(Paint.Sk(col, 0.95), Math.Max(0.7f, f.L(0.016)));
            k.C.DrawLine(f.U(u), f.V(0), f.U(u + 0.02), f.V(h), stem);
            using var top = Oval(f, u + 0.02, h - 0.12, 0.028, 0.09);
            Coat(k, top, head, 0.5, 0.6);
        }
    }

    private static void Kelps(K k, Frame f, Rng r, Oklch col, int n)
    {
        for (var j = 0; j < n; j++)
        {
            var bu = -0.25 + j * 0.2;
            var h = r.Range(0.75, 1.05);
            var phase = r.Range(0, 6);
            using var ribbon = new SKPath();
            var left = new List<SKPoint>();
            var right = new List<SKPoint>();
            for (var i = 0; i <= 14; i++)
            {
                var t = i / 14.0;
                var cu = bu + Math.Sin(t * 4 + phase) * 0.06 * t;
                var w = 0.05 * (1 - t * 0.5);
                left.Add(f.P(cu - w, t * h));
                right.Add(f.P(cu + w, t * h));
            }
            ribbon.MoveTo(left[0]);
            foreach (var pt in left.Skip(1)) ribbon.LineTo(pt);
            for (var i = right.Count - 1; i >= 0; i--) ribbon.LineTo(right[i]);
            ribbon.Close();
            Coat(k, ribbon, col.Lighter(r.Jitter(0.03)), 0.4, 0.6);
        }
    }

    private static void Rosette(K k, Frame f, Rng r, Oklch col)
    {
        for (var i = 0; i < 9; i++)
        {
            var a = Math.PI * (0.1 + 0.8 * i / 8.0);
            var len = 0.45 * (1 - Math.Abs(i - 4) * 0.08);
            using var leaf = Leaf(f.U(0.12), f.V(0.05), f.U(0.12 + Math.Cos(a) * len), f.V(0.05 + Math.Sin(a) * len), f.L(0.06), 0);
            Coat(k, leaf, col.Lighter(r.Jitter(0.03)), 0.5, 0.6);
        }
    }

    // ------------------------------------------------------------------ warm lights

    private static void Campfire(K k)
    {
        var f = new Frame(k.X, k.Y, k.S);
        var log = k.Colors.Grade(new Oklch(0.38, 0.05, 45));
        var stone = Oklab.Mix(k.Colors.LandNear.ToOklab(), new Oklch(0.45, 0.01, 60).ToOklab(), 0.5).ToOklch();
        Paint.Glow(k.C, f.U(0), f.V(0.3), f.L(1.6), Paint.Sk(k.Colors.WindowGlow), 0.42);
        for (var i = 0; i < 7; i++)
        {
            var u = -0.5 + i / 6.0;
            using var s = Oval(f, u, 0.03 + Math.Sin(i * 0.9) * 0.01, 0.09, 0.06);
            Coat(k, s, stone.Lighter(i % 2 * 0.03), 0.6);
        }
        using var logs = new SKPath();
        logs.AddPath(Oval(f, -0.1, 0.1, 0.36, 0.06, 18));
        logs.AddPath(Oval(f, 0.1, 0.1, 0.36, 0.06, -18));
        Coat(k, logs, log, 0.7);
        // Flames: three nested soft teardrops, amber to cream.
        foreach (var (w, h, col, a) in new[] { (0.3, 0.85, k.Colors.Window, 0.95), (0.2, 0.62, ScenePalette.AmberSoft, 0.95), (0.1, 0.38, ScenePalette.Cream, 0.95) })
        {
            using var flame = Blob(f, (-w, 0.16), (-w * 0.7, h * 0.45), (0.02, h), (w * 0.75, h * 0.42), (w, 0.16), (0, 0.1));
            using var p = Paint.Fill(Paint.Sk(col, a));
            k.C.DrawPath(flame, p);
        }
        using var sparks = Paint.Fill(Paint.Sk(ScenePalette.AmberSoft, 0.8));
        var r = k.R;
        for (var i = 0; i < 4; i++) k.C.DrawCircle(f.U(r.Jitter(0.3)), f.V(r.Range(0.95, 1.3)), Math.Max(0.6f, f.L(0.02)), sparks);
    }

    /// <summary>A glowing sea anemone: the reef's warm lamp.</summary>
    private static void Anemone(K k)
    {
        var f = new Frame(k.X, k.Y, k.S);
        var body = new Oklch(0.78, 0.11, 55);
        Paint.Glow(k.C, f.U(0), f.V(0.5), f.L(1.5), Paint.Sk(k.Colors.WindowGlow), 0.45);
        using var foot = Blob(f, (-0.3, 0.0), (-0.22, 0.35), (0.22, 0.35), (0.3, 0.0));
        Coat(k, foot, k.Colors.Grade(new Oklch(0.6, 0.1, 30)), 0.6);
        for (var i = 0; i < 11; i++)
        {
            var a = Math.PI * (0.12 + 0.76 * i / 10.0);
            var len = 0.55 + 0.15 * Math.Sin(i * 1.7);
            using var line = new SKPath();
            line.MoveTo(f.P(Math.Cos(a) * 0.15, 0.33));
            line.QuadTo(f.P(Math.Cos(a) * len * 0.5, 0.33 + Math.Sin(a) * len * 0.7), f.P(Math.Cos(a) * len * 0.8 + 0.05 * Math.Sin(i), 0.33 + Math.Sin(a) * len));
            using var st = Paint.Stroke(Paint.Sk(body, 0.95), f.L(0.06));
            k.C.DrawPath(line, st);
            using var tip = Paint.Fill(Paint.Sk(ScenePalette.Cream, 0.95));
            k.C.DrawCircle(f.U(Math.Cos(a) * len * 0.8 + 0.05 * Math.Sin(i)), f.V(0.33 + Math.Sin(a) * len), f.L(0.04), tip);
        }
    }

    /// <summary>A small snowman with a warm scarf and a sleepy smile.</summary>
    private static void Snowman(K k)
    {
        var f = new Frame(k.X, k.Y, k.S);
        var snow = k.Colors.Snow.Lighter(0.16) with { C = 0.02 };
        var scarf = k.Colors.Grade(new Oklch(0.6, 0.15, 25));
        var coal = new Oklch(0.2, 0.01, 60);
        var stick = k.Colors.Grade(new Oklch(0.38, 0.05, 45));
        Contact(k, 0.36);
        using var arms = Paint.Stroke(Paint.Sk(stick), Math.Max(0.8f, f.L(0.022)));
        k.C.DrawLine(f.U(-0.18), f.V(0.5), f.U(-0.44), f.V(0.66), arms);
        k.C.DrawLine(f.U(-0.36), f.V(0.61), f.U(-0.4), f.V(0.72), arms);
        k.C.DrawLine(f.U(0.18), f.V(0.5), f.U(0.42), f.V(0.62), arms);
        using var bottom = Oval(f, 0, 0.2, 0.3, 0.21);
        Coat(k, bottom, snow, 0.7);
        using var middle = Oval(f, 0, 0.5, 0.22, 0.16);
        Coat(k, middle, snow, 0.7);
        using var head = Oval(f, 0, 0.75, 0.16, 0.14);
        Coat(k, head, snow, 0.7);
        using var hat = new SKPath();
        hat.AddRoundRect(new SKRect(f.U(-0.18), f.V(0.88), f.U(0.18), f.V(0.85)), f.L(0.015), f.L(0.015));
        hat.AddRoundRect(new SKRect(f.U(-0.11), f.V(1.0), f.U(0.11), f.V(0.86)), f.L(0.02), f.L(0.02));
        Coat(k, hat, scarf.Lighter(-0.18), 0.6);
        using var wrap = Blob(f, (-0.17, 0.66), (0.0, 0.62), (0.17, 0.66), (0.16, 0.6), (0.0, 0.56), (-0.16, 0.6));
        Coat(k, wrap, scarf, 0.6);
        using var tail = Blob(f, (0.08, 0.6), (0.16, 0.6), (0.2, 0.42), (0.12, 0.43));
        Coat(k, tail, scarf.Lighter(-0.04), 0.6);
        using var nose = Blob(f, (0.0, 0.76), (0.15, 0.735), (0.0, 0.71));
        using var np = Paint.Fill(Paint.Sk(k.Colors.Grade(new Oklch(0.7, 0.14, 55))));
        k.C.DrawPath(nose, np);
        SleepyEye(k, f, -0.06, 0.79, 0.03, coal);
        SleepyEye(k, f, 0.07, 0.79, 0.03, coal);
        Blush(k, f, -0.09, 0.73, 0.03);
        Blush(k, f, 0.1, 0.73, 0.03);
        using var buttons = Paint.Fill(Paint.Sk(coal));
        for (var i = 0; i < 3; i++) k.C.DrawCircle(f.U(0), f.V(0.52 - i * 0.12), Math.Max(0.6f, f.L(0.02)), buttons);
    }
}
