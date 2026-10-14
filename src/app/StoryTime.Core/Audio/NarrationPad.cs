using StoryTime.Core.Engines.Narration.Dsp;

namespace StoryTime.Core.Audio;

/// <summary>
/// The quiet harmonic bed under SoothingNarrator narration (voice-lab.md recipe): a slow
/// Fmaj7 → Cadd9 → Am7 → Dm loop, 10 s per chord with 4 s equal-power crossfades, each tone
/// two slightly detuned oscillators on <see cref="AmbientComposer"/>'s soft pad wavetable,
/// low-passed at 900 Hz. It sits 28 LU under the voice, so the spliced pauses breathe instead
/// of dropping to digital silence.
///
/// <para>Unlike the Premium ambient loop, the pad is a pure function of ABSOLUTE story time:
/// a generator started at any offset produces exactly the samples a generator started at 0
/// would have produced there. That is what lets the overlapped pipeline render windows
/// separately and concatenate them with no seam in the music.</para>
/// </summary>
public sealed class NarrationPad
{
    public const double ChordSeconds = 10.0;
    public const double CrossfadeSeconds = 4.0;
    public const double CycleSeconds = ChordSeconds * 4;
    public const double LowPassHz = 900.0;
    /// <summary>Detune between a tone's two oscillators. Small, and the second oscillator is
    /// half the level of the first, so the pair shimmers instead of throbbing (review round 1:
    /// two equal oscillators 5 cents apart cancelled every 1–2 s, a 5–8 dB throb).</summary>
    public const double DetuneCents = 3.0;
    public const double DetuneLevel = 0.5;
    /// <summary>Upper harmonics in the wavetable, so the bed survives phone speakers that
    /// roll off below ~300 Hz (still under the 900 Hz low-pass).</summary>
    public const double Brightness = 0.6;
    /// <summary>At the end of a story the progression resolves home to Fmaj7 over this long.</summary>
    public const double ResolveSeconds = 3.0;
    /// <summary>The pad's level relative to the voice (integrated, LU).</summary>
    public const double BelowVoiceLu = 28.0;
    private const double WarmUpSeconds = 0.25;

    /// <summary>MIDI voicings (F3 = 53): Fmaj7, Cadd9, Am7, Dm.</summary>
    public static readonly int[][] Chords =
    [
        [53, 57, 60, 64],   // F A C E
        [48, 55, 62, 64],   // C G D E
        [45, 57, 60, 67],   // A A C G
        [50, 53, 57, 62],   // D F A D
    ];

    private static readonly double[] Table = AmbientComposer.BuildPadWavetable(Brightness);
    private static readonly Dictionary<int, double> ReferenceLufs = [];
    private static readonly Lock ReferenceGate = new();

    private readonly int _sampleRate;
    private readonly Biquad _lp1, _lp2;
    private readonly double[][] _freqs;     // per chord, per oscillator (tone × 2), Hz
    private readonly double[][] _phase;     // per chord, per oscillator, cycles in [0,1)
    private readonly double[][] _step;      // per chord, per oscillator, cycles per sample
    private readonly double? _resolveAt;    // absolute seconds; from here the bed settles on Fmaj7
    private readonly double[] _weight = new double[4];
    private readonly double[] _weightStep = new double[4];
    private long _frame;                    // absolute frame index of the next output sample
    private const int ControlInterval = 64; // chord weights move over seconds: block-rate is exact enough

    /// <param name="resolveAtSeconds">Absolute story time from which the progression resolves
    /// to the home chord (the story's last word); null for a window that does not end the story.</param>
    public NarrationPad(int sampleRate, double startSeconds, double? resolveAtSeconds = null)
    {
        _sampleRate = sampleRate;
        _resolveAt = resolveAtSeconds;
        _lp1 = Biquad.LowPass(sampleRate, LowPassHz, 0.5412);   // 4th-order Butterworth pair
        _lp2 = Biquad.LowPass(sampleRate, LowPassHz, 1.3066);
        var detune = Math.Pow(2.0, DetuneCents / 2 / 1200.0);
        _freqs = Chords.Select(chord => chord.SelectMany(m =>
        {
            var f = AmbientComposer.MidiToHz(m);
            return new[] { f / detune, f * detune };
        }).ToArray()).ToArray();
        _step = _freqs.Select(fs => fs.Select(f => f / sampleRate).ToArray()).ToArray();

        var start = (long)Math.Round(startSeconds * sampleRate);
        var warm = (long)(WarmUpSeconds * sampleRate);
        _frame = start - warm;
        // Oscillator phase is a function of absolute time, so any start point continues the
        // exact waveform a generator started at 0 would produce.
        var t0 = _frame / (double)sampleRate;
        _phase = _freqs.Select(fs => fs.Select(f => Frac(f * t0)).ToArray()).ToArray();
        // Run the filters into their steady state over the warm-up, discarding output.
        Span<float> scratch = stackalloc float[256];
        var remaining = warm;
        while (remaining > 0)
        {
            var n = (int)Math.Min(remaining, scratch.Length);
            Render(scratch[..n]);
            remaining -= n;
        }
    }

