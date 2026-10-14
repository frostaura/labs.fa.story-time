using StoryTime.Contracts;
using StoryTime.Poster.Color;
using StoryTime.Poster.Composition;
using StoryTime.Poster.Spec;
using Xunit;

namespace StoryTime.Poster.Tests;

/// <summary>Colour harmony constraints across every palette × scene × time × mood.</summary>
public class PaletteTests
{
    private static IEnumerable<PosterSpec> AllCombos() =>
        from p in TestKit.Palettes
        from s in TestKit.Scenes
        from t in TestKit.Times
        from m in TestKit.Moods
        select PosterSpecRules.Normalize(new PosterSpec { Palette = p, Scene = s, TimeOfDay = t, Mood = m });

    private static double HueDistance(double a, double b) => Math.Abs(((a - b) % 360 + 540) % 360 - 180);

    [Fact]
    public void Hearth_anchors_are_exact()
    {
        Assert.Equal("#F3E9DC", Hex(ScenePalette.Cream.ToSk()));
        Assert.Equal("#E8A857", Hex(ScenePalette.Amber.ToSk()));
    }

    private static string Hex(SkiaSharp.SKColor c) => $"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}";

    /// <summary>Bedtime skies, not night-black wallpaper: darkest at the top (the title band
    /// needs it), glowing toward the horizon, calm chroma, and land that keeps a real value
    /// a step below the sky.</summary>
    [Fact]
    public void Night_skies_stay_dark_calm_and_ordered()
    {
        foreach (var spec in AllCombos())
        {
            var c = ScenePalette.Build(spec);
            Assert.True(c.SkyZenith.L < c.SkyMid.L && c.SkyMid.L < c.SkyHorizon.L, $"{spec.Palette}/{spec.TimeOfDay}: sky not darkest at the top");
            Assert.InRange(c.SkyZenith.L, 0.15, 0.42);
            Assert.InRange(c.SkyHorizon.L, 0.36, 0.76);
            // Calm: low chroma everywhere except the single warm accent family.
            foreach (var col in new[] { c.SkyZenith, c.SkyMid, c.SkyHorizon, c.LandNear, c.Haze })
                Assert.True(col.C <= 0.14, $"{spec.Palette}: chroma {col.C:0.000} too loud");
            Assert.InRange(c.Window.H, 60, 85);              // warm lights are amber, always
            Assert.InRange(c.LandNear.L, 0.15, 0.42);        // never pure black, never bright
            Assert.True(spec.Scene is PosterScene.ReefAtDusk or PosterScene.CloudKingdom || c.SkyHorizon.L - c.LandNear.L >= 0.15, $"{spec.Palette}/{spec.Scene}: land does not separate from the sky");
        }
    }

    [Fact]
    public void Ground_bands_step_by_atmospheric_perspective()
    {
        foreach (var spec in AllCombos().Where(s => s.Scene is not (PosterScene.SnowyVillage or PosterScene.CloudKingdom)))
        {
            var c = ScenePalette.Build(spec);
            var prev = double.MaxValue;
            for (var t = 0.0; t <= 1.0001; t += 0.25)
            {
                var g = c.Ground(t);
                Assert.True(g.L < prev, $"{spec.Palette}/{spec.Scene}: ground at depth {t} not darker than the band behind it");
                prev = g.L;
            }
            Assert.True(c.Ground(0).L - c.Ground(1).L >= 0.07, $"{spec.Palette}: depth range too flat to read");
            // Far bands are hazier: closer in hue and lightness to the horizon haze.
            Assert.True(Math.Abs(c.Ground(0).L - c.Haze.L) < Math.Abs(c.Ground(1).L - c.Haze.L));
            // ... and closer in colour too (a glowing dusk lends far hills its tint).
            static double D(Oklab a, Oklab b) => Math.Sqrt((a.L - b.L) * (a.L - b.L) + (a.A - b.A) * (a.A - b.A) + (a.B - b.B) * (a.B - b.B));
            Assert.True(D(c.Ground(0).ToOklab(), c.Haze.ToOklab()) < D(c.Ground(1).ToOklab(), c.Haze.ToOklab()));
        }
    }

    [Fact]
    public void Every_palette_is_its_own_colour_script()
    {
        var scripts = TestKit.Palettes.Select(p => (p, s: ScenePalette.Family(p))).ToList();
        foreach (var (a, sa) in scripts)
        foreach (var (b, sb) in scripts.Where(x => x.p > a))
        {
            var d = HueDistance(sa.SkyHue, sb.SkyHue) + HueDistance(sa.GlowHue, sb.GlowHue) + HueDistance(sa.LandHue, sb.LandHue);
            Assert.True(d >= 30, $"{a} and {b} are the same colour script");
        }
        // Real range: sky hues spread over at least five 60° sectors.
        Assert.True(scripts.Select(x => (int)(x.s.SkyHue / 60)).Distinct().Count() >= 5, "sky hues cluster");
    }

    [Fact]
    public void Palettes_are_analogous_families()
    {
        foreach (var p in TestKit.Palettes)
        {
            var script = ScenePalette.Family(p);
            var (sky, land) = (script.SkyHue, script.LandHue);
            // Sky and land within a 120° arc: a family, never a clash — except the deliberate
            // lamplight scheme (warm amber land under a cool sky, so the windows sing).
            var lamplight = HueDistance(land, ScenePalette.Amber.H) <= 30;
            Assert.True(HueDistance(sky, land) <= 120 || lamplight, $"{p}: sky {sky}° vs land {land}°");
            var c = ScenePalette.Build(new PosterSpec { Palette = p, Scene = PosterScene.StarryHills, TimeOfDay = PosterTimeOfDay.Moonlit });
            Assert.True(HueDistance(c.SkyMid.H, sky) <= 12, $"{p}: sky drifted from its family");
        }
    }

    [Fact]
    public void All_derived_colours_map_into_srgb_gamut()
    {
        foreach (var spec in AllCombos().Take(2000))
        {
            var c = ScenePalette.Build(spec);
            foreach (var col in new[] { c.SkyZenith, c.SkyMid, c.SkyHorizon, c.HorizonGlow, c.Haze, c.LandNear, c.Foliage,
                         c.WaterTop, c.WaterDeep, c.MoonGlow, c.Rim, c.Snow, c.CloudLit, c.CloudShade, c.Accent })
                Assert.True(col.ToOklab().MapToGamut().InGamut());
        }
    }

    [Fact]
    public void Oklab_round_trips_srgb()
    {
        foreach (var hex in new[] { "#201813", "#F3E9DC", "#E8A857", "#3A2E52", "#000000", "#FFFFFF" })
        {
            var c = SkiaSharp.SKColor.Parse(hex);
            var back = Oklab.FromSk(c).ToSk();
            Assert.Equal(c.Red, back.Red);
            Assert.Equal(c.Green, back.Green);
            Assert.Equal(c.Blue, back.Blue);
        }
    }
}
