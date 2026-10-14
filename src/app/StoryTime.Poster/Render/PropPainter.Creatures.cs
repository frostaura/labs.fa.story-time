using SkiaSharp;
using StoryTime.Contracts;
using StoryTime.Poster.Color;
using StoryTime.Poster.Composition;
using StoryTime.Poster.Core;

namespace StoryTime.Poster.Render;

/// <summary>The creature sheet. Every creature is drawn from one shape language, the one
/// warm bedtime books share: a big round head on a small soft body, rounded forms with no
/// sharp points, a sleepy, kind face (closed smiling eyes, a dot of a nose, a blush), local
/// colour kept (a fox is orange, a turtle green) and graded into the night, a gradient
/// within each form toward its light, a cool moon rim, a warm rim from the nearest lantern
/// and a soft ink contour so the character still reads at card size.
///
/// Coordinates are authored in the creature's own unit: u across (facing +u, the flip
/// mirrors it), v up from the anchor at its base, 1 = the size the planner gave it. Every
/// standing creature is ~1 unit tall; the wide ones (curled fox, swimming whale, turtle,
/// fish school) declare their height in <see cref="HeightFactor"/>.</summary>
internal static partial class PropPainter
{
    public static bool IsCreatureProp(Prop p) => p is Prop.Fox or Prop.FoxCurled or Prop.Rabbit or Prop.Owl or Prop.Deer
        or Prop.Bear or Prop.Cat or Prop.Hedgehog or Prop.Otter or Prop.Whale or Prop.Turtle or Prop.FishSchool
        or Prop.Camel or Prop.Dinosaur;

    /// <summary>Drawn height as a fraction of the placement size (the planner sizes heroes
    /// by what is actually drawn).</summary>
    public static double HeightFactor(Prop p, int variant) => p switch
    {
        Prop.FoxCurled => 0.62,
        Prop.Whale => variant == 1 ? 0.54 : 0.86,
        Prop.Turtle => variant == 1 ? 0.66 : 0.7,
        Prop.FishSchool => 0.9,
        Prop.Hedgehog => 0.7,
        Prop.Camel => 0.96,
        _ => 1.0,
    };

    // ------------------------------------------------------------------ the kit

    private readonly record struct Frame(float X, float Y, float S)
    {
        public float U(double u) => X + (float)u * S;
        public float V(double v) => Y - (float)v * S;
        public SKPoint P(double u, double v) => new(U(u), V(v));
        public float L(double len) => (float)len * S;
    }

    private static SKPath Oval(Frame f, double u, double v, double ru, double rv, double rotDeg = 0)
    {
        var path = new SKPath();
        path.AddOval(new SKRect(f.U(u - ru), f.V(v + rv), f.U(u + ru), f.V(v - rv)));
        if (rotDeg != 0) path.Transform(SKMatrix.CreateRotationDegrees((float)-rotDeg, f.U(u), f.V(v)));
        return path;
    }

    /// <summary>A closed path through the given (u, v) points as a smooth Catmull-Rom loop.</summary>
    private static SKPath Blob(Frame f, params (double U, double V)[] pts)
    {
        var path = new SKPath();
        var n = pts.Length;
        SKPoint Pt(int i) => f.P(pts[(i % n + n) % n].U, pts[(i % n + n) % n].V);
        path.MoveTo(Pt(0));
        for (var i = 0; i < n; i++)
        {
            var p0 = Pt(i - 1);
            var p1 = Pt(i);
            var p2 = Pt(i + 1);
            var p3 = Pt(i + 2);
            path.CubicTo(
                p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6,
                p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6,
                p2.X, p2.Y);
        }
        path.Close();
        return path;
    }

    /// <summary>The creature's colour for this poster: local colour graded into the night,
    /// hazed a little more when it stands back on the mid layer.</summary>
    private static Oklch Local(K k, double l, double c, double h)
    {
        var graded = k.Colors.Grade(new Oklch(l, c, h));
        if (k.P.Depth < 3) graded = Oklab.Mix(graded.ToOklab(), k.Colors.Haze.ToOklab(), (3 - k.P.Depth) * 0.14).ToOklch();
        return graded;
    }

    /// <summary>One painted form: a soft gradient toward its light, a moon rim, a warm rim
    /// from the nearest lantern, and a soft ink contour.</summary>
    private static void Coat(K k, SKPath path, Oklch col, double contour = 1, double form = 1)
    {
        var b = path.Bounds;
        if (b.IsEmpty) return;
        var lightDir = k.WarmAmt > 0.12 ? k.WarmDir : -Math.Sign(k.RimDx);
        var gx = b.MidX + b.Width * 0.28f * lightDir;
        var gy = b.Top + b.Height * 0.3f;
        var radius = Math.Max(b.Width, b.Height) * 0.95f;
        using (var fill = new SKPaint
               {
                   IsAntialias = true,
                   IsDither = true,
                   Shader = SKShader.CreateRadialGradient(new SKPoint(gx, gy), Math.Max(1, radius),
                       [Paint.Sk(col.Lighter(0.06 * form)), Paint.Sk(col), Paint.Sk(col.Lighter(-0.075 * form).ScaleC(0.92))],
                       [0f, 0.5f, 1f], SKShaderTileMode.Clamp),
               })
            k.C.DrawPath(path, fill);
        var rw = Math.Max(0.8f, k.S * 0.022f);
        if (k.RimAlpha > 0) Paint.Rim(k.C, path, Paint.Sk(k.Colors.Rim, Math.Min(0.75, k.RimAlpha * 0.85)), k.RimDx, rw * 0.8f);
        if (k.WarmAmt > 0.05)
            Paint.Rim(k.C, path, Paint.Sk(k.Colors.WarmLight, Math.Min(0.8, k.WarmAmt * 0.85)), -k.WarmDir * rw * 1.2f, rw * 0.35f);
        if (contour > 0)
        {
            using var line = Paint.Stroke(Paint.Sk(col.Lighter(-0.26).ScaleC(0.8), 0.55 * contour), Math.Max(0.7f, k.S * 0.012f));
            k.C.DrawPath(path, line);
        }
    }

    /// <summary>A soft lighter (or darker) patch clipped inside a form: a chest, a muzzle.</summary>
    private static void Mark(K k, SKPath clip, SKPath patch, Oklch col, double alpha = 1, double soften = 0.05)
    {
        k.C.Save();
        k.C.ClipPath(clip, SKClipOperation.Intersect, true);
        using var p = new SKPaint
        {
            IsAntialias = true,
            Color = Paint.Sk(col, alpha),
            MaskFilter = soften > 0 ? SKMaskFilter.CreateBlur(SKBlurStyle.Normal, Math.Max(0.4f, k.S * (float)soften * 0.25f)) : null,
        };
        k.C.DrawPath(patch, p);
        k.C.Restore();
    }

