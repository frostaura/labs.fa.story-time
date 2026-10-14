using System.Text;
using SkiaSharp;
using StoryTime.Contracts;
using StoryTime.Poster.Color;
using StoryTime.Poster.Composition;
using StoryTime.Poster.Spec;
using Xunit;
using Xunit.Abstractions;

namespace StoryTime.Poster.Tests;

/// <summary>The picture-book bar, measured. Each test writes its numbers to the report
/// directory before it asserts, so a failing run still leaves the evidence behind.
///
///   * Hero: the focal subject is found by rendering the poster with and without it
///     (a visibility mask — occlusion by the foreground counts against it), and must stand
///     20–38% of the poster's height.
///   * Warm light: every poster carries a warm, bright focal glow.
///   * Foreground framing: the near layer rises into the lower corners.
///   * Palette range: hue entropy and mean OKLab distance across 300 posters.
///   * Every listed feature is visible at card size.</summary>
public class ArtDirectionTests(ITestOutputHelper output)
{
    // ------------------------------------------------------------------ helpers

    internal static SKImage RenderPlan(PosterPlan plan, int w, int h)
    {
        using var set = PosterEngine.RenderLayers(plan, w, h, travelScale: 0, bakeAmbient: true);
        return PosterEngine.Composite(set);
    }

    internal static PosterPlan With(PosterPlan p, IReadOnlyList<Placement>? placements = null,
        IReadOnlyList<AmbientParticle>? ambient = null, bool dropMilkyWay = false, bool dropAurora = false,
        bool dropClouds = false, bool dropShootingStar = false, IReadOnlyList<LightPlan>? lights = null) => new()
    {
        Spec = p.Spec,
        Colors = p.Colors,
        Horizon = p.Horizon,
        Side = p.Side,
        Layout = p.Layout,
        Moon = p.Moon,
        Stars = p.Stars,
        MilkyWay = dropMilkyWay ? null : p.MilkyWay,
        Clouds = dropClouds ? [] : p.Clouds,
        Aurora = dropAurora ? null : p.Aurora,
        ShootingStar = dropShootingStar ? null : p.ShootingStar,
        Bands = p.Bands,
        Water = p.Water,
        Placements = placements ?? p.Placements,
        Ambient = ambient ?? p.Ambient,
        Lights = lights ?? p.Lights,
        Focal = p.Focal,
        TitleSafe = p.TitleSafe,
        Underwater = p.Underwater,
    };

    private static Oklab[] Lab(SKImage img)
    {
        var px = TestKit.Pixels(img);
        var lab = new Oklab[px.Length / 4];
        for (var i = 0; i < lab.Length; i++) lab[i] = Oklab.FromSk(new SKColor(px[i * 4], px[i * 4 + 1], px[i * 4 + 2]));
        return lab;
    }

    private static double Delta(Oklab a, Oklab b) =>
        Math.Sqrt((a.L - b.L) * (a.L - b.L) + (a.A - b.A) * (a.A - b.A) + (a.B - b.B) * (a.B - b.B));

    /// <summary>Pixels that change by more than <paramref name="threshold"/> (OKLab ΔE)
    /// between two renders.</summary>
    private static bool[] DiffMask(Oklab[] a, Oklab[] b, double threshold)
    {
        var mask = new bool[a.Length];
        for (var i = 0; i < a.Length; i++) mask[i] = Delta(a[i], b[i]) > threshold;
        return mask;
    }

