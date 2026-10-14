namespace StoryTime.Core.Engines.Narration;

/// <summary>
/// Utterance edge shaping, evolved from the 2026-07-31 cozy chain's ShapeChunk to the
/// measured recipe (voice-lab.md): every voice adds 0–250 ms of its own leading and trailing
/// silence, which would make the pause map inexact, so each utterance is trimmed at −50 dBFS,
/// keeps 30 ms either side, and gets 8 ms fades so splices never click.
/// (The old −0.6 st pitch-shift resample and RMS StoryGain are retired: the measured chain
/// has no pitch shift, and loudness is now a BS.1770 meter — see Dsp/Loudness.cs.)
/// </summary>
public static class AudioShaper
{
    /// <summary>−50 dBFS: anything quieter at an utterance edge is the voice's own silence.</summary>
    public const double SilenceThresholdDbfs = -50.0;
    public static readonly float SilenceThreshold = (float)Math.Pow(10.0, SilenceThresholdDbfs / 20.0);
    /// <summary>Silence kept either side of the first/last audible sample.</summary>
    public const double KeepSeconds = 0.030;
    /// <summary>Linear fade at both new edges.</summary>
    public const double FadeSeconds = 0.008;
    /// <summary>An utterance quieter than this (RMS) is a failed render; try the next voice.</summary>
    public const double SanityRmsDbfs = -60.0;

    /// <summary>Edges more than this far below the utterance's loudest 10 ms are heard as
    /// silence too (a soft breathy lead-in at −48 dBFS before a −12 dBFS line), so the trim
    /// threshold is the higher of −50 dBFS and (active level − this).</summary>
    public const double RelativeTrimDb = 35.0;

    /// <summary>Trim edge silence to <see cref="KeepSeconds"/> and fade both edges. Returns an
    /// empty array for an all-silent input.</summary>
    public static float[] TrimEdges(ReadOnlySpan<float> samples, int sampleRate)
    {
        if (samples.Length == 0) return [];
        var threshold = Math.Max(SilenceThreshold, (float)DbToGain(ActiveLevelDbfs(samples, sampleRate) - RelativeTrimDb));
        var first = 0;
        while (first < samples.Length && Math.Abs(samples[first]) < threshold) first++;
        if (first == samples.Length) return [];
        var last = samples.Length - 1;
        while (last > first && Math.Abs(samples[last]) < threshold) last--;

        var keep = (int)Math.Round(KeepSeconds * sampleRate);
        var start = Math.Max(0, first - keep);
        var end = Math.Min(samples.Length - 1, last + keep);
        var shaped = samples[start..(end + 1)].ToArray();

        var fade = Math.Min((int)Math.Round(FadeSeconds * sampleRate), shaped.Length / 2);
        for (var i = 0; i < fade; i++)
        {
            var g = (i + 0.5f) / fade;
            shaped[i] *= g;
            shaped[^(i + 1)] *= g;
        }
        return shaped;
    }

    /// <summary>Loudness proxy for one utterance: the 90th-percentile 10 ms RMS (dBFS).</summary>
    public static double ActiveLevelDbfs(ReadOnlySpan<float> samples, int sampleRate)
    {
        var frame = Math.Max(1, sampleRate / 100);
        var count = samples.Length / frame;
        if (count == 0) return RmsDbfs(samples);
        var levels = new double[count];
        for (var f = 0; f < count; f++) levels[f] = RmsDbfs(samples.Slice(f * frame, frame));
        Array.Sort(levels);
        return levels[(int)(0.9 * (count - 1))];
    }

    /// <summary>How far below an utterance's active level a 10 ms frame still sounds like
    /// silence to a listener (perceptual edge of a pause).</summary>
    public const double PerceivedSilenceDb = 30.0;

    /// <summary>Soft (perceptually silent) lead-in and tail of a trimmed utterance, in samples:
    /// the kept edge silence plus any breathy onset/decay more than
    /// <see cref="PerceivedSilenceDb"/> under its active level. Pauses are shortened by these
    /// so the silence a listener hears matches the plan.</summary>
    public static (int Head, int Tail) SoftEdges(ReadOnlySpan<float> samples, int sampleRate)
    {
        var frame = Math.Max(1, sampleRate / 200);                       // 5 ms
        var count = samples.Length / frame;
        if (count == 0) return (0, 0);
        var threshold = ActiveLevelDbfs(samples, sampleRate) - PerceivedSilenceDb;
        var first = 0;
        while (first < count && RmsDbfs(samples.Slice(first * frame, frame)) < threshold) first++;
        var last = count - 1;
        while (last > first && RmsDbfs(samples.Slice(last * frame, frame)) < threshold) last--;
        return (first * frame, Math.Max(0, samples.Length - (last + 1) * frame));
    }

    /// <summary>RMS of a block in dBFS (−inf → −200).</summary>
    public static double RmsDbfs(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0) return -200;
        double sum = 0;
        foreach (var s in samples) sum += (double)s * s;
        var ms = sum / samples.Length;
        return ms <= 1e-20 ? -200 : 10.0 * Math.Log10(ms);
    }

    public static double DbToGain(double db) => Math.Pow(10.0, db / 20.0);
    public static double GainToDb(double gain) => gain <= 1e-10 ? -200 : 20.0 * Math.Log10(gain);
}
