using StoryTime.Core.Audio;
using StoryTime.Core.Engines.Narration;
using StoryTime.Core.Engines.Narration.Dsp;
using Xunit;

namespace StoryTime.Core.Tests.Engines;

internal static class Signals
{
    public static float[] Sine(double hz, double seconds, int rate, double amplitude = 0.5, double phase = 0)
    {
        var n = (int)Math.Round(seconds * rate);
        var x = new float[n];
        for (var i = 0; i < n; i++) x[i] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / rate + phase));
        return x;
    }

    /// <summary>RMS gain (dB) of a filter-like process on a steady sine, skipping the transient.</summary>
    public static double GainDb(Action<float[]> process, double hz, int rate)
    {
        var x = Sine(hz, 1.0, rate, 0.25);
        var y = (float[])x.Clone();
        process(y);
        var skip = rate / 2;
        return 20 * Math.Log10(Rms(y.AsSpan(skip)) / Rms(x.AsSpan(skip)));
    }

    public static double Rms(ReadOnlySpan<float> x)
    {
        double s = 0;
        foreach (var v in x) s += (double)v * v;
        return Math.Sqrt(s / x.Length);
    }

    public static float[] Noise(double seconds, int rate, double amplitude, uint seed = 1)
    {
        var n = (int)(seconds * rate);
        var x = new float[n];
        var s = seed;
        for (var i = 0; i < n; i++)
        {
            s ^= s << 13; s ^= s >> 17; s ^= s << 5;
            x[i] = (float)(amplitude * (s / (double)uint.MaxValue * 2 - 1));
        }
        return x;
    }
}

public class EdgeTrimTests
{
    [Fact]
    public void Trims_To30ms_AndFades8ms()
    {
        const int rate = 22050;
        var x = new float[rate];                         // 1 s
        for (var i = 5000; i < 15000; i++) x[i] = 0.3f;  // audible middle
        var y = AudioShaper.TrimEdges(x, rate);
        var keep = (int)Math.Round(0.030 * rate);
        Assert.Equal(10000 + 2 * keep, y.Length);
        Assert.True(Math.Abs(y[0]) < 1e-6);
        var fade = (int)Math.Round(0.008 * rate);
        Assert.Equal(0.0, y[keep - 1], 6);              // kept silence stays silent
        Assert.True(y[keep + fade + 5] > 0.29f);        // inside: untouched
    }

    [Fact]
    public void Threshold_IsMinus50Dbfs()
    {
        const int rate = 48000;
        var x = new float[rate];
        for (var i = 0; i < 1000; i++) x[i] = 0.002f;   // −54 dBFS: treated as silence
        for (var i = 20000; i < 21000; i++) x[i] = 0.004f; // −48 dBFS: audible
        var y = AudioShaper.TrimEdges(x, rate);
        Assert.Equal(1000 + 2 * (int)Math.Round(0.030 * rate), y.Length);
    }

    [Fact]
    public void AllSilent_IsEmpty() => Assert.Empty(AudioShaper.TrimEdges(new float[4000], 22050));
}

public class ResamplerTests
{
    [Theory]
    [InlineData(22050)]
    [InlineData(16000)]
    [InlineData(44100)]
    public void Sine_IsReproducedAccurately_At48k(int inRate)
    {
        var x = Signals.Sine(1000, 1.0, inRate, 0.5);
        var y = Resampler.Resample(x, inRate, 48000);
        Assert.Equal((int)(((long)x.Length * 48000 + inRate - 1) / inRate), y.Length);
        // Compare the middle against the ideal 48 kHz sine.
        double err = 0, sig = 0;
        for (var n = 4800; n < y.Length - 4800; n++)
        {
            var ideal = 0.5 * Math.Sin(2 * Math.PI * 1000 * n / 48000.0);
            err += (y[n] - ideal) * (y[n] - ideal);
            sig += ideal * ideal;
        }
        var snrDb = 10 * Math.Log10(sig / err);
        Assert.True(snrDb > 70, $"resampled sine SNR {snrDb:0.0} dB");
    }

    [Fact]
    public void Identity_WhenRatesMatch()
    {
        var x = Signals.Sine(440, 0.1, 48000);
        Assert.Equal(x, Resampler.Resample(x, 48000, 48000));
    }

    [Fact]
    public void Downsampling_RejectsContentAboveTheNewNyquist()
    {
        var x = Signals.Sine(20000, 1.0, 48000, 0.5);       // above 11.025 kHz Nyquist of 22.05k
        var y = Resampler.Resample(x, 48000, 22050);
        Assert.True(20 * Math.Log10(Signals.Rms(y.AsSpan(2000, y.Length - 4000)) / 0.3535) < -60);
    }
}