    /// <summary>The hero's visible extent: 2nd–98th percentile rows and columns of its mask.</summary>
    internal static (double Height, double Width, double Area, double CenterX) HeroExtent(PosterPlan plan, int size)
    {
        if (plan.Focal is not { } focal) return (0, 0, 0, 0);
        using var full = RenderPlan(plan, size, size);
        var focalGroup = plan.Placements.Where(p => ReferenceEquals(p, focal) || p == focal).ToList();
        using var without = RenderPlan(With(plan, plan.Placements.Where(p => !focalGroup.Contains(p)).ToList(),
            lights: plan.Lights.Where(l => !l.HeroOwned).ToList()), size, size);
        var mask = DiffMask(Lab(full), Lab(without), 0.06);
        var ys = new List<int>();
        var xs = new List<int>();
        for (var i = 0; i < mask.Length; i++)
            if (mask[i]) { ys.Add(i / size); xs.Add(i % size); }
        if (ys.Count < 10) return (0, 0, ys.Count / (double)mask.Length, 0);
        ys.Sort();
        xs.Sort();
        var h = ys[(int)(ys.Count * 0.98)] - ys[(int)(ys.Count * 0.02)];
        var w = xs[(int)(xs.Count * 0.98)] - xs[(int)(xs.Count * 0.02)];
        return (h / (double)size, w / (double)size, ys.Count / (double)mask.Length, xs.Average() / size);
    }

    /// <summary>The warmest bright patch: share of pixels that are warm (hue 30–100°),
    /// saturated, and well above the poster's median lightness.</summary>
    internal static (double WarmArea, double PeakLift) WarmLight(SKImage img)
    {
        var lab = Lab(img);
        var ls = lab.Select(l => l.L).OrderBy(v => v).ToArray();
        var median = ls[ls.Length / 2];
        var warm = 0;
        var peak = 0.0;
        foreach (var c in lab)
        {
            var lch = c.ToOklch();
            if (lch.H is < 30 or > 100 || lch.C < 0.06) continue;
            var lift = c.L - median;
            if (lift < 0.22) continue;
            warm++;
            peak = Math.Max(peak, lift);
        }
        return (warm / (double)lab.Length, peak);
    }

    /// <summary>Near-layer alpha coverage of the lower-left and lower-right framing zones
    /// (above the ground line, where framing has to rise to count).</summary>
    internal static (double Left, double Right) ForegroundCoverage(PosterPlan plan, int size)
    {
        using var set = PosterEngine.RenderLayers(plan, size, size, travelScale: 0);
        var near = set.Layers[3];
        var px = TestKit.Pixels(near.Image);
        double Zone(double x0, double x1, double y0, double y1)
        {
            int hit = 0, total = 0;
            for (var y = (int)(y0 * size); y < (int)(y1 * size); y++)
            for (var x = (int)(x0 * size); x < (int)(x1 * size); x++)
            {
                total++;
                var lx = x - near.Bounds.Left;
                var ly = y - near.Bounds.Top;
                if (lx < 0 || ly < 0 || lx >= near.Image.Width || ly >= near.Image.Height) continue;
                if (px[(ly * near.Image.Width + lx) * 4 + 3] > 160) hit++;
            }
            return total == 0 ? 0 : hit / (double)total;
        }
        return (Zone(0, 0.24, 0.62, 0.9), Zone(0.76, 1, 0.62, 0.9));
    }

    // ------------------------------------------------------------------ tests

    private static IEnumerable<PosterSpec> Sample(int n, int offset = 0) => Enumerable.Range(offset, n).Select(TestKit.RandomSpec);

