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
internal static partial class PropPainter
{
    private readonly record struct K(
        SKCanvas C, PosterPlan Plan, Placement P, PosterViewport Vp, float X, float Y, float S, Rng R,
        Oklch Ink, Oklch RimColor, double RimAlpha, float RimDx, float RimDy)
    {
        /// <summary>Direction (in the prop's own, unflipped frame) toward the nearest warm
        /// light, and how strongly it reaches this prop (0..1).</summary>
        public float WarmDir { get; init; }
        public double WarmAmt { get; init; }
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
        var isCreature = IsCreatureProp(p.Prop);
        var ink = plan.Colors.Snowy && isCreature
            ? plan.Colors.LandNear with { L = plan.Colors.LandNear.L + 0.03 }
            : plan.Colors.Ground(Math.Min(1, tone + (isCreature ? 0.12 : 0.05)));
        if (plan.Colors.Snowy && !isCreature && p.Prop is not (Prop.House or Prop.Cabin or Prop.Fence))
            ink = plan.Colors.Ground(Math.Min(1, tone + 0.4)) with { L = plan.Colors.LandNear.L + 0.02 + (1 - tone) * 0.14 };
        if (plan.Colors.Snowy && p.Prop is Prop.House or Prop.Cabin)
            ink = plan.Colors.LandNear with { L = plan.Colors.LandNear.L + 0.05 + (1 - tone) * 0.08, H = ScenePalette.HueLerp(plan.Colors.LandNear.H, 40, 0.4) };
        if (plan.Underwater && p.Prop is Prop.BranchCoral or Prop.FanCoral or Prop.Kelp)
            ink = ink with
            {
                // Dusty rose corals and sea-green kelp, both quiet: no candy magenta on teal.
                H = p.Prop == Prop.Kelp ? 165 : 18,
                C = Math.Min(0.045, ink.C + 0.01),
                L = ink.L + (p.Prop == Prop.Kelp ? -0.01 : 0.03),
            };

        var x = vp.X(p.X);
        var y = vp.Y(p.Y);
        var s = vp.S(p.Size);
        var lx = plan.Moon is { } m ? Math.Sign(m.X - p.X) : 0;
        var rw = Math.Max(1f, s * (isCreature ? 0.028f : 0.022f));
        var rimAlpha = plan.Colors.RimStrength * (isCreature ? 0.95 : 0.6) * (0.4 + 0.6 * tone);
        // Mirror: the rim must follow the moon, not the flip.
        var rimDx = -lx * rw * 0.75f * (p.Flip ? -1 : 1);
        // The nearest warm light that is not this prop's own: it warms the side facing it.
        float warmDir = 0;
        double warmAmt = 0;
        foreach (var light in plan.Lights)
        {
            if (Math.Abs(light.Depth - p.Depth) > 1 || Math.Abs(light.X - p.X) < 1e-6 && Math.Abs(light.Y - p.Y) < p.Size) continue;
            var d = Math.Sqrt((light.X - p.X) * (light.X - p.X) + (light.Y - (p.Y - p.Size * 0.4)) * (light.Y - (p.Y - p.Size * 0.4)));
            var reach = Math.Max(0.12, light.Radius * 1.6) + p.Size * 0.5;
            var amt = light.Strength * Math.Clamp(1 - d / reach, 0, 1);
            if (amt <= warmAmt) continue;
            warmAmt = amt;
            warmDir = Math.Sign(light.X - p.X) * (p.Flip ? -1 : 1);
        }
        var k = new K(canvas, plan, p, vp, x, y, s, new Rng(p.Seed), ink, plan.Colors.Rim, rimAlpha, rimDx, rw)
        {
            WarmDir = warmDir,
            WarmAmt = warmAmt,
        };

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
            case Prop.FrameCluster: FrameCluster(k); break;
            case Prop.Campfire: Campfire(k); break;
            case Prop.Anemone: Anemone(k); break;
            case Prop.Snowman: Snowman(k); break;
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

    /// <summary>A sleepy closed eye: a short cream arc (the owl taught the set that a face
    /// is what makes a silhouette a character).</summary>
    private static void SleepyEye(K k, float x, float y, float w)
    {
        using var lid = Paint.Stroke(Paint.Sk(ScenePalette.Cream, 0.6), Math.Max(0.7f, w * 0.28f));
        k.C.DrawArc(new SKRect(x - w, y - w * 0.8f, x + w, y + w * 0.8f), 20, 140, false, lid);
    }

    /// <summary>A soft lighter patch (chest, belly) clipped to the body — one inner value step.</summary>
    private static void Patch(K k, SKPath body, float cx, float cy, float rx, float ry)
    {
        k.C.Save();
        k.C.ClipPath(body, SKClipOperation.Intersect, true);
        using var p = new SKPaint
        {
            IsAntialias = true,
            Color = Paint.Sk(k.Ink.Lighter(0.06), 0.9),
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, Math.Max(0.5f, rx * 0.25f)),
        };
        k.C.DrawOval(cx, cy, rx, ry, p);
        k.C.Restore();
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

    /// <summary>A gorgonian sea fan: a short stem splitting into many fine branches that
    /// spread into a wide, lacy, irregular fan — unmistakably coral, never a balloon.</summary>
    private static void FanCoral(K k)
    {
        var (x, y, s, r) = (k.X, k.Y, k.S, k.R);
        using var path = new SKPath();
        void Branch(float bx, float by, float ang, float len, int depth, float width)
        {
            var ex = bx + MathF.Cos(ang) * len;
            var ey = by + MathF.Sin(ang) * len;
            using var seg = new SKPath();
            seg.MoveTo(bx, by);
            seg.QuadTo(bx + MathF.Cos(ang + 0.15f) * len * 0.5f, by + MathF.Sin(ang + 0.15f) * len * 0.5f, ex, ey);
            using var st = Paint.Stroke(SKColors.White, Math.Max(0.7f, width));
            AddStroke(path, seg, st);
            if (depth == 0) return;
            var spread = 0.32f + (float)r.Jitter(0.08);
            Branch(ex, ey, ang - spread, len * 0.74f, depth - 1, width * 0.72f);
            Branch(ex, ey, ang + spread, len * 0.74f, depth - 1, width * 0.72f);
            if (depth >= 3 && r.Chance(0.5)) Branch(ex, ey, ang + (float)r.Jitter(0.1), len * 0.7f, depth - 2, width * 0.6f);
        }
        Branch(x, y + s * 0.03f, -MathF.PI / 2 + (float)r.Jitter(0.1), s * 0.26f, 4, s * 0.05f);
        k.Sil(path, 0.04, 0);
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

    /// <summary>A toadstool. Lit ones are the storybook kind: a warm glowing cap with cream
    /// spots and a lit underside.</summary>
    private static void Mushroom(K k)
    {
        var (x, y, s) = (k.X, k.Y, k.S);
        var f = new Frame(x, y, s);
        var stemCol = Local(k, 0.86, 0.035, 80);
        var capCol = k.P.Lit ? new Oklch(0.78, 0.11, 62) : Local(k, 0.58, 0.12, 30);
        if (k.P.Lit) Paint.Glow(k.C, x, y - s * 0.55f, s * 1.5f, Paint.Sk(k.Colors.WindowGlow), 0.32);
        using var stem = Blob(f, (-0.13, 0.0), (-0.1, 0.36), (-0.08, 0.56), (0.08, 0.56), (0.1, 0.36), (0.13, 0.0));
        Coat(k, stem, stemCol, 0.6, 0.6);
        using var cap = Blob(f, (-0.44, 0.5), (-0.36, 0.76), (0.0, 0.9), (0.36, 0.76), (0.44, 0.5), (0.0, 0.56));
        Coat(k, cap, capCol, 0.7, 0.8);
        using var gills = Oval(f, 0, 0.53, 0.36, 0.04);
        Mark(k, cap, gills, k.P.Lit ? ScenePalette.AmberSoft : stemCol.Lighter(-0.1), 0.9, 0.02);
        using var spots = new SKPath();
        foreach (var (su, sv, sr) in new[] { (-0.2, 0.7, 0.06), (0.06, 0.8, 0.05), (0.24, 0.66, 0.045), (-0.05, 0.65, 0.035) })
            spots.AddCircle(f.U(su), f.V(sv), f.L(sr));
        Mark(k, cap, spots, new Oklch(0.94, 0.03, 85), 0.95, 0.01);
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
        // The pool mirrors the sky: always lighter than the sand around it, never a pit.
        using var water = new SKPaint
        {
            IsAntialias = true,
            IsDither = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, Math.Max(0.6f, s * 0.008f)),
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, rect.Top), new SKPoint(0, rect.Bottom),
                [Paint.Sk(k.Colors.SkyHorizon.Lighter(0.02)), Paint.Sk(k.Colors.SkyMid.Lighter(-0.03))], SKShaderTileMode.Clamp),
        };
        k.C.DrawOval(rect, water);
        using var farEdge = Paint.Stroke(Paint.Sk(ScenePalette.Cream, 0.15), Math.Max(0.7f, s * 0.01f));
        k.C.DrawArc(rect, 200, 140, false, farEdge);
        if (k.Plan.Moon is not null)
        {
            // One soft, horizontally smeared moon highlight — light on water, not a ladder.
            using var glint = new SKPaint
            {
                IsAntialias = true,
                Color = Paint.Sk(ScenePalette.Cream, 0.4),
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, Math.Max(0.8f, s * 0.02f)),
            };
            var gx = x + (float)(k.Plan.Moon!.X - k.P.X) * s * 0.4f;
            k.C.DrawOval(gx, y - s * 0.01f, s * 0.16f, s * 0.018f, glint);
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

