using SkiaSharp;
using StoryTime.Poster.Composition;

namespace StoryTime.Poster.Render;

/// <summary>The living layer: twinkling stars, drifting fireflies, moonglade glints,
/// falling snow, rising bubbles. Cheap by design (a few dozen soft dots), drawn by the
/// view on a slow clock. Every motion is slow and non-periodic-feeling — periods of 2–6 s
/// for opacity, 14 s and up for drift (WCAG 2.3.3; Apple's guidance against sustained
/// ~0.2 Hz oscillation) — and at t = 0 the layer is a valid still frame, which is what
/// thumbnails and Reduce Motion show.</summary>
public static class AmbientPainter
{
    /// <param name="offsets">Pixel offset per depth (0..3) — the parallax shift of the layer
    /// each particle belongs to, so particles travel with their layer.</param>
    public static void Draw(SKCanvas canvas, IReadOnlyList<AmbientParticle> particles, PosterViewport vp,
        double t, ReadOnlySpan<SKPoint> offsets)
    {
        foreach (var p in particles)
        {
            var off = p.Depth < offsets.Length ? offsets[p.Depth] : SKPoint.Empty;
            var w = 2 * Math.PI / Math.Max(0.5, p.Period);
            var color = new SKColor(p.Color);
            switch (p.Kind)
            {
                case AmbientKind.Twinkle:
                {
                    var a = p.Alpha * (1 + p.Amp * Math.Sin(w * t + p.Phase));
                    SkyPainter.DrawStar(canvas, vp.X(p.X) + off.X, vp.Y(p.Y) + off.Y, Math.Max(0.6f, vp.S(p.Size)), color,
                        Math.Clamp(a, 0, 1), p.Alpha > 0.85);
                    break;
                }
                case AmbientKind.Firefly:
                {
                    var x = p.X + p.Amp * Math.Sin(w * t + p.Phase);
                    var y = p.Y + p.Amp * 0.6 * Math.Sin(w * 0.73 * t + p.Phase * 1.3);
                    var pulse = 0.55 + 0.45 * Math.Sin(w * 3.7 * t + p.Phase * 2.1);
                    var px = vp.X(x) + off.X;
                    var py = vp.Y(y) + off.Y;
                    var r = Math.Max(0.7f, vp.S(p.Size));
                    Paint.Glow(canvas, px, py, r * 5f, color, p.Alpha * 0.32 * pulse);
                    using var core = Paint.Fill(color.WithAlpha((byte)Math.Clamp(p.Alpha * pulse * 255, 0, 255)));
                    canvas.DrawCircle(px, py, r, core);
                    break;
                }
                case AmbientKind.Glint:
                {
                    var a = p.Alpha * (0.5 + 0.5 * Math.Sin(w * t + p.Phase));
                    var x = vp.X(p.X + p.Amp * Math.Sin(w * 0.5 * t + p.Phase)) + off.X;
                    var y = vp.Y(p.Y) + off.Y;
                    var len = vp.S(p.Size);
                    var th = Math.Max(0.7f, vp.Unit * 0.0025f);
                    using var dash = Paint.Fill(color.WithAlpha((byte)Math.Clamp(a * 255, 0, 255)));
                    canvas.DrawRoundRect(x - len / 2, y - th / 2, len, th, th / 2, th / 2, dash);
                    break;
                }
                case AmbientKind.Snowflake:
                {
                    var fall = (p.Y + t / p.Period) % 1.08;
                    var x = p.X + p.Amp * Math.Sin(w * 0.8 * t + p.Phase);
                    using var flake = Paint.Fill(color.WithAlpha((byte)Math.Clamp(p.Alpha * 255, 0, 255)));
                    canvas.DrawCircle(vp.X(x) + off.X, vp.Y(fall - 0.04) + off.Y, Math.Max(0.6f, vp.S(p.Size)), flake);
                    break;
                }
                case AmbientKind.Bubble:
                {
                    var rise = (p.Y - t / p.Period) % 1.0;
                    if (rise < 0) rise += 1.0;
                    var x = p.X + p.Amp * Math.Sin(w * t + p.Phase);
                    using var ring = Paint.Stroke(color.WithAlpha((byte)Math.Clamp(p.Alpha * 255, 0, 255)), Math.Max(0.6f, vp.Unit * 0.0015f));
                    canvas.DrawCircle(vp.X(x) + off.X, vp.Y(rise * 0.75) + off.Y, Math.Max(1f, vp.S(p.Size)), ring);
                    break;
                }
                case AmbientKind.Mote:
                {
                    var x = p.X + p.Amp * Math.Sin(w * t + p.Phase);
                    var y = p.Y + p.Amp * Math.Cos(w * 0.6 * t + p.Phase);
                    var a = p.Alpha * (0.6 + 0.4 * Math.Sin(w * 2 * t + p.Phase));
                    Paint.Glow(canvas, vp.X(x) + off.X, vp.Y(y) + off.Y, Math.Max(1.5f, vp.S(p.Size) * 4), color, a * 0.5);
                    break;
                }
            }
        }
    }
}
