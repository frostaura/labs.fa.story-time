using System.Globalization;
using System.Text.Json;
using StoryTime.Core.Engines.Narration;
using StoryTime.Core.Engines.Narration.Dsp;

namespace StoryTime.NarrationLab;

/// <summary>
/// Measures what a profile's voice stages do to audio: on steady sines (pitch error in
/// cents, duration error in samples, sinusoid-fit residual, amplitude ripple, worst
/// second-difference spike relative to an ideal sine) and, given a pcm dir from
/// render-utterances.swift, on real utterances (median F0 shift vs the target, duration ratio).
/// </summary>
internal static class ShiftCheck
{
    private const int Rate = Resampler.TargetRate;

    public static void Run(MasteringProfile profile, string? pcmDir)
    {
        Console.WriteLine($"profile {profile.Name}: {string.Join(" → ", profile.VoiceStages.Select(s => s.Name))}");
        var stages = profile.VoiceStages;
        if (stages.Count == 0) { Console.WriteLine("no voice stages"); return; }
        double pitch = 1, tempo = 1;
        foreach (var s in stages.OfType<PitchTimeStage>()) { pitch *= s.PitchFactor; tempo *= s.Tempo; }

        Console.WriteLine($"{"sine Hz",8}{"cents",9}{"len err",9}{"resid dB",10}{"ripple dB",11}{"click ×",9}{"ms/s",8}");
        foreach (var hz in new[] { 110.0, 137.0, 220.0, 440.0, 1000.0 })
        {
            const double a = 0.5;
            var x = Sine(hz, 4.0, a);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var y = Apply(stages, x);
            sw.Stop();
            var outHz = hz * pitch;
            int from = Rate / 4, to = y.Length - Rate / 4;
            var cents = 1200 * Math.Log2(Frequency(y, from, to) / outHz);
            var lenErr = y.Length - (int)Math.Round(x.Length / tempo);
            var amps = new List<double>();
            double sig = 0, err = 0, worst = 0;
            var block = Rate / 100;
            for (var b = from; b + block <= to; b += block)
            {
                var (s, c) = Fit(y, outHz, b, block);
                amps.Add(20 * Math.Log10(Math.Sqrt(s * s + c * c)));
                for (var i = 0; i < block; i++)
                {
                    var w = 2 * Math.PI * outHz * (b + i) / Rate;
                    var f = s * Math.Sin(w) + c * Math.Cos(w);
                    sig += f * f; err += (y[b + i] - f) * (y[b + i] - f);
                }
            }
            var om = 2 * Math.PI * outHz / Rate;
            for (var i = from + 1; i < to - 1; i++) worst = Math.Max(worst, Math.Abs(y[i + 1] - 2 * y[i] + y[i - 1]));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{hz,8:0}{cents,9:0.000}{lenErr,9}{10 * Math.Log10(err / sig),10:0.0}{amps.Max() - amps.Min(),11:0.0000}{worst / (a * om * om),9:0.00}{sw.Elapsed.TotalMilliseconds / 4,8:0.0}"));
        }

        if (pcmDir is null) return;
        var cent = new List<double>();
        var ratios = new List<double>();
        double inSeconds = 0, cpuMs = 0;
        foreach (var json in Directory.EnumerateFiles(pcmDir, "*.json").Order())
        {
            using var meta = JsonDocument.Parse(File.ReadAllText(json));
            var rate = meta.RootElement.GetProperty("rate").GetInt32();
            var bytes = File.ReadAllBytes(Path.ChangeExtension(json, ".f32"));
            var raw = new float[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, raw, 0, raw.Length * 4);
            var trimmed = AudioShaper.TrimEdges(raw, rate);
            if (trimmed.Length < rate / 4) continue;
            var x = Resampler.Resample(trimmed, rate, Rate);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var y = Apply(stages, x);
            cpuMs += sw.Elapsed.TotalMilliseconds;
            inSeconds += x.Length / (double)Rate;
            ratios.Add(y.Length / (double)x.Length);
            double? f0In = MedianF0(x), f0Out = MedianF0(y);
            if (f0In is { } fi && f0Out is { } fo) cent.Add(1200 * Math.Log2(fo / fi / pitch));
        }
        cent.Sort();
        Console.WriteLine($"real utterances: {ratios.Count}, {inSeconds:0.0} s in; duration ×{ratios.Average():0.0000} (target ×{1 / tempo:0.0000}); " +
            $"median-F0 error vs target {cent[cent.Count / 2]:+0.0;-0.0} cents (IQR {cent[cent.Count / 4]:+0.0;-0.0} … {cent[3 * cent.Count / 4]:+0.0;-0.0}); " +
            $"stage CPU {cpuMs / inSeconds:0.0} ms per audio second");
    }