    /// <summary>A cabin or a house in local colour: timber or plaster walls, a painted roof
    /// (red, slate or moss by seed), lit windows, a door, smoke from a lit cabin.</summary>
    private static void Cabin(K k, bool cabin)
    {
        var (x, y, s, r) = (k.X, k.Y, k.S, k.R);
        var w = s * (cabin ? 1.05f : (float)r.Range(0.75, 0.95));
        var wallH = s * (cabin ? 0.48f : (float)r.Range(0.45, 0.6));
        var roofH = s * (k.P.Variant == 1 ? 0.5f : 0.36f);
        var left = x - w / 2;
        var wallTop = y - wallH;
        var f = new Frame(x, y, s);
        Shadow(k, w * 0.62f);
        var wallLocal = cabin ? new Oklch(0.56, 0.075, 52) : r.Pick(new[] { new Oklch(0.8, 0.04, 80), new Oklch(0.6, 0.075, 40), new Oklch(0.7, 0.05, 70) });
        var roofLocal = r.Pick(new[] { new Oklch(0.48, 0.11, 30), new Oklch(0.46, 0.05, 245), new Oklch(0.48, 0.07, 150), new Oklch(0.42, 0.06, 50) });
        var wall = Local(k, wallLocal.L, wallLocal.C, wallLocal.H);
        var roofCol = Local(k, roofLocal.L, roofLocal.C, roofLocal.H);
        using var walls = new SKPath();
        walls.AddRect(new SKRect(left, wallTop, left + w, y + s * 0.02f));
        var chimneyX = x + w * (float)r.Range(0.18, 0.3);
        var hasChimney = cabin || r.Chance(0.6);
        if (hasChimney)
        {
            using var ch = new SKPath();
            ch.AddRect(new SKRect(chimneyX - w * 0.06f, wallTop - roofH * 0.85f, chimneyX + w * 0.06f, wallTop - roofH * 0.2f));
            Coat(k, ch, Local(k, 0.5, 0.06, 35), 0.6, 0.5);
        }
        Coat(k, walls, wall, 0.7, 0.5);
        if (cabin)
        {
            using var logs = Paint.Stroke(Paint.Sk(wall.Lighter(-0.08), 0.6), Math.Max(0.6f, s * 0.012f));
            for (var i = 1; i < 5; i++) k.C.DrawLine(left, wallTop + wallH * i / 5f, left + w, wallTop + wallH * i / 5f, logs);
        }
        var over = w * 0.08f;
        using var roof = new SKPath();
        roof.MoveTo(left - over, wallTop + s * 0.02f);
        roof.LineTo(x, wallTop - roofH);
        roof.LineTo(left + w + over, wallTop + s * 0.02f);
        roof.Close();
        Coat(k, roof, roofCol, 0.7, 0.6);

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
            k.Flat(snow, k.Colors.Snow.Lighter(0.16), 0.95);
        }

