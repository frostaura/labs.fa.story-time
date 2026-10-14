namespace StoryTime.Core.Engines.Narration.Dsp;

/// <summary>
/// Band-limited arbitrary-ratio resampler (Kaiser-windowed sinc, table-interpolated).
/// AVSpeechSynthesizer hands back Float32 mono at 16, 22.05 or 48 kHz depending on the voice;
/// everything downstream runs at <see cref="TargetRate"/>. Each utterance is resampled on its
/// own: utterances are trimmed and faded first, so zero-padding at the edges is exact.
/// Cutoff sits at 95% of the lower Nyquist; stop-band ≈ −90 dB.
/// </summary>
public static class Resampler
{
    public const int TargetRate = 48_000;
    private const int ZeroCrossings = 24;
    private const int TableResolution = 512;
    private const double Beta = 9.0;
    private const double CutoffFraction = 0.95;

    private static readonly Lazy<double[]> Kernel = new(BuildKernel);

    private static double[] BuildKernel()
    {
        var n = ZeroCrossings * TableResolution + 2;
        var table = new double[n];
        var i0Beta = BesselI0(Beta);
        for (var k = 0; k < n; k++)
        {
            var u = k / (double)TableResolution;                 // 0..ZC (in zero crossings)
            if (u >= ZeroCrossings) { table[k] = 0; continue; }
            var sinc = u == 0 ? 1.0 : Math.Sin(Math.PI * u) / (Math.PI * u);
            var r = u / ZeroCrossings;
            var w = BesselI0(Beta * Math.Sqrt(1 - r * r)) / i0Beta;
            table[k] = sinc * w;
        }
        return table;
    }

    private static double BesselI0(double x)
    {
        double sum = 1, term = 1, half = x / 2;
        for (var k = 1; k < 50; k++)
        {
            term *= half / k;
            var t2 = term * term;
            sum += t2;
            if (t2 < 1e-17 * sum) break;
        }
        return sum;
    }

    /// <summary>Resample mono samples from <paramref name="inRate"/> to <paramref name="outRate"/>.</summary>
    public static float[] Resample(ReadOnlySpan<float> x, int inRate, int outRate = TargetRate)
    {
        if (inRate <= 0 || outRate <= 0) throw new ArgumentOutOfRangeException(nameof(inRate));
        if (x.Length == 0) return [];
        if (inRate == outRate) return x.ToArray();

        var table = Kernel.Value;
        var ratio = (double)outRate / inRate;
        var fc = Math.Min(1.0, ratio) * CutoffFraction;     // cutoff, in input-Nyquist units
        var halfWidth = ZeroCrossings / fc;                  // kernel half-width in input samples
        var outLen = (int)(((long)x.Length * outRate + inRate - 1) / inRate);
        var y = new float[outLen];

        for (var n = 0; n < outLen; n++)
        {
            var t = n / ratio;                               // position in input samples
            var i0 = Math.Max(0, (int)Math.Ceiling(t - halfWidth));
            var i1 = Math.Min(x.Length - 1, (int)Math.Floor(t + halfWidth));
            double acc = 0;
            for (var i = i0; i <= i1; i++)
            {
                var u = Math.Abs(t - i) * fc * TableResolution;  // zero crossings × resolution
                var k = (int)u;
                if (k >= table.Length - 1) continue;
                var frac = u - k;
                var h = table[k] + (table[k + 1] - table[k]) * frac;
                acc += x[i] * h;
            }
            y[n] = (float)(acc * fc);
        }
        return y;
    }
}
