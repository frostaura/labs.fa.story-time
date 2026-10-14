namespace StoryTime.Core.Engines.Narration.Dsp;

/// <summary>
/// ITU-R BS.1770-4 integrated loudness for ONE (mono) channel, streaming.
///
/// <para><b>Mono convention.</b> A mono file is measured as a single channel with weight
/// G = 1.0 — no dual-mono +3 dB. This is what <c>ffmpeg -af ebur128</c> reports for a mono
/// input, so our numbers cross-check against it directly. Under this convention a 997 Hz
/// sine at −20 dBFS peak reads −23.0 LUFS, the same value EBU Tech 3341 gives for the
/// stereo pair at −23 dBFS (the −0.691 constant cancels the K-filter's gain at 997 Hz).</para>
///
/// <para>K-weighting coefficients are derived for any sample rate from the BS.1770 analog
/// prototype (the libebur128 derivation); at 48 kHz they reproduce the published table.
/// Gating: 400 ms blocks every 100 ms, absolute gate −70 LUFS, relative gate −10 LU.</para>
/// </summary>
public sealed class LoudnessMeter
{
    private readonly Biquad _shelf;
    private readonly Biquad _highPass;
    private readonly int _subBlock;
    private double _subSum;
    private int _subCount;
    private readonly List<double> _subPowers = [];
    private readonly List<double> _blockPowers = [];

    public LoudnessMeter(int sampleRate)
    {
        SampleRate = sampleRate;
        (_shelf, _highPass) = KWeighting(sampleRate);
        _subBlock = sampleRate / 10;
    }

    public int SampleRate { get; }

    /// <summary>K-weighting stage 1 (high shelf) and stage 2 (RLB high-pass).</summary>
    public static (Biquad Shelf, Biquad HighPass) KWeighting(int fs)
    {
        // Stage 1: high-shelf ≈ +4 dB above ~1.5 kHz.
        var f0 = 1681.974450955533;
        var g = 3.999843853973347;
        var q = 0.7071752369554196;
        var k = Math.Tan(Math.PI * f0 / fs);
        var vh = Math.Pow(10.0, g / 20.0);
        var vb = Math.Pow(vh, 0.4996667741545416);
        var a0 = 1.0 + k / q + k * k;
        var shelf = Biquad.FromCoefficients(
            (vh + vb * k / q + k * k) / a0,
            2.0 * (k * k - vh) / a0,
            (vh - vb * k / q + k * k) / a0,
            2.0 * (k * k - 1.0) / a0,
            (1.0 - k / q + k * k) / a0);

        // Stage 2: RLB high-pass ≈ 38 Hz.
        f0 = 38.13547087602444;
        q = 0.5003270373238773;
        k = Math.Tan(Math.PI * f0 / fs);
        var d = 1.0 + k / q + k * k;
        var hp = Biquad.FromCoefficients(1.0, -2.0, 1.0, 2.0 * (k * k - 1.0) / d, (1.0 - k / q + k * k) / d);
        return (shelf, hp);
    }

    public void Add(ReadOnlySpan<float> samples)
    {
        foreach (var s in samples)
        {
            var y = _highPass.Tick(_shelf.Tick(s));
            _subSum += y * y;
            if (++_subCount == _subBlock)
            {
                _subPowers.Add(_subSum / _subBlock);
                _subSum = 0;
                _subCount = 0;
                if (_subPowers.Count >= 4)
                {
                    var n = _subPowers.Count;
                    _blockPowers.Add((_subPowers[n - 1] + _subPowers[n - 2] + _subPowers[n - 3] + _subPowers[n - 4]) / 4.0);
                }
            }
        }
    }

    /// <summary>Gated integrated loudness, LUFS (−inf as −200 when nothing passes the gate).</summary>
    public double IntegratedLufs
    {
        get
        {
            const double absGate = -70.0;
            var passAbs = _blockPowers.Where(p => Lufs(p) > absGate).ToList();
            if (passAbs.Count == 0) return -200;
            var relGate = Lufs(passAbs.Average()) - 10.0;
            var passRel = passAbs.Where(p => Lufs(p) > relGate).ToList();
            return passRel.Count == 0 ? -200 : Lufs(passRel.Average());
        }
    }

    /// <summary>Number of 400 ms blocks measured so far.</summary>
    public int BlockCount => _blockPowers.Count;

    private static double Lufs(double meanSquare) => meanSquare <= 0 ? -200 : -0.691 + 10.0 * Math.Log10(meanSquare);

    /// <summary>Convenience: integrated loudness of a whole buffer.</summary>
    public static double Measure(ReadOnlySpan<float> samples, int sampleRate)
    {
        var m = new LoudnessMeter(sampleRate);
        m.Add(samples);
        return m.IntegratedLufs;
    }
}

/// <summary>
/// True-peak estimate per BS.1770-4 Annex 2 in spirit: 4× oversampling through a 49-tap
/// Kaiser-windowed sinc interpolator (12 taps per phase), max |sample| over all phases.
/// Streaming. Reported in dBTP.
/// </summary>
public sealed class TruePeakMeter
{
    private const int Oversample = 4;
    private const int TapsPerPhase = 12;
    private static readonly double[][] Phases = BuildPhases();
    private readonly double[] _history = new double[TapsPerPhase];
    private int _pos;

    public double PeakLinear { get; private set; }
    public double PeakDbtp => AudioShaper.GainToDb(PeakLinear);

    private static double[][] BuildPhases()
    {
        var length = Oversample * TapsPerPhase + 1;            // 49
        var centre = (length - 1) / 2.0;                         // 24
        var h = new double[length];
        const double beta = 7.0;
        var i0b = Bessel(beta);
        for (var n = 0; n < length; n++)
        {
            var x = (n - centre) / Oversample;
            var sinc = x == 0 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);
            var r = (n - centre) / centre;
            h[n] = sinc * Bessel(beta * Math.Sqrt(Math.Max(0, 1 - r * r))) / i0b;
        }
        var phases = new double[Oversample][];
        for (var p = 0; p < Oversample; p++)
        {
            phases[p] = new double[TapsPerPhase];
            for (var j = 0; j < TapsPerPhase; j++)
            {
                var idx = p + j * Oversample;
                phases[p][j] = idx < length ? h[idx] : 0;
            }
        }
        // Normalise each phase to unity DC gain so a constant reads its own value.
        foreach (var ph in phases)
        {
            var sum = ph.Sum();
            for (var j = 0; j < ph.Length; j++) ph[j] /= sum;
        }
        return phases;
    }

    private static double Bessel(double x)
    {
        double sum = 1, term = 1, half = x / 2;
        for (var k = 1; k < 40; k++) { term *= half / k; sum += term * term; }
        return sum;
    }

    public void Add(ReadOnlySpan<float> samples)
    {
        foreach (var s in samples)
        {
            _history[_pos] = s;
            _pos = (_pos + 1) % TapsPerPhase;
            if (Math.Abs(s) > PeakLinear) PeakLinear = Math.Abs(s);
            for (var p = 0; p < Oversample; p++)
            {
                var taps = Phases[p];
                double acc = 0;
                var idx = _pos;                                   // oldest sample
                for (var j = TapsPerPhase - 1; j >= 0; j--)
                {
                    acc += _history[idx] * taps[j];
                    if (++idx == TapsPerPhase) idx = 0;
                }
                var a = Math.Abs(acc);
                if (a > PeakLinear) PeakLinear = a;
            }
        }
    }

    public static double MeasureDbtp(ReadOnlySpan<float> samples)
    {
        var m = new TruePeakMeter();
        m.Add(samples);
        return m.PeakDbtp;
    }
}