    /// <summary>A closed, smiling sleepy eye (‿) with two tiny lashes.</summary>
    private static void SleepyEye(K k, Frame f, double u, double v, double w, Oklch ink)
    {
        var sw = Math.Max(0.8f, k.S * 0.016f);
        using var lid = Paint.Stroke(Paint.Sk(ink, 0.95), sw);
        var r = new SKRect(f.U(u - w), f.V(v + w * 0.7), f.U(u + w), f.V(v - w * 0.7));
        k.C.DrawArc(r, 20, 140, false, lid);
        using var lash = Paint.Stroke(Paint.Sk(ink, 0.8), sw * 0.7f);
        k.C.DrawLine(f.U(u + w * 0.85), f.V(v - w * 0.05), f.U(u + w * 1.25), f.V(v - w * 0.25), lash);
        k.C.DrawLine(f.U(u + w * 0.45), f.V(v - w * 0.5), f.U(u + w * 0.6), f.V(v - w * 0.9), lash);
    }

    /// <summary>A soft open eye: a dark oval with a cream catch-light (curious moods).</summary>
    private static void OpenEye(K k, Frame f, double u, double v, double r, Oklch ink)
    {
        using var eye = Paint.Fill(Paint.Sk(ink.Lighter(-0.04)));
        k.C.DrawOval(f.U(u), f.V(v), f.L(r * 0.8), f.L(r), eye);
        using var glint = Paint.Fill(Paint.Sk(ScenePalette.Cream, 0.95));
        k.C.DrawCircle(f.U(u + r * 0.3), f.V(v + r * 0.35), Math.Max(0.5f, f.L(r * 0.32)), glint);
    }

    private static void Eye(K k, Frame f, double u, double v, double w, Oklch ink)
    {
        if (k.Plan.Spec.Mood == PosterMood.Curious) OpenEye(k, f, u, v, w * 0.62, ink);
        else SleepyEye(k, f, u, v, w, ink);
    }

