using SkiaSharp;
using StoryTime.Contracts;
using StoryTime.Poster.Color;
using StoryTime.Poster.Composition;
using StoryTime.Poster.Core;

namespace StoryTime.Poster.Render;

/// <summary>Parametric silhouette families. Every prop is built from a handful of curves
/// with seeded variation (tier counts, lean, proportions, which windows are lit), drawn as
/// a toned silhouette with a moonlit rim. Children's-book rules: round, friendly shapes;
/// creatures calm and at rest (sitting, curled, wading, drifting — never stalking); the
/// only warm light is small amber windows and lanterns.</summary>
internal static class PropPainter
{
    private readonly record struct K(
        SKCanvas C, PosterPlan Plan, Placement P, PosterViewport Vp, float X, float Y, float S, Rng R,
        Oklch Ink, Oklch RimColor, double RimAlpha, float RimDx, float RimDy)
    {
        public ScenePalette Colors => Plan.Colors;
        public void Sil(SKPath path, double topLift = 0.02, double baseDrop = 0.02) =>
            Paint.Silhouette(C, path, Ink, RimColor, RimAlpha, RimDx, RimDy, topLift, baseDrop);
        public void Flat(SKPath path, Oklch color, double alpha = 1)
        {
            using var p = Paint.Fill(Paint.Sk(color, alpha));
            C.DrawPath(path, p);
        }
    }

    public static double DefaultTone(int depth) => depth switch { 1 => 0.32, 2 => 0.64, _ => 1.0 };

    public static void Paint_(SKCanvas canvas, PosterPlan plan, Placement p, PosterViewport vp)
    {
        var tone = p.Tone >= 0 ? p.Tone : DefaultTone(p.Depth);
        var isCreature = p.Feature is { } f && Spec.PosterSpecRules.IsCreature(f);
        var ink = plan.Colors.Snowy && isCreature
            ? plan.Colors.LandNear with { L = plan.Colors.LandNear.L + 0.03 }
            : plan.Colors.Ground(Math.Min(1, tone + (isCreature ? 0.12 : 0.05)));
        if (plan.Colors.Snowy && !isCreature && p.Prop is not (Prop.House or Prop.Cabin or Prop.Fence))
            ink = plan.Colors.Ground(Math.Min(1, tone + 0.4)) with { L = plan.Colors.LandNear.L + 0.02 + (1 - tone) * 0.14 };
        if (plan.Underwater && p.Prop is Prop.BranchCoral or Prop.FanCoral or Prop.Kelp)
            ink = ink with { H = ScenePalette.HueLerp(ink.H, p.Prop == Prop.Kelp ? 160 : 10, 0.35), C = ink.C + 0.015 };

        var x = vp.X(p.X);
        var y = vp.Y(p.Y);
        var s = vp.S(p.Size);
        var lx = plan.Moon is { } m ? Math.Sign(m.X - p.X) : 0;
        var rw = Math.Max(1f, s * (isCreature ? 0.028f : 0.022f));
        var rimAlpha = plan.Colors.RimStrength * (isCreature ? 0.95 : 0.6) * (0.4 + 0.6 * tone);
        // Mirror: the rim must follow the moon, not the flip.
        var rimDx = -lx * rw * 0.75f * (p.Flip ? -1 : 1);
        var k = new K(canvas, plan, p, vp, x, y, s, new Rng(p.Seed), ink, plan.Colors.Rim, rimAlpha, rimDx, rw);

        canvas.Save();
        if (p.Flip) canvas.Scale(-1, 1, x, y);
        switch (p.Prop)
        {
            case Prop.Pine: Pine(k); break;
            case Prop.RoundTree: RoundTree(k); break;
            case Prop.Palm: Palm(k); break;
            case Prop.GrassTuft: GrassTuft(k); break;
            case Prop.Reeds: Reeds(k); break;
            case Prop.Rock: Rock(k); break;
            case Prop.Bush: RoundTree(k with { S = s * 0.5f }); break;
            case Prop.Fern: Fern(k); break;
            case Prop.Kelp: Kelp(k); break;
            case Prop.BranchCoral: BranchCoral(k); break;
            case Prop.FanCoral: FanCoral(k); break;
            case Prop.BigLeaf: BigLeaf(k); break;
            case Prop.Fence: Fence(k); break;
            case Prop.Mushroom: Mushroom(k); break;
            case Prop.Flower: Flower(k); break;
            case Prop.Vines: Vines(k); break;
            case Prop.Pool: Pool(k); break;
            case Prop.Trunk: Trunk(k); break;
            case Prop.Cabin: Cabin(k, cabin: true); break;
            case Prop.House: Cabin(k, cabin: false); break;
            case Prop.Castle: Castle(k); break;
            case Prop.Windmill: Windmill(k); break;
            case Prop.Tent: Tent(k); break;
            case Prop.Lantern: Lantern(k); break;
            case Prop.Telescope: Telescope(k); break;
            case Prop.Boat: Boat(k); break;
            case Prop.Sailboat: Sailboat(k); break;
            case Prop.Balloon: Balloon(k); break;
            case Prop.Train: Train(k); break;
            case Prop.Lighthouse: Lighthouse(k); break;
            case Prop.Fox: Fox(k); break;
            case Prop.FoxCurled: FoxCurled(k); break;
            case Prop.Rabbit: Rabbit(k); break;
            case Prop.Owl: Owl(k); break;
            case Prop.Deer: Deer(k); break;
            case Prop.Bear: Bear(k); break;
            case Prop.Cat: Cat(k); break;
            case Prop.Hedgehog: Hedgehog(k); break;
            case Prop.Otter: Otter(k); break;
            case Prop.Whale: Whale(k); break;
            case Prop.Turtle: Turtle(k); break;
            case Prop.FishSchool: FishSchool(k); break;
            case Prop.Camel: Camel(k); break;
            case Prop.Dinosaur: Dinosaur(k); break;
            case Prop.Birds: Birds(k); break;
        }
        canvas.Restore();
    }

    // =================================================================== helpers

