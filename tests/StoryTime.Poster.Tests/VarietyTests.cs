using System.Text;
using SkiaSharp;
using StoryTime.Contracts;
using StoryTime.Core.Engines;
using Xunit;
using Xunit.Abstractions;

namespace StoryTime.Poster.Tests;

/// <summary>"100 consecutive stories rarely look alike", measured. Each poster gets a 64-bit
/// perceptual hash (32×32 luminance → 2-D DCT → the 8×8 lowest frequencies against their
/// median — the classic pHash) plus a mean-colour signature in OKLab; two posters "look
/// alike" when their hashes are within 10 bits AND their average colours are close.</summary>
public class VarietyTests(ITestOutputHelper output)
{
    private const int NearDuplicateBits = 10;
    private const double NearColour = 0.03;

    private sealed record Signature(ulong Hash, double L, double A, double B);

    private static Signature Sign(PosterSpec spec)
    {
        using var img = PosterEngine.RenderFlattened(spec, 64, 64);
        var px = TestKit.Pixels(img);
        var gray = new double[32, 32];
        double sl = 0, sa = 0, sb = 0;
        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 64; x++)
        {
            var i = (y * 64 + x) * 4;
            gray[y / 2, x / 2] += TestKit.Luminance(px, i) / 4;
            var lab = Color.Oklab.FromSk(new SKColor(px[i], px[i + 1], px[i + 2]));
            sl += lab.L; sa += lab.A; sb += lab.B;
        }
        var n = 64.0 * 64;
        var coeffs = new double[64];
        for (var u = 0; u < 8; u++)
        for (var v = 0; v < 8; v++)
        {
            double sum = 0;
            for (var y = 0; y < 32; y++)
            for (var x = 0; x < 32; x++)
                sum += gray[y, x] * Math.Cos((2 * x + 1) * u * Math.PI / 64) * Math.Cos((2 * y + 1) * v * Math.PI / 64);
            coeffs[v * 8 + u] = sum;
        }
        var median = coeffs.Skip(1).OrderBy(c => c).ElementAt(31);
        ulong hash = 0;
        for (var k = 0; k < 64; k++) if (coeffs[k] > median) hash |= 1UL << k;
        return new Signature(hash, sl / n, sa / n, sb / n);
    }

    private static int Bits(ulong a, ulong b) => System.Numerics.BitOperations.PopCount(a ^ b);

    private static bool Alike(Signature a, Signature b) =>
        Bits(a.Hash, b.Hash) <= NearDuplicateBits
        && Math.Sqrt((a.L - b.L) * (a.L - b.L) + (a.A - b.A) * (a.A - b.A) + (a.B - b.B) * (a.B - b.B)) < NearColour;

    private (double Mean, int P5, int Min, double AlikeFraction, int[] Histogram) Measure(IReadOnlyList<Signature> sigs)
    {
        var distances = new List<int>();
        var alike = 0;
        var pairs = 0;
        for (var i = 0; i < sigs.Count; i++)
        for (var j = i + 1; j < sigs.Count; j++)
        {
            distances.Add(Bits(sigs[i].Hash, sigs[j].Hash));
            if (Alike(sigs[i], sigs[j])) alike++;
            pairs++;
        }
        distances.Sort();
        var hist = new int[9];
        foreach (var d in distances) hist[Math.Min(8, d / 4)]++;
        return (distances.Average(), distances[(int)(distances.Count * 0.05)], distances[0], (double)alike / pairs, hist);
    }

    [Fact]
    public async Task One_child_hundred_nights_and_three_hundred_random_seeds()
    {
        // (a) The real case: one child, fixed interests, 100 consecutive template stories.
        var profile = new ChildProfile { Name = "Mila", Age = 5, Interests = ["Foxes", "Ocean", "Magic"] };
        var nights = new List<Signature>();
        var scenes = new Dictionary<PosterScene, int>();
        for (var night = 0; night < 100; night++)
        {
            var engine = new TemplateStoryEngine(() => 1009 * (night + 1));
            var outline = await engine.GenerateOutlineAsync(profile, null, 5, CancellationToken.None);
            var spec = await engine.GeneratePosterSpecAsync(outline, CancellationToken.None);
            scenes[spec.Scene] = scenes.GetValueOrDefault(spec.Scene) + 1;
            nights.Add(Sign(spec));
        }

        // (b) 300 random valid specs.
        var random = Enumerable.Range(0, 300).Select(i => Sign(TestKit.RandomSpec(i))).ToList();

        var a = Measure(nights);
        var b = Measure(random);
        var report = new StringBuilder();
        report.AppendLine($"pHash (64-bit) pairwise Hamming distance; 'alike' = <= {NearDuplicateBits} bits AND mean OKLab colour within {NearColour}");
        report.AppendLine($"one child, 100 consecutive nights: mean {a.Mean:0.0} bits, p5 {a.P5}, min {a.Min}, alike pairs {a.AlikeFraction:P2}");
        report.AppendLine($"   scenes used: {string.Join(", ", scenes.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}"))}");
        report.AppendLine($"   histogram (4-bit buckets): {string.Join(" ", a.Histogram)}");
        report.AppendLine($"300 random seeds/specs: mean {b.Mean:0.0} bits, p5 {b.P5}, min {b.Min}, alike pairs {b.AlikeFraction:P2}");
        report.AppendLine($"   histogram (4-bit buckets): {string.Join(" ", b.Histogram)}");
        output.WriteLine(report.ToString());
        File.WriteAllText(Path.Combine(TestKit.ReportDir, "variety.txt"), report.ToString());

        Assert.True(scenes.Count >= 4, "one child's hundred nights should visit several scenes");
        Assert.True(a.AlikeFraction < 0.02, $"{a.AlikeFraction:P2} of night pairs look alike");
        Assert.True(b.AlikeFraction < 0.01, $"{b.AlikeFraction:P2} of random pairs look alike");
        Assert.True(a.Mean >= 18 && b.Mean >= 20, "posters cluster too tightly");
    }
}