public class MasteringStageTests
{
    private const int Rate = 48000;

    [Fact]
    public void HighPass70_CutsRumble()
    {
        Assert.InRange(Biquad.HighPass(Rate, 70).MagnitudeDb(30, Rate), -16, -12);
        Assert.InRange(Biquad.HighPass(Rate, 70).MagnitudeDb(70, Rate), -3.2, -2.8);
        Assert.InRange(Biquad.HighPass(Rate, 70).MagnitudeDb(400, Rate), -0.1, 0.1);
    }

    [Theory]
    [InlineData(180, 2.5, 0.9)]
    [InlineData(3200, -1.5, 1.0)]
    public void Peaking_HitsItsGain_AtCentre_AndIsFlatFarAway(double hz, double db, double q)
    {
        var f = Biquad.Peaking(Rate, hz, db, q);
        Assert.InRange(Signals.GainDb(x => Biquad.Peaking(Rate, hz, db, q).Process(x), hz, Rate), db - 0.1, db + 0.1);
        Assert.InRange(f.MagnitudeDb(hz / 16, Rate), -0.15, 0.15);
    }

    [Fact]
    public void HighShelf7k_Minus3()
    {
        Assert.InRange(Signals.GainDb(x => Biquad.HighShelf(Rate, 7000, -3).Process(x), 15000, Rate), -3.2, -2.6);
        Assert.InRange(Signals.GainDb(x => Biquad.HighShelf(Rate, 7000, -3).Process(x), 500, Rate), -0.1, 0.05);
    }

    [Fact]
    public void LowPass12k()
    {
        Assert.InRange(Biquad.LowPass(Rate, 12000).MagnitudeDb(12000, Rate), -3.2, -2.8);
        Assert.InRange(Biquad.LowPass(Rate, 12000).MagnitudeDb(1000, Rate), -0.05, 0.05);
        Assert.True(Biquad.LowPass(Rate, 12000).MagnitudeDb(20000, Rate) < -10);
    }

    [Fact]
    public void Chain_EqProbe_MatchesTheRecipeShape()
    {
        // EQ only (compressor + reverb off) — the whole static curve at a few probe points.
        double Probe(double hz) => Signals.GainDb(x => new MasteringChain(Rate, MasteringProfile.VoiceLab, reverb: false, compressor: false).Process(x), hz, Rate);
        Assert.True(Probe(40) < -7, "rumble cut");
        Assert.InRange(Probe(180), 1.8, 2.8);           // warmth (+2.5 bell riding the HP skirt)
        Assert.InRange(Probe(1000), -0.6, 0.6);         // neutral mid
        Assert.InRange(Probe(3200), -2.6, -1.4);        // presence dip (+ the 7 kHz shelf's skirt)
        Assert.InRange(Probe(10000), -6, -3.5);         // shelf + low-pass
        Assert.True(Probe(16000) < -9, "air and hiss rolled off");
    }

    [Fact]
    public void Compressor_StaticCurve()
    {
        var c = new Compressor(Rate);
        Assert.Equal(-40, c.OutputLevelDb(-40), 6);                  // below knee: unity
        Assert.Equal(-26 + 10 / 2.5, c.OutputLevelDb(-16), 6);        // above knee: 2.5:1
        var atThreshold = c.OutputLevelDb(-26);
        Assert.InRange(atThreshold, -26.6, -26.4);                    // soft knee: (1/R−1)·(W/2)²/(2W)
    }

    [Fact]
    public void Compressor_ReducesLoudPassages_LeavesQuietOnesAtMakeup()
    {
        var loud = Signals.GainDb(x => new Compressor(Rate).Process(x), 300, Rate);   // 0.25 amp ≈ −15 dB RMS
        Assert.InRange(loud, -7.5, -4.5);                                             // ≈ −(11·0.6) + 2
        var quiet = Signals.GainDb(x =>
        {
            for (var i = 0; i < x.Length; i++) x[i] *= 0.01f;     // −55 dB: under the knee
            new Compressor(Rate).Process(x);
            for (var i = 0; i < x.Length; i++) x[i] *= 100f;
        }, 300, Rate);
        Assert.InRange(quiet, 1.9, 2.1);                                              // make-up only
    }