    [Fact]
    public void Every_poster_has_a_sized_visible_hero()
    {
        const int size = 200;
        var heights = new List<double>();
        var failures = new List<string>();
        foreach (var spec in Sample(120))
        {
            var plan = PosterEngine.Plan(spec);
            var (h, w, area, _) = HeroExtent(plan, size);
            heights.Add(h);
            if (h is < 0.20 or > 0.38 || area < 0.012)
                failures.Add($"{spec.Scene} {plan.Focal?.Prop} seed {spec.Seed}: height {h:0.000} area {area:0.000}");
        }
        heights.Sort();
        var report = $"hero visible height (fraction of poster): min {heights[0]:0.000} p10 {heights[heights.Count / 10]:0.000} " +
                     $"median {heights[heights.Count / 2]:0.000} max {heights[^1]:0.000}; outside 0.20–0.38: {failures.Count}/{heights.Count}\n";
        output.WriteLine(report + string.Join("\n", failures.Take(30)));
        File.WriteAllText(Path.Combine(TestKit.ReportDir, "hero.txt"), report + string.Join("\n", failures));
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(30)));
    }

    [Fact]
    public void Every_poster_has_a_warm_focal_light()
    {
        var areas = new List<double>();
        var failures = new List<string>();
        foreach (var spec in Sample(120, 500))
        {
            var plan = PosterEngine.Plan(spec);
            using var img = RenderPlan(plan, 160, 160);
            var (area, lift) = WarmLight(img);
            areas.Add(area);
            if (plan.Lights.Count == 0 || area < 0.002 || lift < 0.3)
                failures.Add($"{spec.Scene} {spec.TimeOfDay} {spec.Palette} seed {spec.Seed}: lights {plan.Lights.Count} warm {area:0.0000} lift {lift:0.00}");
        }
        areas.Sort();
        var report = $"warm focal light: posters with a warm glow ≥0.2% area & ≥0.30 L lift: {areas.Count - failures.Count}/{areas.Count}; " +
                     $"warm area median {areas[areas.Count / 2]:0.0000} p10 {areas[areas.Count / 10]:0.0000}\n";
        output.WriteLine(report + string.Join("\n", failures.Take(30)));
        File.WriteAllText(Path.Combine(TestKit.ReportDir, "warm-light.txt"), report + string.Join("\n", failures));
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(30)));
    }

    [Fact]
    public void Foreground_frames_the_lower_corners()
    {
        const int size = 200;
        var strong = new List<double>();
        var weak = new List<double>();
        var failures = new List<string>();
        foreach (var spec in Sample(120, 1000))
        {
            var plan = PosterEngine.Plan(spec);
            var (l, r) = ForegroundCoverage(plan, size);
            strong.Add(Math.Max(l, r));
            weak.Add(Math.Min(l, r));
            if (Math.Max(l, r) < 0.30 || Math.Min(l, r) < 0.05)
                failures.Add($"{spec.Scene} seed {spec.Seed}: left {l:0.00} right {r:0.00}");
        }
        strong.Sort();
        weak.Sort();
        var report = $"foreground framing coverage of the lower corners (y 0.62–0.90): stronger side median {strong[strong.Count / 2]:0.00} p10 {strong[strong.Count / 10]:0.00}; " +
                     $"weaker side median {weak[weak.Count / 2]:0.00} p10 {weak[weak.Count / 10]:0.00}; failing {failures.Count}/{strong.Count}\n";
        output.WriteLine(report + string.Join("\n", failures.Take(30)));
        File.WriteAllText(Path.Combine(TestKit.ReportDir, "foreground.txt"), report + string.Join("\n", failures));
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(30)));
    }

    /// <summary>Palette range across 300 random posters: Shannon entropy of the posters'
    /// mean-hue histogram (12 × 30° bins; max 3.58 bits), mean pairwise OKLab distance of
    /// their mean colours, the share that sits in navy/violet (mean hue 240–320°), and the
    /// median within-poster value range (p95 − p5 of L) — the "blur together" symptom.</summary>
    [Fact]
    public void Palette_range_is_broad_across_three_hundred_posters()
    {
        var means = new List<Oklab>();
        var ranges = new List<double>();
        foreach (var spec in Sample(300, 2000))
        {
            using var img = PosterEngine.RenderFlattened(spec, 64, 64);
            var lab = Lab(img);
            means.Add(new Oklab(lab.Average(c => c.L), lab.Average(c => c.A), lab.Average(c => c.B)));
            var ls = lab.Select(c => c.L).OrderBy(v => v).ToArray();
            ranges.Add(ls[(int)(ls.Length * 0.95)] - ls[(int)(ls.Length * 0.05)]);
        }
        var bins = new int[12];
        var navy = 0;
        foreach (var m in means)
        {
            var h = m.ToOklch().H;
            bins[(int)(h / 30) % 12]++;
            if (h is >= 240 and <= 320) navy++;
        }
        var entropy = -bins.Where(b => b > 0).Sum(b => (double)b / means.Count * Math.Log2((double)b / means.Count));
        double dist = 0;
        var pairs = 0;
        for (var i = 0; i < means.Count; i++)
        for (var j = i + 1; j < means.Count; j++) { dist += Delta(means[i], means[j]); pairs++; }
        dist /= pairs;
        ranges.Sort();
        var report = new StringBuilder();
        report.AppendLine($"mean-hue entropy {entropy:0.00} bits (max 3.58); histogram {string.Join(" ", bins)}");
        report.AppendLine($"mean pairwise OKLab distance of poster means {dist:0.0000}");
        report.AppendLine($"navy/violet share (mean hue 240–320°) {navy / (double)means.Count:P1}");
        report.AppendLine($"within-poster value range (L p95−p5): median {ranges[ranges.Count / 2]:0.000}, p10 {ranges[ranges.Count / 10]:0.000}");
        report.AppendLine($"mean poster lightness {means.Average(m => m.L):0.000}");
        output.WriteLine(report.ToString());
        File.WriteAllText(Path.Combine(TestKit.ReportDir, "palette-range.txt"), report.ToString());
        Assert.True(entropy >= 2.6, $"hue entropy {entropy:0.00} bits — palettes cluster");
        Assert.True(dist >= 0.06, $"mean OKLab distance {dist:0.0000} — posters blur together");
        Assert.True(navy / (double)means.Count <= 0.35, $"{navy / (double)means.Count:P0} navy/violet");
        // Baseline (pass 1, the "dark wallpaper"): 0.25. The picture-book pass reaches ~0.39.
        Assert.True(ranges[ranges.Count / 2] >= 0.37, "within-poster value range too narrow");
    }

    /// <summary>Every feature the story asked for must read at card size: removing it must
    /// change a visible patch of the 160 px poster (≥0.25% of the area at ΔE &gt; 0.05).
    /// Scene-intrinsic features (a village that IS the town) are exempt.</summary>
    [Fact]
    public void Every_listed_feature_is_visible_at_card_size()
    {
        const int size = 160;
        var failures = new List<string>();
        var checkedCount = 0;
        var minArea = new Dictionary<PosterFeature, double>();
        foreach (var spec in Sample(160, 3000))
        {
            var plan = PosterEngine.Plan(spec);
            if (spec.Features.Count == 0) continue;
            using var fullImg = RenderPlan(plan, size, size);
            var full = Lab(fullImg);
            foreach (var f in spec.Features)
            {
                PosterPlan? without = f switch
                {
                    PosterFeature.Village when spec.Scene is PosterScene.TownRooftops => null,
                    PosterFeature.Fireflies => With(plan, ambient: plan.Ambient.Where(a => a.Kind != AmbientKind.Firefly).ToList()),
                    PosterFeature.Snowfall => With(plan, ambient: plan.Ambient.Where(a => a.Kind != AmbientKind.Snowflake).ToList()),
                    PosterFeature.MilkyWay => With(plan, dropMilkyWay: true),
                    PosterFeature.Aurora => With(plan, dropAurora: true),
                    PosterFeature.Clouds => With(plan, dropClouds: true),
                    PosterFeature.ShootingStar => With(plan, dropShootingStar: true),
                    _ => With(plan, plan.Placements.Where(p => p.Feature != f).ToList(),
                        lights: plan.Lights.Where(l => l.Feature != f).ToList()),
                };
                if (without is null) continue;
                checkedCount++;
                using var img = RenderPlan(without, size, size);
                var mask = DiffMask(full, Lab(img), 0.05);
                var area = mask.Count(m => m) / (double)mask.Length;
                minArea[f] = Math.Min(minArea.GetValueOrDefault(f, 1), area);
                if (area < 0.0025) failures.Add($"{spec.Scene} {f} seed {spec.Seed}: {area:0.0000}");
            }
        }
        var report = $"feature visibility at {size}px: {checkedCount - failures.Count}/{checkedCount} pass (≥0.25% area at ΔE>0.05)\n" +
                     "smallest visible area per feature: " + string.Join(", ", minArea.OrderBy(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value:0.0000}")) + "\n";
        output.WriteLine(report + string.Join("\n", failures.Take(40)));
        File.WriteAllText(Path.Combine(TestKit.ReportDir, "feature-visibility.txt"), report + string.Join("\n", failures));
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(40)));
    }
}
