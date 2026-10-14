using SkiaSharp;
using StoryTime.Contracts;
using StoryTime.Poster.Composition;
using StoryTime.Poster.Spec;
using Xunit;
using Xunit.Abstractions;

namespace StoryTime.Poster.Tests;

/// <summary>Pixel-level guarantees: determinism, coverage of the whole vocabulary,
/// overscan that never reveals an edge, sane alpha, a readable title band, no banding.</summary>
public class RenderTests(ITestOutputHelper output)
{
    [Fact]
    public void Same_spec_renders_identical_pixels_and_seeds_differ()
    {
        for (var i = 0; i < 12; i++)
        {
            var spec = TestKit.RandomSpec(i);
            using var a = PosterEngine.RenderFlattened(spec, 160, 160);
            using var b = PosterEngine.RenderFlattened(PosterSpecJson.Parse(PosterSpecJson.Serialize(spec))!, 160, 160);
            Assert.Equal(TestKit.Hash(a), TestKit.Hash(b));
            using var c = PosterEngine.RenderFlattened(new PosterSpec
            {
                Scene = spec.Scene, TimeOfDay = spec.TimeOfDay, Mood = spec.Mood, Palette = spec.Palette,
                Features = spec.Features, Seed = spec.Seed + 1,
            }, 160, 160);
            Assert.NotEqual(TestKit.Hash(a), TestKit.Hash(c));
        }
    }

    [Fact]
    public void Layers_are_deterministic_too()
    {
        var spec = TestKit.RandomSpec(3);
        using var a = PosterEngine.RenderLayers(spec, 180, 180);
        using var b = PosterEngine.RenderLayers(spec, 180, 180);
        for (var d = 0; d < 4; d++)
        {
            Assert.Equal(a.Layers[d].Bounds, b.Layers[d].Bounds);
            Assert.Equal(TestKit.Hash(a.Layers[d].Image), TestKit.Hash(b.Layers[d].Image));
        }
        Assert.Equal(TestKit.Hash(a.Backdrop), TestKit.Hash(b.Backdrop));
    }

    [Fact]
    public void Every_scene_renders_with_every_palette_and_time()
    {
        foreach (var scene in TestKit.Scenes)
        foreach (var palette in TestKit.Palettes)
        {
            var spec = PosterSpecRules.Normalize(new PosterSpec
            {
                Scene = scene, Palette = palette, TimeOfDay = TestKit.Times[(int)palette % 4], Mood = TestKit.Moods[(int)scene % 6],
                Features = Enum.GetValues<PosterFeature>().Where(f => PosterSpecRules.IsCompatible(scene, f)).Take(3).ToList(),
                Seed = (int)scene * 100 + (int)palette,
            });
            using var img = PosterEngine.RenderFlattened(spec, 96, 96);
            var px = TestKit.Pixels(img);
            double min = 1, max = 0;
            for (var i = 0; i < px.Length; i += 4)
            {
                Assert.Equal(255, px[i + 3]);                 // fully opaque frame
                var l = TestKit.Luminance(px, i);
                min = Math.Min(min, l);
                max = Math.Max(max, l);
            }
            Assert.True(max - min > 0.02, $"{scene}/{palette} is a blank rectangle");
            Assert.True(max < 0.95, $"{scene}/{palette} has blown highlights");
        }
    }

    [Fact]
    public void Plans_are_finite_and_inside_their_frame_for_500_specs()
    {
        for (var i = 0; i < 500; i++)
        {
            var plan = PosterEngine.Plan(TestKit.RandomSpec(i));
            Assert.InRange(plan.Horizon, 0.5, 0.8);
            foreach (var p in plan.Placements)
            {
                Assert.True(double.IsFinite(p.X) && double.IsFinite(p.Y) && double.IsFinite(p.Size), $"{p.Prop} not finite");
                Assert.InRange(p.X, -0.6, 1.6);
                Assert.True(p.Y is >= -0.1 and <= 1.2, $"{plan.Spec.Scene}: {p.Prop} at y {p.Y:0.000}");
                Assert.InRange(p.Size, 0.01, 0.8);
            }
            foreach (var s in plan.Stars)
            {
                Assert.True(double.IsFinite(s.X) && double.IsFinite(s.Y));
                Assert.InRange(s.Alpha, 0, 1);
            }
            foreach (var b in plan.Bands)
                for (var x = -0.3; x <= 1.3; x += 0.01)
                    Assert.True(double.IsFinite(b.Profile.Y(x)), $"band {b.Profile.Kind} y({x}) not finite");
            foreach (var a in plan.Ambient)
            {
                Assert.True(a.Period >= 2.0, "ambient changes faster than 2 s");
                if (a.Kind is AmbientKind.Firefly or AmbientKind.Snowflake or AmbientKind.Bubble or AmbientKind.Mote)
                    Assert.True(a.Period >= 7.0, $"{a.Kind} drifts faster than a calm 7 s cycle");
            }
        }
    }

