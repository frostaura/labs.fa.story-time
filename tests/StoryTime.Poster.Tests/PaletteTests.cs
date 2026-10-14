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

    [Fact]
    public void Night_skies_stay_dark_calm_and_ordered()
    {
        foreach (var spec in AllCombos())
        {
            var c = ScenePalette.Build(spec);
            Assert.True(c.SkyZenith.L < c.SkyMid.L && c.SkyMid.L < c.SkyHorizon.L, $"{spec.Palette}/{spec.TimeOfDay}: sky not darkest at the top");
            Assert.InRange(c.SkyZenith.L, 0.12, 0.34);
            Assert.InRange(c.SkyHorizon.L, 0.26, 0.62);
            // Calm: low chroma everywhere except the single warm accent family.
            foreach (var col in new[] { c.SkyZenith, c.SkyMid, c.SkyHorizon, c.LandNear, c.Haze })
                Assert.True(col.C <= 0.11, $"{spec.Palette}: chroma {col.C:0.000} too loud");
            Assert.InRange(c.Window.H, 60, 85);              // warm lights are amber, always
            Assert.InRange(c.LandNear.L, 0.10, 0.36);        // never pure black, never bright
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
            Assert.True(c.Ground(1).C >= c.Ground(0).C - 1e-9 || c.Ground(1).C < 0.06);
        }
    }

    [Fact]
    public void Palettes_are_analogous_families()
    {
        foreach (var p in TestKit.Palettes)
        {
            var (sky, land, _, _) = ScenePalette.Family(p);
            // Sky and land within a 120° arc: a family, never a clash.
            Assert.True(HueDistance(sky, land) <= 120, $"{p}: sky {sky}° vs land {land}°");
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
