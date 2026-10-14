using SkiaSharp;
using StoryTime.Poster.Composition;

namespace StoryTime.Poster.Render;

/// <summary>Warm light pools. Each light washes the ground and everything already painted
/// on its own layer (SrcATop: it lights what is there, it never paints sky), then adds a
/// soft bloom in the air around the source. This is the poster's focal glow — the value
/// and temperature accent the eye lands on first.</summary>
internal static class LightPainter
{
    public static void Paint(SKCanvas canvas, PosterPlan plan, int depth, PosterViewport vp)
    {
        var c = plan.Colors;
        foreach (var light in plan.Lights)
        {
            if (light.Depth != depth) continue;
            var x = vp.X(light.X);
            var y = vp.Y(light.Y);
            var radius = vp.S(light.Radius);
            if (radius < 1) continue;
            var s = light.Strength;
            var warm = Render.Paint.Sk(c.WarmLight);
            using (var pool = new SKPaint
                   {
                       IsAntialias = true,
                       IsDither = true,
                       BlendMode = SKBlendMode.SrcATop,
                       Shader = SKShader.CreateRadialGradient(new SKPoint(x, y + radius * 0.25f), radius,
                           [warm.WithAlpha((byte)(110 * s)), warm.WithAlpha((byte)(46 * s)), warm.WithAlpha(0)],
                           [0f, 0.45f, 1f], SKShaderTileMode.Clamp),
                   })
                canvas.DrawCircle(x, y + radius * 0.25f, radius, pool);
            Render.Paint.Glow(canvas, x, y, radius * 0.75f, Render.Paint.Sk(c.WindowGlow), 0.2 * s);
        }
    }
}