    [Fact]
    public void Composition_keeps_title_band_clear_and_subjects_on_thirds()
    {
        for (var i = 0; i < 400; i++)
        {
            var plan = PosterEngine.Plan(TestKit.RandomSpec(i));
            var title = plan.TitleSafe;
            if (plan.Moon is { } m)
            {
                Assert.True(m.Y - m.R >= title.Bottom - 1e-9, "moon intrudes into the title band");
                Assert.True(m.Y + m.R < plan.Horizon - 0.05, "moon sits on the horizon");
                Assert.True(Math.Abs(m.X - 0.5) > 0.15, "moon centred — it belongs on a third");
            }
            Assert.All(plan.Clouds, c => Assert.True(c.Y - c.Height * 1.4 >= title.Bottom - 0.02, "cloud in title band"));
            Assert.All(plan.Stars.Where(s => title.Contains(s.X, s.Y)), s => Assert.False(s.Sparkle));
            if (plan.Focal is { } f && f.Depth == 3 && plan.Spec.Scene != PosterScene.ReefAtDusk)
                Assert.InRange(Math.Abs(f.X - 0.5), 0.10, 0.40);
        }
    }

    /// <summary>Every silhouette family draws something (catches NaN geometry, which Skia
    /// silently drops — a palm once vanished that way).</summary>
    [Fact]
    public void Every_prop_paints_pixels()
    {
        var plan = PosterEngine.Plan(PosterSpecRules.Normalize(new PosterSpec { Scene = PosterScene.StarryHills, Seed = 1 }));
        var vp = new StoryTime.Poster.Render.PosterViewport(200, 200, 0, 0);
        foreach (var prop in Enum.GetValues<Prop>())
        {
            using var bmp = new SKBitmap(new SKImageInfo(200, 200, SKColorType.Rgba8888, SKAlphaType.Premul));
            bmp.Erase(SKColors.Transparent);
            using (var canvas = new SKCanvas(bmp))
                StoryTime.Poster.Render.PropPainter.Paint_(canvas, plan,
                    new Placement(prop, 2, 0.5, 0.8, 0.3, false, 0, 7, Lit: true), vp);
            var painted = 0;
            var span = bmp.GetPixelSpan();
            for (var i = 3; i < span.Length; i += 4) if (span[i] > 0) painted++;
            Assert.True(painted > 30, $"{prop} painted {painted} px");
            // Solid silhouettes must have body, not just a stroke (a GetFillPath bug once
            // reduced the cat to its tail and the deer to its antlers).
            if (prop is not (Prop.Birds or Prop.GrassTuft or Prop.Reeds or Prop.Vines or Prop.Flower or Prop.FishSchool
                    or Prop.BranchCoral or Prop.FanCoral or Prop.Bush or Prop.Fence or Prop.Lantern or Prop.Telescope or Prop.Mushroom or Prop.Trunk))
                Assert.True(painted > 0.08 * 60 * 60, $"{prop} is a sliver ({painted} px)");
        }
    }

    [Fact]
    public void Layers_have_sane_premultiplied_alpha_and_content()
    {
        for (var i = 0; i < 24; i++)
        {
            var spec = TestKit.RandomSpec(i);
            using var set = PosterEngine.RenderLayers(spec, 200, 200);
            foreach (var layer in set.Layers)
            {
                var px = TestKit.Pixels(layer.Image);
                var opaque = 0;
                for (var k = 0; k < px.Length; k += 4)
                {
                    var a = px[k + 3];
                    Assert.True(px[k] <= a && px[k + 1] <= a && px[k + 2] <= a, $"layer {layer.Depth}: colour exceeds alpha (bad premultiply)");
                    if (a == 255) opaque++;
                }
                var total = px.Length / 4;
                if (layer.Depth == 0)
                    Assert.True(opaque < total * 0.25, "the sky layer must stay mostly transparent over the backdrop");
                else
                    Assert.True(opaque > total * 0.05, $"{spec.Scene}: layer {layer.Depth} is nearly empty");
            }
            // The backdrop is the one fully opaque plate.
            var bpx = TestKit.Pixels(set.Backdrop);
            for (var k = 3; k < bpx.Length; k += 4) Assert.Equal(255, bpx[k]);
        }
    }