    [Fact]
    public void DeEsser_PullsSibilance_LeavesVowelsAlone()
    {
        // Vowel-like tone: untouched.
        Assert.InRange(Signals.GainDb(x => new DeEsser(Rate).Process(x), 300, Rate), -0.05, 0.05);
        // Band-heavy "sss": high-passed noise, measured in the sibilance band.
        var s = Signals.Noise(1.0, Rate, 0.3);
        var hp = Biquad.HighPass(Rate, 5000);
        hp.Process(s);
        var before = (float[])s.Clone();
        var d = new DeEsser(Rate);
        d.Process(s);
        var bandBefore = (float[])before.Clone();
        var bandAfter = (float[])s.Clone();
        Biquad.BandPass(Rate, 6500, 1.0).Process(bandBefore);
        Biquad.BandPass(Rate, 6500, 1.0).Process(bandAfter);
        var reduction = 20 * Math.Log10(Signals.Rms(bandAfter.AsSpan(Rate / 4)) / Signals.Rms(bandBefore.AsSpan(Rate / 4)));
        Assert.InRange(reduction, -8.5, -2.0);
        Assert.True(d.MaxReductionSeenDb <= new DeEsserSettings().MaxReductionDb + 1e-9);
    }

    [Fact]
    public void Reverb_SendIsMinus17dB_ByEnergy_WithPreDelay_AndDecay()
    {
        var ir = new float[Rate * 2];
        ir[0] = 1f;
        new RoomReverb(Rate).ProcessWetOnly(ir);
        // Pre-delay: nothing before 10 ms.
        var pre = (int)(new ReverbSettings().PreDelaySeconds * Rate);
        Assert.True(ir.Take(pre - 1).All(v => Math.Abs(v) < 1e-9));
        // Wet energy relative to the unit dry impulse.
        var energy = ir.Sum(v => (double)v * v);
        Assert.InRange(10 * Math.Log10(energy), -17.3, -16.7);
        // RT60 from the Schroeder backward integral (T20 extrapolated).
        var edc = new double[ir.Length];
        double acc = 0;
        for (var i = ir.Length - 1; i >= 0; i--) { acc += (double)ir[i] * ir[i]; edc[i] = acc; }
        int At(double db) => Array.FindIndex(edc, e => 10 * Math.Log10(e / edc[0]) <= db);
        var t20 = (At(-25) - At(-5)) / (double)Rate;
        var rt60 = t20 * 3;
        Assert.InRange(rt60, 0.26, 0.44);
    }

    [Fact]
    public void Reverb_WetIsBandLimited()
    {
        // White noise in; compare wet energy per band against the same band of the input.
        // (A steady sine is useless here: it lands on a comb peak or notch by chance.)
        var x = Signals.Noise(4, Rate, 0.3, seed: 7);
        var wet = (float[])x.Clone();
        new RoomReverb(Rate).ProcessWetOnly(wet);
        double Band(double hz)
        {
            var a = (float[])x.Clone();
            var b = (float[])wet.Clone();
            Biquad.BandPass(Rate, hz, 2).Process(a);
            Biquad.BandPass(Rate, hz, 2).Process(b);
            return 20 * Math.Log10(Signals.Rms(b.AsSpan(Rate)) / Signals.Rms(a.AsSpan(Rate)));
        }
        var low = Band(60);
        var mid = Band(1000);
        var high = Band(12000);
        Assert.True(mid - low > 8, $"60 Hz wet {low:0.0} dB vs 1 kHz {mid:0.0} dB");
        Assert.True(mid - high > 10, $"12 kHz wet {high:0.0} dB vs 1 kHz {mid:0.0} dB");
    }

    [Fact]
    public void Chain_IsDeterministic()
    {
        var x = Signals.Noise(0.5, Rate, 0.2);
        var a = (float[])x.Clone();
        var b = (float[])x.Clone();
        new MasteringChain(Rate).Process(a);
        new MasteringChain(Rate).Process(b);
        Assert.Equal(a, b);
    }
}

public class LoudnessTests
{
    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    [InlineData(22050)]
    public void Bs1770_Reference_997HzAtMinus20Dbfs_IsMinus23Lufs(int rate)
    {
        // Mono convention (single channel, G = 1): equals EBU 3341's stereo −23 dBFS case.
        var x = Signals.Sine(997, 10, rate, Math.Pow(10, -20 / 20.0));
        Assert.InRange(LoudnessMeter.Measure(x, rate), -23.1, -22.9);
    }

