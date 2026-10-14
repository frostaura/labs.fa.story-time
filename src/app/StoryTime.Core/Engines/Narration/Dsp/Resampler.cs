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
    /// <summary>Rational ratios with at most this many output phases use a precomputed
    /// polyphase bank (22.05→48 kHz is 320 phases; 16→48 is 3).</summary>
    private const int MaxPhases = 4096;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), (float[] Bank, int Taps)> Banks = new();

    private static int Gcd(int a, int b) { while (b != 0) (a, b) = (b, a % b); return a; }

    /// <summary>Exact-phase polyphase path: output n sits at input position n·M/L, whose
    /// fractional part is one of L fixed phases, each a precomputed tap set.</summary>
    private static float[] Polyphase(ReadOnlySpan<float> x, int up, int down, int inRate, int outRate)
    {
        var (bank, taps) = Banks.GetOrAdd((inRate, outRate), key => BuildBank(key.Item1, key.Item2, up));
        var half = taps / 2;
        var outLen = (int)(((long)x.Length * outRate + inRate - 1) / inRate);
        var y = new float[outLen];
        for (var n = 0; n < outLen; n++)
        {
            var pos = (long)n * down;
            var ip = (int)(pos / up);
            var phase = (int)(pos % up);
            var first = ip - half + 1;                       // input index of tap 0
            var coeffs = bank.AsSpan(phase * taps, taps);
            double acc = 0;
            var j0 = Math.Max(0, -first);
            var j1 = Math.Min(taps, x.Length - first);
            for (var j = j0; j < j1; j++) acc += x[first + j] * coeffs[j];
            y[n] = (float)acc;
        }
        return y;
    }

    private static (float[] Bank, int Taps) BuildBank(int inRate, int outRate, int up)
    {
        var table = Kernel.Value;
        var fc = Math.Min(1.0, (double)outRate / inRate) * CutoffFraction;
        var half = (int)Math.Ceiling(ZeroCrossings / fc);
        var taps = 2 * half;
        var bank = new float[up * taps];
        for (var p = 0; p < up; p++)
        {
            var frac = p / (double)up;                        // t − ip
            for (var j = 0; j < taps; j++)
            {
                var d = frac + (half - 1) - j;                // t − i, with i = ip − half + 1 + j
                var u = Math.Abs(d) * fc * TableResolution;
                var k = (int)u;
                double h = 0;
                if (k < table.Length - 1) h = table[k] + (table[k + 1] - table[k]) * (u - k);
                bank[p * taps + j] = (float)(h * fc);
            }
        }
        return (bank, taps);
    }

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

        var g = Gcd(inRate, outRate);
        var up = outRate / g;                                // L phases
        var down = inRate / g;                               // M input step
        if (up <= MaxPhases) return Polyphase(x, up, down, inRate, outRate);

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