    /// <summary>The overscan proof. Composite only the travelling land/sea layers (no sky
    /// backdrop, no celestial layer) over a magenta sentinel at all nine full-tilt
    /// extremes: below the horizon not one sentinel pixel may show — no edge reveal, no
    /// seam between bands, no gap between buildings.</summary>
    [Fact]
    public void Max_tilt_never_reveals_an_edge_or_seam()
    {
        var failures = new List<string>();
        foreach (var scene in TestKit.Scenes)
        for (var seed = 0; seed < 3; seed++)
        {
            var spec = TestKit.RandomSpec(seed * 31 + (int)scene) is var s && s.Scene == scene
                ? s
                : PosterSpecRules.Normalize(new PosterSpec { Scene = scene, Seed = seed * 977 + 13, TimeOfDay = TestKit.Times[seed % 4] });
            const int size = 226;
            using var set = PosterEngine.RenderLayers(spec, size, size);
            var vp = set.Viewport;
            Assert.True(vp.BleedX >= set.Layers.Max(l => l.Travel), "bleed smaller than the deepest travel");
            // Below the lowest point of the far silhouette (or the waterline), shifted by
            // the far layer's own travel, everything must be land or sea.
            var plan = set.Plan;
            var farTravel = PosterEngine.TravelFraction[1];
            var lowest = plan.Water is { } w ? w.Line
                : plan.Bands.Where(b => b.Depth == 1 && b.Profile.Floor is null)
                    .Max(b => Enumerable.Range(0, 81).Max(k => b.Profile.Y(-0.2 + k * 0.0175)));
            var checkFrom = (int)Math.Ceiling((lowest + farTravel + 0.01) * size);
            foreach (var tx in new[] { -1.0, 0, 1 })
            foreach (var ty in new[] { -1.0, 0, 1 })
            {
                using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
                surface.Canvas.Clear(new SKColor(255, 0, 255));
                foreach (var layer in set.Layers.Where(l => l.Depth > 0))
                    surface.Canvas.DrawImage(layer.Image,
                        layer.Bounds.Left - vp.BleedX + (float)(tx * layer.Travel),
                        layer.Bounds.Top - vp.BleedY + (float)(ty * layer.Travel));
                using var img = surface.Snapshot();
                var px = TestKit.Pixels(img);
                var leaks = 0;
                for (var y = checkFrom; y < size; y++)
                for (var x = 0; x < size; x++)
                {
                    var i = (y * size + x) * 4;
                    if (px[i] > 120 && px[i + 1] < 70 && px[i + 2] > 120 && px[i] - px[i + 1] > 90) leaks++;
                }
                if (leaks > 0) failures.Add($"{spec.Scene} seed {spec.Seed} tilt ({tx},{ty}): {leaks} px");
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>WCAG-style check for a cream title (#F3E9DC) set over the title band: mean
    /// background ≥ 4.5:1, and even the brightest 2% of pixels ≥ 3:1 (the large-text bar —
    /// titles are display type).</summary>
    [Fact]
    public void Title_band_keeps_cream_text_readable()
    {
        var worstMean = double.MaxValue;
        var worstPeak = double.MaxValue;
        var failures = new List<string>();
        foreach (var scene in TestKit.Scenes)
        foreach (var time in TestKit.Times)
        for (var seed = 0; seed < 3; seed++)
        {
            var spec = PosterSpecRules.Normalize(new PosterSpec
            {
                Scene = scene, TimeOfDay = time, Mood = TestKit.Moods[seed * 2 % 6], Palette = TestKit.Palettes[(seed * 5 + (int)scene) % 12],
                Features = Enum.GetValues<PosterFeature>().Where(f => PosterSpecRules.IsCompatible(scene, f)).Skip(seed).Take(3).ToList(),
                Seed = 4000 + seed * 101 + (int)time * 7,
            });
            const int size = 240;
            using var img = PosterEngine.RenderFlattened(spec, size, size);
            var px = TestKit.Pixels(img);
            var t = ScenePlanner.TitleSafe;
            var lums = new List<double>();
            for (var y = (int)(t.Top * size); y < (int)(t.Bottom * size); y++)
            for (var x = (int)(t.Left * size); x < (int)(t.Right * size); x++)
                lums.Add(TestKit.Luminance(px, (y * size + x) * 4));
            lums.Sort();
            var mean = lums.Average();
            var p98 = lums[(int)(lums.Count * 0.98)];
            var cMean = StoryTime.Poster.Color.Srgb.ContrastRatio(TestKit.CreamLuminance, mean);
            var cPeak = StoryTime.Poster.Color.Srgb.ContrastRatio(TestKit.CreamLuminance, p98);
            worstMean = Math.Min(worstMean, cMean);
            worstPeak = Math.Min(worstPeak, cPeak);
            if (cMean < 4.5 || cPeak < 3.0) failures.Add($"{scene}/{time}/{spec.Palette}: mean {cMean:0.00} p98 {cPeak:0.00}");
        }
        output.WriteLine($"title band contrast vs cream: worst mean {worstMean:0.00}:1, worst p98 {worstPeak:0.00}:1");
        File.WriteAllText(Path.Combine(TestKit.ReportDir, "title-contrast.txt"), $"worst mean {worstMean:0.00}:1\nworst p98 {worstPeak:0.00}:1\n");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>Banding: down a tall column of the darkest skies, the dithered ramp has no
    /// long runs of one identical value and no visible steps between 16-px means. The
    /// same metric on an undithered ramp DOES show long runs — so it measures something.</summary>
    [Fact]
    public void Sky_gradients_do_not_band()
    {
        const int size = 900;
        var worstRun = 0;
        var worstStep = 0.0;
        foreach (var scene in new[] { PosterScene.StarryHills, PosterScene.MoonlitSea, PosterScene.ReefAtDusk, PosterScene.TownRooftops })
        foreach (var time in TestKit.Times)
        {
            var spec = PosterSpecRules.Normalize(new PosterSpec { Scene = scene, TimeOfDay = time, Seed = 5 });
            using var set = PosterEngine.RenderLayers(spec, size, size, travelScale: 0);
            var px = TestKit.Pixels(set.Backdrop);
            var (run, step) = ColumnBanding(px, size, size / 2, (int)(set.Plan.Horizon * size));
            worstRun = Math.Max(worstRun, run);
            worstStep = Math.Max(worstStep, step);
        }
        output.WriteLine($"sky banding: longest identical run {worstRun}px, largest 16px-mean second difference {worstStep:0.00} LSB");
        Assert.True(worstRun <= 14, $"identical-value run of {worstRun}px — the ramp is banding");
        Assert.True(worstStep <= 1.6, $"a {worstStep:0.00} LSB step between neighbouring bands");

        // Control: Skia's undithered gradient across the same tones does band.
        using var control = SKSurface.Create(new SKImageInfo(8, size));
        using (var p = new SKPaint
               {
                   IsDither = false,
                   Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, size),
                       [new SKColor(0x14, 0x12, 0x26), new SKColor(0x2E, 0x2A, 0x48)], SKShaderTileMode.Clamp),
               })
            control.Canvas.DrawRect(0, 0, 8, size, p);
        using var cimg = control.Snapshot();
        var (cRun, _) = ColumnBanding(TestKit.Pixels(cimg), 8, 4, size);
        Assert.True(cRun >= 20, $"metric sanity: an undithered ramp should band (run {cRun})");
    }

    private static (int Run, double Step) ColumnBanding(byte[] px, int width, int x, int rows)
    {
        int run = 1, longest = 1;
        var values = new double[rows];
        for (var y = 0; y < rows; y++)
        {
            var i = (y * width + x) * 4;
            values[y] = 0.299 * px[i] + 0.587 * px[i + 1] + 0.114 * px[i + 2];
            if (y > 0)
            {
                var j = ((y - 1) * width + x) * 4;
                if (px[i] == px[j] && px[i + 1] == px[j + 1] && px[i + 2] == px[j + 2]) longest = Math.Max(longest, ++run);
                else run = 1;
            }
        }
        // Staircase detector: the second difference of 16-px means. A smooth ramp (however
        // steep) has ~0; a banded one jumps by a whole step at each band edge.
        var means = new List<double>();
        for (var y = 0; y + 16 <= rows; y += 16) means.Add(values.Skip(y).Take(16).Average());
        double step = 0;
        for (var i = 1; i + 1 < means.Count; i++)
            step = Math.Max(step, Math.Abs(means[i + 1] - 2 * means[i] + means[i - 1]));
        return (longest, step);
    }

    [Fact]
    public void Ambient_animates_and_is_a_valid_still_at_zero()
    {
        var spec = PosterSpecRules.Normalize(new PosterSpec
        {
            Scene = PosterScene.FireflyMeadow, TimeOfDay = PosterTimeOfDay.Moonlit, Features = [PosterFeature.Fireflies], Seed = 3,
        });
        using var set = PosterEngine.RenderLayers(spec, 200, 200);
        Assert.Contains(set.Plan.Ambient, a => a.Kind == AmbientKind.Firefly);
        using var t0 = PosterEngine.Composite(set, 0, 0, 0);
        using var t3 = PosterEngine.Composite(set, 0, 0, 3.3);
        Assert.NotEqual(TestKit.Hash(t0), TestKit.Hash(t3));
        // The still (thumbnail / Reduce Motion) bakes the same particles at t = 0.
        using var still = PosterEngine.RenderLayers(spec, 200, 200, bakeAmbient: true);
        Assert.True(still.AmbientBaked);
        using var baked = PosterEngine.Composite(still);
        using var bare = PosterEngine.Composite(set, includeAmbient: false);
        Assert.NotEqual(TestKit.Hash(bare), TestKit.Hash(baked)); // the particles are really in the still
    }
}