    [Fact]
    public void KWeighting_At48k_MatchesThePublishedCoefficients()
    {
        var (shelf, hp) = LoudnessMeter.KWeighting(48000);
        // Published BS.1770 stage-1 response is ≈ +4 dB at high frequencies, ≈ 0 dB at 100 Hz.
        Assert.InRange(shelf.MagnitudeDb(10000, 48000), 3.9, 4.1);
        Assert.InRange(shelf.MagnitudeDb(100, 48000), -0.05, 0.05);
        Assert.InRange(hp.MagnitudeDb(38.135, 48000), -6.2, -5.8);   // Q 0.5: −6 dB at f0
    }

    [Fact]
    public void RelativeGate_IgnoresQuietStretches()
    {
        const int rate = 48000;
        var loud = Signals.Sine(997, 10, rate, 0.1);           // −23 LUFS
        var quiet = Signals.Sine(997, 10, rate, 0.001);        // −63 LUFS: below the relative gate
        var both = loud.Concat(quiet).ToArray();
        Assert.InRange(LoudnessMeter.Measure(both, rate), -23.1, -22.9);
        Assert.Equal(-200, LoudnessMeter.Measure(new float[rate * 2], rate));   // silence: nothing passes
    }

    [Fact]
    public void TruePeak_SeesInterSamplePeaks()
    {
        // fs/4 sine at 45° phase: every sample sits at 0.707 of the true peak (−3 dB).
        var x = Signals.Sine(12000, 0.2, 48000, 1.0, Math.PI / 4);
        var sample = x.Max(v => Math.Abs(v));
        Assert.InRange(20 * Math.Log10(sample), -3.1, -2.9);
        Assert.InRange(TruePeakMeter.MeasureDbtp(x), -0.5, 0.2);
    }
}

public class NarrationPadTests
{
    [Fact]
    public void ChordWeights_AreEqualPower_AndCycle()
    {
        for (var t = 0.0; t < 80; t += 0.37)
        {
            var power = Enumerable.Range(0, 4).Sum(k => Math.Pow(NarrationPad.ChordWeight(k, t), 2));
            Assert.InRange(power, 0.999, 1.001);
        }
        Assert.Equal(1.0, NarrationPad.ChordWeight(0, 5), 9);       // Fmaj7 alone mid-chord
        Assert.Equal(1.0, NarrationPad.ChordWeight(1, 15), 9);      // Cadd9
        Assert.Equal(1.0, NarrationPad.ChordWeight(3, 35), 9);      // Dm
        Assert.Equal(1.0, NarrationPad.ChordWeight(0, 45), 9);      // loops after 40 s
        Assert.InRange(NarrationPad.ChordWeight(0, 10), 0.70, 0.72); // crossfade midpoint
        Assert.Equal(0.0, NarrationPad.ChordWeight(0, 12.01), 9);   // 4 s crossfade ends
    }

    [Fact]
    public void StartingAnywhere_ReproducesTheSameTimeline()
    {
        // The overlapped pipeline renders windows separately: a pad started at 37.5 s must
        // continue exactly as one started at 0 would.
        const int rate = 48000;
        var whole = new float[rate * 40];
        new NarrationPad(rate, 0).Render(whole);
        var part = new float[rate * 2];
        new NarrationPad(rate, 37.5).Render(part);
        var offset = (int)(37.5 * rate);
        var maxDiff = 0.0;
        for (var i = 0; i < part.Length; i++) maxDiff = Math.Max(maxDiff, Math.Abs(part[i] - whole[offset + i]));
        Assert.True(maxDiff < 1e-4, $"seam mismatch {maxDiff}");
    }

    [Fact]
    public void Pad_IsLowPassed_At900Hz()
    {
        const int rate = 48000;
        var x = new float[rate * 10];
        new NarrationPad(rate, 0).Render(x);
        var hf = (float[])x.Clone();
        Biquad.HighPass(rate, 2000).Process(hf);
        var ratio = 20 * Math.Log10(Signals.Rms(hf) / Signals.Rms(x));
        Assert.True(ratio < -30, $"energy above 2 kHz {ratio:0.0} dB");
    }

    [Fact]
    public void GainFor_Puts_ThePad_28LU_UnderTheVoice()
    {
        const int rate = 48000;
        var gain = NarrationPad.GainFor(-18, rate);
        var x = new float[(int)(NarrationPad.CycleSeconds * rate)];
        new NarrationPad(rate, 0).Render(x);
        for (var i = 0; i < x.Length; i++) x[i] = (float)(x[i] * gain);
        Assert.InRange(LoudnessMeter.Measure(x, rate), -46.2, -45.8);
    }
}
