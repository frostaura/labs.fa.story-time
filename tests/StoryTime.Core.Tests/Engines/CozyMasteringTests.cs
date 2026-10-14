using StoryTime.Core.Engines.Narration;
using Xunit;

namespace StoryTime.Core.Tests.Engines;

/// <summary>
/// Cozy mastering chain (founder voice-character pass 2026-07-31): pitch-down resample
/// with duration compensation, shelf-EQ stability, RMS-targeted loudness under the
/// −6 dBFS ceiling, and byte-determinism of the whole chain.
/// </summary>
public class CozyMasteringTests : IDisposable
{
    public CozyMasteringTests() => AudioShaper.CozyMastering = true;
    public void Dispose() => AudioShaper.CozyMastering = true; // restore the product default

    private const int Rate = 24000;

    private static float[] Sine(double freqHz, double seconds, float amplitude = 0.5f)
    {
        var n = (int)(seconds * Rate);
        var x = new float[n];
        for (var i = 0; i < n; i++)
            x[i] = amplitude * (float)Math.Sin(2 * Math.PI * freqHz * i / Rate);
        return x;
    }

    private static double Rms(float[] x) => Math.Sqrt(x.Sum(s => (double)s * s) / x.Length);

    // ---- pitch shift / duration compensation ----

    [Fact]
    public void Resample_LengthMatchesStep()
    {
        var x = new float[Rate]; // 1 s
        var y = AudioShaper.Resample(x, AudioShaper.PitchRatio);
        Assert.Equal(x.Length / AudioShaper.PitchRatio, y.Length, 1.0); // ±1 sample
    }

    [Fact]
    public void MasterChunk_ShiftsPitchDown_ByTheConfiguredSemitones()
    {
        // 440 Hz in → expect ≈ 440 · 2^(−0.6/12) ≈ 425 Hz out. Count zero crossings.
        var mastered = AudioShaper.MasterChunk(Sine(440, 1.0), Rate);
        var crossings = 0;
        for (var i = 1; i < mastered.Length; i++)
            if (mastered[i - 1] < 0 != mastered[i] < 0) crossings++;
        var freq = crossings / 2.0 / (mastered.Length / (double)Rate);
        Assert.InRange(freq, 440 * AudioShaper.PitchRatio - 2, 440 * AudioShaper.PitchRatio + 2);
    }

    [Fact]
    public void RenderSpeed_ExactlyCompensatesTheResampleStretch()
    {
        // duration ∝ 1/speed; the resampler stretches by 1/PitchRatio → net = prosody speed.
        Assert.Equal(ProsodyDirector.BaseSpeed,
            AudioShaper.RenderSpeed(ProsodyDirector.BaseSpeed) * (float)AudioShaper.PitchRatio, 5);
        AudioShaper.CozyMastering = false;
        Assert.Equal(0.85f, AudioShaper.RenderSpeed(0.85f)); // identity when off
    }

    // ---- shelf filters ----

    [Fact]
    public void HighShelf_CutsHighs_LeavesMidsAlone()
    {
        var high = Sine(8000, 0.5);
        var mid = Sine(500, 0.5);
        var highDb = FilterGainDb(Biquad.HighShelf(Rate, AudioShaper.HighShelfHz, AudioShaper.HighShelfDb), high);
        var midDb = FilterGainDb(Biquad.HighShelf(Rate, AudioShaper.HighShelfHz, AudioShaper.HighShelfDb), mid);
        Assert.InRange(highDb, AudioShaper.HighShelfDb - 1.0, AudioShaper.HighShelfDb + 1.0);
        Assert.InRange(midDb, -0.75, 0.25);
    }

    [Fact]
    public void LowShelf_WarmsLows_LeavesMidsAlone()
    {
        var low = Sine(100, 0.5);
        var mid = Sine(1500, 0.5);
        var lowDb = FilterGainDb(Biquad.LowShelf(Rate, AudioShaper.LowShelfHz, AudioShaper.LowShelfDb), low);
        var midDb = FilterGainDb(Biquad.LowShelf(Rate, AudioShaper.LowShelfHz, AudioShaper.LowShelfDb), mid);
        Assert.InRange(lowDb, AudioShaper.LowShelfDb - 1.0, AudioShaper.LowShelfDb + 1.0);
        Assert.InRange(midDb, -0.25, 0.25);
    }

    private static double FilterGainDb(Biquad filter, float[] signal)
    {
        var copy = (float[])signal.Clone();
        filter.Process(copy);
        // Skip the first 1000 samples (filter settle) for the steady-state measurement.
        return 20 * Math.Log10(Rms(copy.Skip(1000).ToArray()) / Rms(signal.Skip(1000).ToArray()));
    }

