namespace StoryTime.Core.Engines.Narration.Dsp;

/// <summary>
/// Small-room reverb (<see cref="ReverbSettings"/>; the measured recipe is 0.35 s decay
/// (RT60), 10 ms pre-delay, the wet path band-limited to 200 Hz–5 kHz, sent 17 dB below dry). Schroeder topology
/// (four damped parallel combs → two series all-passes), mono. The wet level is defined by
/// impulse-response ENERGY: the wet IR carries exactly −17 dB of the dry impulse's energy, so
/// the send level is a measurable property rather than a knob. Streaming and deterministic.
/// </summary>
public sealed class RoomReverb
{
    private static readonly double[] CombMs = [23.7, 27.1, 31.3, 35.9];
    private static readonly double[] AllPassMs = [5.0, 1.7];
    private const double AllPassGain = 0.5;
    private const double Damping = 0.18;

    private readonly float[] _pre;
    private int _preIdx;
    private readonly Biquad _hp, _lp;
    private readonly float[][] _comb;
    private readonly int[] _combIdx;
    private readonly double[] _combFb;
    private readonly double[] _combLp;
    private readonly float[][] _ap;
    private readonly int[] _apIdx;
    private readonly double _wetGain;

    public ReverbSettings Settings { get; }

    public RoomReverb(int sampleRate, ReverbSettings? settings = null) : this(sampleRate, settings ?? new ReverbSettings(), calibrate: true) { }

    private RoomReverb(int sampleRate, ReverbSettings s, bool calibrate)
    {
        Settings = s;
        _pre = new float[Math.Max(1, (int)Math.Round(s.PreDelaySeconds * sampleRate))];
        _hp = Biquad.HighPass(sampleRate, s.WetLowHz);
        _lp = Biquad.LowPass(sampleRate, s.WetHighHz);
        _comb = new float[CombMs.Length][];
        _combIdx = new int[CombMs.Length];
        _combFb = new double[CombMs.Length];
        _combLp = new double[CombMs.Length];
        for (var c = 0; c < CombMs.Length; c++)
        {
            var len = (int)Math.Round(CombMs[c] / 1000.0 * sampleRate);
            _comb[c] = new float[len];
            // Feedback for a 60 dB decay in DecaySeconds: g = 10^(−3·d/RT60).
            _combFb[c] = Math.Pow(10.0, -3.0 * (len / (double)sampleRate) / s.DecaySeconds);
        }
        _ap = AllPassMs.Select(ms => new float[(int)Math.Round(ms / 1000.0 * sampleRate)]).ToArray();
        _apIdx = new int[AllPassMs.Length];

        if (!calibrate) { _wetGain = 1.0; return; }
        // Calibrate the send by IR energy: a unit impulse through an uncalibrated twin.
        var twin = new RoomReverb(sampleRate, s, calibrate: false);
        var energy = 0.0;
        var n = (int)(s.DecaySeconds * 4 * sampleRate);
        for (var i = 0; i < n; i++)
        {
            var w = twin.Wet(i == 0 ? 1.0 : 0.0);
            energy += w * w;
        }
        _wetGain = AudioShaper.DbToGain(s.WetDb) / Math.Sqrt(energy);
    }

    /// <summary>Wet output only (pre-delay → band → combs → all-passes), uncalibrated.</summary>
    private double Wet(double x)
    {
        var delayed = _pre[_preIdx];
        _pre[_preIdx] = (float)x;
        if (++_preIdx == _pre.Length) _preIdx = 0;

        var input = _lp.Tick(_hp.Tick(delayed));
        double sum = 0;
        for (var c = 0; c < _comb.Length; c++)
        {
            var buf = _comb[c];
            var idx = _combIdx[c];
            var y = buf[idx];
            _combLp[c] = y * (1 - Damping) + _combLp[c] * Damping;   // gentle HF damping
            buf[idx] = (float)(input + _combLp[c] * _combFb[c]);
            _combIdx[c] = idx + 1 == buf.Length ? 0 : idx + 1;
            sum += y;
        }
        var s = sum * 0.25;
        for (var a = 0; a < _ap.Length; a++)
        {
            var buf = _ap[a];
            var idx = _apIdx[a];
            var bufOut = buf[idx];
            var y = -s + bufOut;
            buf[idx] = (float)(s + bufOut * AllPassGain);
            _apIdx[a] = idx + 1 == buf.Length ? 0 : idx + 1;
            s = y;
        }
        return s;
    }

    /// <summary>dry + calibrated wet, in place.</summary>
    public void Process(Span<float> samples)
    {
        for (var i = 0; i < samples.Length; i++)
        {
            double x = samples[i];
            samples[i] = (float)(x + Wet(x) * _wetGain);
        }
    }

    /// <summary>Wet-only rendering of a block (tests measure the IR with this).</summary>
    public void ProcessWetOnly(Span<float> samples)
    {
        for (var i = 0; i < samples.Length; i++) samples[i] = (float)(Wet(samples[i]) * _wetGain);
    }
}
