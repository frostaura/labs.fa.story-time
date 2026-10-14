using System.Diagnostics;
using System.Text;
using StoryTime.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace StoryTime.Poster.Tests;

/// <summary>Render time and memory budgets at iPhone Air resolution (3× scale). The
/// player poster is 226 pt square → 678 px visible, plus a 40 pt (120 px) bleed per side;
/// a Home card is 220×200 pt rendered at 2× with half travel. Budgets are set for the Mac
/// with headroom for an A-series phone (roughly 1.5–2.5× slower single-threaded); the
/// measured numbers are written to /private/tmp/storytime-posters/perf.txt.</summary>
public class PerformanceTests(ITestOutputHelper output)
{
    private const int PlayerPx = 678;
    private const double PlayerBudgetMs = 250;     // median full layer set, Mac
    private const double ThumbBudgetMs = 120;      // 512 px flattened thumbnail, Mac
    private const long PlayerRawBudget = 16L << 20; // decoded RGBA, all plates
    private const long PlayerPngBudget = 3L << 20;  // encoded, what the cache holds

    [Fact]
    public void Player_poster_renders_within_budget_and_memory()
    {
        var specs = Enumerable.Range(0, 12).Select(i => GoldenTests.Cases[i].Spec).ToList();
        // Warm-up (JIT, Skia init) is not what a user feels on the second poster.
        using (PosterEngine.RenderLayers(specs[0], 200, 200)) { }

        var times = new List<double>();
        var raw = new List<long>();
        var png = new List<long>();
        var encodeMs = new List<double>();
        foreach (var spec in specs)
        {
            var sw = Stopwatch.StartNew();
            using var set = PosterEngine.RenderLayers(spec, PlayerPx, PlayerPx);
            times.Add(sw.Elapsed.TotalMilliseconds);
            raw.Add(set.Bytes);
            sw.Restart();
            long encoded = PosterEngine.EncodePng(set.Backdrop).Length + PosterEngine.EncodePng(set.Vignette).Length;
            foreach (var l in set.Layers) encoded += PosterEngine.EncodePng(l.Image).Length;
            encodeMs.Add(sw.Elapsed.TotalMilliseconds);
            png.Add(encoded);
        }
        var thumbs = new List<double>();
        foreach (var spec in specs)
        {
            var sw = Stopwatch.StartNew();
            using var img = PosterEngine.RenderFlattened(spec, 512, 512);
            thumbs.Add(sw.Elapsed.TotalMilliseconds);
        }
        var cardTimes = new List<double>();
        var cardRaw = new List<long>();
        foreach (var spec in specs)
        {
            var sw = Stopwatch.StartNew();
            using var set = PosterEngine.RenderLayers(spec, 440, 400, 0.5, bakeAmbient: true);
            cardTimes.Add(sw.Elapsed.TotalMilliseconds);
            cardRaw.Add(set.Bytes);
        }

        static double Median(List<double> v) => v.OrderBy(x => x).ElementAt(v.Count / 2);
        var report = new StringBuilder();
        report.AppendLine($"player layer set {PlayerPx}px + bleed: median {Median(times):0} ms, max {times.Max():0} ms");
        report.AppendLine($"   decoded RGBA (backdrop + 4 cropped layers + vignette): median {raw.OrderBy(x => x).ElementAt(6) / 1048576.0:0.0} MB, max {raw.Max() / 1048576.0:0.0} MB");
        report.AppendLine($"   PNG-encoded (cache): median {png.OrderBy(x => x).ElementAt(6) / 1048576.0:0.00} MB, max {png.Max() / 1048576.0:0.00} MB; encode median {Median(encodeMs):0} ms");
        report.AppendLine($"thumbnail 512px flattened: median {Median(thumbs):0} ms, max {thumbs.Max():0} ms");
        report.AppendLine($"Home card 440x400px (2x, half travel, baked): median {Median(cardTimes):0} ms, decoded max {cardRaw.Max() / 1048576.0:0.0} MB");
        output.WriteLine(report.ToString());
        File.WriteAllText(Path.Combine(TestKit.ReportDir, "perf.txt"), report.ToString());

        Assert.True(Median(times) <= PlayerBudgetMs, $"player render median {Median(times):0} ms > {PlayerBudgetMs} ms");
        Assert.True(Median(thumbs) <= ThumbBudgetMs, $"thumbnail median {Median(thumbs):0} ms > {ThumbBudgetMs} ms");
        Assert.True(raw.Max() <= PlayerRawBudget, $"player poster decodes to {raw.Max() / 1048576.0:0.0} MB");
        Assert.True(png.Max() <= PlayerPngBudget, $"player poster caches {png.Max() / 1048576.0:0.00} MB of PNG");
    }
}
