namespace StoryTime.Core.Engines.Narration.Dsp;

/// <summary>
/// One RBJ (Audio-EQ-Cookbook) biquad section, transposed direct form II, double-precision
/// state. Evolved from the 2026-07-31 cozy-mastering shelves: same cookbook maths, now with
/// the full set the measured chain needs (HP / LP / peaking / shelves / band-pass) and
/// STREAMING state, because the chain now runs over a continuous story timeline rather than
/// over isolated chunks. Deterministic, allocation-free per sample.
/// </summary>
public sealed class Biquad
{
    private readonly double _b0, _b1, _b2, _a1, _a2;
    private double _z1, _z2;

    /// <summary>1/√2 — maximally flat 2-pole response.</summary>
    public const double ButterworthQ = 0.70710678118654752;

    private Biquad(double b0, double b1, double b2, double a0, double a1, double a2)
    {
        _b0 = b0 / a0; _b1 = b1 / a0; _b2 = b2 / a0;
        _a1 = a1 / a0; _a2 = a2 / a0;
    }

    /// <summary>Butterworth-style 2-pole high-pass (Q 0.7071 ≈ ffmpeg highpass default).</summary>
    public static Biquad HighPass(int sampleRate, double freqHz, double q = ButterworthQ)
    {
        var (w0, alpha) = Angle(sampleRate, freqHz, q);
        var cos = Math.Cos(w0);
        return new Biquad((1 + cos) / 2, -(1 + cos), (1 + cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
    }

    public static Biquad LowPass(int sampleRate, double freqHz, double q = ButterworthQ)
    {
        var (w0, alpha) = Angle(sampleRate, freqHz, q);
        var cos = Math.Cos(w0);
        return new Biquad((1 - cos) / 2, 1 - cos, (1 - cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
    }

    /// <summary>Band-pass with 0 dB gain at the centre (the de-esser's sibilance band).</summary>
    public static Biquad BandPass(int sampleRate, double freqHz, double q)
    {
        var (w0, alpha) = Angle(sampleRate, freqHz, q);
        var cos = Math.Cos(w0);
        return new Biquad(alpha, 0, -alpha, 1 + alpha, -2 * cos, 1 - alpha);
    }

    /// <summary>Peaking EQ (bell).</summary>
    public static Biquad Peaking(int sampleRate, double freqHz, double gainDb, double q)
    {
        var a = Math.Pow(10.0, gainDb / 40.0);
        var (w0, alpha) = Angle(sampleRate, freqHz, q);
        var cos = Math.Cos(w0);
        return new Biquad(1 + alpha * a, -2 * cos, 1 - alpha * a, 1 + alpha / a, -2 * cos, 1 - alpha / a);
    }

    /// <summary>High shelf, slope S = 1.</summary>
    public static Biquad HighShelf(int sampleRate, double freqHz, double gainDb)
    {
        var (a, w0, alpha) = ShelfPrelude(sampleRate, freqHz, gainDb);
        var cos = Math.Cos(w0);
        var sq = 2.0 * Math.Sqrt(a) * alpha;
        return new Biquad(
            a * ((a + 1) + (a - 1) * cos + sq),
            -2.0 * a * ((a - 1) + (a + 1) * cos),
            a * ((a + 1) + (a - 1) * cos - sq),
            (a + 1) - (a - 1) * cos + sq,
            2.0 * ((a - 1) - (a + 1) * cos),
            (a + 1) - (a - 1) * cos - sq);
    }

    /// <summary>Low shelf, slope S = 1.</summary>
    public static Biquad LowShelf(int sampleRate, double freqHz, double gainDb)
    {
        var (a, w0, alpha) = ShelfPrelude(sampleRate, freqHz, gainDb);
        var cos = Math.Cos(w0);
        var sq = 2.0 * Math.Sqrt(a) * alpha;
        return new Biquad(
            a * ((a + 1) - (a - 1) * cos + sq),
            2.0 * a * ((a - 1) - (a + 1) * cos),
            a * ((a + 1) - (a - 1) * cos - sq),
            (a + 1) + (a - 1) * cos + sq,
            -2.0 * ((a - 1) + (a + 1) * cos),
            (a + 1) + (a - 1) * cos - sq);
    }

    /// <summary>Raw normalised coefficients (b0, b1, b2, a1, a2) — used by the BS.1770
    /// K-weighting filters, whose coefficients are specified directly.</summary>
    public static Biquad FromCoefficients(double b0, double b1, double b2, double a1, double a2)
        => new(b0, b1, b2, 1.0, a1, a2);

    private static (double W0, double Alpha) Angle(int sampleRate, double freqHz, double q)
    {
        var w0 = 2.0 * Math.PI * freqHz / sampleRate;
        return (w0, Math.Sin(w0) / (2.0 * q));
    }

    private static (double A, double W0, double Alpha) ShelfPrelude(int sampleRate, double freqHz, double gainDb)
    {
        var a = Math.Pow(10.0, gainDb / 40.0);
        var w0 = 2.0 * Math.PI * freqHz / sampleRate;
        var alpha = Math.Sin(w0) / 2.0 * Math.Sqrt(2.0); // S = 1
        return (a, w0, alpha);
    }

    /// <summary>One sample through the filter (state persists).</summary>
    public double Tick(double x)
    {
        var y = _b0 * x + _z1;
        _z1 = _b1 * x - _a1 * y + _z2;
        _z2 = _b2 * x - _a2 * y;
        return y;
    }

    /// <summary>Filter a block in place; state carries into the next block.</summary>
    public void Process(Span<float> samples)
    {
        for (var i = 0; i < samples.Length; i++) samples[i] = (float)Tick(samples[i]);
    }

    public void Reset() => _z1 = _z2 = 0;

    /// <summary>Analytic magnitude response at <paramref name="freqHz"/>, in dB.</summary>
    public double MagnitudeDb(double freqHz, int sampleRate)
    {
        var w = 2.0 * Math.PI * freqHz / sampleRate;
        var (c1, s1, c2, s2) = (Math.Cos(w), Math.Sin(w), Math.Cos(2 * w), Math.Sin(2 * w));
        var nr = _b0 + _b1 * c1 + _b2 * c2;
        var ni = -(_b1 * s1 + _b2 * s2);
        var dr = 1 + _a1 * c1 + _a2 * c2;
        var di = -(_a1 * s1 + _a2 * s2);
        return 10.0 * Math.Log10((nr * nr + ni * ni) / (dr * dr + di * di));
    }
}
