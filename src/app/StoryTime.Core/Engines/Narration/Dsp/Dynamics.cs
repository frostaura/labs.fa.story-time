namespace StoryTime.Core.Engines.Narration.Dsp;

/// <summary>
/// Feed-forward RMS compressor with a soft knee (Giannoulis/Massberg/Reiss gain computer),
/// matching the measured ffmpeg chain: −26 dB threshold, 2.5:1, attack 12 ms, release 180 ms,
/// knee 6 dB, make-up 2 dB. The gain is recomputed every <see cref="ControlInterval"/> samples
/// (0.17 ms at 48 kHz, far below the attack) and interpolated per sample, which keeps the
/// per-sample cost to a multiply-add on devices without a JIT.
/// </summary>
public sealed class Compressor
{
    public const double DefaultThresholdDb = -26.0;
    public const double DefaultRatio = 2.5;
    public const double DefaultAttackMs = 12.0;
    public const double DefaultReleaseMs = 180.0;
    public const double DefaultKneeDb = 6.0;
    public const double DefaultMakeupDb = 2.0;
    private const int ControlInterval = 8;

    private readonly double _threshold, _ratio, _knee, _makeup, _attack, _release;
    private double _env;
    private double _gain = 1.0;
    private int _countdown;
    private double _gainStep;

    public Compressor(int sampleRate, double thresholdDb = DefaultThresholdDb, double ratio = DefaultRatio,
        double attackMs = DefaultAttackMs, double releaseMs = DefaultReleaseMs, double kneeDb = DefaultKneeDb,
        double makeupDb = DefaultMakeupDb)
    {
        _threshold = thresholdDb;
        _ratio = ratio;
        _knee = kneeDb;
        _makeup = makeupDb;
        _attack = Math.Exp(-1.0 / (attackMs / 1000.0 * sampleRate));
        _release = Math.Exp(-1.0 / (releaseMs / 1000.0 * sampleRate));
        _gain = AudioShaper.DbToGain(makeupDb);
    }

    /// <summary>Largest gain reduction seen so far (dB, positive), for reports.</summary>
    public double MaxGainReductionDb { get; private set; }

    /// <summary>Static curve: output level for an input level (both dB), make-up excluded.</summary>
    public double OutputLevelDb(double inputDb)
    {
        var over = inputDb - _threshold;
        if (2 * over < -_knee) return inputDb;
        if (2 * Math.Abs(over) <= _knee)
            return inputDb + (1 / _ratio - 1) * (over + _knee / 2) * (over + _knee / 2) / (2 * _knee);
        return _threshold + over / _ratio;
    }

    public void Process(Span<float> samples)
    {
        for (var i = 0; i < samples.Length; i++)
        {
            double x = samples[i];
            var x2 = x * x;
            var coef = x2 > _env ? _attack : _release;
            _env = coef * _env + (1 - coef) * x2;

            if (_countdown-- <= 0)
            {
                _countdown = ControlInterval - 1;
                var level = 10.0 * Math.Log10(_env + 1e-20);
                var reduction = level - OutputLevelDb(level);
                if (reduction > MaxGainReductionDb) MaxGainReductionDb = reduction;
                var target = AudioShaper.DbToGain(_makeup - reduction);
                _gainStep = (target - _gain) / ControlInterval;
            }
            _gain += _gainStep;
            samples[i] = (float)(x * _gain);
        }
    }
}

/// <summary>
/// Split-band, relative-threshold de-esser (<see cref="DeEsserSettings"/>; defaults: centre
/// 6.5 kHz, −12 dB relative threshold, 3:1, at most 8 dB). A 0 dB band-pass isolates the
/// sibilance band; when its short-term energy rises above the threshold relative to the whole
/// signal, that band alone is pulled down. Relative
/// detection keeps it voice-independent: compact and premium voices arrive at different
/// levels, but an "s" is always band-heavy.
/// </summary>
public sealed class DeEsser
{
    private const int ControlInterval = 8;

    private readonly Biquad _band;
    private readonly double _attack, _release, _threshold, _slope, _maxReduction;
    private double _envFull, _envBand;
    private double _g = 1.0;
    private double _gStep;
    private int _countdown;

    public DeEsser(int sampleRate, DeEsserSettings? settings = null)
    {
        var s = settings ?? new DeEsserSettings();
        _band = Biquad.BandPass(sampleRate, s.CentreHz, s.Q);
        _threshold = s.ThresholdDb;
        _slope = 1 - 1 / s.Ratio;
        _maxReduction = s.MaxReductionDb;
        _attack = Math.Exp(-1.0 / (0.001 * sampleRate));
        _release = Math.Exp(-1.0 / (0.060 * sampleRate));
    }