    private static double Frac(double x) => x - Math.Floor(x);

    /// <summary>Equal-power chord weight for chord <paramref name="k"/> at time t (seconds).</summary>
    public static double ChordWeight(int k, double t)
    {
        var tau = t % CycleSeconds;
        if (tau < 0) tau += CycleSeconds;
        var d = tau - k * ChordSeconds;
        if (d < -CycleSeconds / 2) d += CycleSeconds;
        if (d >= CycleSeconds / 2) d -= CycleSeconds;
        var half = CrossfadeSeconds / 2;
        if (d < -half || d >= ChordSeconds + half) return 0;
        if (d < half) return Math.Sin((d + half) / CrossfadeSeconds * Math.PI / 2);
        if (d < ChordSeconds - half) return 1;
        return Math.Cos((d - (ChordSeconds - half)) / CrossfadeSeconds * Math.PI / 2);
    }

    /// <summary>Chord weight including the end-of-story resolution to Fmaj7 (equal power).</summary>
    private double Weight(int k, double t)
    {
        var w = ChordWeight(k, t);
        if (_resolveAt is not { } at || t <= at) return w;
        var x = Math.Min(1.0, (t - at) / ResolveSeconds);
        var r = 0.5 - 0.5 * Math.Cos(Math.PI * x);
        return Math.Sqrt((1 - r) * w * w + (k == 0 ? r : 0));
    }

    /// <summary>Render the next block of unit-level pad (calibrate with <see cref="GainFor"/>).</summary>
    public void Render(Span<float> dest)
    {
        const int size = AmbientComposer.WavetableSize;
        const double amp = 0.18;
        for (var i = 0; i < dest.Length; i++)
        {
            var f = _frame + i;
            if (f % ControlInterval == 0 || i == 0)
            {
                // Chord weights at this block's start and end, interpolated per sample.
                var blockLeft = ControlInterval - (int)(((f % ControlInterval) + ControlInterval) % ControlInterval);
                var t0 = f / (double)_sampleRate;
                var t1 = (f + blockLeft) / (double)_sampleRate;
                for (var k = 0; k < Chords.Length; k++)
                {
                    _weight[k] = Weight(k, t0);
                    _weightStep[k] = (Weight(k, t1) - _weight[k]) / blockLeft;
                }
            }
            double sum = 0;
            for (var k = 0; k < Chords.Length; k++)
            {
                var w = _weight[k];
                _weight[k] += _weightStep[k];
                var phases = _phase[k];
                var steps = _step[k];
                if (w <= 0 && _weightStep[k] <= 0)
                {
                    for (var o = 0; o < phases.Length; o++) { var p = phases[o] + steps[o]; phases[o] = p >= 1 ? p - 1 : p; }
                    continue;
                }
                double chord = 0;
                for (var o = 0; o < phases.Length; o++)
                {
                    var idx = phases[o] * size;
                    var i0 = (int)idx;
                    var frac = idx - i0;
                    var v = Table[i0] + (Table[(i0 + 1) & (size - 1)] - Table[i0]) * frac;
                    chord += (o & 1) == 0 ? v : v * DetuneLevel;
                    var p = phases[o] + steps[o];
                    phases[o] = p >= 1 ? p - 1 : p;
                }
                sum += chord * Math.Max(0, w);
            }
            dest[i] = (float)_lp2.Tick(_lp1.Tick(sum * amp));
        }
        _frame += dest.Length;
    }

    /// <summary>Integrated loudness of the unit-level pad over one full chord cycle.</summary>
    public static double UnitLoudnessLufs(int sampleRate)
    {
        lock (ReferenceGate)
        {
            if (ReferenceLufs.TryGetValue(sampleRate, out var cached)) return cached;
            var pad = new NarrationPad(sampleRate, 0);
            var buffer = new float[(int)(CycleSeconds * sampleRate)];
            pad.Render(buffer);
            var lufs = LoudnessMeter.Measure(buffer, sampleRate);
            ReferenceLufs[sampleRate] = lufs;
            return lufs;
        }
    }

    /// <summary>Linear gain that puts the pad <paramref name="belowVoiceLu"/> under a voice
    /// measuring <paramref name="voiceLufs"/>.</summary>
    public static double GainFor(double voiceLufs, int sampleRate, double belowVoiceLu = BelowVoiceLu)
        => Engines.Narration.AudioShaper.DbToGain(voiceLufs - belowVoiceLu - UnitLoudnessLufs(sampleRate));
}
