namespace StoryTime.Core.Engines.Narration.Dsp;

/// <summary>
/// One per-utterance voice stage: it reshapes the rendered voice itself (pitch, tempo, and
/// later F0 contour, breathiness, a vocoder…) before the utterance is laid on the story
/// timeline, so every downstream number — soft edges, level match, pause give-back, sentence
/// timestamps — is measured on the voice the child actually hears.
/// <para>Contract: input is one trimmed utterance, mono, at <see cref="Resampler.TargetRate"/>,
/// with silent faded edges. A stage returns a NEW array (any length), keeps the edges silent,
/// is deterministic, and is safe to call from any thread. Stages run in profile order.</para>
/// </summary>
public interface IVoiceStage
{
    /// <summary>Short id for reports (e.g. "pitch-time −1 st ×0.93").</summary>
    string Name { get; }

    float[] Process(float[] utterance, int sampleRate);
}

/// <summary>
/// Pitch and tempo, independently: a band-limited resample moves pitch (and formants) by
/// 2^(semitones/12) and stretches time by its inverse, then a WSOLA time-stretch brings the
/// duration to exactly 1/<see cref="Tempo"/> of the input. For the bedtime profiles (−1 st,
/// ×0.93) the WSOLA factor is only ≈1.015, so almost all of the work is the clean resample.
/// </summary>
public sealed record PitchTimeStage(double PitchSemitones, double Tempo) : IVoiceStage
{
    public string Name => $"pitch-time {PitchSemitones:+0.##;−0.##;0} st ×{Tempo:0.###}";

    /// <summary>Pitch multiplier (−1 st ≈ 0.9439).</summary>
    public double PitchFactor => Math.Pow(2, PitchSemitones / 12.0);

    public float[] Process(float[] utterance, int sampleRate)
    {
        if (!(Tempo > 0)) throw new ArgumentOutOfRangeException(nameof(Tempo));
        var targetLength = (int)Math.Round(utterance.Length / Tempo);
        if (PitchSemitones == 0)
            return Tempo == 1.0 ? (float[])utterance.Clone() : Wsola.Stretch(utterance, sampleRate, targetLength);
        var p = PitchFactor;
        var shifted = Resampler.ResampleByRatio(utterance, 1.0 / p);   // pitch ×p, duration ×1/p
        return Wsola.Stretch(shifted, sampleRate, targetLength);
    }
}

/// <summary>
/// Waveform-similarity overlap-add time-stretch (Verhelst &amp; Roelands 1993), pure C#.
/// 30 ms periodic-Hann frames at 50% hop (the windows sum to exactly 1, so no amplitude
/// ripple); each frame's source position may move ±8 ms from its ideal place to the offset
/// whose waveform best continues the previous frame (normalised cross-correlation: a coarse
/// pass on an 8× box-decimated copy, then a sample-exact refinement), so splices land
/// in phase and do not click. Duration is exact by construction and drift-free: every frame
/// is anchored to its ideal position, the search only nudges it.
/// </summary>
public static class Wsola
{
    public const double FrameSeconds = 0.030;
    public const double ToleranceSeconds = 0.008;
    private const int Decimation = 8;

    /// <summary>Stretch <paramref name="x"/> to exactly <paramref name="targetLength"/> samples
    /// without changing its pitch.</summary>
    public static float[] Stretch(ReadOnlySpan<float> x, int sampleRate, int targetLength)
    {
        if (targetLength <= 0 || x.Length == 0) return new float[Math.Max(0, targetLength)];
        var n = Math.Max(2 * Decimation, (int)Math.Round(FrameSeconds * sampleRate) / (2 * Decimation) * (2 * Decimation));
        var hop = n / 2;
        var tol = Math.Max(Decimation, (int)Math.Round(ToleranceSeconds * sampleRate));
        var stretch = targetLength / (double)x.Length;

        // Padded input: hop zeros in front (so frame 0's rising half covers silence and the
        // window sum is 1 from output sample 0), a frame plus the tolerance of zeros behind.
        var padded = new float[hop + x.Length + n + 2 * tol + Decimation];
        x.CopyTo(padded.AsSpan(hop));
        var dec = new float[padded.Length / Decimation];
        for (var j = 0; j < dec.Length; j++)
        {
            float acc = 0;
            for (var i = 0; i < Decimation; i++) acc += padded[j * Decimation + i];
            dec[j] = acc / Decimation;
        }

        var window = new float[n];
        for (var i = 0; i < n; i++) window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n));

        var y = new float[targetLength + n + hop];
        var frames = (targetLength + hop) / hop + 1;
        var maxStart = padded.Length - n;
        long previous = 0;
        for (var k = 0; k < frames; k++)
        {
            var ideal = (int)Math.Round(k * hop / stretch);
            var pos = Math.Clamp(ideal, 0, maxStart);
            if (k > 0)
            {
                var natural = (int)Math.Min(previous + hop, maxStart);
                pos = BestOffset(padded, dec, natural, pos, n, tol, maxStart);
            }
            var o = k * hop;
            for (var i = 0; i < n && o + i < y.Length; i++) y[o + i] += window[i] * padded[pos + i];
            previous = pos;
        }
        return y.AsSpan(hop, targetLength).ToArray();
    }

    /// <summary>Candidate start in [ideal − tol, ideal + tol] whose frame best matches the
    /// natural continuation at <paramref name="natural"/>. Ties (and silence) keep the ideal.</summary>
    private static int BestOffset(float[] x, float[] dec, int natural, int ideal, int n, int tol, int maxStart)
    {
        // Coarse: decimated domain, step = one decimated sample.
        var dn = n / Decimation;
        var dNat = natural / Decimation;
        var dIdeal = ideal / Decimation;
        var dTol = tol / Decimation;
        var best = ideal;
        var bestScore = double.NegativeInfinity;
        var templateEnergy = Energy(dec, dNat, dn);
        if (templateEnergy < 1e-12) return ideal;
        for (var d = 0; d <= 2 * dTol; d++)
        {
            var delta = d % 2 == 0 ? d / 2 : -(d + 1) / 2;          // 0, −1, +1, −2, +2 …
            var c = dIdeal + delta;
            if (c < 0 || c * Decimation > maxStart || c + dn > dec.Length) continue;
            var score = Ncc(dec, dNat, c, dn);
            if (score > bestScore) { bestScore = score; best = c * Decimation; }
        }

        // Fine: sample-exact around the coarse winner, within the tolerance.
        var lo = Math.Max(Math.Max(0, ideal - tol), best - Decimation);
        var hi = Math.Min(Math.Min(maxStart, ideal + tol), best + Decimation);
        var fine = Math.Clamp(best, lo, hi);
        var fineScore = double.NegativeInfinity;
        for (var c = lo; c <= hi; c++)
        {
            var score = Ncc(x, natural, c, n);
            if (score > fineScore) { fineScore = score; fine = c; }
        }
        return fine;
    }

    private static double Energy(float[] x, int start, int length)
    {
        double e = 0;
        for (var i = 0; i < length; i++) { var v = x[start + i]; e += v * v; }
        return e;
    }

    /// <summary>Cross-correlation normalised by the candidate's energy (the template's is
    /// constant across candidates).</summary>
    private static double Ncc(float[] x, int template, int candidate, int length)
    {
        double xy = 0, yy = 1e-9;
        for (var i = 0; i < length; i++)
        {
            var a = x[template + i];
            var b = x[candidate + i];
            xy += a * b;
            yy += b * b;
        }
        return xy / Math.Sqrt(yy);
    }
}