    public double MaxReductionSeenDb { get; private set; }

    public void Process(Span<float> samples)
    {
        for (var i = 0; i < samples.Length; i++)
        {
            double x = samples[i];
            var band = _band.Tick(x);
            var xf = x * x;
            var xb = band * band;
            _envFull = (xf > _envFull ? _attack : _release) * (_envFull - xf) + xf;
            _envBand = (xb > _envBand ? _attack : _release) * (_envBand - xb) + xb;

            if (_countdown-- <= 0)
            {
                _countdown = ControlInterval - 1;
                var ratioDb = 10.0 * Math.Log10((_envBand + 1e-20) / (_envFull + 1e-20));
                var over = ratioDb - _threshold;
                var reduction = over <= 0 ? 0 : Math.Min(_maxReduction, over * _slope);
                if (reduction > MaxReductionSeenDb) MaxReductionSeenDb = reduction;
                _gStep = (AudioShaper.DbToGain(-reduction) - _g) / ControlInterval;
            }
            _g += _gStep;
            samples[i] = (float)(x - (1 - _g) * band);
        }
    }
}

/// <summary>
/// Look-ahead peak limiter for the normalised VOICE stem (never the voice + pad mix).
/// Compact voices carry transients 16–20 dB above their loudness, so normalising to −18 LUFS
/// under a −2 dBTP ceiling would otherwise leave the story 2–5 dB too quiet. Gain is the
/// sliding minimum of the required gain over the look-ahead window, released exponentially,
/// then box-smoothed over the window — so it is already down when the peak arrives and
/// never steps. Latency = look-ahead; callers feed the input that many samples early.
/// </summary>
public sealed class PeakLimiter
{
    private readonly double _ceiling;
    private readonly double _release;
    private readonly int _look;
    private readonly float[] _delay;
    private readonly double[] _box;           // released gain ring (look)
    private int _pos, _boxPos;
    private double _boxSum, _released = 1.0;
    // Monotonic deque (ring of capacity look+1) for the sliding minimum.
    private readonly long[] _qIndex;
    private readonly double[] _qGain;
    private int _qHead, _qCount;
    private long _n;

    public PeakLimiter(int sampleRate, double ceilingDbfs, double lookaheadMs = 5, double releaseMs = 60)
    {
        _ceiling = AudioShaper.DbToGain(ceilingDbfs);
        _look = Math.Max(1, (int)Math.Round(lookaheadMs / 1000 * sampleRate));
        _release = Math.Exp(-1.0 / (releaseMs / 1000 * sampleRate));
        _delay = new float[_look + 1];
        _qIndex = new long[_look + 2];
        _qGain = new double[_look + 2];
        _box = new double[_look];
        Array.Fill(_box, 1.0);
        _boxSum = _look;
    }

    /// <summary>Output lags input by this many samples.</summary>
    public int LatencySamples => _look;
    public double MaxReductionDb { get; private set; }

    public void Process(Span<float> samples)
    {
        for (var i = 0; i < samples.Length; i++)
        {
            var x = samples[i];
            var a = Math.Abs((double)x);
            var g = a > _ceiling ? _ceiling / a : 1.0;

            // Sliding minimum of the required gain over the last look+1 inputs.
            var cap = _qIndex.Length;
            while (_qCount > 0 && _qGain[(_qHead + _qCount - 1) % cap] >= g) _qCount--;
            var tail = (_qHead + _qCount) % cap;
            _qIndex[tail] = _n;
            _qGain[tail] = g;
            _qCount++;
            while (_qIndex[_qHead] <= _n - _delay.Length) { _qHead = (_qHead + 1) % cap; _qCount--; }
            var min = _qGain[_qHead];

            _released = min < _released ? min : min - (min - _released) * _release;
            _boxSum += _released - _box[_boxPos];
            _box[_boxPos] = _released;
            _boxPos = (_boxPos + 1) % _box.Length;
            var gain = Math.Min(1.0, _boxSum / _box.Length);

            var outIdx = (_pos + 1) % _delay.Length;   // oldest = input from `look` samples ago
            _delay[_pos] = x;
            var y = _delay[outIdx] * gain;
            _pos = outIdx;
            _n++;

            var reduction = -AudioShaper.GainToDb(gain);
            if (reduction > MaxReductionDb) MaxReductionDb = reduction;
            samples[i] = (float)y;
        }
    }
}
