using StoryTime.Core.Engines.Narration.Dsp;
using Xunit;

namespace StoryTime.Core.Tests.Engines;

/// <summary>Artifact measurements for the pitch/time voice stage, on signals whose ideal
/// output is known exactly.</summary>
internal static class ShiftProbe
{
    /// <summary>Frequency from interpolated rising zero crossings over [from, to).</summary>
    public static double Frequency(float[] y, int rate, int from, int to)
    {
        double first = -1, last = -1;
        var count = 0;
        for (var i = Math.Max(1, from); i < to; i++)
        {
            if (y[i - 1] < 0 && y[i] >= 0)
            {
                var t = i - 1 + y[i - 1] / (double)(y[i - 1] - y[i]);
                if (first < 0) first = t; else count++;
                last = t;
            }
        }
        return count * rate / (last - first);
    }

    /// <summary>Least-squares sinusoid amplitude at <paramref name="hz"/> per block (exact for
    /// any block length, unlike a block RMS over a fractional number of periods).</summary>
    public static IEnumerable<double> Amplitudes(float[] y, int rate, double hz, int from, int to, int block)
    {
        for (var b = from; b + block <= to; b += block)
        {
            var (a, k) = Fit(y, rate, hz, b, block);
            yield return Math.Sqrt(a * a + k * k);
        }
    }

    private static (double Sin, double Cos) Fit(float[] y, int rate, double hz, int b, int block)
    {
        double ss = 0, cc = 0, sc = 0, ys = 0, yc = 0;
        for (var i = 0; i < block; i++)
        {
            var w = 2 * Math.PI * hz * (b + i) / rate;
            double s = Math.Sin(w), c = Math.Cos(w);
            ss += s * s; cc += c * c; sc += s * c; ys += y[b + i] * s; yc += y[b + i] * c;
        }
        var det = ss * cc - sc * sc;
        return ((ys * cc - yc * sc) / det, (yc * ss - ys * sc) / det);
    }

    /// <summary>Residual (dB below the signal) after a least-squares sinusoid fit at
    /// <paramref name="hz"/> in each 20 ms block — splice glitches, amplitude ripple and
    /// distortion all land in the residual; a slow phase drift does not.</summary>
    public static double ResidualDb(float[] y, int rate, double hz, int from, int to)
    {
        var block = rate / 50;
        double sig = 0, err = 0;
        for (var b = from; b + block <= to; b += block)
        {
            var (a, k) = Fit(y, rate, hz, b, block);
            for (var i = 0; i < block; i++)
            {
                var w = 2 * Math.PI * hz * (b + i) / rate;
                var fit = a * Math.Sin(w) + k * Math.Cos(w);
                sig += fit * fit;
                err += (y[b + i] - fit) * (y[b + i] - fit);
            }
        }
        return 10 * Math.Log10(err / sig);
    }

    /// <summary>Worst sample-to-sample second difference relative to an ideal sine's
    /// (A·ω²): a splice discontinuity shows up as a spike far above 1.</summary>
    public static double ClickRatio(float[] y, int rate, double hz, double amplitude, int from, int to)
    {
        var w = 2 * Math.PI * hz / rate;
        var ideal = amplitude * w * w;
        double worst = 0;
        for (var i = from + 1; i < to - 1; i++)
            worst = Math.Max(worst, Math.Abs(y[i + 1] - 2 * y[i] + y[i - 1]));
        return worst / ideal;
    }
}

public class PitchTimeStageTests
{
    private const int Rate = 48000;

    [Theory]
    [InlineData(-1.0, 0.93)]    // E and G
    [InlineData(-1.5, 0.92)]    // F
    [InlineData(0.0, 0.93)]     // tempo only
    [InlineData(-1.0, 1.0)]     // pitch only
    public void Sine_LandsOnThePitch_WithinAFewCents(double semitones, double tempo)
    {
        const double hz = 220;
        var x = Signals.Sine(hz, 2.0, Rate, 0.5);
        var y = new PitchTimeStage(semitones, tempo).Process(x, Rate);
        var measured = ShiftProbe.Frequency(y, Rate, Rate / 4, y.Length - Rate / 4);
        var expected = hz * Math.Pow(2, semitones / 12);
        var cents = 1200 * Math.Log2(measured / expected);
        Assert.True(Math.Abs(cents) < 2, $"{measured:0.000} Hz vs {expected:0.000} Hz = {cents:0.00} cents");
    }

