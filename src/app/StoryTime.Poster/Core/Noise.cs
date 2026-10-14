namespace StoryTime.Poster.Core;

/// <summary>Deterministic lattice noise for silhouettes and textures. Gradient (Perlin-
/// style) noise in 1D and value noise in 2D, plus fBm and a ridged variant, all built on
/// <see cref="Hash.Unit"/> so the output is identical on every platform.
///
/// Parameter defaults follow Quilez's fBm article: gain 0.5 (H = 1, the roughness measured
/// on real mountain silhouettes) and a slightly detuned lacunarity (2.01) so octave peaks
/// never stack into a visible period.</summary>
public static class Noise
{
    private static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);

    /// <summary>1D gradient noise in roughly [-1,1].</summary>
    public static double Gradient1(double x, uint seed)
    {
        var i = (int)Math.Floor(x);
        var f = x - i;
        var g0 = Hash.Unit(i, 0, seed) * 2 - 1;
        var g1 = Hash.Unit(i + 1, 0, seed) * 2 - 1;
        var v0 = g0 * f;
        var v1 = g1 * (f - 1);
        return (v0 + (v1 - v0) * Fade(f)) * 2.0;
    }

    /// <summary>2D value noise in [0,1].</summary>
    public static double Value2(double x, double y, uint seed)
    {
        var ix = (int)Math.Floor(x);
        var iy = (int)Math.Floor(y);
        var fx = x - ix;
        var fy = y - iy;
        var a = Hash.Unit(ix, iy, seed);
        var b = Hash.Unit(ix + 1, iy, seed);
        var c = Hash.Unit(ix, iy + 1, seed);
        var d = Hash.Unit(ix + 1, iy + 1, seed);
        var ux = Fade(fx);
        var uy = Fade(fy);
        return a + (b - a) * ux + (c - a) * uy + (a - b - c + d) * ux * uy;
    }

    /// <summary>1D fBm, normalised to roughly [-1,1].</summary>
    public static double Fbm1(double x, uint seed, int octaves, double gain = 0.5, double lacunarity = 2.01)
    {
        double sum = 0, amp = 1, norm = 0, freq = 1;
        for (var o = 0; o < octaves; o++)
        {
            sum += amp * Gradient1(x * freq + o * 17.31, seed + (uint)o * 101u);
            norm += amp;
            amp *= gain;
            freq *= lacunarity;
        }
        return sum / norm;
    }

    /// <summary>1D ridged multifractal (Musgrave form): (offset − |n|)² per octave, each
    /// octave weighted by the previous signal so crests stay sharp and valleys stay soft.
    /// Output roughly [0,1], peaks high.</summary>
    public static double Ridged1(double x, uint seed, int octaves, double gain = 0.5, double lacunarity = 2.01)
    {
        double sum = 0, amp = 0.5, weight = 1, freq = 1, norm = 0;
        for (var o = 0; o < octaves; o++)
        {
            var n = Gradient1(x * freq + o * 9.7, seed + (uint)o * 131u);
            var s = 1.0 - Math.Abs(n);
            s *= s;
            s *= weight;
            weight = Math.Clamp(s * 2.0, 0, 1);
            sum += s * amp;
            norm += amp;
            amp *= gain;
            freq *= lacunarity;
        }
        return sum / norm;
    }

    /// <summary>2D value fBm in [0,1].</summary>
    public static double Fbm2(double x, double y, uint seed, int octaves, double gain = 0.5)
    {
        double sum = 0, amp = 1, norm = 0, freq = 1;
        for (var o = 0; o < octaves; o++)
        {
            sum += amp * Value2(x * freq, y * freq, seed + (uint)o * 977u);
            norm += amp;
            amp *= gain;
            freq *= 2.03;
        }
        return sum / norm;
    }

    public static double SmoothStep(double e0, double e1, double x)
    {
        var t = Math.Clamp((x - e0) / (e1 - e0), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