    private static float[] Apply(IReadOnlyList<IVoiceStage> stages, float[] x)
    {
        foreach (var s in stages) x = s.Process(x, Rate);
        return x;
    }

    private static float[] Sine(double hz, double seconds, double a)
    {
        var x = new float[(int)(seconds * Rate)];
        for (var i = 0; i < x.Length; i++) x[i] = (float)(a * Math.Sin(2 * Math.PI * hz * i / Rate));
        return x;
    }

    private static double Frequency(float[] y, int from, int to)
    {
        double first = -1, last = -1;
        var count = 0;
        for (var i = from; i < to; i++)
            if (y[i - 1] < 0 && y[i] >= 0)
            {
                var t = i - 1 + y[i - 1] / (double)(y[i - 1] - y[i]);
                if (first < 0) first = t; else count++;
                last = t;
            }
        return count * Rate / (last - first);
    }

    private static (double Sin, double Cos) Fit(float[] y, double hz, int b, int block)
    {
        double ss = 0, cc = 0, sc = 0, ys = 0, yc = 0;
        for (var i = 0; i < block; i++)
        {
            var w = 2 * Math.PI * hz * (b + i) / Rate;
            double s = Math.Sin(w), c = Math.Cos(w);
            ss += s * s; cc += c * c; sc += s * c; ys += y[b + i] * s; yc += y[b + i] * c;
        }
        var det = ss * cc - sc * sc;
        return ((ys * cc - yc * sc) / det, (yc * ss - ys * sc) / det);
    }

    /// <summary>Median F0 over voiced 40 ms frames (normalised autocorrelation peak ≥ 0.8 in
    /// 70–400 Hz, parabolic-interpolated lag), or null when too little is voiced.</summary>
    private static double? MedianF0(float[] x)
    {
        const int frame = Rate / 25, minLag = Rate / 400, maxLag = Rate / 70;
        var f0 = new List<double>();
        for (var s = 0; s + frame + maxLag < x.Length; s += frame / 2)
        {
            double e0 = 0;
            for (var i = 0; i < frame; i++) e0 += x[s + i] * x[s + i];
            if (e0 / frame < 1e-5) continue;
            var r = new double[maxLag + 2];
            for (var lag = minLag - 1; lag <= maxLag + 1; lag++)
            {
                double xy = 0, yy = 0;
                for (var i = 0; i < frame; i++) { xy += x[s + i] * x[s + i + lag]; yy += x[s + i + lag] * x[s + i + lag]; }
                r[lag] = xy / Math.Sqrt(e0 * yy + 1e-12);
            }
            var best = minLag;
            for (var lag = minLag; lag <= maxLag; lag++) if (r[lag] > r[best]) best = lag;
            if (r[best] < 0.8) continue;
            var d = (r[best - 1] - r[best + 1]) / (2 * (r[best - 1] - 2 * r[best] + r[best + 1]));
            f0.Add(Rate / (best + d));
        }
        if (f0.Count < 5) return null;
        f0.Sort();
        return f0[f0.Count / 2];
    }
}