    [Theory]
    [InlineData(-1.0, 0.93)]
    [InlineData(-1.5, 0.92)]
    [InlineData(0.0, 0.93)]
    [InlineData(-1.0, 1.0)]
    public void Duration_IsExactlyOneOverTempo(double semitones, double tempo)
    {
        foreach (var seconds in new[] { 0.05, 0.7, 3.3 })
        {
            var x = Signals.Sine(180, seconds, Rate, 0.3);
            var y = new PitchTimeStage(semitones, tempo).Process(x, Rate);
            Assert.Equal((int)Math.Round(x.Length / tempo), y.Length);
        }
    }

    [Theory]
    [InlineData(-1.0, 0.93, 220)]
    [InlineData(-1.5, 0.92, 220)]
    [InlineData(-1.0, 0.93, 137)]   // a period that never divides the frame
    public void Sine_HasNoClicks_NoRipple_AndAClearResidual(double semitones, double tempo, double hz)
    {
        const double amplitude = 0.5;
        var x = Signals.Sine(hz, 3.0, Rate, amplitude);
        var y = new PitchTimeStage(semitones, tempo).Process(x, Rate);
        var outHz = hz * Math.Pow(2, semitones / 12);
        int from = Rate / 4, to = y.Length - Rate / 4;

        var clicks = ShiftProbe.ClickRatio(y, Rate, outHz, amplitude, from, to);
        Assert.True(clicks < 1.5, $"worst second difference {clicks:0.00}× an ideal sine's");

        var residual = ShiftProbe.ResidualDb(y, Rate, outHz, from, to);
        Assert.True(residual < -50, $"residual {residual:0.0} dB");

        // Window sum is exactly 1 and splices are in phase: the fitted amplitude per 10 ms
        // block never ripples by more than 0.1 dB.
        var levels = ShiftProbe.Amplitudes(y, Rate, outHz, from, to, Rate / 100).Select(a => 20 * Math.Log10(a)).ToList();
        Assert.True(levels.Max() - levels.Min() < 0.1, $"ripple {levels.Max() - levels.Min():0.000} dB");
        Assert.InRange(levels.Average(), 20 * Math.Log10(amplitude) - 0.1, 20 * Math.Log10(amplitude) + 0.1);
    }

    [Fact]
    public void SilentEdges_StaySilent_AndTheOnsetStaysInPlace()
    {
        // A trimmed utterance: 30 ms silence, a 0.5 s tone, 30 ms silence.
        var pad = (int)(0.030 * Rate);
        var tone = Signals.Sine(200, 0.5, Rate, 0.4);
        var x = new float[pad + tone.Length + pad];
        tone.CopyTo(x, pad);
        var y = new PitchTimeStage(-1.0, 0.93).Process(x, Rate);
        var headEnd = (int)(pad / 0.93) - (int)(0.012 * Rate);   // allow the resampler's/frame's smear
        Assert.True(y.Take(headEnd).All(v => Math.Abs(v) < 1e-3), "leading silence leaks");
        Assert.True(y.Skip(y.Length - headEnd).All(v => Math.Abs(v) < 1e-3), "trailing silence leaks");
    }

    [Fact]
    public void IsDeterministic()
    {
        var x = Signals.Noise(0.6, Rate, 0.2);
        var stage = new PitchTimeStage(-1.0, 0.93);
        Assert.Equal(stage.Process(x, Rate), stage.Process(x, Rate));
    }