    private static void Shadow(K k, float halfWidth, float offsetX = 0)
    {
        using var p = new SKPaint
        {
            IsAntialias = true,
            Color = SKColors.Black.WithAlpha(46),
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, Math.Max(0.8f, k.S * 0.04f)),
        };
        k.C.DrawOval(k.X + offsetX, k.Y, halfWidth, k.S * 0.035f, p);
    }

    private static void Window(K k, float x, float y, float w, float h, bool lit)
    {
        if (lit)
        {
            Paint.Glow(k.C, x + w / 2, y + h / 2, Math.Max(w, h) * 2.4f, Paint.Sk(k.Colors.WindowGlow), 0.42);
            using var wp = Paint.Fill(Paint.Sk(k.Colors.Window));
            k.C.DrawRoundRect(x, y, w, h, w * 0.18f, w * 0.18f, wp);
            using var bar = Paint.Stroke(Paint.Sk(k.Colors.Window.Lighter(-0.22), 0.7), Math.Max(0.6f, w * 0.1f), SKStrokeCap.Butt);
            k.C.DrawLine(x + w / 2, y, x + w / 2, y + h, bar);
            k.C.DrawLine(x, y + h / 2, x + w, y + h / 2, bar);
        }
        else
        {
            using var wp = Paint.Fill(Paint.Sk(k.Ink.Lighter(0.06), 0.8));
            k.C.DrawRoundRect(x, y, w, h, w * 0.18f, w * 0.18f, wp);
        }
    }

    /// <summary>Appends the outline of a stroked path. (GetFillPath REPLACES its destination,
    /// which once erased a deer's body behind its antlers and a cat behind its tail.)</summary>
    private static void AddStroke(SKPath dst, SKPath src, SKPaint stroke)
    {
        using var outline = new SKPath();
        stroke.GetFillPath(src, outline);
        dst.AddPath(outline);
    }

    private static SKPath Leaf(float x0, float y0, float x1, float y1, float width, float bend)
    {
        var path = new SKPath();
        var mx = (x0 + x1) / 2;
        var my = (y0 + y1) / 2;
        var dx = x1 - x0;
        var dy = y1 - y0;
        var len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 0.01f) return path;
        var nx = -dy / len;
        var ny = dx / len;
        path.MoveTo(x0, y0);
        path.QuadTo(mx + nx * (width + bend), my + ny * (width + bend), x1, y1);
        path.QuadTo(mx - nx * (width - bend), my - ny * (width - bend), x0, y0);
        path.Close();
        return path;
    }

    // =================================================================== flora

    private static void Pine(K k)
    {
        var (x, y, s, r) = (k.X, k.Y, k.S, k.R);
        var snowy = k.P.Variant >= 2;
        var tiers = 4 + r.Int(3);
        var width = s * (float)r.Range(0.42, 0.52);
        var lean = (float)r.Jitter(0.03) * s;
        using var path = new SKPath();
        // Trunk
        path.AddRect(new SKRect(x - s * 0.025f, y - s * 0.14f, x + s * 0.025f, y + s * 0.02f));
        var top = y - s;
        var bottom = y - s * 0.1f;
        var snow = new List<SKPath>();
        for (var i = 0; i < tiers; i++)
        {
            var t0 = (float)i / tiers;
            var t1 = (float)(i + 1.35f) / tiers;
            var yb = bottom - (bottom - top) * t0;
            var yt = bottom - (bottom - top) * Math.Min(1f, t1);
            var w = width * (1 - t0 * 0.82f) * (float)r.Range(0.92, 1.08);
            var cx = x + lean * t0;
            var droop = s * 0.03f;
            path.MoveTo(cx - w / 2, yb);
            path.QuadTo(cx - w * 0.22f, yb - droop * 0.4f, cx, yt);
            path.QuadTo(cx + w * 0.22f, yb - droop * 0.4f, cx + w / 2, yb);
            path.QuadTo(cx, yb - droop, cx - w / 2, yb);
            path.Close();
            if (snowy)
            {
                var sp = new SKPath();
                sp.MoveTo(cx - w * 0.42f, yb - droop * 0.2f);
                sp.QuadTo(cx - w * 0.2f, yb - (yb - yt) * 0.35f, cx, yt + (yb - yt) * 0.1f);
                sp.QuadTo(cx - w * 0.05f, yb - (yb - yt) * 0.25f, cx - w * 0.42f, yb - droop * 0.2f);
                sp.Close();
                snow.Add(sp);
            }
        }
        k.Sil(path);
        foreach (var sp in snow)
        {
            k.Flat(sp, k.Colors.Snow.Lighter(-0.04), 0.8);
            sp.Dispose();
        }
    }

    private static void RoundTree(K k)
    {
        var (x, y, s, r) = (k.X, k.Y, k.S, k.R);
        using var path = new SKPath();
        // Tapered trunk with a gentle lean and a forked top.
        var lean = (float)r.Jitter(0.05) * s;
        path.MoveTo(x - s * 0.05f, y + s * 0.02f);
        path.QuadTo(x - s * 0.03f, y - s * 0.3f, x - s * 0.02f + lean, y - s * 0.52f);
        path.LineTo(x + s * 0.02f + lean, y - s * 0.52f);
        path.QuadTo(x + s * 0.03f, y - s * 0.3f, x + s * 0.05f, y + s * 0.02f);
        path.Close();
        var cx = x + lean;
        var cy = y - s * 0.68f;
        var n = 7 + r.Int(4);
        for (var i = 0; i < n; i++)
        {
            var a = Math.PI * (1.05 + 0.9 * i / (n - 1));
            var rad = s * (float)r.Range(0.13, 0.2);
            var dist = s * (float)r.Range(0.16, 0.24);
            path.AddCircle(cx + (float)Math.Cos(a) * dist, cy + (float)Math.Sin(a) * dist * 0.75f + s * 0.06f, rad);
        }
        path.AddCircle(cx, cy - s * 0.02f, s * 0.24f);
        path.AddCircle(cx - s * 0.16f, cy + s * 0.08f, s * 0.17f);
        path.AddCircle(cx + s * 0.17f, cy + s * 0.07f, s * 0.16f);
        k.Sil(path, 0.03, 0.03);
    }

    private static void Palm(K k)
    {
        var (x, y, s, r) = (k.X, k.Y, k.S, k.R);
        var bend = s * (float)r.Range(0.12, 0.22);
        var topX = x + bend;
        var topY = y - s;
        using var path = new SKPath();
        // Ringed, gently curving trunk.
        path.MoveTo(x - s * 0.04f, y + s * 0.02f);
        path.QuadTo(x + bend * 0.15f - s * 0.032f, y - s * 0.55f, topX - s * 0.02f, topY);
        path.LineTo(topX + s * 0.02f, topY);
        path.QuadTo(x + bend * 0.15f + s * 0.032f, y - s * 0.55f, x + s * 0.04f, y + s * 0.02f);
        path.Close();
        var n = 7 + r.Int(3);
        for (var i = 0; i < n; i++)
        {
            var u = (i + 0.5) / n;
            var theta = (float)(Math.PI * (-1.02 + 1.04 * u) + r.Jitter(0.08));
            var len = s * (float)r.Range(0.36, 0.48) * (1 - 0.25f * MathF.Abs(MathF.Sin(theta + MathF.PI / 2)) * 0);
            using var frond = Frond(topX, topY, theta, len, s * 0.075f, r);
            path.AddPath(frond);
        }
        path.AddCircle(topX - s * 0.025f, topY + s * 0.035f, s * 0.032f);
        path.AddCircle(topX + s * 0.025f, topY + s * 0.04f, s * 0.03f);
        k.Sil(path);
    }

    /// <summary>A feathered palm frond: an arching midrib whose leaflets droop with gravity,
    /// built as one serrated outline.</summary>
    private static SKPath Frond(float tx, float ty, float theta, float len, float width, Rng r)
    {
        var dx = MathF.Cos(theta);
        var dy = MathF.Sin(theta);
        var ex = tx + dx * len;
        var ey = ty + dy * len * 0.6f + len * (0.1f + 0.2f * MathF.Abs(dx));
        var cx = tx + dx * len * 0.55f;
        var cy = ty + dy * len * 0.7f - len * 0.08f;
        const int steps = 14;
        var left = new List<SKPoint>();
        var right = new List<SKPoint>();
        for (var i = 0; i <= steps; i++)
        {
            var t = (float)i / steps;
            var px = (1 - t) * (1 - t) * tx + 2 * (1 - t) * t * cx + t * t * ex;
            var py = (1 - t) * (1 - t) * ty + 2 * (1 - t) * t * cy + t * t * ey;
            var tdx = 2 * (1 - t) * (cx - tx) + 2 * t * (ex - cx);
            var tdy = 2 * (1 - t) * (cy - ty) + 2 * t * (ey - cy);
            var tl = MathF.Max(0.001f, MathF.Sqrt(tdx * tdx + tdy * tdy));
            var nx = -tdy / tl;
            var ny = tdx / tl;
            var w = width * MathF.Pow(MathF.Max(0, MathF.Sin(MathF.PI * MathF.Min(1, t * 1.15f))), 0.8f) * (i % 2 == 0 ? 1f : 0.55f);
            // Leaflets hang: the lower edge reaches further down than the upper edge.
            left.Add(new SKPoint(px + nx * w * 0.6f, py + ny * w * 0.6f));
            right.Add(new SKPoint(px - nx * w + 0, py - ny * w + w * 0.6f));
        }
        var path = new SKPath();
        path.MoveTo(left[0]);
        foreach (var pt in left.Skip(1)) path.LineTo(pt);
        for (var i = right.Count - 1; i >= 0; i--) path.LineTo(right[i]);
        path.Close();
        _ = r;
        return path;
    }

    private static void GrassTuft(K k)
    {
        var (x, y, s, r) = (k.X, k.Y, k.S, k.R);
        using var path = new SKPath();
        var n = 5 + r.Int(5);
        for (var i = 0; i < n; i++)
        {
            var bx = x + (float)r.Jitter(0.18) * s;
            var h = s * (float)r.Range(0.5, 1.0);
            var lean = (float)r.Jitter(0.35) * s;
            var w = s * 0.035f;
            path.MoveTo(bx - w, y + s * 0.05f);
            path.QuadTo(bx - w * 0.5f + lean * 0.3f, y - h * 0.5f, bx + lean, y - h);
            path.QuadTo(bx + w * 0.5f + lean * 0.3f, y - h * 0.5f, bx + w, y + s * 0.05f);
            path.Close();
        }
        k.Sil(path, 0.0, 0.0);
    }

    private static void Reeds(K k)
    {
        var (x, y, s, r) = (k.X, k.Y, k.S, k.R);
        using var path = new SKPath();
        var n = 4 + r.Int(3);
        for (var i = 0; i < n; i++)
        {
            var bx = x + (float)r.Jitter(0.2) * s;
            var h = s * (float)r.Range(0.6, 1.0);
            var lean = (float)r.Jitter(0.15) * s;
            var w = s * 0.012f;
            path.MoveTo(bx - w, y + s * 0.05f);
            path.QuadTo(bx + lean * 0.4f, y - h * 0.5f, bx + lean - w, y - h);
            path.LineTo(bx + lean + w, y - h);
            path.QuadTo(bx + lean * 0.4f + w * 2, y - h * 0.5f, bx + w, y + s * 0.05f);
            path.Close();
            if (r.Chance(0.6))
                path.AddOval(new SKRect(bx + lean * 0.9f - s * 0.022f, y - h * 0.88f, bx + lean * 0.9f + s * 0.022f, y - h * 0.68f));
        }
        for (var i = 0; i < 3; i++)
        {
            var bx = x + (float)r.Jitter(0.2) * s;
            var tip = new SKPoint(bx + (float)r.Jitter(0.4) * s, y - s * (float)r.Range(0.35, 0.6));
            using var blade = Leaf(bx, y + s * 0.04f, tip.X, tip.Y, s * 0.02f, s * 0.02f);
            path.AddPath(blade);
        }
        k.Sil(path, 0, 0);
    }

    private static void Rock(K k)
    {
        var (x, y, s, r) = (k.X, k.Y, k.S, k.R);
        using var path = new SKPath();
        var n = 9;
        var pts = new List<SKPoint>();
        for (var i = 0; i <= n; i++)
        {
            var a = Math.PI + Math.PI * i / n;
            var rad = s * (float)r.Range(0.4, 0.55);
            pts.Add(new SKPoint(x + (float)Math.Cos(a) * rad * 1.3f, y + (float)Math.Sin(a) * rad * 0.7f + s * 0.06f));
        }
        path.MoveTo(pts[0]);
        for (var i = 1; i < pts.Count; i++)
        {
            var mid = new SKPoint((pts[i - 1].X + pts[i].X) / 2, (pts[i - 1].Y + pts[i].Y) / 2);
            path.QuadTo(pts[i - 1], mid);
        }
        path.LineTo(pts[^1]);
        path.LineTo(x + s * 0.7f, y + s * 0.2f);
        path.LineTo(x - s * 0.7f, y + s * 0.2f);
        path.Close();
        k.Sil(path, 0.04, 0.02);
    }

    private static void Fern(K k)
    {
        var (x, y, s, r) = (k.X, k.Y, k.S, k.R);
        using var path = new SKPath();
        var n = 5 + r.Int(3);
        for (var i = 0; i < n; i++)
        {
            var ang = (float)(-Math.PI / 2 + (i - (n - 1) / 2.0) / n * 2.4 + r.Jitter(0.1));
            var len = s * (float)r.Range(0.7, 1.0);
            var ex = x + MathF.Cos(ang) * len;
            var ey = y + MathF.Sin(ang) * len * 0.8f + len * 0.2f;
            using var leaf = Leaf(x, y + s * 0.03f, ex, ey, s * 0.08f, s * 0.05f * MathF.Sign(MathF.Cos(ang)));
            path.AddPath(leaf);
        }
        k.Sil(path, 0.02, 0);
    }

    private static void Kelp(K k)
    {
        var (x, y, s, r) = (k.X, k.Y, k.S, k.R);
        using var path = new SKPath();
        var strands = 2 + r.Int(3);
        for (var j = 0; j < strands; j++)
        {
            var bx = x + (float)r.Jitter(0.12) * s;
            var h = s * (float)r.Range(0.7, 1.0);
            var phase = (float)r.Range(0, 6);
            var w = s * 0.03f;
            var steps = 18;
            var left = new List<SKPoint>();
            var right = new List<SKPoint>();
            for (var i = 0; i <= steps; i++)
            {
                var t = (float)i / steps;
                var sway = MathF.Sin(t * 5 + phase) * s * 0.06f * t;
                var cx = bx + sway;
                var cy = y + s * 0.03f - t * h;
                var ww = w * (1 - t * 0.6f);
                left.Add(new SKPoint(cx - ww, cy));
                right.Add(new SKPoint(cx + ww, cy));
                if (i % 3 == 2)
                {
                    using var leaf = Leaf(cx, cy, cx + (i % 2 == 0 ? 1 : -1) * s * 0.12f, cy - s * 0.06f, s * 0.025f, 0);
                    path.AddPath(leaf);
                }
            }
            path.MoveTo(left[0]);
            foreach (var pt in left.Skip(1)) path.LineTo(pt);
            for (var i = right.Count - 1; i >= 0; i--) path.LineTo(right[i]);
            path.Close();
        }
        k.Sil(path, 0.03, 0);
    }

    private static void BranchCoral(K k)
    {
        var (x, y, s, r) = (k.X, k.Y, k.S, k.R);
        using var stroke = Paint.Stroke(SKColors.White, s * 0.07f);
        using var path = new SKPath();
        void Branch(float bx, float by, float ang, float len, int depth)
        {
            var ex = bx + MathF.Cos(ang) * len;
            var ey = by + MathF.Sin(ang) * len;
            path.MoveTo(bx, by);
            path.QuadTo(bx + MathF.Cos(ang + 0.3f) * len * 0.5f, by + MathF.Sin(ang + 0.3f) * len * 0.5f, ex, ey);
            if (depth == 0) return;
            Branch(ex, ey, ang - 0.45f + (float)r.Jitter(0.15), len * 0.72f, depth - 1);
            Branch(ex, ey, ang + 0.45f + (float)r.Jitter(0.15), len * 0.72f, depth - 1);
        }
        Branch(x, y + s * 0.03f, -MathF.PI / 2 + (float)r.Jitter(0.15), s * 0.38f, 3);
        using var outline = new SKPath();
        stroke.StrokeWidth = s * 0.075f;
        stroke.GetFillPath(path, outline);
        k.Sil(outline, 0.04, 0);
    }

    private static void FanCoral(K k)
    {
        var (x, y, s, r) = (k.X, k.Y, k.S, k.R);
        using var fan = new SKPath();
        fan.MoveTo(x, y + s * 0.02f);
        fan.CubicTo(x - s * 0.7f, y - s * 0.3f, x - s * 0.5f, y - s * 1.0f, x, y - s * 0.95f);
        fan.CubicTo(x + s * 0.5f, y - s * 1.0f, x + s * 0.7f, y - s * 0.3f, x, y + s * 0.02f);
        fan.Close();
        k.Sil(fan, 0.03, 0.02);
        using var veins = new SKPath();
        for (var i = 0; i < 9; i++)
        {
            var a = -MathF.PI / 2 + (i - 4) * 0.17f + (float)r.Jitter(0.04);
            veins.MoveTo(x, y);
            veins.QuadTo(x + MathF.Cos(a) * s * 0.4f, y + MathF.Sin(a) * s * 0.5f, x + MathF.Cos(a) * s * 0.55f, y + MathF.Sin(a) * s * 0.9f);
        }
        // Lace: lighter veins over the opaque fan.
        using var lace = Paint.Stroke(Paint.Sk(k.Ink.Lighter(0.06), 0.8), Math.Max(0.7f, s * 0.018f));
        k.C.Save();
        k.C.ClipPath(fan, SKClipOperation.Intersect, true);
        k.C.DrawPath(veins, lace);
        k.C.Restore();
        // A short stem rooting it in the reef.
        using var stem = Paint.Fill(Paint.Sk(k.Ink.Lighter(-0.02)));
        k.C.DrawRect(x - s * 0.025f, y - s * 0.06f, s * 0.05f, s * 0.1f, stem);
    }

    /// <summary>Variant 0/2: a fan of broad jungle leaves rising from the bottom edge
    /// (monstera-slit / banana); variant 1: a palm frond reaching in from the side edge.</summary>
    private static void BigLeaf(K k)
    {
        var (x, y, s, r) = (k.X, k.Y, k.S, k.R);
        if (k.P.Variant == 1)
        {
            using var frond = Frond(x, y - s * 0.1f, MathF.PI * 0.94f, s * 1.0f, s * 0.12f, r);
            k.Sil(frond, 0.02, 0);
            return;
        }
        var n = 3 + r.Int(2);
        for (var i = 0; i < n; i++)
        {
            var u = n == 1 ? 0.5f : (float)i / (n - 1);
            var angle = -100f + u * 62f + (float)r.Jitter(6);    // degrees from +x; fans up-right
            var len = s * (float)r.Range(0.72, 1.0) * (1 - MathF.Abs(u - 0.4f) * 0.25f);
            var width = len * (k.P.Variant == 2 ? 0.26f : 0.36f);
            using var leaf = new SKPath();
            // Petiole then a broad blade with a pointed tip, along +x before rotation.
            leaf.MoveTo(0, -s * 0.012f);
            leaf.LineTo(len * 0.18f, -s * 0.01f);
            leaf.CubicTo(len * 0.3f, -width * 0.9f, len * 0.78f, -width * 0.7f, len, -width * 0.05f);
            leaf.CubicTo(len * 0.8f, width * 0.55f, len * 0.32f, width * 0.75f, len * 0.18f, s * 0.01f);
            leaf.LineTo(0, s * 0.012f);
            leaf.Close();
            var m = SKMatrix.CreateRotationDegrees(angle).PostConcat(SKMatrix.CreateTranslation(x + (float)r.Jitter(0.05) * s, y + s * 0.05f));
            leaf.Transform(m);
            var tone = k.Ink.Lighter(r.Jitter(0.015) - u * 0.01);
            Paint.Silhouette(k.C, leaf, tone, k.RimColor, k.RimAlpha * 0.8, k.RimDx, k.RimDy, 0.025, 0.02);
            // Midrib + veins (or monstera slits), a shade darker.
            using var veins = new SKPath();
            veins.MoveTo(len * 0.18f, 0);
            veins.QuadTo(len * 0.6f, -width * 0.12f, len * 0.97f, -width * 0.05f);
            var slits = k.P.Variant == 2 ? 6 : 4;
            for (var j = 1; j <= slits; j++)
            {
                var t = 0.22f + j * 0.7f / (slits + 1);
                var bx = len * t;
                var by = -width * 0.1f * t;
                veins.MoveTo(bx, by);
                veins.LineTo(bx + len * 0.1f, by - width * 0.55f);
                veins.MoveTo(bx, by);
                veins.LineTo(bx + len * 0.1f, by + width * 0.5f);
            }
            veins.Transform(m);
            using var vp = Paint.Stroke(Paint.Sk(tone.Lighter(-0.035), 0.9), Math.Max(0.8f, s * (k.P.Variant == 2 ? 0.008f : 0.014f)));
            k.C.Save();
            k.C.ClipPath(leaf, SKClipOperation.Intersect, true);
            k.C.DrawPath(veins, vp);
            k.C.Restore();
        }
    }

    private static void Fence(K k)
    {
        var (x, y, s) = (k.X, k.Y, k.S);
        using var path = new SKPath();
        var posts = 4;
        var h = s * 0.32f;
        for (var i = 0; i < posts; i++)
        {
            var px = x + i * s / (posts - 1);
            path.AddRoundRect(new SKRect(px - s * 0.018f, y - h, px + s * 0.018f, y + s * 0.02f), s * 0.006f, s * 0.006f);
        }
        path.AddRect(new SKRect(x - s * 0.01f, y - h * 0.78f, x + s * 1.01f, y - h * 0.68f));
        path.AddRect(new SKRect(x - s * 0.01f, y - h * 0.42f, x + s * 1.01f, y - h * 0.32f));
        k.Sil(path, 0.02, 0);
        if (k.Colors.Snowy)
        {
            using var caps = new SKPath();
            for (var i = 0; i < posts; i++)
            {
                var px = x + i * s / (posts - 1);
                caps.AddOval(new SKRect(px - s * 0.028f, y - h - s * 0.018f, px + s * 0.028f, y - h + s * 0.012f));
            }
            caps.AddRect(new SKRect(x, y - h * 0.8f, x + s, y - h * 0.76f));
            k.Flat(caps, k.Colors.Snow, 0.9);
        }
    }

    private static void Mushroom(K k)
    {
        var (x, y, s) = (k.X, k.Y, k.S);
        using var stem = new SKPath();
        stem.MoveTo(x - s * 0.12f, y + s * 0.02f);
        stem.QuadTo(x - s * 0.1f, y - s * 0.4f, x - s * 0.07f, y - s * 0.55f);
        stem.LineTo(x + s * 0.07f, y - s * 0.55f);
        stem.QuadTo(x + s * 0.1f, y - s * 0.4f, x + s * 0.12f, y + s * 0.02f);
        stem.Close();
        using var cap = new SKPath();
        cap.MoveTo(x - s * 0.42f, y - s * 0.5f);
        cap.CubicTo(x - s * 0.4f, y - s * 1.0f, x + s * 0.4f, y - s * 1.0f, x + s * 0.42f, y - s * 0.5f);
        cap.QuadTo(x, y - s * 0.6f, x - s * 0.42f, y - s * 0.5f);
        cap.Close();
        if (k.P.Lit)
        {
            var glow = new Oklch(0.82, 0.08, 175);
            Paint.Glow(k.C, x, y - s * 0.7f, s * 1.4f, Paint.Sk(glow), 0.28);
            k.Sil(stem);
            k.Flat(cap, glow.Lighter(-0.12), 0.95);
        }
        else
        {
            stem.AddPath(cap);
            k.Sil(stem);
        }
    }

    private static void Flower(K k)
    {
        var (x, y, s, r) = (k.X, k.Y, k.S, k.R);
        using var stem = new SKPath();
        var top = new SKPoint(x + (float)r.Jitter(0.15) * s, y - s);
        stem.MoveTo(x, y + s * 0.05f);
        stem.QuadTo(x, y - s * 0.5f, top.X, top.Y);
        using var stroke = Paint.Stroke(SKColors.White, Math.Max(0.7f, s * 0.05f));
        using var fill = new SKPath();
        stroke.GetFillPath(stem, fill);
        k.Sil(fill, 0, 0);
        var hue = r.Pick(new[] { 330.0, 285, 60, 20 });
        var petal = new Oklch(0.72, 0.06, hue);
        using var petals = new SKPath();
        for (var i = 0; i < 5; i++)
        {
            var a = i * MathF.PI * 2 / 5;
            petals.AddCircle(top.X + MathF.Cos(a) * s * 0.12f, top.Y + MathF.Sin(a) * s * 0.12f, s * 0.1f);
        }
        k.Flat(petals, petal, 0.85);
        using var center = Paint.Fill(Paint.Sk(ScenePalette.AmberSoft, 0.9));
        k.C.DrawCircle(top.X, top.Y, s * 0.06f, center);
    }

    private static void Vines(K k)
    {
        var (x, s, r) = (k.X, k.S, k.R);
        var vp = k.Vp;
        using var path = new SKPath();
        var top = vp.Y(-0.02) - 4;
        var n = 2 + r.Int(2);
        for (var i = 0; i < n; i++)
        {
            var vx = x + (float)r.Jitter(0.08) * vp.Unit;
            var len = s * (float)r.Range(0.6, 1.0);
            using var line = new SKPath();
            line.MoveTo(vx, top);
            line.CubicTo(vx + s * 0.08f, top + len * 0.3f, vx - s * 0.08f, top + len * 0.6f, vx + (float)r.Jitter(0.05) * s, top + len);
            using var stroke = Paint.Stroke(SKColors.White, Math.Max(0.8f, s * 0.012f));
            AddStroke(path, line, stroke);
            for (var j = 1; j < 8; j++)
            {
                var t = j / 8f;
                var ly = top + len * t;
                var side = j % 2 == 0 ? 1 : -1;
                using var leaf = Leaf(vx, ly, vx + side * s * 0.07f, ly + s * 0.03f, s * 0.022f, 0);
                path.AddPath(leaf);
            }
        }
        k.Sil(path, 0, 0);
    }

    private static void Pool(K k)
    {
        var (x, y, s) = (k.X, k.Y, k.S);
        var rect = new SKRect(x - s * 0.5f, y - s * 0.09f, x + s * 0.5f, y + s * 0.09f);
        using var bank = Paint.Fill(Paint.Sk(k.Ink.Lighter(-0.02)));
        k.C.DrawOval(new SKRect(rect.Left - s * 0.04f, rect.Top - s * 0.02f, rect.Right + s * 0.04f, rect.Bottom + s * 0.03f), bank);
        using var water = new SKPaint
        {
            IsAntialias = true,
            IsDither = true,
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, rect.Top), new SKPoint(0, rect.Bottom),
                [Paint.Sk(k.Colors.WaterTop.Lighter(0.05)), Paint.Sk(k.Colors.WaterDeep)], SKShaderTileMode.Clamp),
        };
        k.C.DrawOval(rect, water);
        if (k.Plan.Moon is not null)
        {
            using var glint = Paint.Fill(Paint.Sk(ScenePalette.Cream, 0.55));
            for (var i = 0; i < 3; i++)
                k.C.DrawRoundRect(x - s * (0.05f + i * 0.03f), y - s * 0.04f + i * s * 0.035f, s * (0.1f + i * 0.06f), Math.Max(0.8f, s * 0.008f), 1, 1, glint);
        }
    }

    /// <summary>Variant 0: a jungle tree (trunk + crown) rising from below the frame;
    /// variant 1: a brick chimney on the near roof, with a soft smoke wisp.</summary>
    private static void Trunk(K k)
    {
        var (x, y, s, r) = (k.X, k.Y, k.S, k.R);
        using var path = new SKPath();
        if (k.P.Variant == 1)
        {
            path.AddRect(new SKRect(x - s * 0.18f, y - s * 0.75f, x + s * 0.18f, y + s * 0.1f));
            path.AddRect(new SKRect(x - s * 0.22f, y - s * 0.85f, x + s * 0.22f, y - s * 0.72f));
            path.AddRect(new SKRect(x - s * 0.1f, y - s, x - s * 0.02f, y - s * 0.84f));
            path.AddRect(new SKRect(x + s * 0.04f, y - s * 0.96f, x + s * 0.12f, y - s * 0.84f));
            k.Sil(path, 0.02, 0);
            Smoke(k, x - s * 0.06f, y - s, s * 0.9f);
            return;
        }
        // A framing jungle trunk: from below the frame to above it, with two branch stubs.
        var bottom = k.Vp.Height + 4f;
        var top = -4f;
        var w = s * 0.11f;
        path.MoveTo(x - w * 1.3f, bottom);
        path.CubicTo(x - w * 0.9f, y, x - w * 1.1f, (y + top) / 2, x - w * 0.8f, top);
        path.LineTo(x + w * 0.8f, top);
        path.CubicTo(x + w * 1.1f, (y + top) / 2, x + w * 0.9f, y, x + w * 1.3f, bottom);
        path.Close();
        for (var i = 0; i < 2; i++)
        {
            var by = y - s * (0.55f + i * 0.5f);
            var dir = i == 0 ? 1 : -1;
            path.MoveTo(x, by);
            path.QuadTo(x + dir * s * 0.25f, by - s * 0.05f, x + dir * s * 0.42f, by - s * 0.2f);
            path.LineTo(x + dir * s * 0.42f, by - s * 0.17f);
            path.QuadTo(x + dir * s * 0.22f, by + s * 0.03f, x, by + s * 0.06f);
            path.Close();
        }
        k.Sil(path, 0.03, 0.02);
        _ = r;
    }

    private static void Smoke(K k, float x, float y, float s)
    {
        var col = Oklab.Mix(k.Colors.SkyMid.ToOklab(), ScenePalette.Cream.ToOklab(), 0.25).ToOklch();
        using var p = new SKPaint
        {
            IsAntialias = true,
            Color = Paint.Sk(col, 0.22),
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, Math.Max(1f, s * 0.05f)),
        };
        for (var i = 0; i < 5; i++)
        {
            var t = i / 4f;
            var px = x + MathF.Sin(t * 2.5f) * s * 0.12f + t * s * 0.25f * (k.P.Flip ? -1 : 1);
            var py = y - t * s * 0.7f;
            p.Color = Paint.Sk(col, 0.22 * (1 - t * 0.7));
            k.C.DrawCircle(px, py, s * (0.05f + t * 0.07f), p);
        }
    }

    // =================================================================== structures

    private static void Cabin(K k, bool cabin)
    {
        var (x, y, s, r) = (k.X, k.Y, k.S, k.R);
        var w = s * (cabin ? 1.05f : (float)r.Range(0.75, 0.95));
        var wallH = s * (cabin ? 0.48f : (float)r.Range(0.45, 0.6));
        var roofH = s * (k.P.Variant == 1 ? 0.5f : 0.36f);
        var left = x - w / 2;
        var wallTop = y - wallH;
        Shadow(k, w * 0.62f);
        using var path = new SKPath();
        path.AddRect(new SKRect(left, wallTop, left + w, y + s * 0.02f));
        var over = w * 0.08f;
        path.MoveTo(left - over, wallTop + s * 0.02f);
        path.LineTo(x, wallTop - roofH);
        path.LineTo(left + w + over, wallTop + s * 0.02f);
        path.Close();
        var chimneyX = x + w * (float)r.Range(0.18, 0.3);
        var hasChimney = cabin || r.Chance(0.6);
        if (hasChimney)
            path.AddRect(new SKRect(chimneyX - w * 0.06f, wallTop - roofH * 0.85f, chimneyX + w * 0.06f, wallTop - roofH * 0.2f));
        k.Sil(path, 0.03, 0.01);

        if (k.Colors.Snowy || (cabin && k.Plan.Spec.Scene == PosterScene.SnowyVillage))
        {
            using var snow = new SKPath();
            snow.MoveTo(left - over * 1.1f, wallTop + s * 0.01f);
            snow.LineTo(x, wallTop - roofH - s * 0.025f);
            snow.LineTo(left + w + over * 1.1f, wallTop + s * 0.01f);
            snow.LineTo(left + w + over * 0.6f, wallTop + s * 0.035f);
            snow.LineTo(x, wallTop - roofH + s * 0.04f);
            snow.LineTo(left - over * 0.6f, wallTop + s * 0.035f);
            snow.Close();
            k.Flat(snow, k.Colors.Snow, 0.95);
        }

        // Door + windows: warm when lit; at most two lit panes.
        var lit = k.P.Lit;
        var ww = w * 0.17f;
        var wh = wallH * 0.32f;
        var wy = wallTop + wallH * 0.24f;
        Window(k, left + w * 0.14f, wy, ww, wh, lit);
        Window(k, left + w * 0.69f, wy, ww, wh, lit && (cabin || r.Chance(0.5)));
        using var door = Paint.Fill(Paint.Sk(k.Ink.Lighter(-0.03)));
        k.C.DrawRoundRect(x - w * 0.08f, y - wallH * 0.58f, w * 0.16f, wallH * 0.58f + 1, w * 0.04f, w * 0.04f, door);
        if (cabin && lit && hasChimney) Smoke(k, chimneyX, wallTop - roofH * 0.85f, s * 0.9f);
    }

    private static void Castle(K k)
    {
        var (x, y, s, r) = (k.X, k.Y, k.S, k.R);
        using var path = new SKPath();
        var keepW = s * 0.42f;
        var keepH = s * 0.55f;
        path.AddRect(new SKRect(x - keepW / 2, y - keepH, x + keepW / 2, y + s * 0.03f));
        for (var i = 0; i < 4; i++)
        {
            var cx = x - keepW / 2 + keepW * (i + 0.5f) / 4;
            path.AddRect(new SKRect(cx - keepW * 0.06f, y - keepH - s * 0.05f, cx + keepW * 0.06f, y - keepH + 1));
        }
        void Tower(float tx, float th, float tw)
        {
            path.AddRect(new SKRect(tx - tw / 2, y - th, tx + tw / 2, y + s * 0.03f));
            path.MoveTo(tx - tw * 0.65f, y - th);
            path.LineTo(tx, y - th - tw * 1.6f);
            path.LineTo(tx + tw * 0.65f, y - th);
            path.Close();
            path.AddRect(new SKRect(tx - 0.6f, y - th - tw * 1.6f - s * 0.08f, tx + 0.6f, y - th - tw * 1.6f));
            path.MoveTo(tx + 0.6f, y - th - tw * 1.6f - s * 0.08f);
            path.LineTo(tx + s * 0.06f, y - th - tw * 1.6f - s * 0.065f);
            path.LineTo(tx + 0.6f, y - th - tw * 1.6f - s * 0.05f);
            path.Close();
        }
        Tower(x - keepW * 0.62f, s * 0.7f, s * 0.15f);
        Tower(x + keepW * 0.62f, s * 0.62f, s * 0.14f);
        Tower(x + (float)r.Jitter(0.05) * s, s * 0.9f, s * 0.12f);
        k.Sil(path, 0.03, 0.01);
        if (k.P.Lit)
        {
            Window(k, x - keepW * 0.25f, y - keepH * 0.6f, s * 0.04f, s * 0.07f, true);
            Window(k, x + keepW * 0.62f - s * 0.02f, y - s * 0.45f, s * 0.04f, s * 0.06f, r.Chance(0.7));
        }
    }

    private static void Windmill(K k)
    {
        var (x, y, s, r) = (k.X, k.Y, k.S, k.R);
        using var path = new SKPath();
        path.MoveTo(x - s * 0.16f, y + s * 0.02f);
        path.LineTo(x - s * 0.1f, y - s * 0.62f);
        path.LineTo(x + s * 0.1f, y - s * 0.62f);
        path.LineTo(x + s * 0.16f, y + s * 0.02f);
        path.Close();
        path.AddOval(new SKRect(x - s * 0.13f, y - s * 0.74f, x + s * 0.13f, y - s * 0.56f));
        var hub = new SKPoint(x, y - s * 0.66f);
        var rot = (float)r.Range(0.15, 0.6);
        for (var i = 0; i < 4; i++)
        {
            var a = rot + i * MathF.PI / 2;
            var dx = MathF.Cos(a);
            var dy = MathF.Sin(a);
            var nx = -dy;
            var ny = dx;
            var l0 = s * 0.06f;
            var l1 = s * 0.46f;
            var bw = s * 0.07f;
            using var arm = new SKPath();
            arm.MoveTo(hub.X + dx * l0, hub.Y + dy * l0);
            arm.LineTo(hub.X + dx * l1, hub.Y + dy * l1);
            arm.LineTo(hub.X + dx * l1 + nx * bw, hub.Y + dy * l1 + ny * bw);
            arm.LineTo(hub.X + dx * (l0 + s * 0.08f) + nx * bw, hub.Y + dy * (l0 + s * 0.08f) + ny * bw);
            arm.Close();
            using var spar = Paint.Stroke(SKColors.White, Math.Max(0.8f, s * 0.018f));
            using var sparPath = new SKPath();
            sparPath.MoveTo(hub.X, hub.Y);
            sparPath.LineTo(hub.X + dx * l1, hub.Y + dy * l1);
            using var sparFill = new SKPath();
            spar.GetFillPath(sparPath, sparFill);
            path.AddPath(sparFill);
            k.Flat(arm, k.Ink.Lighter(0.06), 0.75);
        }
        k.Sil(path, 0.03, 0.01);
        Window(k, x - s * 0.03f, y - s * 0.3f, s * 0.06f, s * 0.09f, r.Chance(0.6));
    }

    private static void Tent(K k)
    {
        var (x, y, s) = (k.X, k.Y, k.S);
        Shadow(k, s * 0.62f);
        using var path = new SKPath();
        path.MoveTo(x - s * 0.62f, y + s * 0.02f);
        path.QuadTo(x - s * 0.3f, y - s * 0.32f, x, y - s * 0.66f);
        path.QuadTo(x + s * 0.3f, y - s * 0.32f, x + s * 0.62f, y + s * 0.02f);
        path.Close();
        // Poles poking out of the peak.
        path.MoveTo(x - s * 0.01f, y - s * 0.62f);
        path.LineTo(x - s * 0.07f, y - s * 0.76f);
        path.LineTo(x - s * 0.05f, y - s * 0.77f);
        path.LineTo(x + s * 0.02f, y - s * 0.64f);
        path.Close();
        k.Sil(path, 0.04, 0.01);
        using var door = new SKPath();
        door.MoveTo(x - s * 0.16f, y + s * 0.01f);
        door.QuadTo(x - s * 0.05f, y - s * 0.22f, x, y - s * 0.44f);
        door.QuadTo(x + s * 0.05f, y - s * 0.22f, x + s * 0.16f, y + s * 0.01f);
        door.Close();
        if (k.P.Lit)
        {
            Paint.Glow(k.C, x, y - s * 0.12f, s * 0.6f, Paint.Sk(k.Colors.WindowGlow), 0.35);
            using var dp = new SKPaint
            {
                IsAntialias = true,
                Shader = SKShader.CreateLinearGradient(new SKPoint(0, y - s * 0.44f), new SKPoint(0, y),
                    [Paint.Sk(k.Colors.Window.Lighter(-0.08)), Paint.Sk(k.Colors.Window.Lighter(0.04))], SKShaderTileMode.Clamp),
            };
            k.C.DrawPath(door, dp);
        }
        else k.Flat(door, k.Ink.Lighter(-0.04));
    }

    /// <summary>Variant 0: a small camp lantern set on the ground (wild scenes); variant 1:
    /// a crook-topped lamp post (village and town only).</summary>
    private static void Lantern(K k)
    {
        var (x, y, s) = (k.X, k.Y, k.S);
        if (k.P.Variant == 0)
        {
            // s is the lantern's height: a cap, a glass body, a wire handle.
            Shadow(k, s * 0.5f);
            Paint.Glow(k.C, x, y - s * 0.45f, s * 3.2f, Paint.Sk(k.Colors.WindowGlow), 0.36);
            using var body = new SKPath();
            body.AddRoundRect(new SKRect(x - s * 0.34f, y - s * 0.12f, x + s * 0.34f, y + s * 0.02f), s * 0.05f, s * 0.05f);
            body.MoveTo(x - s * 0.36f, y - s * 0.72f);
            body.LineTo(x, y - s * 0.95f);
            body.LineTo(x + s * 0.36f, y - s * 0.72f);
            body.Close();
            using var handle = new SKPath();
            handle.AddArc(new SKRect(x - s * 0.2f, y - s * 1.2f, x + s * 0.2f, y - s * 0.82f), 180, 180);
            using var handleStroke = Paint.Stroke(SKColors.White, Math.Max(0.7f, s * 0.05f));
            AddStroke(body, handle, handleStroke);
            k.Sil(body, 0.02, 0);
            using var glass = Paint.Fill(Paint.Sk(k.Colors.Window));
            k.C.DrawRoundRect(x - s * 0.26f, y - s * 0.72f, s * 0.52f, s * 0.6f, s * 0.08f, s * 0.08f, glass);
            using var flame = Paint.Fill(Paint.Sk(ScenePalette.Cream, 0.9));
            k.C.DrawOval(x, y - s * 0.42f, s * 0.07f, s * 0.13f, flame);
            return;
        }
        using var path = new SKPath();
        path.AddRect(new SKRect(x - s * 0.025f, y - s * 0.78f, x + s * 0.025f, y + s * 0.02f));
        path.AddRect(new SKRect(x - s * 0.1f, y - s * 0.02f, x + s * 0.1f, y + s * 0.02f));
        path.MoveTo(x, y - s * 0.78f);
        path.QuadTo(x + s * 0.2f, y - s * 0.84f, x + s * 0.26f, y - s * 0.74f);
        path.LineTo(x + s * 0.25f, y - s * 0.72f);
        path.QuadTo(x + s * 0.2f, y - s * 0.8f, x, y - s * 0.74f);
        path.Close();
        var lx = x + s * 0.255f;
        var ly = y - s * 0.62f;
        path.MoveTo(lx - s * 0.08f, ly - s * 0.06f);
        path.LineTo(lx, ly - s * 0.12f);
        path.LineTo(lx + s * 0.08f, ly - s * 0.06f);
        path.Close();
        k.Sil(path, 0.02, 0);
        Paint.Glow(k.C, lx, ly + s * 0.02f, s * 0.55f, Paint.Sk(k.Colors.WindowGlow), 0.4);
        using var pane = Paint.Fill(Paint.Sk(k.Colors.Window));
        k.C.DrawRoundRect(lx - s * 0.055f, ly - s * 0.06f, s * 0.11f, s * 0.13f, s * 0.02f, s * 0.02f, pane);
        using var frame = Paint.Stroke(Paint.Sk(k.Ink), Math.Max(0.6f, s * 0.012f));
        k.C.DrawRoundRect(lx - s * 0.055f, ly - s * 0.06f, s * 0.11f, s * 0.13f, s * 0.02f, s * 0.02f, frame);
    }

    private static void Telescope(K k)
    {
        var (x, y, s) = (k.X, k.Y, k.S);
        using var path = new SKPath();
        using var legs = new SKPath();
        legs.MoveTo(x, y - s * 0.45f); legs.LineTo(x - s * 0.22f, y + s * 0.02f);
        legs.MoveTo(x, y - s * 0.45f); legs.LineTo(x + s * 0.2f, y + s * 0.02f);
        legs.MoveTo(x, y - s * 0.45f); legs.LineTo(x + s * 0.02f, y + s * 0.02f);
        using var st = Paint.Stroke(SKColors.White, Math.Max(0.8f, s * 0.03f));
        AddStroke(path, legs, st);
        // Tube aimed up at the sky (toward the moon side after flip handling).
        using var tube = new SKPath();
        tube.AddRoundRect(new SKRect(-s * 0.32f, -s * 0.05f, s * 0.36f, s * 0.05f), s * 0.03f, s * 0.03f);
        tube.AddRoundRect(new SKRect(s * 0.3f, -s * 0.07f, s * 0.42f, s * 0.07f), s * 0.02f, s * 0.02f);
        var m = SKMatrix.CreateRotationDegrees(-38);
        m = m.PostConcat(SKMatrix.CreateTranslation(x, y - s * 0.5f));
        tube.Transform(m);
        path.AddPath(tube);
        k.Sil(path, 0.03, 0);
    }

    private static void Reflect(K k, SKPath path, float waterline, double alpha)
    {
        using var mirrored = new SKPath(path);
        mirrored.Transform(SKMatrix.CreateScale(1, -0.75f, 0, waterline));
        using var p = new SKPaint
        {
            IsAntialias = true,
            Color = Paint.Sk(k.Ink.Lighter(0.02), alpha),
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, Math.Max(0.6f, k.S * 0.012f)),
        };
        k.C.DrawPath(mirrored, p);
    }

    private static void Boat(K k)
    {
        var (x, y, s) = (k.X, k.Y, k.S);
        using var path = new SKPath();
        path.MoveTo(x - s * 0.55f, y - s * 0.16f);
        path.QuadTo(x - s * 0.4f, y + s * 0.02f, x - s * 0.2f, y + s * 0.02f);
        path.LineTo(x + s * 0.28f, y + s * 0.02f);
        path.QuadTo(x + s * 0.48f, y + s * 0.0f, x + s * 0.55f, y - s * 0.18f);
        path.QuadTo(x, y - s * 0.1f, x - s * 0.55f, y - s * 0.16f);
        path.Close();
        // Mast with a hanging lantern.
        path.AddRect(new SKRect(x + s * 0.12f, y - s * 0.62f, x + s * 0.145f, y - s * 0.1f));
        Reflect(k, path, y + s * 0.02f, 0.35);
        k.Sil(path, 0.03, 0);
        if (k.P.Lit)
        {
            var lx = x + s * 0.2f;
            var ly = y - s * 0.5f;
            Paint.Glow(k.C, lx, ly, s * 0.5f, Paint.Sk(k.Colors.WindowGlow), 0.42);
            using var lp = Paint.Fill(Paint.Sk(k.Colors.Window));
            k.C.DrawRoundRect(lx - s * 0.035f, ly - s * 0.05f, s * 0.07f, s * 0.09f, s * 0.015f, s * 0.015f, lp);
            // The lantern's broken reflection.
            using var refl = Paint.Fill(Paint.Sk(k.Colors.Window, 0.4));
            for (var i = 0; i < 4; i++)
                k.C.DrawRoundRect(lx - s * (0.04f + i * 0.015f), y + s * (0.08f + i * 0.07f), s * (0.08f + i * 0.03f), Math.Max(0.7f, s * 0.012f), 1, 1, refl);
        }
    }

    private static void Sailboat(K k)
    {
        var (x, y, s) = (k.X, k.Y, k.S);
        using var hull = new SKPath();
        hull.MoveTo(x - s * 0.42f, y - s * 0.1f);
        hull.QuadTo(x - s * 0.3f, y + s * 0.02f, x - s * 0.15f, y + s * 0.02f);
        hull.LineTo(x + s * 0.25f, y + s * 0.02f);
        hull.QuadTo(x + s * 0.4f, y, x + s * 0.46f, y - s * 0.12f);
        hull.Close();
        hull.AddRect(new SKRect(x - s * 0.012f, y - s * 0.95f, x + s * 0.012f, y - s * 0.08f));
        using var sail = new SKPath();
        sail.MoveTo(x + s * 0.02f, y - s * 0.92f);
        sail.QuadTo(x + s * 0.32f, y - s * 0.5f, x + s * 0.34f, y - s * 0.16f);
        sail.LineTo(x + s * 0.02f, y - s * 0.16f);
        sail.Close();
        using var jib = new SKPath();
        jib.MoveTo(x - s * 0.02f, y - s * 0.8f);
        jib.QuadTo(x - s * 0.18f, y - s * 0.45f, x - s * 0.3f, y - s * 0.14f);
        jib.LineTo(x - s * 0.02f, y - s * 0.14f);
        jib.Close();
        using var all = new SKPath(hull);
        all.AddPath(sail);
        all.AddPath(jib);
        Reflect(k, all, y + s * 0.02f, 0.3);
        k.Sil(hull, 0.03, 0);
        var sailColor = Oklab.Mix(ScenePalette.Cream.ToOklab(), k.Colors.SkyMid.ToOklab(), 0.5).ToOklch();
        Paint.Silhouette(k.C, sail, sailColor, ScenePalette.Cream, 0.4, k.RimDx, k.RimDy, 0.03, 0.03);
        Paint.Silhouette(k.C, jib, sailColor.Lighter(-0.04), ScenePalette.Cream, 0.3, k.RimDx, k.RimDy, 0.03, 0.03);
    }

    private static void Balloon(K k)
    {
        var (x, y, s, r) = (k.X, k.Y, k.S, k.R);
        // Anchored at the envelope's centre (it floats).
        var top = y - s * 0.55f;
        using var env = new SKPath();
        env.MoveTo(x, y + s * 0.42f);
        env.CubicTo(x - s * 0.18f, y + s * 0.25f, x - s * 0.48f, y + s * 0.05f, x - s * 0.46f, y - s * 0.18f);
        env.CubicTo(x - s * 0.44f, y - s * 0.48f, x - s * 0.2f, top, x, top);
        env.CubicTo(x + s * 0.2f, top, x + s * 0.44f, y - s * 0.48f, x + s * 0.46f, y - s * 0.18f);
        env.CubicTo(x + s * 0.48f, y + s * 0.05f, x + s * 0.18f, y + s * 0.25f, x, y + s * 0.42f);
        env.Close();
        var bodyColor = Oklab.Mix(k.Colors.Accent.ToOklab(), k.Colors.SkyMid.ToOklab(), 0.45).ToOklch();
        Paint.Silhouette(k.C, env, bodyColor, ScenePalette.Cream, 0.45, k.RimDx, k.RimDy, 0.03, 0.05);
        // Gores: darker panels.
        k.C.Save();
        k.C.ClipPath(env, SKClipOperation.Intersect, true);
        using var gore = Paint.Fill(Paint.Sk(bodyColor.Lighter(-0.06), 0.85));
        for (var i = -2; i <= 2; i += 2)
            k.C.DrawOval(x + i * s * 0.16f, y - s * 0.06f, s * 0.07f, s * 0.6f, gore);
        k.C.Restore();
        using var ropes = Paint.Stroke(Paint.Sk(k.Ink, 0.9), Math.Max(0.6f, s * 0.01f));
        k.C.DrawLine(x - s * 0.1f, y + s * 0.38f, x - s * 0.07f, y + s * 0.56f, ropes);
        k.C.DrawLine(x + s * 0.1f, y + s * 0.38f, x + s * 0.07f, y + s * 0.56f, ropes);
        using var basket = new SKPath();
        basket.AddRoundRect(new SKRect(x - s * 0.08f, y + s * 0.55f, x + s * 0.08f, y + s * 0.67f), s * 0.02f, s * 0.02f);
        k.Sil(basket, 0.02, 0);
        if (k.P.Lit) Paint.Glow(k.C, x, y + s * 0.45f, s * 0.22f, Paint.Sk(k.Colors.WindowGlow), 0.5);
        _ = r;
    }

    private static void Train(K k)
    {
        var (x, y, s, r) = (k.X, k.Y, k.S, k.R);
        // s is the car height; the train runs along the far ridge.
        var carW = s * 2.2f;
        var cars = 3;
        using var path = new SKPath();
        var start = x - (cars * carW + s * 2.6f) / 2;
        // Engine
        var ex = start;
        path.AddRoundRect(new SKRect(ex, y - s * 0.8f, ex + s * 1.8f, y - s * 0.1f), s * 0.2f, s * 0.2f);
        path.AddRect(new SKRect(ex + s * 1.3f, y - s * 1.2f, ex + s * 2.4f, y - s * 0.1f));
        path.AddRect(new SKRect(ex + s * 0.25f, y - s * 1.25f, ex + s * 0.55f, y - s * 0.75f));
        path.MoveTo(ex - s * 0.3f, y - s * 0.05f);
        path.LineTo(ex, y - s * 0.45f);
        path.LineTo(ex, y - s * 0.05f);
        path.Close();
        for (var i = 0; i < cars; i++)
        {
            var cx = ex + s * 2.6f + i * carW;
            path.AddRoundRect(new SKRect(cx, y - s * 1.0f, cx + carW - s * 0.2f, y - s * 0.1f), s * 0.15f, s * 0.15f);
        }
        path.AddRect(new SKRect(start - s * 0.3f, y - s * 0.12f, start + s * 2.6f + cars * carW, y));
        // Rails and sleepers running on past both ends: the train is on a line, not a ridge.
        var railL = start - s * 4f;
        var railR = start + s * 2.6f + cars * carW + s * 4f;
        path.AddRect(new SKRect(railL, y + s * 0.02f, railR, y + s * 0.1f));
        for (var sx = railL; sx < railR; sx += s * 0.6f)
            path.AddRect(new SKRect(sx, y + s * 0.08f, sx + s * 0.18f, y + s * 0.2f));
        k.Sil(path, 0.02, 0);
        // Lit windows
        for (var i = 0; i < cars; i++)
        {
            var cx = ex + s * 2.6f + i * carW;
            for (var j = 0; j < 3; j++)
                Window(k, cx + s * 0.25f + j * s * 0.6f, y - s * 0.78f, s * 0.36f, s * 0.32f, k.P.Lit && r.Chance(0.8));
        }
        Window(k, ex + s * 1.55f, y - s * 1.0f, s * 0.5f, s * 0.35f, k.P.Lit);
        // Steam: soft puffs trailing back over the cars.
        var col = Oklab.Mix(k.Colors.SkyMid.ToOklab(), ScenePalette.Cream.ToOklab(), 0.35).ToOklch();
        using var steam = new SKPaint { IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, Math.Max(0.8f, s * 0.2f)) };
        for (var i = 0; i < 6; i++)
        {
            steam.Color = Paint.Sk(col, 0.3 * (1 - i / 7.0));
            k.C.DrawCircle(ex + s * 0.4f + i * s * 1.1f, y - s * (1.5f + i * 0.18f), s * (0.35f + i * 0.12f), steam);
        }
    }

    private static void Lighthouse(K k)
    {
        var (x, y, s) = (k.X, k.Y, k.S);
        var c = k.Colors;
        var baseW = s * 0.16f;
        var topW = s * 0.11f;
        var towerTop = y - s * 0.72f;
        using var tower = new SKPath();
        tower.MoveTo(x - baseW, y + s * 0.02f);
        tower.LineTo(x - topW, towerTop);
        tower.LineTo(x + topW, towerTop);
        tower.LineTo(x + baseW, y + s * 0.02f);
        tower.Close();
        var towerColor = Oklab.Mix(ScenePalette.Cream.ToOklab(), c.SkyMid.ToOklab(), 0.55).ToOklch();
        Paint.Silhouette(k.C, tower, towerColor, ScenePalette.Cream, 0.5, k.RimDx, k.RimDy, 0.03, 0.08);
        // Stripes
        k.C.Save();
        k.C.ClipPath(tower, SKClipOperation.Intersect, true);
        using var stripe = Paint.Fill(Paint.Sk(k.Ink.Lighter(0.03), 0.9));
        for (var i = 0; i < 2; i++)
        {
            var sy = y - s * (0.2f + i * 0.26f);
            k.C.DrawRect(x - baseW * 1.2f, sy - s * 0.1f, baseW * 2.4f, s * 0.1f, stripe);
        }
        k.C.Restore();
        // Gallery, lamp room, cap
        using var top = new SKPath();
        top.AddRect(new SKRect(x - topW * 1.45f, towerTop - s * 0.03f, x + topW * 1.45f, towerTop + s * 0.01f));
        top.MoveTo(x - topW * 1.15f, towerTop - s * 0.17f);
        top.LineTo(x, towerTop - s * 0.28f);
        top.LineTo(x + topW * 1.15f, towerTop - s * 0.17f);
        top.Close();
        k.Sil(top, 0.02, 0);
        var lampY = towerTop - s * 0.1f;
        if (k.P.Lit)
        {
            // A soft, wide beam — a gesture of light, not a searchlight.
            var dir = k.Plan.Moon is { } m && Math.Sign(m.X - k.P.X) != 0 ? -Math.Sign(m.X - k.P.X) * (k.P.Flip ? -1 : 1) : 1;
            using var beam = new SKPath();
            beam.MoveTo(x, lampY);
            beam.LineTo(x + dir * s * 1.6f, lampY - s * 0.2f);
            beam.LineTo(x + dir * s * 1.6f, lampY + s * 0.16f);
            beam.Close();
            using var bp = new SKPaint
            {
                IsAntialias = true,
                IsDither = true,
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, s * 0.04f),
                Shader = SKShader.CreateLinearGradient(new SKPoint(x, 0), new SKPoint(x + dir * s * 1.6f, 0),
                    [Paint.Sk(c.WindowGlow, 0.28), Paint.Sk(c.WindowGlow, 0)], SKShaderTileMode.Clamp),
            };
            k.C.DrawPath(beam, bp);
            Paint.Glow(k.C, x, lampY, s * 0.38f, Paint.Sk(c.WindowGlow), 0.5);
        }
        using var lamp = Paint.Fill(Paint.Sk(k.P.Lit ? c.Window : towerColor.Lighter(-0.1)));
        k.C.DrawRect(x - topW * 0.85f, lampY - s * 0.06f, topW * 1.7f, s * 0.1f, lamp);
    }

    // =================================================================== creatures

    private static void Fox(K k)
    {
        var (cx, cy, s) = (k.X, k.Y, k.S);
        Shadow(k, s * 0.44f, -s * 0.06f);
        using var path = new SKPath();
        path.MoveTo(cx - s * 0.34f, cy);
        path.CubicTo(cx - s * 0.46f, cy - s * 0.30f, cx - s * 0.28f, cy - s * 0.56f, cx - s * 0.10f, cy - s * 0.64f);
        path.CubicTo(cx - s * 0.02f, cy - s * 0.68f, cx + s * 0.06f, cy - s * 0.66f, cx + s * 0.10f, cy - s * 0.60f);
        path.CubicTo(cx + s * 0.20f, cy - s * 0.44f, cx + s * 0.26f, cy - s * 0.22f, cx + s * 0.26f, cy);
        path.Close();
        var hx = cx + s * 0.02f;
        var hy = cy - s * 0.72f;
        path.AddCircle(hx, hy, s * 0.17f);
        path.MoveTo(hx + s * 0.06f, hy - s * 0.10f);
        path.LineTo(hx + s * 0.30f, hy + s * 0.045f);
        path.LineTo(hx + s * 0.05f, hy + s * 0.13f);
        path.Close();
        path.MoveTo(hx - s * 0.15f, hy - s * 0.06f);
        path.LineTo(hx - s * 0.115f, hy - s * 0.38f);
        path.LineTo(hx - s * 0.005f, hy - s * 0.12f);
        path.Close();
        path.MoveTo(hx - s * 0.015f, hy - s * 0.125f);
        path.LineTo(hx + s * 0.05f, hy - s * 0.36f);
        path.LineTo(hx + s * 0.135f, hy - s * 0.085f);
        path.Close();
        path.MoveTo(cx - s * 0.30f, cy);
        path.CubicTo(cx - s * 0.72f, cy - s * 0.02f, cx - s * 0.78f, cy - s * 0.46f, cx - s * 0.46f, cy - s * 0.62f);
        path.CubicTo(cx - s * 0.38f, cy - s * 0.66f, cx - s * 0.30f, cy - s * 0.64f, cx - s * 0.28f, cy - s * 0.58f);
        path.CubicTo(cx - s * 0.52f, cy - s * 0.50f, cx - s * 0.54f, cy - s * 0.16f, cx - s * 0.16f, cy - s * 0.10f);
        path.Close();
        k.Sil(path);
    }

    /// <summary>A fox curled asleep: a soft oval, the tail wrapped around the nose, two
    /// small ears — the most bedtime silhouette in the set.</summary>
    private static void FoxCurled(K k)
    {
        var (cx, cy, s) = (k.X, k.Y, k.S);
        Shadow(k, s * 0.6f);
        using var path = new SKPath();
        path.AddOval(new SKRect(cx - s * 0.55f, cy - s * 0.42f, cx + s * 0.5f, cy + s * 0.02f));
        // Head tucked on the right, ears up.
        var hx = cx + s * 0.28f;
        var hy = cy - s * 0.28f;
        path.AddOval(new SKRect(hx - s * 0.2f, hy - s * 0.16f, hx + s * 0.18f, hy + s * 0.14f));
        path.MoveTo(hx - s * 0.12f, hy - s * 0.1f);
        path.LineTo(hx - s * 0.06f, hy - s * 0.34f);
        path.LineTo(hx + s * 0.02f, hy - s * 0.12f);
        path.Close();
        path.MoveTo(hx + s * 0.02f, hy - s * 0.12f);
        path.LineTo(hx + s * 0.12f, hy - s * 0.32f);
        path.LineTo(hx + s * 0.16f, hy - s * 0.06f);
        path.Close();
        k.Sil(path);
        // The tail sweeps around the front, a lighter tip.
        using var tail = new SKPath();
        tail.MoveTo(cx - s * 0.5f, cy - s * 0.1f);
        tail.CubicTo(cx - s * 0.6f, cy + s * 0.08f, cx + s * 0.1f, cy + s * 0.1f, cx + s * 0.42f, cy - s * 0.06f);
        tail.CubicTo(cx + s * 0.5f, cy - s * 0.1f, cx + s * 0.48f, cy - s * 0.02f, cx + s * 0.44f, cy + s * 0.0f);
        tail.CubicTo(cx + s * 0.1f, cy + s * 0.1f, cx - s * 0.4f, cy + s * 0.06f, cx - s * 0.5f, cy - s * 0.1f);
        tail.Close();
        Paint.Silhouette(k.C, tail, k.Ink.Lighter(0.02), k.RimColor, k.RimAlpha, k.RimDx, k.RimDy);
        using var tip = Paint.Fill(Paint.Sk(k.Ink.Lighter(0.1), 0.9));
        k.C.DrawCircle(cx + s * 0.43f, cy - s * 0.04f, s * 0.04f, tip);
    }

    private static void Rabbit(K k)
    {
        var (cx, cy, s) = (k.X, k.Y, k.S);
        Shadow(k, s * 0.36f, -s * 0.03f);
        using var path = new SKPath();
        path.MoveTo(cx - s * 0.30f, cy);
        path.CubicTo(cx - s * 0.44f, cy - s * 0.26f, cx - s * 0.30f, cy - s * 0.50f, cx - s * 0.10f, cy - s * 0.52f);
        path.CubicTo(cx - s * 0.02f, cy - s * 0.56f, cx + s * 0.07f, cy - s * 0.62f, cx + s * 0.10f, cy - s * 0.60f);
        path.CubicTo(cx + s * 0.17f, cy - s * 0.44f, cx + s * 0.20f, cy - s * 0.20f, cx + s * 0.18f, cy);
        path.Close();
        var hx = cx + s * 0.075f;
        var hy = cy - s * 0.64f;
        path.AddOval(new SKRect(hx - s * 0.13f, hy - s * 0.115f, hx + s * 0.155f, hy + s * 0.115f));
        path.MoveTo(hx - s * 0.075f, hy - s * 0.02f);
        path.CubicTo(hx - s * 0.135f, hy - s * 0.34f, hx - s * 0.075f, hy - s * 0.52f, hx - s * 0.035f, hy - s * 0.52f);
        path.CubicTo(hx - s * 0.005f, hy - s * 0.50f, hx + s * 0.015f, hy - s * 0.30f, hx + s * 0.015f, hy - s * 0.045f);
        path.Close();
        path.MoveTo(hx + s * 0.035f, hy - s * 0.045f);
        path.CubicTo(hx + s * 0.06f, hy - s * 0.30f, hx + s * 0.13f, hy - s * 0.435f, hx + s * 0.175f, hy - s * 0.40f);
        path.CubicTo(hx + s * 0.185f, hy - s * 0.36f, hx + s * 0.145f, hy - s * 0.22f, hx + s * 0.095f, hy - s * 0.015f);
        path.Close();
        path.AddCircle(cx - s * 0.315f, cy - s * 0.115f, s * 0.075f);
        path.AddOval(new SKRect(cx + s * 0.06f, cy - s * 0.05f, cx + s * 0.22f, cy));
        k.Sil(path);
    }

    /// <summary>An owl on a branch that reaches in from the frame edge.</summary>
    private static void Owl(K k)
    {
        var (cx, cy, s) = (k.X, k.Y, k.S);
        using var path = new SKPath();
        // Branch from beyond the right edge (mirrored by flip for the left edge).
        // A trunk just inside the nearer frame edge (in pre-flip coordinates when mirrored).
        var trunkX = k.P.Flip ? 2 * cx - (k.Vp.X(0) + k.Vp.Unit * 0.02f) : k.Vp.X(1.0) - k.Vp.Unit * 0.02f;
        var reach = Math.Max(trunkX - cx, s * 0.6f);
        var far = trunkX + k.Vp.Unit * 2;
        path.MoveTo(trunkX - s * 0.22f, k.Vp.Height + 4f);
        path.CubicTo(trunkX - s * 0.16f, cy, trunkX - s * 0.2f, cy - s, trunkX - s * 0.14f, -4f);
        path.LineTo(far, -4f);
        path.LineTo(far, k.Vp.Height + 4f);
        path.Close();
        path.MoveTo(cx - s * 0.5f, cy + s * 0.01f);
        path.QuadTo(cx + reach * 0.4f, cy - s * 0.06f, cx + reach, cy - s * 0.12f);
        path.LineTo(cx + reach, cy + s * 0.1f);
        path.QuadTo(cx + reach * 0.4f, cy + s * 0.08f, cx - s * 0.5f, cy + s * 0.06f);
        path.Close();
        // A twig with two leaves.
        using var twig = Leaf(cx - s * 0.35f, cy + s * 0.02f, cx - s * 0.6f, cy - s * 0.12f, s * 0.04f, s * 0.02f);
        path.AddPath(twig);
        path.MoveTo(cx - s * 0.26f, cy);
        path.CubicTo(cx - s * 0.34f, cy - s * 0.30f, cx - s * 0.30f, cy - s * 0.62f, cx - s * 0.185f, cy - s * 0.745f);
        path.LineTo(cx - s * 0.235f, cy - s * 0.92f);
        path.CubicTo(cx - s * 0.12f, cy - s * 0.85f, cx + s * 0.12f, cy - s * 0.85f, cx + s * 0.235f, cy - s * 0.92f);
        path.LineTo(cx + s * 0.185f, cy - s * 0.745f);
        path.CubicTo(cx + s * 0.30f, cy - s * 0.62f, cx + s * 0.34f, cy - s * 0.30f, cx + s * 0.26f, cy);
        path.Close();
        k.Sil(path);
        // Sleepy closed eyes and a small beak: a bedtime owl, never glowing eyes in the dark.
        using var lids = Paint.Stroke(Paint.Sk(ScenePalette.Cream, 0.55), Math.Max(0.7f, s * 0.022f));
        k.C.DrawArc(new SKRect(cx - s * 0.15f, cy - s * 0.72f, cx - s * 0.04f, cy - s * 0.62f), 20, 140, false, lids);
        k.C.DrawArc(new SKRect(cx + s * 0.04f, cy - s * 0.72f, cx + s * 0.15f, cy - s * 0.62f), 20, 140, false, lids);
        using var beak = Paint.Fill(Paint.Sk(ScenePalette.AmberSoft, 0.6));
        using var bp = new SKPath();
        bp.MoveTo(cx - s * 0.025f, cy - s * 0.6f);
        bp.LineTo(cx + s * 0.025f, cy - s * 0.6f);
        bp.LineTo(cx, cy - s * 0.55f);
        bp.Close();
        k.C.DrawPath(bp, beak);
        // Belly: a lighter feathered patch.
        using var belly = Paint.Fill(Paint.Sk(k.Ink.Lighter(0.05), 0.85));
        k.C.DrawOval(cx, cy - s * 0.25f, s * 0.15f, s * 0.2f, belly);
    }

    private static void Deer(K k)
    {
        var (cx, cy, s) = (k.X, k.Y, k.S);
        Shadow(k, s * 0.42f, -s * 0.02f);
        using var path = new SKPath();
        var legTop = cy - s * 0.42f;
        using (var legs = new SKPath())
        {
            legs.MoveTo(cx - s * 0.30f, legTop); legs.LineTo(cx - s * 0.335f, cy);
            legs.MoveTo(cx - s * 0.20f, legTop); legs.LineTo(cx - s * 0.165f, cy);
            legs.MoveTo(cx + s * 0.18f, legTop); legs.LineTo(cx + s * 0.145f, cy);
            legs.MoveTo(cx + s * 0.28f, legTop); legs.LineTo(cx + s * 0.315f, cy);
            using var st = Paint.Stroke(SKColors.White, s * 0.045f);
            AddStroke(path, legs, st);
        }
        path.MoveTo(cx - s * 0.36f, cy - s * 0.40f);
        path.CubicTo(cx - s * 0.42f, cy - s * 0.52f, cx - s * 0.30f, cy - s * 0.60f, cx - s * 0.12f, cy - s * 0.585f);
        path.CubicTo(cx + s * 0.02f, cy - s * 0.575f, cx + s * 0.14f, cy - s * 0.565f, cx + s * 0.22f, cy - s * 0.60f);
        path.CubicTo(cx + s * 0.30f, cy - s * 0.70f, cx + s * 0.335f, cy - s * 0.80f, cx + s * 0.36f, cy - s * 0.875f);
        path.LineTo(cx + s * 0.475f, cy - s * 0.915f);
        path.LineTo(cx + s * 0.475f, cy - s * 0.875f);
        path.CubicTo(cx + s * 0.44f, cy - s * 0.845f, cx + s * 0.42f, cy - s * 0.85f, cx + s * 0.40f, cy - s * 0.835f);
        path.CubicTo(cx + s * 0.36f, cy - s * 0.70f, cx + s * 0.33f, cy - s * 0.56f, cx + s * 0.30f, cy - s * 0.44f);
        path.CubicTo(cx + s * 0.20f, cy - s * 0.36f, cx - s * 0.22f, cy - s * 0.36f, cx - s * 0.36f, cy - s * 0.40f);
        path.Close();
        using (var antlers = new SKPath())
        {
            var ax = cx + s * 0.375f;
            var ay = cy - s * 0.925f;
            antlers.MoveTo(ax, ay);
            antlers.CubicTo(ax - s * 0.04f, ay - s * 0.14f, ax - s * 0.10f, ay - s * 0.20f, ax - s * 0.145f, ay - s * 0.235f);
            antlers.MoveTo(ax - s * 0.045f, ay - s * 0.125f); antlers.LineTo(ax + s * 0.02f, ay - s * 0.20f);
            antlers.MoveTo(ax - s * 0.10f, ay - s * 0.19f); antlers.LineTo(ax - s * 0.075f, ay - s * 0.27f);
            antlers.MoveTo(ax - s * 0.04f, ay + s * 0.01f);
            antlers.CubicTo(ax - s * 0.10f, ay - s * 0.10f, ax - s * 0.17f, ay - s * 0.145f, ax - s * 0.22f, ay - s * 0.16f);
            antlers.MoveTo(ax - s * 0.115f, ay - s * 0.095f); antlers.LineTo(ax - s * 0.10f, ay - s * 0.185f);
            using var thin = Paint.Stroke(SKColors.White, s * 0.028f);
            AddStroke(path, antlers, thin);
        }
        path.MoveTo(cx + s * 0.33f, cy - s * 0.87f);
        path.LineTo(cx + s * 0.24f, cy - s * 0.95f);
        path.LineTo(cx + s * 0.325f, cy - s * 0.925f);
        path.Close();
        path.MoveTo(cx - s * 0.36f, cy - s * 0.50f);
        path.QuadTo(cx - s * 0.45f, cy - s * 0.545f, cx - s * 0.435f, cy - s * 0.435f);
        path.QuadTo(cx - s * 0.40f, cy - s * 0.455f, cx - s * 0.355f, cy - s * 0.43f);
        path.Close();
        k.Sil(path);
    }

    /// <summary>A bear cub sitting, looking up: round body, round head and ears.</summary>
    private static void Bear(K k)
    {
        var (cx, cy, s) = (k.X, k.Y, k.S);
        Shadow(k, s * 0.48f);
        using var path = new SKPath();
        path.MoveTo(cx - s * 0.42f, cy);
        path.CubicTo(cx - s * 0.5f, cy - s * 0.35f, cx - s * 0.3f, cy - s * 0.62f, cx, cy - s * 0.62f);
        path.CubicTo(cx + s * 0.3f, cy - s * 0.62f, cx + s * 0.46f, cy - s * 0.35f, cx + s * 0.4f, cy);
        path.Close();
        var hx = cx + s * 0.06f;
        var hy = cy - s * 0.74f;
        path.AddCircle(hx, hy, s * 0.22f);
        path.AddCircle(hx - s * 0.16f, hy - s * 0.17f, s * 0.08f);
        path.AddCircle(hx + s * 0.17f, hy - s * 0.16f, s * 0.08f);
        path.AddOval(new SKRect(hx + s * 0.1f, hy - s * 0.02f, hx + s * 0.3f, hy + s * 0.1f));
        path.AddOval(new SKRect(cx - s * 0.32f, cy - s * 0.1f, cx - s * 0.08f, cy + s * 0.02f));
        path.AddOval(new SKRect(cx + s * 0.1f, cy - s * 0.1f, cx + s * 0.34f, cy + s * 0.02f));
        k.Sil(path);
    }

    /// <summary>A cat sitting, seen from the side-back, tail curled round its paws.</summary>
    private static void Cat(K k)
    {
        var (cx, cy, s) = (k.X, k.Y, k.S);
        Shadow(k, s * 0.34f);
        using var path = new SKPath();
        path.MoveTo(cx - s * 0.28f, cy);
        path.CubicTo(cx - s * 0.36f, cy - s * 0.3f, cx - s * 0.2f, cy - s * 0.6f, cx - s * 0.02f, cy - s * 0.62f);
        path.CubicTo(cx + s * 0.14f, cy - s * 0.6f, cx + s * 0.22f, cy - s * 0.3f, cx + s * 0.2f, cy);
        path.Close();
        var hx = cx + s * 0.0f;
        var hy = cy - s * 0.72f;
        path.AddOval(new SKRect(hx - s * 0.16f, hy - s * 0.13f, hx + s * 0.16f, hy + s * 0.13f));
        path.MoveTo(hx - s * 0.15f, hy - s * 0.04f);
        path.LineTo(hx - s * 0.13f, hy - s * 0.27f);
        path.LineTo(hx - s * 0.02f, hy - s * 0.12f);
        path.Close();
        path.MoveTo(hx + s * 0.02f, hy - s * 0.12f);
        path.LineTo(hx + s * 0.13f, hy - s * 0.27f);
        path.LineTo(hx + s * 0.15f, hy - s * 0.04f);
        path.Close();
        using (var tail = new SKPath())
        {
            tail.MoveTo(cx - s * 0.2f, cy - s * 0.02f);
            tail.CubicTo(cx - s * 0.55f, cy - s * 0.02f, cx - s * 0.55f, cy - s * 0.3f, cx - s * 0.42f, cy - s * 0.38f);
            using var st = Paint.Stroke(SKColors.White, s * 0.07f);
            AddStroke(path, tail, st);
        }
        k.Sil(path);
    }

    private static void Hedgehog(K k)
    {
        var (cx, cy, s, r) = (k.X, k.Y, k.S, k.R);
        Shadow(k, s * 0.5f);
        using var path = new SKPath();
        // Spiky dome
        var n = 13;
        path.MoveTo(cx - s * 0.5f, cy);
        for (var i = 0; i <= n; i++)
        {
            var a = Math.PI + Math.PI * i / n * 0.85;
            var rr = s * (i % 2 == 0 ? 0.5f : 0.42f) * (float)r.Range(0.95, 1.05);
            path.LineTo(cx - s * 0.04f + (float)Math.Cos(a) * rr * 1.05f, cy + (float)Math.Sin(a) * rr * 0.9f);
        }
        path.QuadTo(cx + s * 0.38f, cy - s * 0.2f, cx + s * 0.58f, cy - s * 0.08f);
        path.QuadTo(cx + s * 0.5f, cy + s * 0.0f, cx + s * 0.3f, cy);
        path.Close();
        k.Sil(path);
        using var nose = Paint.Fill(Paint.Sk(k.Ink.Lighter(-0.05)));
        k.C.DrawCircle(cx + s * 0.58f, cy - s * 0.08f, s * 0.035f, nose);
    }

    /// <summary>An otter floating on its back, paws on its chest, tail out.</summary>
    private static void Otter(K k)
    {
        var (cx, cy, s) = (k.X, k.Y, k.S);
        using var path = new SKPath();
        path.AddOval(new SKRect(cx - s * 0.48f, cy - s * 0.2f, cx + s * 0.32f, cy + s * 0.04f));
        path.AddCircle(cx + s * 0.38f, cy - s * 0.16f, s * 0.15f);
        path.AddCircle(cx + s * 0.3f, cy - s * 0.3f, s * 0.04f);
        path.MoveTo(cx - s * 0.44f, cy - s * 0.04f);
        path.QuadTo(cx - s * 0.7f, cy - s * 0.1f, cx - s * 0.78f, cy - s * 0.02f);
        path.QuadTo(cx - s * 0.66f, cy + s * 0.02f, cx - s * 0.44f, cy + s * 0.02f);
        path.Close();
        path.AddOval(new SKRect(cx + s * 0.06f, cy - s * 0.3f, cx + s * 0.2f, cy - s * 0.16f));
        Reflect(k, path, cy + s * 0.02f, 0.3);
        k.Sil(path);
        // Water ripples hugging the body.
        using var ripple = Paint.Fill(Paint.Sk(ScenePalette.Cream, 0.22));
        k.C.DrawRoundRect(cx - s * 0.6f, cy + s * 0.04f, s * 1.1f, Math.Max(0.8f, s * 0.018f), 2, 2, ripple);
        k.C.DrawRoundRect(cx - s * 0.4f, cy + s * 0.11f, s * 0.7f, Math.Max(0.7f, s * 0.014f), 2, 2, ripple);
    }

    /// <summary>Variant 0: flukes rising from the sea; variant 1: a whole whale gliding far
    /// off in the blue (underwater).</summary>
    private static void Whale(K k)
    {
        var (cx, cy, s) = (k.X, k.Y, k.S);
        using var path = new SKPath();
        if (k.P.Variant == 1)
        {
            // A humpback gliding: long tapered body, pale throat, long swept-back flipper,
            // a horizontal "W" fluke. Soft and far away in the blue.
            path.MoveTo(cx + s * 0.5f, cy - s * 0.02f);
            path.CubicTo(cx + s * 0.42f, cy - s * 0.14f, cx + s * 0.1f, cy - s * 0.17f, cx - s * 0.2f, cy - s * 0.1f);
            path.CubicTo(cx - s * 0.36f, cy - s * 0.06f, cx - s * 0.5f, cy - s * 0.02f, cx - s * 0.58f, cy);
            path.CubicTo(cx - s * 0.66f, cy - s * 0.08f, cx - s * 0.74f, cy - s * 0.1f, cx - s * 0.8f, cy - s * 0.08f);
            path.QuadTo(cx - s * 0.72f, cy, cx - s * 0.8f, cy + s * 0.08f);
            path.CubicTo(cx - s * 0.74f, cy + s * 0.1f, cx - s * 0.66f, cy + s * 0.08f, cx - s * 0.58f, cy + s * 0.01f);
            path.CubicTo(cx - s * 0.4f, cy + s * 0.07f, cx - s * 0.1f, cy + s * 0.12f, cx + s * 0.2f, cy + s * 0.09f);
            path.CubicTo(cx + s * 0.38f, cy + s * 0.07f, cx + s * 0.5f, cy + s * 0.04f, cx + s * 0.5f, cy - s * 0.02f);
            path.Close();
            using (var flipper = Leaf(cx + s * 0.18f, cy + s * 0.06f, cx - s * 0.12f, cy + s * 0.3f, s * 0.05f, s * 0.02f))
                path.AddPath(flipper);
            k.Sil(path, 0.03, 0.02);
            using var throat = new SKPath();
            throat.MoveTo(cx + s * 0.48f, cy + s * 0.01f);
            throat.CubicTo(cx + s * 0.35f, cy + s * 0.07f, cx + s * 0.05f, cy + s * 0.1f, cx - s * 0.15f, cy + s * 0.08f);
            throat.CubicTo(cx + s * 0.05f, cy + s * 0.06f, cx + s * 0.3f, cy + s * 0.03f, cx + s * 0.48f, cy + s * 0.01f);
            throat.Close();
            k.Flat(throat, k.Ink.Lighter(0.07), 0.8);
            using var eye = Paint.Fill(Paint.Sk(k.Ink.Lighter(-0.05)));
            k.C.DrawCircle(cx + s * 0.34f, cy - s * 0.01f, Math.Max(0.6f, s * 0.012f), eye);
            return;
        }
        var tipY = cy - s * 0.72f;
        path.MoveTo(cx - s * 0.16f, cy);
        path.CubicTo(cx - s * 0.13f, cy - s * 0.22f, cx - s * 0.10f, cy - s * 0.34f, cx - s * 0.09f, cy - s * 0.42f);
        path.CubicTo(cx - s * 0.20f, cy - s * 0.48f, cx - s * 0.40f, cy - s * 0.58f, cx - s * 0.50f, tipY + s * 0.04f);
        path.QuadTo(cx - s * 0.505f, tipY, cx - s * 0.475f, tipY + s * 0.005f);
        path.CubicTo(cx - s * 0.33f, cy - s * 0.62f, cx - s * 0.12f, cy - s * 0.52f, cx, cy - s * 0.50f);
        path.CubicTo(cx + s * 0.12f, cy - s * 0.52f, cx + s * 0.33f, cy - s * 0.62f, cx + s * 0.475f, tipY + s * 0.005f);
        path.QuadTo(cx + s * 0.505f, tipY, cx + s * 0.50f, tipY + s * 0.04f);
        path.CubicTo(cx + s * 0.40f, cy - s * 0.58f, cx + s * 0.20f, cy - s * 0.48f, cx + s * 0.09f, cy - s * 0.42f);
        path.CubicTo(cx + s * 0.10f, cy - s * 0.34f, cx + s * 0.13f, cy - s * 0.22f, cx + s * 0.16f, cy);
        path.Close();
        Reflect(k, path, cy, 0.3);
        k.Sil(path);
        using var ripple = Paint.Fill(Paint.Sk(ScenePalette.Cream, 0.25));
        k.C.DrawRoundRect(cx - s * 0.3f, cy - s * 0.01f, s * 0.6f, Math.Max(0.8f, s * 0.02f), 2, 2, ripple);
        k.C.DrawRoundRect(cx - s * 0.12f, cy + s * 0.05f, s * 0.36f, Math.Max(0.7f, s * 0.016f), 2, 2, ripple);
    }

    private static void Turtle(K k)
    {
        var (cx, cy, s) = (k.X, k.Y, k.S);
        var swimming = k.P.Variant == 0;
        // Swimming turtles are anchored at their centre; beach turtles at the base.
        var by = swimming ? cy + s * 0.15f : cy;
        if (!swimming) Shadow(k, s * 0.5f);
        using var path = new SKPath();
        path.MoveTo(cx - s * 0.42f, by - s * 0.04f);
        path.CubicTo(cx - s * 0.38f, by - s * 0.42f, cx + s * 0.32f, by - s * 0.42f, cx + s * 0.38f, by - s * 0.04f);
        path.QuadTo(cx, by + s * 0.04f, cx - s * 0.42f, by - s * 0.04f);
        path.Close();
        path.AddOval(new SKRect(cx + s * 0.34f, by - s * 0.2f, cx + s * 0.58f, by - s * 0.02f));
        if (swimming)
        {
            using var f1 = Leaf(cx + s * 0.15f, by - s * 0.06f, cx + s * 0.3f, by + s * 0.28f, s * 0.07f, s * 0.04f);
            using var f2 = Leaf(cx - s * 0.25f, by - s * 0.06f, cx - s * 0.45f, by + s * 0.12f, s * 0.05f, -s * 0.02f);
            path.AddPath(f1);
            path.AddPath(f2);
        }
        else
        {
            path.AddOval(new SKRect(cx + s * 0.15f, by - s * 0.08f, cx + s * 0.32f, by + s * 0.02f));
            path.AddOval(new SKRect(cx - s * 0.36f, by - s * 0.08f, cx - s * 0.18f, by + s * 0.02f));
        }
        k.Sil(path);
        // Shell plates: a few soft lighter patches.
        using var plate = Paint.Stroke(Paint.Sk(k.Ink.Lighter(0.06), 0.8), Math.Max(0.7f, s * 0.015f));
        k.C.DrawArc(new SKRect(cx - s * 0.22f, by - s * 0.32f, cx + s * 0.18f, by - s * 0.02f), 200, 140, false, plate);
        k.C.DrawLine(cx - s * 0.02f, by - s * 0.3f, cx - s * 0.02f, by - s * 0.04f, plate);
    }

    private static void FishSchool(K k)
    {
        var (cx, cy, s, r) = (k.X, k.Y, k.S, k.R);
        var col = Oklab.Mix(ScenePalette.Cream.ToOklab(), k.Colors.SkyHorizon.ToOklab(), 0.55).ToOklch();
        var n = 7 + r.Int(5);
        for (var i = 0; i < n; i++)
        {
            var t = (float)i / n;
            var fx = cx + (t - 0.5f) * s * 1.6f + (float)r.Jitter(0.08) * s;
            var fy = cy + MathF.Sin(t * 3.1f) * s * 0.18f + (float)r.Jitter(0.06) * s;
            var fs = s * (float)r.Range(0.1, 0.14);
            using var fish = new SKPath();
            fish.MoveTo(fx + fs, fy);
            fish.QuadTo(fx, fy - fs * 0.5f, fx - fs * 0.7f, fy);
            fish.LineTo(fx - fs * 1.1f, fy - fs * 0.35f);
            fish.LineTo(fx - fs * 1.0f, fy);
            fish.LineTo(fx - fs * 1.1f, fy + fs * 0.35f);
            fish.LineTo(fx - fs * 0.7f, fy);
            fish.QuadTo(fx, fy + fs * 0.5f, fx + fs, fy);
            fish.Close();
            k.Flat(fish, col, r.Range(0.45, 0.75));
        }
    }

    private static void Camel(K k)
    {
        var (cx, cy, s) = (k.X, k.Y, k.S);
        Shadow(k, s * 0.42f);
        using var path = new SKPath();
        var legTop = cy - s * 0.42f;
        using (var legs = new SKPath())
        {
            legs.MoveTo(cx - s * 0.26f, legTop); legs.LineTo(cx - s * 0.29f, cy);
            legs.MoveTo(cx - s * 0.16f, legTop); legs.LineTo(cx - s * 0.12f, cy);
            legs.MoveTo(cx + s * 0.16f, legTop); legs.LineTo(cx + s * 0.13f, cy);
            legs.MoveTo(cx + s * 0.25f, legTop); legs.LineTo(cx + s * 0.29f, cy);
            using var st = Paint.Stroke(SKColors.White, s * 0.05f);
            AddStroke(path, legs, st);
        }
        // Body with two soft humps, neck curving forward and up, small head.
        path.MoveTo(cx - s * 0.34f, cy - s * 0.42f);
        path.CubicTo(cx - s * 0.36f, cy - s * 0.6f, cx - s * 0.22f, cy - s * 0.78f, cx - s * 0.12f, cy - s * 0.68f);
        path.CubicTo(cx - s * 0.06f, cy - s * 0.64f, cx - s * 0.02f, cy - s * 0.64f, cx + s * 0.04f, cy - s * 0.7f);
        path.CubicTo(cx + s * 0.14f, cy - s * 0.78f, cx + s * 0.26f, cy - s * 0.62f, cx + s * 0.3f, cy - s * 0.52f);
        path.CubicTo(cx + s * 0.38f, cy - s * 0.58f, cx + s * 0.42f, cy - s * 0.74f, cx + s * 0.44f, cy - s * 0.84f);
        path.CubicTo(cx + s * 0.46f, cy - s * 0.9f, cx + s * 0.56f, cy - s * 0.9f, cx + s * 0.6f, cy - s * 0.84f);
        path.LineTo(cx + s * 0.6f, cy - s * 0.8f);
        path.CubicTo(cx + s * 0.54f, cy - s * 0.78f, cx + s * 0.5f, cy - s * 0.74f, cx + s * 0.5f, cy - s * 0.68f);
        path.CubicTo(cx + s * 0.48f, cy - s * 0.52f, cx + s * 0.4f, cy - s * 0.4f, cx + s * 0.3f, cy - s * 0.38f);
        path.CubicTo(cx + s * 0.1f, cy - s * 0.34f, cx - s * 0.2f, cy - s * 0.36f, cx - s * 0.34f, cy - s * 0.42f);
        path.Close();
        path.MoveTo(cx + s * 0.5f, cy - s * 0.88f);
        path.LineTo(cx + s * 0.48f, cy - s * 0.95f);
        path.LineTo(cx + s * 0.53f, cy - s * 0.9f);
        path.Close();
        using (var tail = new SKPath())
        {
            tail.MoveTo(cx - s * 0.34f, cy - s * 0.55f);
            tail.QuadTo(cx - s * 0.42f, cy - s * 0.5f, cx - s * 0.4f, cy - s * 0.38f);
            using var st = Paint.Stroke(SKColors.White, s * 0.025f);
            AddStroke(path, tail, st);
        }
        k.Sil(path);
    }

    /// <summary>A gentle long-neck dinosaur grazing at the treetops — big, slow, kind.</summary>
    private static void Dinosaur(K k)
    {
        var (cx, cy, s) = (k.X, k.Y, k.S);
        Shadow(k, s * 0.5f);
        using var path = new SKPath();
        using (var legs = new SKPath())
        {
            foreach (var lx in new[] { -0.22f, -0.1f, 0.14f, 0.26f })
            {
                legs.MoveTo(cx + lx * s, cy - s * 0.3f);
                legs.LineTo(cx + lx * s, cy);
            }
            using var st = Paint.Stroke(SKColors.White, s * 0.08f, SKStrokeCap.Butt);
            AddStroke(path, legs, st);
        }
        path.MoveTo(cx - s * 0.75f, cy - s * 0.18f);
        path.CubicTo(cx - s * 0.5f, cy - s * 0.24f, cx - s * 0.4f, cy - s * 0.5f, cx - s * 0.05f, cy - s * 0.52f);
        path.CubicTo(cx + s * 0.18f, cy - s * 0.53f, cx + s * 0.26f, cy - s * 0.5f, cx + s * 0.3f, cy - s * 0.46f);
        // Neck rising in a long S toward the head.
        path.CubicTo(cx + s * 0.42f, cy - s * 0.62f, cx + s * 0.44f, cy - s * 0.9f, cx + s * 0.5f, cy - s * 1.02f);
        path.CubicTo(cx + s * 0.54f, cy - s * 1.1f, cx + s * 0.66f, cy - s * 1.1f, cx + s * 0.7f, cy - s * 1.04f);
        path.QuadTo(cx + s * 0.72f, cy - s * 1.0f, cx + s * 0.64f, cy - s * 0.99f);
        path.CubicTo(cx + s * 0.58f, cy - s * 0.98f, cx + s * 0.56f, cy - s * 0.9f, cx + s * 0.54f, cy - s * 0.8f);
        path.CubicTo(cx + s * 0.52f, cy - s * 0.6f, cx + s * 0.44f, cy - s * 0.36f, cx + s * 0.3f, cy - s * 0.28f);
        path.CubicTo(cx + s * 0.1f, cy - s * 0.24f, cx - s * 0.3f, cy - s * 0.24f, cx - s * 0.5f, cy - s * 0.15f);
        path.QuadTo(cx - s * 0.65f, cy - s * 0.1f, cx - s * 0.75f, cy - s * 0.18f);
        path.Close();
        k.Sil(path);
    }

    private static void Birds(K k)
    {
        var (cx, cy, s) = (k.X, k.Y, k.S);
        Span<(float dx, float dy, float size)> flock =
        [
            (0f, 0f, 1.0f), (-0.55f, -0.28f, 0.75f), (0.5f, -0.42f, 0.8f),
            (-1.05f, -0.62f, 0.55f), (1.0f, -0.15f, 0.6f), (0.25f, -0.85f, 0.5f),
        ];
        using var stroke = Paint.Stroke(Paint.Sk(k.Ink.Lighter(0.04), 0.85), Math.Max(0.8f, s * 0.035f));
        foreach (var (dx, dy, size) in flock)
        {
            var bx = cx + dx * s * 0.8f;
            var by = cy + dy * s * 0.8f;
            var wing = s * 0.16f * size;
            using var bird = new SKPath();
            bird.MoveTo(bx - wing, by);
            bird.QuadTo(bx - wing * 0.5f, by - wing * 0.75f, bx, by);
            bird.QuadTo(bx + wing * 0.5f, by - wing * 0.75f, bx + wing, by);
            k.C.DrawPath(bird, stroke);
        }
    }
}
