using SkiaSharp;

namespace StoryTime.Poster.Color;

/// <summary>A colour in OKLCH (Björn Ottosson's perceptual space): L lightness 0..1,
/// C chroma (≈0..0.37), H hue in degrees. Every palette in the engine is authored and
/// interpolated here, so a gradient or a haze mix keeps its perceived lightness and hue
/// instead of sliding through the muddy greys an sRGB lerp produces.</summary>
public readonly record struct Oklch(double L, double C, double H)
{
    public Oklab ToOklab()
    {
        var rad = H * Math.PI / 180.0;
        return new Oklab(L, C * Math.Cos(rad), C * Math.Sin(rad));
    }

    public SKColor ToSk(byte alpha = 255) => ToOklab().ToSk(alpha);

    public Oklch WithL(double l) => this with { L = l };
    public Oklch WithC(double c) => this with { C = c };
    public Oklch WithH(double h) => this with { H = h };
    public Oklch Lighter(double dl) => this with { L = L + dl };
    public Oklch ScaleC(double k) => this with { C = C * k };
}

/// <summary>OKLab coordinates plus the conversions the engine needs (sRGB both ways,
/// linear-light, gamut mapping by chroma reduction, WCAG luminance/contrast).</summary>
public readonly record struct Oklab(double L, double A, double B)
{
    public Oklch ToOklch()
    {
        var c = Math.Sqrt(A * A + B * B);
        var h = Math.Atan2(B, A) * 180.0 / Math.PI;
        if (h < 0) h += 360;
        return new Oklch(L, c, h);
    }

    public static Oklab Mix(Oklab a, Oklab b, double t) =>
        new(a.L + (b.L - a.L) * t, a.A + (b.A - a.A) * t, a.B + (b.B - a.B) * t);

    /// <summary>Linear-light sRGB (unclamped).</summary>
    public (double R, double G, double B) ToLinearRgb()
    {
        var l_ = L + 0.3963377774 * A + 0.2158037573 * B;
        var m_ = L - 0.1055613458 * A - 0.0638541728 * B;
        var s_ = L - 0.0894841775 * A - 1.2914855480 * B;
        var l = l_ * l_ * l_;
        var m = m_ * m_ * m_;
        var s = s_ * s_ * s_;
        return (
            +4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
            -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
            -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s);
    }

    public static Oklab FromLinearRgb(double r, double g, double b)
    {
        var l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        var m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        var s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
        return new Oklab(
            0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
            1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
            0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
    }

    public static Oklab FromSk(SKColor c) =>
        FromLinearRgb(Srgb.ToLinear(c.Red / 255.0), Srgb.ToLinear(c.Green / 255.0), Srgb.ToLinear(c.Blue / 255.0));

    public static Oklab FromHex(string hex) => FromSk(SKColor.Parse(hex));

    /// <summary>In gamut when every linear channel sits in [0,1] (tiny epsilon).</summary>
    public bool InGamut()
    {
        var (r, g, b) = ToLinearRgb();
        const double e = 1e-4;
        return r >= -e && r <= 1 + e && g >= -e && g <= 1 + e && b >= -e && b <= 1 + e;
    }

    /// <summary>Gamut-maps by reducing chroma at constant L and hue (binary search) —
    /// the perceptual choice: a too-saturated night teal becomes a slightly greyer teal,
    /// never a clipped, hue-shifted one.</summary>
    public Oklab MapToGamut()
    {
        if (InGamut()) return this;
        var lch = ToOklch();
        double lo = 0, hi = lch.C;
        for (var i = 0; i < 24; i++)
        {
            var mid = (lo + hi) / 2;
            if ((lch with { C = mid }).ToOklab().InGamut()) lo = mid; else hi = mid;
        }
        return (lch with { C = lo }).ToOklab();
    }

    public SKColor ToSk(byte alpha = 255)
    {
        var (r, g, b) = MapToGamut().ToLinearRgb();
        return new SKColor(Srgb.Encode8(r), Srgb.Encode8(g), Srgb.Encode8(b), alpha);
    }
}

/// <summary>sRGB transfer function helpers (IEC 61966-2-1) and WCAG 2.x luminance.</summary>
public static class Srgb
{
    public static double ToLinear(double c) =>
        c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    public static double FromLinear(double c)
    {
        c = Math.Clamp(c, 0, 1);
        return c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;
    }

    public static byte Encode8(double linear) => (byte)Math.Clamp(Math.Round(FromLinear(linear) * 255), 0, 255);

    /// <summary>WCAG 2.x relative luminance of an 8-bit sRGB colour.</summary>
    public static double RelativeLuminance(byte r, byte g, byte b) =>
        0.2126 * ToLinear(r / 255.0) + 0.7152 * ToLinear(g / 255.0) + 0.0722 * ToLinear(b / 255.0);

    public static double RelativeLuminance(SKColor c) => RelativeLuminance(c.Red, c.Green, c.Blue);

    /// <summary>WCAG contrast ratio (L1+0.05)/(L2+0.05), lighter over darker.</summary>
    public static double ContrastRatio(double lumA, double lumB)
    {
        var hi = Math.Max(lumA, lumB);
        var lo = Math.Min(lumA, lumB);
        return (hi + 0.05) / (lo + 0.05);
    }

    /// <summary>4096-entry linear→sRGB8 lookup for the per-pixel sky pass (pow is the hot
    /// cost there). Input is linear light scaled to [0,4095].</summary>
    internal static readonly float[] EncodeLut = BuildLut();

    private static float[] BuildLut()
    {
        var lut = new float[4097];
        for (var i = 0; i <= 4096; i++) lut[i] = (float)(FromLinear(i / 4096.0) * 255.0);
        return lut;
    }

    /// <summary>Linear [0,1] → sRGB in 0..255 as a float (pre-quantisation, so the caller
    /// can add dither before rounding).</summary>
    internal static float EncodeFloat(float linear)
    {
        if (linear <= 0) return 0;
        if (linear >= 1) return 255;
        var f = linear * 4096f;
        var i = (int)f;
        var t = f - i;
        return EncodeLut[i] + (EncodeLut[i + 1] - EncodeLut[i]) * t;
    }
}
