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