    [Fact]
    public void RationalApproximation_IsSubCent()
    {
        var r = 1 / Math.Pow(2, -1.0 / 12);
        var (up, down) = Resampler.Rational(r, 4096);
        Assert.True(up <= 4096);
        Assert.True(Math.Abs(1200 * Math.Log2(up / (double)down / r)) < 0.01);
    }
}

public class BedtimeProfileTests
{
    private const int Rate = 48000;

    [Fact]
    public void Default_IsG_AndEFGAreSelectableByLetter()
    {
        Assert.Same(MasteringProfile.WarmMomG, MasteringProfile.Default);
        Assert.Same(MasteringProfile.WarmMomE, MasteringProfile.Named("E"));
        Assert.Same(MasteringProfile.WarmMomF, MasteringProfile.Named("f"));
        Assert.Same(MasteringProfile.WarmMomG, MasteringProfile.Named("G-warm-dark"));
        Assert.Same(MasteringProfile.VoiceLab, MasteringProfile.Named("voice-lab"));
        Assert.Throws<ArgumentException>(() => MasteringProfile.Named("Z"));
    }

    [Fact]
    public void Profiles_CarryTheAuditionedVoiceStages()
    {
        Assert.Equal(new PitchTimeStage(-1.0, 0.93), Assert.Single(MasteringProfile.WarmMomE.VoiceStages));
        Assert.Equal(new PitchTimeStage(-1.5, 0.92), Assert.Single(MasteringProfile.WarmMomF.VoiceStages));
        Assert.Equal(new PitchTimeStage(-1.0, 0.93), Assert.Single(MasteringProfile.WarmMomG.VoiceStages));
        Assert.Empty(MasteringProfile.VoiceLab.VoiceStages);
        foreach (var p in new[] { MasteringProfile.WarmMomE, MasteringProfile.WarmMomF, MasteringProfile.WarmMomG })
        {
            Assert.Equal(-19.0, p.TargetLufs);
            Assert.Equal(-3.0, p.PeakCeilingDbtp);
            Assert.Equal(28.0, p.PadBelowVoiceLu);
            Assert.Equal(new CompressorSettings(-28, 2.5, 10, 220), p.Compressor);
        }
        Assert.Equal(-20.0, MasteringProfile.WarmMomE.Reverb!.WetDb);
        Assert.Equal(-19.0, MasteringProfile.WarmMomG.Reverb!.WetDb);
    }

    private static double Probe(MasteringProfile profile, double hz) =>
        Signals.GainDb(x => new MasteringChain(Rate, profile, reverb: false, compressor: false).Process(x), hz, Rate);

    [Fact]
    public void E_EqCurve_IsWarmAndSoftened()
    {
        var e = MasteringProfile.WarmMomE;
        Assert.True(Probe(e, 30) < -8, "rumble cut");
        Assert.InRange(Probe(e, 200), 3.8, 5.5);      // +4 @200 with the 380 bell's skirt
        Assert.InRange(Probe(e, 1000), -1.2, 0.6);
        Assert.InRange(Probe(e, 2500), -4.2, -2.8);   // −3 presence dip (+ shelf skirt)
        Assert.InRange(Probe(e, 9000), -10, -6);      // shelf + LP 9k (−3 at corner)
        Assert.True(Probe(e, 14000) < -12, "air rolled off");
    }

    [Fact]
    public void G_IsDarkerThanE_AboveSevenKilohertz_AndIdenticalBelow()
    {
        // LP 7.5k vs 9k (Butterworth): −1.5 dB at 7.5k, −2.8 dB at 10k, more above.
        Assert.InRange(Probe(MasteringProfile.WarmMomG, 7500) - Probe(MasteringProfile.WarmMomE, 7500), -1.8, -1.2);
        Assert.InRange(Probe(MasteringProfile.WarmMomG, 10000) - Probe(MasteringProfile.WarmMomE, 10000), -3.2, -2.4);
        Assert.Equal(Probe(MasteringProfile.WarmMomE, 300), Probe(MasteringProfile.WarmMomG, 300), 1);
    }
}