    private static void Blush(K k, Frame f, double u, double v, double r)
    {
        using var p = new SKPaint
        {
            IsAntialias = true,
            Color = Paint.Sk(new Oklch(0.72, 0.09, 18), 0.38),
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, Math.Max(0.5f, f.L(r * 0.45))),
        };
        k.C.DrawOval(f.U(u), f.V(v), f.L(r * 1.2), f.L(r * 0.8), p);
    }

    private static void Nose(K k, Frame f, double u, double v, double w, Oklch ink)
    {
        using var path = Blob(f, (u - w, v + w * 0.4), (u + w, v + w * 0.4), (u + w * 0.15, v - w * 0.6), (u - w * 0.15, v - w * 0.6));
        using var p = Paint.Fill(Paint.Sk(ink));
        k.C.DrawPath(path, p);
        using var hi = Paint.Fill(Paint.Sk(ScenePalette.Cream, 0.5));
        k.C.DrawCircle(f.U(u - w * 0.3), f.V(v + w * 0.15), Math.Max(0.4f, f.L(w * 0.25)), hi);
    }

    private static void Whiskers(K k, Frame f, double u, double v, double len, Oklch col)
    {
        using var p = Paint.Stroke(Paint.Sk(col, 0.45), Math.Max(0.5f, k.S * 0.006f));
        for (var i = -1; i <= 1; i++)
            k.C.DrawLine(f.U(u), f.V(v + i * 0.01), f.U(u + len), f.V(v + i * len * 0.25 + 0.01), p);
    }

    private static Oklch Ink(Oklch body) => new(Math.Min(0.24, body.L * 0.4), Math.Min(0.03, body.C * 0.4), body.H);

    private static readonly Oklch CreamLocal = new(0.93, 0.03, 80);

    /// <summary>Ground contact: a soft shadow plus a little occlusion line.</summary>
    private static void Contact(K k, double halfWidth, double offsetU = 0)
    {
        using var p = new SKPaint
        {
            IsAntialias = true,
            Color = SKColors.Black.WithAlpha(60),
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, Math.Max(0.8f, k.S * 0.035f)),
        };
        k.C.DrawOval(k.X + (float)offsetU * k.S, k.Y + k.S * 0.005f, (float)halfWidth * k.S, k.S * 0.04f, p);
    }

    // ------------------------------------------------------------------ creatures

    private static void Fox(K k)
    {
        var f = new Frame(k.X, k.Y, k.S);
        var fur = Local(k, 0.68, 0.15, 48);
        var cream = Local(k, CreamLocal.L, CreamLocal.C, CreamLocal.H);
        var sock = Local(k, 0.34, 0.05, 40);
        var ink = Ink(fur);
        Contact(k, 0.42, -0.06);

        // Tail: a big soft brush curling round the front paws, cream tip forward.
        using var tail = Blob(f, (-0.18, 0.05), (-0.42, 0.06), (-0.6, 0.18), (-0.56, 0.36), (-0.4, 0.3), (-0.3, 0.15), (0.0, 0.1), (0.32, 0.12),
            (0.36, 0.04), (0.1, -0.01));
        Coat(k, tail, fur);
        using var tip = Oval(f, 0.3, 0.07, 0.11, 0.07, 10);
        Mark(k, tail, tip, cream);

        // Body: a soft pear.
        using var body = Blob(f, (-0.26, 0.04), (-0.3, 0.22), (-0.2, 0.46), (0.0, 0.56), (0.18, 0.46), (0.24, 0.24), (0.2, 0.04));
        Coat(k, body, fur);
        using var bib = Blob(f, (0.02, 0.52), (0.18, 0.42), (0.17, 0.2), (0.06, 0.1), (-0.04, 0.24));
        Mark(k, body, bib, cream);
        // Front paws in dark socks, resting on the tail.
        foreach (var pu in new[] { 0.02, 0.15 })
        {
            using var leg = Blob(f, (pu - 0.045, 0.3), (pu + 0.045, 0.3), (pu + 0.05, 0.1), (pu + 0.06, 0.04), (pu - 0.05, 0.04), (pu - 0.05, 0.12));
            Coat(k, leg, fur, 0.6);
            using var s = Oval(f, pu + 0.005, 0.08, 0.06, 0.07);
            Mark(k, leg, s, sock, 1, 0.02);
        }
        if (k.P.Variant == 0) { }

        // Ears behind the head.
        using var earL = Blob(f, (-0.2, 0.74), (-0.17, 0.98), (-0.12, 0.99), (-0.02, 0.82));
        using var earR = Blob(f, (0.1, 0.84), (0.2, 1.0), (0.25, 0.99), (0.3, 0.74));
        Coat(k, earL, fur);
        Coat(k, earR, fur);
        using var innerL = Blob(f, (-0.16, 0.78), (-0.15, 0.93), (-0.06, 0.82));
        using var innerR = Blob(f, (0.15, 0.84), (0.22, 0.95), (0.25, 0.78));
        Mark(k, earL, innerL, sock, 0.9, 0.02);
        Mark(k, earR, innerR, sock, 0.9, 0.02);

        // Head: wide, with cheek fluff, a short snout toward +u.
        using var head = Blob(f, (-0.2, 0.62), (-0.24, 0.56), (-0.14, 0.5), (0.04, 0.47), (0.22, 0.5), (0.36, 0.56), (0.39, 0.61),
            (0.3, 0.7), (0.2, 0.82), (0.04, 0.85), (-0.12, 0.8));
        Coat(k, head, fur);
        using var mask = Blob(f, (-0.16, 0.56), (0.0, 0.5), (0.22, 0.51), (0.38, 0.59), (0.3, 0.64), (0.12, 0.62), (-0.06, 0.6));
        Mark(k, head, mask, cream);
        Nose(k, f, 0.38, 0.605, 0.028, ink);
        Eye(k, f, -0.02, 0.69, 0.045, ink);
        Eye(k, f, 0.17, 0.7, 0.045, ink);
        Blush(k, f, 0.23, 0.6, 0.04);
    }

    /// <summary>Asleep in a ring: nose tucked into the tail, ears still up.</summary>
    private static void FoxCurled(K k)
    {
        var f = new Frame(k.X, k.Y, k.S);
        var fur = Local(k, 0.68, 0.15, 48);
        var cream = Local(k, CreamLocal.L, CreamLocal.C, CreamLocal.H);
        var sock = Local(k, 0.34, 0.05, 40);
        var ink = Ink(fur);
        Contact(k, 0.62);
        using var body = Blob(f, (-0.58, 0.04), (-0.66, 0.2), (-0.5, 0.4), (-0.15, 0.46), (0.2, 0.42), (0.5, 0.3), (0.6, 0.12), (0.5, 0.02), (0.0, -0.01));
        Coat(k, body, fur);
        using var belly = Oval(f, 0.0, 0.08, 0.4, 0.09);
        Mark(k, body, belly, cream, 0.8);
        using var ear1 = Blob(f, (0.02, 0.4), (0.06, 0.6), (0.11, 0.6), (0.17, 0.42));
        using var ear2 = Blob(f, (0.2, 0.4), (0.27, 0.58), (0.32, 0.56), (0.35, 0.38));
        Coat(k, ear1, fur);
        Coat(k, ear2, fur);
        using var in1 = Blob(f, (0.06, 0.43), (0.09, 0.55), (0.13, 0.44));
        Mark(k, ear1, in1, sock, 0.9, 0.02);
        using var head = Blob(f, (-0.08, 0.2), (0.0, 0.38), (0.2, 0.46), (0.42, 0.36), (0.48, 0.22), (0.3, 0.14), (0.05, 0.12));
        Coat(k, head, fur);
        using var muzzle = Blob(f, (0.16, 0.22), (0.3, 0.3), (0.46, 0.26), (0.4, 0.17), (0.22, 0.16));
        Mark(k, head, muzzle, cream);
        // The tail wraps over the front, its cream tip under the chin.
        using var tail = Blob(f, (-0.62, 0.12), (-0.5, 0.0), (0.0, -0.02), (0.4, 0.02), (0.62, 0.1), (0.56, 0.18), (0.2, 0.13), (-0.3, 0.12));
        Coat(k, tail, fur);
        using var tip = Oval(f, 0.52, 0.1, 0.1, 0.07);
        Mark(k, tail, tip, cream);
        SleepyEye(k, f, 0.26, 0.31, 0.045, ink);
        Nose(k, f, 0.47, 0.25, 0.024, ink);
        Blush(k, f, 0.33, 0.24, 0.035);
    }

    private static void Rabbit(K k)
    {
        var f = new Frame(k.X, k.Y, k.S);
        var fur = Local(k, 0.72, 0.035, 65);
        var cream = Local(k, CreamLocal.L, CreamLocal.C, CreamLocal.H);
        var pink = Local(k, 0.76, 0.08, 15);
        var ink = Ink(fur);
        Contact(k, 0.42);
        // The far ear, folded back.
        using var earBack = Oval(f, -0.02, 0.82, 0.065, 0.19, 28);
        Coat(k, earBack, fur.Lighter(-0.04));
        using var body = Blob(f, (-0.42, 0.08), (-0.44, 0.26), (-0.3, 0.46), (-0.05, 0.52), (0.18, 0.4), (0.3, 0.16), (0.24, 0.02), (-0.2, 0.0));
        Coat(k, body, fur);
        using var belly = Oval(f, 0.14, 0.18, 0.13, 0.15);
        Mark(k, body, belly, cream, 0.9);
        using var tail = Oval(f, -0.44, 0.2, 0.085, 0.08);
        Coat(k, tail, cream, 0.6);
        using var paw = Oval(f, 0.22, 0.04, 0.085, 0.045);
        Coat(k, paw, fur.Lighter(0.03), 0.7);
        using var head = Oval(f, 0.17, 0.52, 0.2, 0.18);
        Coat(k, head, fur);
        using var cheek = Oval(f, 0.27, 0.47, 0.1, 0.07);
        Mark(k, head, cheek, cream, 0.9);
        // The near ear stands tall, with a pink lining.
        using var ear = Oval(f, 0.1, 0.84, 0.07, 0.2, -6);
        Coat(k, ear, fur);
        using var lining = Oval(f, 0.105, 0.85, 0.032, 0.14, -6);
        Mark(k, ear, lining, pink, 0.9, 0.02);
        Eye(k, f, 0.22, 0.56, 0.045, ink);
        Nose(k, f, 0.36, 0.5, 0.022, pink.Lighter(-0.15));
        Blush(k, f, 0.27, 0.46, 0.035);
        Whiskers(k, f, 0.35, 0.48, 0.1, cream);
    }

    /// <summary>A round sleepy owl on a branch stub (the planner stands its tree at the frame
    /// edge on the +u side).</summary>
    private static void Owl(K k)
    {
        var f = new Frame(k.X, k.Y, k.S);
        var feather = Local(k, 0.56, 0.07, 55);
        var disc = Local(k, 0.8, 0.045, 75);
        var beak = Local(k, 0.76, 0.11, 75);
        var bark = Local(k, 0.38, 0.04, 45);
        var ink = Ink(feather);
        // The branch, reaching in from the tree at the frame edge.
        using var branch = Blob(f, (-0.34, 0.02), (-0.2, 0.06), (0.2, 0.07), (2.0, 0.12), (2.0, -0.05), (0.2, -0.05), (-0.2, -0.04));
        Coat(k, branch, bark, 0.8);
        using var leaf = Blob(f, (-0.34, 0.02), (-0.46, 0.1), (-0.58, 0.07), (-0.46, 0.0));
        Coat(k, leaf, Local(k, 0.55, 0.1, 140), 0.6);
        using var body = Blob(f, (-0.28, 0.12), (-0.33, 0.42), (-0.24, 0.72), (-0.18, 0.92), (-0.08, 0.84), (0.08, 0.84), (0.18, 0.92),
            (0.24, 0.72), (0.33, 0.42), (0.28, 0.12), (0.0, 0.04));
        Coat(k, body, feather);
        using var chest = Oval(f, 0.0, 0.3, 0.2, 0.22);
        Mark(k, body, chest, disc.Lighter(-0.05), 0.85);
        // Chevron speckles on the chest.
        using var speck = Paint.Stroke(Paint.Sk(feather, 0.75), Math.Max(0.6f, k.S * 0.012f));
        for (var i = 0; i < 5; i++)
        {
            var su = -0.1 + (i % 3) * 0.1;
            var sv = 0.38 - (i / 3) * 0.1 - (i % 2) * 0.03;
            k.C.DrawLine(f.U(su - 0.025), f.V(sv + 0.015), f.U(su), f.V(sv), speck);
            k.C.DrawLine(f.U(su), f.V(sv), f.U(su + 0.025), f.V(sv + 0.015), speck);
        }
        using var wingL = Oval(f, -0.27, 0.36, 0.08, 0.22, -12);
        using var wingR = Oval(f, 0.27, 0.36, 0.08, 0.22, 12);
        Coat(k, wingL, feather.Lighter(-0.05), 0.8);
        Coat(k, wingR, feather.Lighter(-0.05), 0.8);
        using var faceL = Oval(f, -0.11, 0.63, 0.14, 0.13);
        using var faceR = Oval(f, 0.11, 0.63, 0.14, 0.13);
        using var face = faceL.Op(faceR, SKPathOp.Union) ?? new SKPath(faceL);
        Coat(k, face, disc, 0.5, 0.5);
        Eye(k, f, -0.1, 0.64, 0.06, ink);
        Eye(k, f, 0.1, 0.64, 0.06, ink);
        using var b = Blob(f, (-0.03, 0.58), (0.03, 0.58), (0.0, 0.5));
        Coat(k, b, beak, 0.5);
        Blush(k, f, -0.17, 0.57, 0.035);
        Blush(k, f, 0.17, 0.57, 0.035);
        using var talon = Paint.Stroke(Paint.Sk(beak, 0.95), Math.Max(0.7f, k.S * 0.02f));
        foreach (var tu in new[] { -0.09, 0.09 })
            k.C.DrawLine(f.U(tu - 0.03), f.V(0.06), f.U(tu + 0.03), f.V(0.06), talon);
    }

    /// <summary>A fawn. Variant 0 stands, head up and ears wide; variant 1 lies folded with
    /// its head raised.</summary>
    private static void Deer(K k)
    {
        var f = new Frame(k.X, k.Y, k.S);
        var coat = Local(k, 0.66, 0.11, 58);
        var cream = Local(k, CreamLocal.L, CreamLocal.C, CreamLocal.H);
        var hoof = Local(k, 0.3, 0.03, 45);
        var ink = Ink(coat);
        var lying = k.P.Variant == 1;
        Contact(k, lying ? 0.46 : 0.36, -0.05);
        SKPath body;
        double hu, hv;
        if (lying)
        {
            using var legs = Blob(f, (0.0, 0.0), (0.32, 0.02), (0.36, 0.08), (0.1, 0.1), (-0.1, 0.06));
            Coat(k, legs, coat.Lighter(-0.03), 0.7);
            body = Blob(f, (-0.46, 0.04), (-0.48, 0.2), (-0.3, 0.38), (0.0, 0.4), (0.2, 0.34), (0.26, 0.16), (0.18, 0.02));
            hu = 0.24; hv = 0.6;
        }
        else
        {
            // Far legs first (darker), then the body, then the near legs.
            foreach (var (lu, near) in new[] { (-0.24, false), (0.06, false), (-0.31, true), (0.14, true) })
            {
                using var leg = Blob(f, (lu - 0.035, 0.42), (lu + 0.035, 0.42), (lu + 0.03, 0.18), (lu + 0.025, 0.03), (lu - 0.025, 0.03), (lu - 0.03, 0.2));
                Coat(k, leg, near ? coat : coat.Lighter(-0.07), 0.7);
                using var h = Oval(f, lu, 0.04, 0.035, 0.04);
                Mark(k, leg, h, hoof, 1, 0.01);
            }
            body = Blob(f, (-0.4, 0.42), (-0.36, 0.56), (-0.1, 0.62), (0.14, 0.6), (0.24, 0.5), (0.18, 0.36), (-0.1, 0.34), (-0.34, 0.34));
            hu = 0.26; hv = 0.78;
        }
        using (body)
        {
            Coat(k, body, coat);
            using var belly = Oval(f, lying ? -0.12 : -0.1, lying ? 0.08 : 0.37, 0.26, 0.06);
            Mark(k, body, belly, cream, 0.8);
            // Fawn spots along the back.
            using var spots = new SKPath();
            var r = new Rng(k.P.Seed ^ 0x5B07u);
            for (var i = 0; i < 7; i++)
            {
                var su = (lying ? -0.38 : -0.32) + i * 0.075 + r.Jitter(0.012);
                var sv = (lying ? 0.28 : 0.53) + Math.Sin(i * 1.7) * 0.025;
                spots.AddCircle(f.U(su), f.V(sv), f.L(0.018 + r.Range(0, 0.008)));
            }
            Mark(k, body, spots, cream, 0.85, 0.01);
            using var tail = Oval(f, lying ? -0.47 : -0.4, lying ? 0.22 : 0.54, 0.035, 0.05, 30);
            Coat(k, tail, cream, 0.5);
        }
        // Neck rising to the big head.
        using var neck = Blob(f, (hu - 0.16, hv - (lying ? 0.32 : 0.26)), (hu - 0.1, hv - 0.06), (hu + 0.04, hv - 0.04), (hu + 0.02, hv - (lying ? 0.3 : 0.24)));
        Coat(k, neck, coat, 0.7);
        using var earL = Oval(f, hu - 0.14, hv + 0.11, 0.11, 0.045, 28);
        using var earR = Oval(f, hu + 0.12, hv + 0.13, 0.11, 0.045, -30);
        Coat(k, earL, coat.Lighter(-0.03));
        Coat(k, earR, coat);
        using var inL = Oval(f, hu - 0.14, hv + 0.11, 0.065, 0.022, 28);
        using var inR = Oval(f, hu + 0.12, hv + 0.13, 0.065, 0.022, -30);
        Mark(k, earL, inL, cream, 0.8, 0.02);
        Mark(k, earR, inR, cream, 0.8, 0.02);
        using var head = Blob(f, (hu - 0.13, hv), (hu - 0.08, hv + 0.11), (hu + 0.05, hv + 0.12), (hu + 0.14, hv + 0.04), (hu + 0.2, hv - 0.04),
            (hu + 0.16, hv - 0.09), (hu + 0.04, hv - 0.09), (hu - 0.09, hv - 0.07));
        Coat(k, head, coat);
        using var snout = Oval(f, hu + 0.13, hv - 0.05, 0.07, 0.045);
        Mark(k, head, snout, cream, 0.85);
        Nose(k, f, hu + 0.19, hv - 0.035, 0.022, ink);
        Eye(k, f, hu + 0.02, hv + 0.02, 0.04, ink);
        Blush(k, f, hu + 0.08, hv - 0.04, 0.03);
    }

    private static void Bear(K k)
    {
        var f = new Frame(k.X, k.Y, k.S);
        var fur = Local(k, 0.5, 0.075, 50);
        var muzzle = Local(k, 0.78, 0.05, 70);
        var ink = Ink(fur);
        Contact(k, 0.4);
        using var body = Blob(f, (-0.32, 0.06), (-0.36, 0.3), (-0.24, 0.52), (0.0, 0.58), (0.24, 0.52), (0.36, 0.3), (0.32, 0.06), (0.0, 0.0));
        Coat(k, body, fur);
        using var belly = Oval(f, 0.02, 0.24, 0.2, 0.2);
        Mark(k, body, belly, muzzle, 0.7);
        foreach (var fu in new[] { -0.17, 0.2 })
        {
            using var foot = Oval(f, fu, 0.07, 0.11, 0.075);
            Coat(k, foot, fur.Lighter(0.02), 0.7);
            using var pad = Oval(f, fu, 0.07, 0.06, 0.045);
            Mark(k, foot, pad, muzzle, 0.85, 0.02);
        }
        foreach (var au in new[] { -0.22, 0.22 })
        {
            using var arm = Oval(f, au, 0.3, 0.08, 0.13, au < 0 ? -18 : 18);
            Coat(k, arm, fur, 0.7);
        }
        foreach (var eu in new[] { -0.17, 0.2 })
        {
            using var ear = Oval(f, eu, 0.9, 0.085, 0.08);
            Coat(k, ear, fur);
            using var inner = Oval(f, eu, 0.895, 0.045, 0.04);
            Mark(k, ear, inner, muzzle, 0.75, 0.02);
        }
        using var head = Oval(f, 0.02, 0.72, 0.25, 0.21);
        Coat(k, head, fur);
        using var snout = Oval(f, 0.06, 0.65, 0.12, 0.085);
        Coat(k, snout, muzzle, 0.6, 0.5);
        Nose(k, f, 0.06, 0.69, 0.035, ink);
        using var mouth = Paint.Stroke(Paint.Sk(ink, 0.8), Math.Max(0.6f, k.S * 0.01f));
        k.C.DrawArc(new SKRect(f.U(0.02), f.V(0.66), f.U(0.1), f.V(0.6)), 20, 140, false, mouth);
        Eye(k, f, -0.07, 0.76, 0.04, ink);
        Eye(k, f, 0.15, 0.76, 0.04, ink);
        Blush(k, f, -0.13, 0.68, 0.035);
        Blush(k, f, 0.22, 0.68, 0.035);
    }

    /// <summary>A loaf of a cat, tail wrapped round its paws. Ginger, grey or cream tabby by seed.</summary>
    private static void Cat(K k)
    {
        var f = new Frame(k.X, k.Y, k.S);
        var r = new Rng(k.P.Seed ^ 0xCA7u);
        var kind = r.Int(3);
        var fur = kind switch
        {
            0 => Local(k, 0.72, 0.12, 60),
            1 => Local(k, 0.62, 0.02, 250),
            _ => Local(k, 0.84, 0.035, 80),
        };
        var stripe = fur.Lighter(-0.12).ScaleC(1.1);
        var cream = Local(k, CreamLocal.L, CreamLocal.C, CreamLocal.H);
        var pink = Local(k, 0.76, 0.08, 15);
        var ink = Ink(fur);
        Contact(k, 0.38);
        using var body = Blob(f, (-0.3, 0.04), (-0.34, 0.26), (-0.22, 0.52), (0.0, 0.58), (0.2, 0.5), (0.3, 0.26), (0.28, 0.04), (0.0, 0.0));
        Coat(k, body, fur);
        using var chest = Oval(f, 0.08, 0.28, 0.13, 0.2);
        Mark(k, body, chest, cream, 0.85);
        using var bands = new SKPath();
        for (var i = 0; i < 3; i++) bands.AddOval(new SKRect(f.U(-0.33), f.V(0.44 - i * 0.12), f.U(-0.12), f.V(0.41 - i * 0.12)));
        Mark(k, body, bands, stripe, 0.7, 0.02);
        foreach (var pu in new[] { 0.0, 0.14 })
        {
            using var paw = Oval(f, pu, 0.05, 0.065, 0.05);
            Coat(k, paw, cream, 0.6);
        }
        // Tail round the front, its tip curling up.
        using var tailLine = new SKPath();
        tailLine.MoveTo(f.P(-0.28, 0.08));
        tailLine.CubicTo(f.P(-0.1, -0.04), f.P(0.3, -0.03), f.P(0.34, 0.1));
        using var tail = new SKPath();
        using (var st = Paint.Stroke(SKColors.White, f.L(0.075))) AddStroke(tail, tailLine, st);
        Coat(k, tail, fur, 0.8);
        using var ears = new SKPath();
        using var earL = Blob(f, (-0.2, 0.76), (-0.17, 0.98), (-0.13, 0.99), (-0.02, 0.85));
        using var earR = Blob(f, (0.12, 0.86), (0.23, 0.99), (0.27, 0.97), (0.3, 0.74));
        Coat(k, earL, fur);
        Coat(k, earR, fur);
        using var inL = Blob(f, (-0.16, 0.8), (-0.15, 0.93), (-0.07, 0.84));
        Mark(k, earL, inL, pink, 0.85, 0.02);
        using var inR = Blob(f, (0.16, 0.86), (0.23, 0.94), (0.26, 0.8));
        Mark(k, earR, inR, pink, 0.85, 0.02);
        using var head = Blob(f, (-0.2, 0.68), (-0.16, 0.56), (0.04, 0.5), (0.24, 0.55), (0.32, 0.66), (0.24, 0.82), (0.04, 0.86), (-0.14, 0.82));
        Coat(k, head, fur);
        using var muzzle = Oval(f, 0.09, 0.6, 0.11, 0.07);
        Mark(k, head, muzzle, cream, 0.85);
        using var brow = new SKPath();
        for (var i = 0; i < 3; i++) brow.AddOval(new SKRect(f.U(0.02 + i * 0.04), f.V(0.86), f.U(0.04 + i * 0.04), f.V(0.77)));
        Mark(k, head, brow, stripe, 0.7, 0.01);
        Eye(k, f, -0.03, 0.69, 0.045, ink);
        Eye(k, f, 0.18, 0.69, 0.045, ink);
        Nose(k, f, 0.09, 0.62, 0.02, pink.Lighter(-0.12));
        Blush(k, f, -0.07, 0.61, 0.035);
        Blush(k, f, 0.24, 0.61, 0.035);
        Whiskers(k, f, 0.18, 0.6, 0.12, cream);
    }

    private static void Hedgehog(K k)
    {
        var f = new Frame(k.X, k.Y, k.S);
        var spine = Local(k, 0.46, 0.055, 55);
        var face = Local(k, 0.8, 0.05, 70);
        var ink = Ink(spine);
        Contact(k, 0.5);
        // Spines: a scalloped dome of soft rounded points.
        using var dome = new SKPath();
        using (var core = Blob(f, (-0.5, 0.04), (-0.52, 0.28), (-0.36, 0.56), (-0.06, 0.66), (0.2, 0.54), (0.26, 0.26), (0.18, 0.04))) dome.AddPath(core);
        for (var i = 0; i < 13; i++)
        {
            var a = Math.PI * (0.98 - i * 0.85 / 12);
            var cu = -0.14 + Math.Cos(a) * 0.38;
            var cv = 0.16 + Math.Sin(a) * 0.5;
            using var spike = Blob(f, (cu - 0.055, cv - 0.02), (cu + Math.Cos(a) * 0.09, cv + Math.Sin(a) * 0.09), (cu + 0.055, cv + 0.02));
            dome.AddPath(spike);
        }
        using var spines = dome.Simplify() ?? new SKPath(dome);
        Coat(k, spines, spine);
        using var tips = Paint.Stroke(Paint.Sk(face, 0.4), Math.Max(0.6f, k.S * 0.01f));
        var r = new Rng(k.P.Seed ^ 0xED6Eu);
        for (var i = 0; i < 9; i++)
        {
            var u = r.Range(-0.45, 0.1);
            var v = r.Range(0.25, 0.65);
            k.C.DrawLine(f.U(u), f.V(v), f.U(u - 0.04), f.V(v + 0.05), tips);
        }
        using var head = Blob(f, (0.04, 0.06), (0.02, 0.4), (0.2, 0.58), (0.44, 0.48), (0.66, 0.22), (0.62, 0.12), (0.36, 0.03));
        Coat(k, head, face);
        using var ear = Oval(f, 0.18, 0.56, 0.055, 0.05);
        Coat(k, ear, face.Lighter(-0.06), 0.6);
        Nose(k, f, 0.65, 0.18, 0.035, ink);
        Eye(k, f, 0.36, 0.34, 0.045, ink);
        Blush(k, f, 0.44, 0.24, 0.04);
        foreach (var fu in new[] { 0.12, 0.36 })
        {
            using var foot = Oval(f, fu, 0.025, 0.05, 0.03);
            Coat(k, foot, face.Lighter(-0.1), 0.5);
        }
    }

    /// <summary>An otter sitting up on a wet rock in the water, a shell held to its chest.</summary>
    private static void Otter(K k)
    {
        var f = new Frame(k.X, k.Y, k.S);
        var fur = Local(k, 0.52, 0.07, 50);
        var cream = Local(k, 0.8, 0.045, 72);
        var rockCol = Oklab.Mix(k.Colors.WaterDeep.ToOklab(), new Oklch(0.3, 0.02, 60).ToOklab(), 0.5).ToOklch();
        var ink = Ink(fur);
        using var rock = Blob(f, (-0.42, 0.0), (-0.36, 0.12), (-0.1, 0.18), (0.2, 0.17), (0.4, 0.1), (0.46, 0.0));
        Coat(k, rock, rockCol, 0.6);
        // Ripples round the rock.
        using var ripple = Paint.Stroke(Paint.Sk(ScenePalette.Cream, 0.35), Math.Max(0.7f, k.S * 0.012f));
        k.C.DrawOval(f.U(0.02), f.V(-0.01), f.L(0.58), f.L(0.05), ripple);
        k.C.DrawOval(f.U(0.02), f.V(-0.03), f.L(0.74), f.L(0.07), ripple);
        using var tail = Blob(f, (-0.12, 0.2), (-0.36, 0.2), (-0.5, 0.26), (-0.38, 0.3), (-0.1, 0.28));
        Coat(k, tail, fur, 0.7);
        using var body = Blob(f, (-0.18, 0.16), (-0.24, 0.4), (-0.16, 0.64), (0.02, 0.7), (0.18, 0.62), (0.22, 0.38), (0.18, 0.16));
        Coat(k, body, fur);
        using var belly = Oval(f, 0.02, 0.4, 0.13, 0.2);
        Mark(k, body, belly, cream, 0.85);
        // A shell held with both paws.
        using var shell = Blob(f, (-0.04, 0.42), (0.02, 0.52), (0.1, 0.42), (0.03, 0.36));
        Coat(k, shell, Local(k, 0.78, 0.09, 30), 0.6);
        foreach (var pu in new[] { -0.07, 0.11 })
        {
            using var paw = Oval(f, pu, 0.46, 0.045, 0.04);
            Coat(k, paw, fur.Lighter(-0.03), 0.6);
        }
        using var ears = new SKPath();
        foreach (var eu in new[] { -0.12, 0.16 })
        {
            using var ear = Oval(f, eu, 0.9, 0.045, 0.04);
            Coat(k, ear, fur, 0.6);
        }
        using var head = Oval(f, 0.02, 0.8, 0.19, 0.15);
        Coat(k, head, fur);
        using var muzzle = Oval(f, 0.04, 0.74, 0.11, 0.07);
        Mark(k, head, muzzle, cream, 0.9);
        Nose(k, f, 0.04, 0.775, 0.03, ink);
        Eye(k, f, -0.07, 0.84, 0.035, ink);
        Eye(k, f, 0.13, 0.84, 0.035, ink);
        Blush(k, f, -0.11, 0.76, 0.03);
        Blush(k, f, 0.18, 0.76, 0.03);
        Whiskers(k, f, 0.12, 0.74, 0.1, cream);
    }

    /// <summary>Variant 0: a whale's back rising at the surface, smiling, blowing a fan of
    /// spray; variant 1: a whale swimming (underwater).</summary>
    private static void Whale(K k)
    {
        var f = new Frame(k.X, k.Y, k.S);
        var skin = Local(k, 0.56, 0.06, 245);
        var belly = Local(k, 0.84, 0.03, 230);
        var ink = Ink(skin);
        if (k.P.Variant == 1)
        {
            using var fin = Blob(f, (-0.1, 0.2), (0.1, 0.0), (0.22, 0.04), (0.08, 0.22));
            Coat(k, fin, skin.Lighter(-0.06), 0.7);
            using var body = Blob(f, (-0.8, 0.36), (-0.4, 0.5), (0.2, 0.62), (0.62, 0.5), (0.78, 0.32), (0.6, 0.12), (0.1, 0.08), (-0.5, 0.24));
            Coat(k, body, skin);
            using var under = Blob(f, (0.78, 0.3), (0.6, 0.12), (0.1, 0.08), (-0.4, 0.22), (0.0, 0.2), (0.5, 0.24));
            Mark(k, body, under, belly, 0.85);
            using var grooves = Paint.Stroke(Paint.Sk(skin, 0.5), Math.Max(0.6f, k.S * 0.008f));
            for (var i = 0; i < 4; i++) k.C.DrawLine(f.U(0.0 + i * 0.08), f.V(0.17 + i * 0.01), f.U(0.4 + i * 0.07), f.V(0.2 + i * 0.012), grooves);
            using var fluke = Blob(f, (-0.76, 0.34), (-0.96, 0.6), (-1.02, 0.56), (-0.9, 0.36), (-1.04, 0.18), (-0.96, 0.14));
            Coat(k, fluke, skin);
            Eye(k, f, 0.48, 0.32, 0.045, ink);
            using var mouth = Paint.Stroke(Paint.Sk(ink, 0.7), Math.Max(0.7f, k.S * 0.01f));
            k.C.DrawArc(new SKRect(f.U(0.46), f.V(0.3), f.U(0.76), f.V(0.18)), 10, 120, false, mouth);
            Blush(k, f, 0.6, 0.26, 0.04);
            return;
        }
        // Surface: the far ripple, the spray fan, the tail, the back, then the near ripple
        // over the waterline — so the whale sits IN the water, not in a dish.
        using var rippleP = Paint.Stroke(Paint.Sk(ScenePalette.Cream, 0.32), Math.Max(0.7f, k.S * 0.012f));
        var ring = new SKRect(f.U(-0.82), f.V(0.04), f.U(0.82), f.V(-0.06));
        k.C.DrawArc(ring, 180, 180, false, rippleP);
        using (var spray = Paint.Fill(Paint.Sk(ScenePalette.Cream, 0.85)))
        {
            foreach (var (su, sv, sr) in new[] { (0.2, 0.62, 0.045), (0.32, 0.7, 0.05), (0.44, 0.6, 0.045), (0.26, 0.5, 0.035), (0.38, 0.5, 0.035), (0.32, 0.42, 0.03) })
                k.C.DrawCircle(f.U(su), f.V(sv), f.L(sr), spray);
        }
        using var tail = Blob(f, (-0.5, 0.0), (-0.58, 0.16), (-0.82, 0.4), (-0.7, 0.44), (-0.58, 0.32), (-0.46, 0.44), (-0.34, 0.4), (-0.44, 0.14), (-0.4, 0.0));
        Coat(k, tail, skin);
        using var back = Blob(f, (-0.46, -0.02), (-0.36, 0.14), (-0.1, 0.3), (0.2, 0.36), (0.5, 0.28), (0.66, 0.1), (0.68, -0.02));
        Coat(k, back, skin);
        using var chin = Oval(f, 0.4, 0.02, 0.26, 0.08);
        Mark(k, back, chin, belly, 0.85);
        // Water laps over the lower body.
        using (var lap = new SKPaint
               {
                   IsAntialias = true,
                   Shader = SKShader.CreateLinearGradient(f.P(0, 0.06), f.P(0, -0.04),
                       [Paint.Sk(k.Colors.WaterTop, 0), Paint.Sk(k.Colors.WaterTop, 0.85)], SKShaderTileMode.Clamp),
               })
            k.C.DrawRect(f.U(-0.9), f.V(0.06), f.L(1.8), f.L(0.12), lap);
        k.C.DrawArc(ring, 0, 180, false, rippleP);
        Eye(k, f, 0.42, 0.18, 0.045, ink);
        using var smile = Paint.Stroke(Paint.Sk(ink, 0.7), Math.Max(0.7f, k.S * 0.01f));
        k.C.DrawArc(new SKRect(f.U(0.42), f.V(0.14), f.U(0.66), f.V(0.04)), 10, 110, false, smile);
        Blush(k, f, 0.52, 0.1, 0.04);
    }

    /// <summary>Variant 0: a sea turtle gliding (underwater); variant 1: resting on the sand.</summary>
    private static void Turtle(K k)
    {
        var f = new Frame(k.X, k.Y, k.S);
        var shellCol = Local(k, 0.6, 0.1, 112);
        var skinCol = Local(k, 0.72, 0.09, 122);
        var ink = Ink(shellCol);
        var swim = k.P.Variant == 0;
        if (!swim) Contact(k, 0.5);
        var lift = swim ? 0.12 : 0.0;
        foreach (var (fu, fv, rot) in swim
                     ? new[] { (-0.3, 0.12, 30.0), (0.22, 0.12, -40.0), (-0.32, 0.5, -20.0), (0.26, 0.56, 35.0) }
                     : new[] { (-0.32, 0.06, 0.0), (0.28, 0.06, 0.0) })
        {
            using var flipper = Oval(f, fu, fv + lift * 0.2, swim ? 0.17 : 0.1, swim ? 0.06 : 0.07, rot);
            Coat(k, flipper, skinCol, 0.7);
        }
        using var head = Oval(f, 0.5, 0.26 + lift, 0.15, 0.12);
        using var neck = Oval(f, 0.36, 0.22 + lift, 0.1, 0.07);
        Coat(k, neck, skinCol, 0.6);
        Coat(k, head, skinCol);
        using var shell = Blob(f, (-0.46, 0.1 + lift), (-0.42, 0.34 + lift), (-0.2, 0.56 + lift), (0.1, 0.6 + lift), (0.34, 0.42 + lift), (0.38, 0.16 + lift), (0.0, 0.08 + lift));
        Coat(k, shell, shellCol);
        // Plates: rounded hexes, lighter centres.
        using var plates = new SKPath();
        foreach (var (pu, pv) in new[] { (-0.04, 0.34), (-0.24, 0.3), (0.16, 0.3), (-0.12, 0.47), (0.08, 0.47) })
            plates.AddOval(new SKRect(f.U(pu - 0.08), f.V(pv + lift + 0.065), f.U(pu + 0.08), f.V(pv + lift - 0.065)));
        Mark(k, shell, plates, shellCol.Lighter(0.07), 0.9, 0.02);
        using var rimLine = Oval(f, -0.04, 0.12 + lift, 0.42, 0.05);
        Mark(k, shell, rimLine, skinCol.Lighter(-0.06), 0.9, 0.02);
        Eye(k, f, 0.52, 0.3 + lift, 0.04, ink);
        using var smile = Paint.Stroke(Paint.Sk(ink, 0.7), Math.Max(0.6f, k.S * 0.009f));
        k.C.DrawArc(new SKRect(f.U(0.52), f.V(0.25 + lift), f.U(0.64), f.V(0.18 + lift)), 10, 120, false, smile);
        Blush(k, f, 0.56, 0.22 + lift, 0.03);
    }

    /// <summary>A lead fish with a face, its school curling round it.</summary>
    private static void FishSchool(K k)
    {
        var f = new Frame(k.X, k.Y, k.S);
        var r = new Rng(k.P.Seed ^ 0xF15Eu);
        var gold = r.Chance(0.5);
        var body = gold ? Local(k, 0.8, 0.13, 72) : Local(k, 0.74, 0.12, 36);
        var fin = gold ? Local(k, 0.72, 0.12, 50) : Local(k, 0.68, 0.11, 18);
        var ink = Ink(body);
        void Fish(double cu, double cv, double sz, bool face)
        {
            using var tail = Blob(f, (cu - sz * 0.7, cv), (cu - sz * 1.05, cv + sz * 0.32), (cu - sz * 0.98, cv), (cu - sz * 1.05, cv - sz * 0.32));
            Coat(k, tail, fin, 0.6);
            using var fb = Blob(f, (cu - sz * 0.78, cv), (cu - sz * 0.3, cv + sz * 0.38), (cu + sz * 0.35, cv + sz * 0.34), (cu + sz * 0.62, cv + sz * 0.05),
                (cu + sz * 0.5, cv - sz * 0.22), (cu, cv - sz * 0.36), (cu - sz * 0.5, cv - sz * 0.22));
            Coat(k, fb, body, face ? 1 : 0.6);
            using var stripe = Oval(f, cu + sz * 0.05, cv, sz * 0.07, sz * 0.34);
            Mark(k, fb, stripe, ScenePalette.Cream, 0.55, 0.01);
            if (!face) { using var dot = Paint.Fill(Paint.Sk(ink)); k.C.DrawCircle(f.U(cu + sz * 0.38), f.V(cv + sz * 0.06), Math.Max(0.5f, f.L(sz * 0.06)), dot); return; }
            OpenEye(k, f, cu + sz * 0.34, cv + sz * 0.07, sz * 0.09, ink);
            using var smile = Paint.Stroke(Paint.Sk(ink, 0.7), Math.Max(0.6f, k.S * 0.009f));
            k.C.DrawArc(new SKRect(f.U(cu + sz * 0.42), f.V(cv - sz * 0.02), f.U(cu + sz * 0.58), f.V(cv - sz * 0.14)), 20, 120, false, smile);
            Blush(k, f, cu + sz * 0.3, cv - sz * 0.1, sz * 0.06);
        }
        for (var i = 0; i < 7; i++)
        {
            var a = Math.PI * 2 * i / 7 + r.Range(-0.2, 0.2);
            Fish(Math.Cos(a) * 0.62 - 0.05, 0.45 + Math.Sin(a) * 0.3, r.Range(0.13, 0.17), false);
        }
        Fish(0.02, 0.46, 0.36, true);
    }

    /// <summary>A camel kneeling to rest, a striped blanket on its hump.</summary>
    private static void Camel(K k)
    {
        var f = new Frame(k.X, k.Y, k.S);
        var coat = Local(k, 0.72, 0.08, 70);
        var cream = Local(k, 0.86, 0.04, 80);
        var ink = Ink(coat);
        Contact(k, 0.55);
        using var legs = Blob(f, (-0.4, 0.0), (0.3, 0.0), (0.34, 0.08), (-0.4, 0.1));
        Coat(k, legs, coat.Lighter(-0.05), 0.7);
        using var body = Blob(f, (-0.5, 0.06), (-0.52, 0.3), (-0.3, 0.6), (-0.08, 0.68), (0.12, 0.52), (0.3, 0.36), (0.32, 0.08));
        Coat(k, body, coat);
        using var blanket = Blob(f, (-0.26, 0.6), (-0.08, 0.66), (0.06, 0.56), (0.02, 0.42), (-0.26, 0.44));
        Coat(k, blanket, k.Colors.Grade(k.Colors.Accent2), 0.7);
        using var stripes = new SKPath();
        for (var i = 0; i < 2; i++) stripes.AddRect(new SKRect(f.U(-0.3), f.V(0.54 - i * 0.05), f.U(0.1), f.V(0.525 - i * 0.05)));
        Mark(k, blanket, stripes, cream, 0.7, 0.01);
        using var tassel = Paint.Fill(Paint.Sk(cream, 0.9));
        for (var i = 0; i < 3; i++) k.C.DrawCircle(f.U(-0.22 + i * 0.11), f.V(0.43), Math.Max(0.6f, f.L(0.018)), tassel);
        using var neck = Blob(f, (0.12, 0.3), (0.22, 0.48), (0.36, 0.74), (0.5, 0.8), (0.56, 0.66), (0.46, 0.4), (0.34, 0.2));
        Coat(k, neck, coat);
        using var ear = Oval(f, 0.48, 0.92, 0.035, 0.05, 20);
        Coat(k, ear, coat, 0.6);
        using var head = Blob(f, (0.42, 0.78), (0.48, 0.92), (0.62, 0.92), (0.74, 0.82), (0.74, 0.74), (0.6, 0.72));
        Coat(k, head, coat);
        using var muzzle = Oval(f, 0.68, 0.77, 0.07, 0.05);
        Mark(k, head, muzzle, cream, 0.8);
        SleepyEye(k, f, 0.56, 0.84, 0.04, ink);
        Blush(k, f, 0.62, 0.78, 0.03);
        using var mouth = Paint.Stroke(Paint.Sk(ink, 0.7), Math.Max(0.6f, k.S * 0.009f));
        k.C.DrawArc(new SKRect(f.U(0.64), f.V(0.77), f.U(0.73), f.V(0.73)), 10, 120, false, mouth);
    }

    /// <summary>A gentle long-necked grazer: round body, stumpy legs, a smiling small head.</summary>
    private static void Dinosaur(K k)
    {
        var f = new Frame(k.X, k.Y, k.S);
        var skin = Local(k, 0.66, 0.09, 165);
        var belly = Local(k, 0.84, 0.055, 110);
        var spot = skin.Lighter(-0.08);
        var ink = Ink(skin);
        Contact(k, 0.5, -0.1);
        foreach (var (lu, near) in new[] { (-0.3, false), (0.12, false), (-0.22, true), (0.2, true) })
        {
            using var leg = Blob(f, (lu - 0.07, 0.3), (lu + 0.07, 0.3), (lu + 0.075, 0.02), (lu - 0.075, 0.02));
            Coat(k, leg, near ? skin : skin.Lighter(-0.07), 0.7);
        }
        using var tail = Blob(f, (-0.36, 0.24), (-0.6, 0.18), (-0.86, 0.1), (-0.94, 0.12), (-0.6, 0.3), (-0.34, 0.44));
        Coat(k, tail, skin);
        using var body = Blob(f, (-0.44, 0.24), (-0.36, 0.48), (-0.06, 0.58), (0.22, 0.5), (0.32, 0.3), (0.2, 0.16), (-0.2, 0.14));
        Coat(k, body, skin);
        using var under = Oval(f, -0.06, 0.18, 0.3, 0.08);
        Mark(k, body, under, belly, 0.85);
        using var spots = new SKPath();
        foreach (var (su, sv, sr) in new[] { (-0.2, 0.44, 0.05), (-0.02, 0.48, 0.04), (0.12, 0.42, 0.035), (-0.3, 0.34, 0.035) })
            spots.AddCircle(f.U(su), f.V(sv), f.L(sr));
        Mark(k, body, spots, spot, 0.85, 0.01);
        using var neck = Blob(f, (0.12, 0.36), (0.26, 0.52), (0.36, 0.78), (0.4, 0.9), (0.5, 0.9), (0.46, 0.7), (0.34, 0.44), (0.24, 0.3));
        Coat(k, neck, skin);
        using var head = Blob(f, (0.36, 0.88), (0.42, 0.98), (0.56, 1.0), (0.66, 0.94), (0.64, 0.86), (0.48, 0.84));
        Coat(k, head, skin);
        Eye(k, f, 0.5, 0.94, 0.035, ink);
        using var smile = Paint.Stroke(Paint.Sk(ink, 0.7), Math.Max(0.6f, k.S * 0.009f));
        k.C.DrawArc(new SKRect(f.U(0.54), f.V(0.92), f.U(0.65), f.V(0.86)), 10, 120, false, smile);
        Blush(k, f, 0.58, 0.89, 0.025);
    }

    /// <summary>A small flock of round songbirds (or gulls over the sea), dark against a
    /// light sky, light against a dark one, big enough to read on a card.</summary>
    private static void Birds(K k)
    {
        var r = k.R;
        var skyL = k.Colors.SkyMid.L;
        var col = skyL > 0.42 ? k.Colors.SkyZenith.Lighter(-0.14).ScaleC(0.6) : Oklab.Mix(ScenePalette.Cream.ToOklab(), k.Colors.SkyMid.ToOklab(), 0.25).ToOklch();
        var n = 3 + r.Int(2);
        using var p = Paint.Stroke(Paint.Sk(col, 0.95), Math.Max(1f, k.S * 0.028f));
        for (var i = 0; i < n; i++)
        {
            var bx = k.X + (float)(i - n / 2.0) * k.S * 0.3f + (float)r.Jitter(0.05) * k.S;
            var by = k.Y + (float)Math.Sin(i * 1.3) * k.S * 0.12f + (float)r.Jitter(0.04) * k.S;
            var w = k.S * (float)r.Range(0.11, 0.14);
            var up = (float)r.Range(0.35, 0.6);
            // A soft gull curve: two arched wings meeting at the body, nothing more.
            using var bird = new SKPath();
            bird.MoveTo(bx - w, by - w * up * 0.2f);
            bird.QuadTo(bx - w * 0.5f, by - w * up, bx, by);
            bird.QuadTo(bx + w * 0.5f, by - w * up, bx + w, by - w * up * 0.2f);
            k.C.DrawPath(bird, p);
        }
    }
}