    [Fact]
    public void Shelves_AreStable_ImpulseDecays_NoNanNoDc()
    {
        foreach (var filter in new[]
        {
            Biquad.HighShelf(Rate, AudioShaper.HighShelfHz, AudioShaper.HighShelfDb),
            Biquad.LowShelf(Rate, AudioShaper.LowShelfHz, AudioShaper.LowShelfDb),
        })
        {
            var impulse = new float[Rate];
            impulse[0] = 1f;
            filter.Process(impulse);
            Assert.All(impulse, s => Assert.True(float.IsFinite(s)));
            // Tail decays to silence (stability) …
            Assert.True(impulse.Skip(Rate / 2).All(s => Math.Abs(s) < 1e-6f), "impulse response must decay");
            // … and the shelf adds no DC offset worth speaking of.
            Assert.True(Math.Abs(impulse.Sum(s => (double)s) / Rate) < 1e-4, "no DC");
        }
    }

    [Fact]
    public void MasteringThenStoryGain_NeverClips_EvenOnHotInput()
    {
        // The low shelf may push a hot float chunk past 1.0 in the float domain; the
        // no-clip guarantee is end-to-end: StoryGain's −6 dBFS ceiling runs before PCM.
        var mastered = AudioShaper.MasterChunk(Sine(300, 0.5, 0.95f), Rate);
        var gain = AudioShaper.StoryGain([mastered]);
        Assert.True(mastered.Max(Math.Abs) * gain <= AudioShaper.CozyTargetPeak + 1e-4f,
            "post-gain peak must stay under the −6 dBFS ceiling");
    }

    // ---- loudness ----

    [Fact]
    public void StoryGain_TargetsRms_WhenHeadroomAllows()
    {
        // Quiet signal (RMS −40 dBFS, peak well below ceiling·gain) → RMS target decides.
        var quiet = Sine(300, 0.5, 0.014f); // sine RMS = A/√2 ≈ −40 dBFS
        var gain = AudioShaper.StoryGain([quiet, null]);
        var outRmsDb = 20 * Math.Log10(Rms(quiet) * gain);
        // MaxGain (3×) caps the boost long before −26; the cap must hold exactly.
        Assert.Equal(AudioShaper.MaxGain, gain, 3);
        Assert.True(outRmsDb <= AudioShaper.CozyTargetRmsDb + 0.1);
    }

    [Fact]
    public void StoryGain_PeakCeiling_AlwaysWins()
    {
        // Hot, peaky signal: RMS target would ask for more, the −6 dBFS ceiling wins.
        var hot = Sine(300, 0.5, 0.9f);
        var gain = AudioShaper.StoryGain([hot]);
        Assert.True(0.9f * gain <= AudioShaper.CozyTargetPeak + 1e-4f, "peak must stay under −6 dBFS");
    }

    [Fact]
    public void StoryGain_SpeechlikeCrest_LandsNearTheRmsTarget()
    {
        // Crest factor ~12 dB (speech-like): RMS −22 dB, peak 0.5 → RMS target −26 reachable.
        var voiced = Sine(300, 0.5, 0.112f); // RMS ≈ −22 dBFS
        voiced[100] = 0.5f;                  // single 0.5 peak
        var gain = AudioShaper.StoryGain([voiced]);
        var outRmsDb = 20 * Math.Log10(Rms(voiced) * gain);
        Assert.InRange(outRmsDb, AudioShaper.CozyTargetRmsDb - 0.5, AudioShaper.CozyTargetRmsDb + 0.5);
        Assert.True(0.5f * gain <= AudioShaper.CozyTargetPeak + 1e-4f);
    }

    [Fact]
    public void StoryGain_LegacyPath_WhenMasteringOff()
    {
        AudioShaper.CozyMastering = false;
        var x = Sine(300, 0.2, 0.4f);
        Assert.Equal(AudioShaper.NormalizationGain(0.4f), AudioShaper.StoryGain([x]), 3);
    }

    // ---- determinism / bypass ----

    [Fact]
    public void MasterChunk_IsDeterministic_SampleForSample()
    {
        var x = Sine(313, 0.7, 0.4f);
        var a = AudioShaper.MasterChunk((float[])x.Clone(), Rate);
        var b = AudioShaper.MasterChunk((float[])x.Clone(), Rate);
        Assert.Equal(a.Length, b.Length);
        for (var i = 0; i < a.Length; i++) Assert.Equal(a[i], b[i]);
    }

    [Fact]
    public void MasterChunk_PassesThrough_WhenOff()
    {
        AudioShaper.CozyMastering = false;
        var x = Sine(313, 0.1);
        Assert.Same(x, AudioShaper.MasterChunk(x, Rate));
    }

    [Fact]
    public void MasterChunk_EmptyInput_StaysEmpty()
        => Assert.Empty(AudioShaper.MasterChunk([], Rate));
}