        // Door + windows: warm when lit; at most two lit panes.
        var lit = k.P.Lit;
        var ww = w * 0.17f;
        var wh = wallH * 0.32f;
        var wy = wallTop + wallH * 0.24f;
        Window(k, left + w * 0.14f, wy, ww, wh, lit);
        Window(k, left + w * 0.69f, wy, ww, wh, lit && (cabin || r.Chance(0.5)));
        using var door = Paint.Fill(Paint.Sk(roofCol.Lighter(-0.06)));
        k.C.DrawRoundRect(x - w * 0.08f, y - wallH * 0.58f, w * 0.16f, wallH * 0.58f + 1, w * 0.04f, w * 0.04f, door);
        using var knob = Paint.Fill(Paint.Sk(ScenePalette.AmberSoft, 0.9));
        k.C.DrawCircle(x + w * 0.04f, y - wallH * 0.28f, Math.Max(0.5f, w * 0.012f), knob);
        if (cabin && lit && hasChimney) Smoke(k, chimneyX, wallTop - roofH * 0.85f, s * 0.9f);
        _ = f;
    }

    private static void Castle(K k)
    {
        var (x, y, s, r) = (k.X, k.Y, k.S, k.R);
        var stone = Local(k, 0.72, 0.035, 75);
        var roofCol = r.Chance(0.5) ? Local(k, 0.58, 0.1, 350) : Local(k, 0.55, 0.08, 250);
        var pennant = k.Colors.Grade(k.Colors.Accent2);
        using var path = new SKPath();
        using var caps = new SKPath();
        using var flags = new SKPath();
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
            caps.MoveTo(tx - tw * 0.65f, y - th + 1);
            caps.LineTo(tx, y - th - tw * 1.6f);
            caps.LineTo(tx + tw * 0.65f, y - th + 1);
            caps.Close();
            flags.AddRect(new SKRect(tx - 0.6f, y - th - tw * 1.6f - s * 0.08f, tx + 0.6f, y - th - tw * 1.6f));
            flags.MoveTo(tx + 0.6f, y - th - tw * 1.6f - s * 0.08f);
            flags.LineTo(tx + s * 0.07f, y - th - tw * 1.6f - s * 0.065f);
            flags.LineTo(tx + 0.6f, y - th - tw * 1.6f - s * 0.05f);
            flags.Close();
        }
        Tower(x - keepW * 0.62f, s * 0.7f, s * 0.15f);
        Tower(x + keepW * 0.62f, s * 0.62f, s * 0.14f);
        Tower(x + (float)r.Jitter(0.05) * s, s * 0.9f, s * 0.12f);
        using var body = path.Simplify() ?? new SKPath(path);
        Coat(k, body, stone, 0.7, 0.6);
        // Stone courses, a gate.
        using var courses = Paint.Stroke(Paint.Sk(stone.Lighter(-0.08), 0.5), Math.Max(0.5f, s * 0.006f));
        k.C.Save();
        k.C.ClipPath(body, SKClipOperation.Intersect, true);
        for (var i = 1; i < 9; i++) k.C.DrawLine(x - s, y - s * 0.1f * i, x + s, y - s * 0.1f * i, courses);
        k.C.Restore();
        using var gate = new SKPath();
        gate.AddRoundRect(new SKRect(x - s * 0.06f, y - s * 0.2f, x + s * 0.06f, y + s * 0.03f), s * 0.06f, s * 0.06f);
        Coat(k, gate, Local(k, 0.42, 0.06, 45), 0.6, 0.4);
        Coat(k, caps, roofCol, 0.7, 0.6);
        using var fp = Paint.Fill(Paint.Sk(pennant));
        k.C.DrawPath(flags, fp);
        // In a cloud kingdom the castle rises OUT of the clouds: a skirt of puffs over its foot.
        if (k.Plan.Spec.Scene == PosterScene.CloudKingdom)
        {
            var band = k.Plan.Bands.FirstOrDefault(b => b.Depth == k.P.Depth);
            if (band is not null)
            {
                using var skirt = new SKPath();
                for (var i = -3; i <= 3; i++)
                    skirt.AddCircle(x + i * s * 0.13f, y + s * 0.02f - (3 - Math.Abs(i)) * s * 0.012f, s * (0.1f + (float)r.Range(0, 0.04)));
                using var merged = skirt.Simplify() ?? new SKPath(skirt);
                Paint.Silhouette(k.C, merged, TerrainPainter.BandColor(k.Plan, band).Lighter(0.04), ScenePalette.Cream, 0.4, 0, Math.Max(1f, s * 0.02f), 0.03, 0.03);
            }
        }
        if (k.P.Lit)
        {
            Window(k, x - keepW * 0.25f, y - keepH * 0.6f, s * 0.05f, s * 0.08f, true);
            Window(k, x + keepW * 0.08f, y - keepH * 0.6f, s * 0.05f, s * 0.08f, r.Chance(0.6));
            Window(k, x + keepW * 0.62f - s * 0.022f, y - s * 0.45f, s * 0.045f, s * 0.07f, true);
            Window(k, x - keepW * 0.62f - s * 0.022f, y - s * 0.5f, s * 0.045f, s * 0.07f, r.Chance(0.5));
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
            Coat(k, arm, Local(k, 0.6, 0.05, 50), 0.5, 0.3);
        }
        Coat(k, path, Local(k, 0.76, 0.04, 75), 0.7, 0.6);
        Window(k, x - s * 0.03f, y - s * 0.3f, s * 0.06f, s * 0.09f, k.P.Lit || r.Chance(0.6));
    }

    /// <summary>A bell tent seen at three-quarters: a lit front with its flaps tied back, a
    /// darker side running back, guy ropes, bunting and a pennant on the pole.</summary>
    private static void Tent(K k)
    {
        var (x, y, s, r) = (k.X, k.Y, k.S, k.R);
        var f = new Frame(x, y, s);
        Shadow(k, s * 0.66f, s * 0.08f);
        var canvasCol = r.Chance(0.5) ? Local(k, 0.76, 0.07, 72) : Local(k, 0.64, 0.1, 34);
        var pennant = k.Colors.Grade(k.Colors.Accent2);
        using var ropes = Paint.Stroke(Paint.Sk(canvasCol.Lighter(-0.25), 0.7), Math.Max(0.6f, s * 0.008f));
        k.C.DrawLine(f.U(-0.04), f.V(0.62), f.U(-0.66), f.V(0.0), ropes);
        k.C.DrawLine(f.U(0.26), f.V(0.56), f.U(0.82), f.V(0.0), ropes);
        using var pole = Paint.Stroke(Paint.Sk(Local(k, 0.42, 0.05, 50)), Math.Max(0.8f, s * 0.018f));
        k.C.DrawLine(f.U(-0.04), f.V(0.6), f.U(-0.04), f.V(0.8), pole);
        using var flag = Blob(f, (-0.04, 0.8), (0.1, 0.77), (-0.04, 0.73));
        using var fp = Paint.Fill(Paint.Sk(pennant));
        k.C.DrawPath(flag, fp);
        // The side, running back and darker.
        using var side = Blob(f, (-0.04, 0.66), (0.24, 0.6), (0.6, 0.02), (0.44, 0.0), (0.38, 0.02));
        Coat(k, side, canvasCol.Lighter(-0.09), 0.7, 0.5);
        // The front: a soft bell curve.
        using var front = new SKPath();
        front.MoveTo(f.P(-0.52, 0.0));
        front.CubicTo(f.P(-0.36, 0.12), f.P(-0.14, 0.4), f.P(-0.04, 0.66));
        front.CubicTo(f.P(0.06, 0.4), f.P(0.26, 0.12), f.P(0.42, 0.0));
        front.Close();
        Coat(k, front, canvasCol, 0.8, 0.7);
        // The doorway, its flaps tied back.
        using var door = new SKPath();
        door.MoveTo(f.P(-0.2, 0.0));
        door.QuadTo(f.P(-0.1, 0.22), f.P(-0.04, 0.46));
        door.QuadTo(f.P(0.02, 0.22), f.P(0.12, 0.0));
        door.Close();
        if (k.P.Lit)
        {
            Paint.Glow(k.C, f.U(-0.04), f.V(0.14), f.L(0.7), Paint.Sk(k.Colors.WindowGlow), 0.4);
            using var dp = new SKPaint
            {
                IsAntialias = true,
                Shader = SKShader.CreateLinearGradient(f.P(0, 0.46), f.P(0, 0.0),
                    [Paint.Sk(k.Colors.Window.Lighter(-0.08)), Paint.Sk(ScenePalette.AmberSoft), Paint.Sk(new Oklch(0.92, 0.08, 85))], SKShaderTileMode.Clamp),
            };
            k.C.DrawPath(door, dp);
            using var pillow = Oval(f, -0.04, 0.05, 0.08, 0.035);
            Coat(k, pillow, k.Colors.Grade(k.Colors.Accent2).Lighter(0.05), 0.5, 0.3);
        }
        else k.Flat(door, canvasCol.Lighter(-0.22));
        using var flapL = Blob(f, (-0.2, 0.0), (-0.1, 0.22), (-0.04, 0.46), (-0.2, 0.2), (-0.3, 0.02));
        using var flapR = Blob(f, (0.12, 0.0), (0.02, 0.22), (-0.04, 0.46), (0.1, 0.2), (0.2, 0.02));
        Coat(k, flapL, canvasCol.Lighter(0.04), 0.6, 0.5);
        Coat(k, flapR, canvasCol.Lighter(0.04), 0.6, 0.5);
        using var tie = Paint.Fill(Paint.Sk(pennant));
        k.C.DrawCircle(f.U(-0.24), f.V(0.12), Math.Max(0.6f, f.L(0.018)), tie);
        k.C.DrawCircle(f.U(0.16), f.V(0.12), Math.Max(0.6f, f.L(0.018)), tie);
        // Bunting along the front edge.
        for (var i = 0; i < 5; i++)
        {
            var t = 0.2 + i * 0.15;
            var bu = -0.04 + (0.42 + 0.04) * t;
            var bv = 0.66 * (1 - t) - 0.01;
            using var tri = Blob(f, (bu - 0.025, bv), (bu + 0.025, bv - 0.01), (bu, bv - 0.06));
            using var tp = Paint.Fill(Paint.Sk(i % 2 == 0 ? pennant : k.Colors.Grade(CreamLocal)));
            k.C.DrawPath(tri, tp);
        }
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
            using var glass = new SKPaint
            {
                IsAntialias = true,
                Shader = SKShader.CreateRadialGradient(new SKPoint(x, y - s * 0.42f), s * 0.42f,
                    [Paint.Sk(new Oklch(0.94, 0.08, 88)), Paint.Sk(ScenePalette.AmberSoft), Paint.Sk(k.Colors.Window)], [0f, 0.55f, 1f], SKShaderTileMode.Clamp),
            };
            k.C.DrawRoundRect(x - s * 0.28f, y - s * 0.74f, s * 0.56f, s * 0.64f, s * 0.08f, s * 0.08f, glass);
            Paint.Glow(k.C, x, y - s * 0.42f, s * 0.9f, Paint.Sk(ScenePalette.AmberSoft), 0.5);
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

    /// <summary>A brass telescope on a wooden tripod, aimed up at the sky.</summary>
    private static void Telescope(K k)
    {
        var (x, y, s) = (k.X, k.Y, k.S);
        var wood = Local(k, 0.48, 0.07, 50);
        var brass = Local(k, 0.74, 0.11, 78);
        Shadow(k, s * 0.3f);
        using var legs = new SKPath();
        using (var st = Paint.Stroke(SKColors.White, Math.Max(0.8f, s * 0.035f)))
        {
            foreach (var (lx, ly) in new[] { (-0.24f, 0.02f), (0.22f, 0.02f), (0.02f, 0.03f) })
            {
                using var leg = new SKPath();
                leg.MoveTo(x, y - s * 0.45f);
                leg.LineTo(x + s * lx, y + s * ly);
                AddStroke(legs, leg, st);
            }
        }
        Coat(k, legs, wood, 0.6, 0.4);
        using var tube = new SKPath();
        tube.AddRoundRect(new SKRect(-s * 0.34f, -s * 0.06f, s * 0.34f, s * 0.06f), s * 0.04f, s * 0.04f);
        using var lens = new SKPath();
        lens.AddRoundRect(new SKRect(s * 0.28f, -s * 0.085f, s * 0.44f, s * 0.085f), s * 0.025f, s * 0.025f);
        using var eye = new SKPath();
        eye.AddRoundRect(new SKRect(-s * 0.42f, -s * 0.035f, -s * 0.32f, s * 0.035f), s * 0.01f, s * 0.01f);
        var m = SKMatrix.CreateRotationDegrees(-36).PostConcat(SKMatrix.CreateTranslation(x, y - s * 0.52f));
        tube.Transform(m);
        lens.Transform(m);
        eye.Transform(m);
        Coat(k, tube, Local(k, 0.5, 0.09, 250), 0.7, 0.6);
        Coat(k, lens, brass, 0.7, 0.6);
        Coat(k, eye, brass, 0.6, 0.5);
        using var bands = new SKPath();
        bands.AddRect(new SKRect(-s * 0.05f, -s * 0.065f, s * 0.0f, s * 0.065f));
        bands.Transform(m);
        Mark(k, tube, bands, brass, 0.9, 0.01);
        using var star = Paint.Fill(Paint.Sk(ScenePalette.Cream, 0.9));
        var tip = m.MapPoint(new SKPoint(s * 0.44f, 0));
        k.C.DrawCircle(tip.X, tip.Y, Math.Max(0.6f, s * 0.018f), star);
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
        var (x, y, s, r) = (k.X, k.Y, k.S, k.R);
        var hullCol = r.Chance(0.5) ? Local(k, 0.56, 0.12, 30) : Local(k, 0.55, 0.08, 235);
        var wood = Local(k, 0.5, 0.06, 55);
        using var hull = new SKPath();
        hull.MoveTo(x - s * 0.55f, y - s * 0.2f);
        hull.QuadTo(x - s * 0.42f, y + s * 0.02f, x - s * 0.2f, y + s * 0.02f);
        hull.LineTo(x + s * 0.28f, y + s * 0.02f);
        hull.QuadTo(x + s * 0.48f, y + s * 0.0f, x + s * 0.56f, y - s * 0.22f);
        hull.QuadTo(x, y - s * 0.12f, x - s * 0.55f, y - s * 0.2f);
        hull.Close();
        using var mast = new SKPath();
        mast.AddRect(new SKRect(x + s * 0.11f, y - s * 0.66f, x + s * 0.14f, y - s * 0.12f));
        mast.AddRect(new SKRect(x + s * 0.11f, y - s * 0.62f, x + s * 0.24f, y - s * 0.6f));
        using var all = new SKPath(hull);
        all.AddPath(mast);
        Reflect(k, all, y + s * 0.02f, 0.35);
        Coat(k, mast, wood, 0.6, 0.4);
        Coat(k, hull, hullCol, 0.8, 0.6);
        using var stripe = new SKPath();
        stripe.MoveTo(x - s * 0.5f, y - s * 0.14f);
        stripe.QuadTo(x, y - s * 0.07f, x + s * 0.52f, y - s * 0.16f);
        using (var st = Paint.Stroke(Paint.Sk(k.Colors.Grade(CreamLocal), 0.9), Math.Max(0.8f, s * 0.025f))) k.C.DrawPath(stripe, st);
        // Oars shipped along the gunwale.
        using var oar = Paint.Stroke(Paint.Sk(wood, 0.95), Math.Max(0.7f, s * 0.02f));
        k.C.DrawLine(x - s * 0.42f, y - s * 0.24f, x + s * 0.02f, y - s * 0.15f, oar);
        if (k.P.Lit)
        {
            var lx = x + s * 0.24f;
            var ly = y - s * 0.52f;
            Paint.Glow(k.C, lx, ly, s * 0.6f, Paint.Sk(k.Colors.WindowGlow), 0.5);
            using var lp = Paint.Fill(Paint.Sk(k.Colors.Window));
            k.C.DrawRoundRect(lx - s * 0.04f, ly - s * 0.055f, s * 0.08f, s * 0.11f, s * 0.02f, s * 0.02f, lp);
            using var cap = Paint.Fill(Paint.Sk(wood.Lighter(-0.1)));
            k.C.DrawRect(lx - s * 0.045f, ly - s * 0.075f, s * 0.09f, s * 0.025f, cap);
            using var refl = Paint.Fill(Paint.Sk(k.Colors.Window, 0.45));
            for (var i = 0; i < 4; i++)
                k.C.DrawRoundRect(lx - s * (0.04f + i * 0.015f), y + s * (0.08f + i * 0.07f), s * (0.08f + i * 0.03f), Math.Max(0.7f, s * 0.014f), 1, 1, refl);
        }
    }

    private static void Sailboat(K k)
    {
        var (x, y, s, r) = (k.X, k.Y, k.S, k.R);
        var hullCol = r.Chance(0.5) ? Local(k, 0.55, 0.12, 28) : Local(k, 0.52, 0.08, 240);
        using var hull = new SKPath();
        hull.MoveTo(x - s * 0.42f, y - s * 0.12f);
        hull.QuadTo(x - s * 0.3f, y + s * 0.02f, x - s * 0.15f, y + s * 0.02f);
        hull.LineTo(x + s * 0.25f, y + s * 0.02f);
        hull.QuadTo(x + s * 0.4f, y, x + s * 0.46f, y - s * 0.14f);
        hull.Close();
        using var mast = new SKPath();
        mast.AddRect(new SKRect(x - s * 0.012f, y - s * 0.95f, x + s * 0.012f, y - s * 0.08f));
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
        Coat(k, mast, Local(k, 0.5, 0.06, 55), 0.5, 0.4);
        var sailCol = Local(k, 0.9, 0.025, 80);
        Coat(k, sail, sailCol, 0.6, 0.7);
        Coat(k, jib, sailCol.Lighter(-0.04), 0.6, 0.7);
        using var band = new SKPath();
        band.AddRect(new SKRect(x, y - s * 0.42f, x + s * 0.4f, y - s * 0.36f));
        Mark(k, sail, band, k.Colors.Grade(k.Colors.Accent2), 0.85, 0.01);
        Coat(k, hull, hullCol, 0.8, 0.6);
        using var flag = new SKPath();
        flag.MoveTo(x, y - s * 0.95f);
        flag.LineTo(x + s * 0.1f, y - s * 0.92f);
        flag.LineTo(x, y - s * 0.89f);
        flag.Close();
        using var fp = Paint.Fill(Paint.Sk(k.Colors.Grade(k.Colors.Accent2)));
        k.C.DrawPath(flag, fp);
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
        var towerColor = Local(k, 0.88, 0.025, 80);
        Paint.Silhouette(k.C, tower, towerColor, ScenePalette.Cream, 0.5, k.RimDx, k.RimDy, 0.03, 0.08);
        // Stripes
        k.C.Save();
        k.C.ClipPath(tower, SKClipOperation.Intersect, true);
        using var stripe = Paint.Fill(Paint.Sk(Local(k, 0.55, 0.13, 25), 0.95));
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
}
