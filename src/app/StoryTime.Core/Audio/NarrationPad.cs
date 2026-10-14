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
    public const double DetuneCents = 5.0;
    public const double Brightness = 0.35;
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
    private readonly double[][] _freqs;     // per chord, per oscillator (tone × 2)
    private long _frame;                    // absolute frame index of the next output sample

    public NarrationPad(int sampleRate, double startSeconds)
    {
        _sampleRate = sampleRate;
        _lp1 = Biquad.LowPass(sampleRate, LowPassHz, 0.5412);   // 4th-order Butterworth pair
        _lp2 = Biquad.LowPass(sampleRate, LowPassHz, 1.3066);
        var detune = Math.Pow(2.0, DetuneCents / 2 / 1200.0);
        _freqs = Chords.Select(chord => chord.SelectMany(m =>
        {
            var f = AmbientComposer.MidiToHz(m);
            return new[] { f / detune, f * detune };
        }).ToArray()).ToArray();

        var start = (long)Math.Round(startSeconds * sampleRate);
        var warm = (long)(WarmUpSeconds * sampleRate);
        _frame = start - warm;
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

    /// <summary>Render the next block of unit-level pad (calibrate with <see cref="GainFor"/>).</summary>
    public void Render(Span<float> dest)
    {
        const int size = AmbientComposer.WavetableSize;
        var amp = 0.18;
        for (var i = 0; i < dest.Length; i++)
        {
            var t = (_frame + i) / (double)_sampleRate;
            double sum = 0;
            for (var k = 0; k < Chords.Length; k++)
            {
                var w = ChordWeight(k, t);
                if (w <= 0) continue;
                var freqs = _freqs[k];
                double chord = 0;
                for (var o = 0; o < freqs.Length; o++)
                {
                    var phase = freqs[o] * t;
                    phase -= Math.Floor(phase);
                    var idx = phase * size;
                    var i0 = (int)idx;
                    var frac = idx - i0;
                    chord += Table[i0] + (Table[(i0 + 1) & (size - 1)] - Table[i0]) * frac;
                }
                sum += chord * w;
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
