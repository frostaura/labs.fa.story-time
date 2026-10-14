using SkiaSharp;
using StoryTime.Contracts;
using StoryTime.Poster.Spec;
using Xunit;

namespace StoryTime.Poster.Tests;

/// <summary>Golden snapshots for twelve fixed posters (one per scene). Approval workflow:
/// a deliberate visual change is approved by regenerating the goldens
/// (tests/StoryTime.Poster.Tests/golden/regenerate.sh), eyeballing the diff, committing the
/// PNGs — and bumping PosterEngineInfo.ArtVersion so cached device art re-renders.
/// Comparison allows ±3 per channel on ≤0.5% of pixels for cross-CPU float noise; anything
/// else is a real change.</summary>
public class GoldenTests
{
    public static readonly (string Name, PosterSpec Spec)[] Cases =
    [
        ("01-moonlit-sea-lighthouse", S(PosterScene.MoonlitSea, PosterTimeOfDay.Moonlit, PosterMood.Calm, PosterPalette.NightHarbor, 101, PosterFeature.Lighthouse, PosterFeature.Sailboat)),
        ("02-forest-cabin-fox", S(PosterScene.ForestClearing, PosterTimeOfDay.Twilight, PosterMood.Cozy, PosterPalette.PineForest, 202, PosterFeature.Fox, PosterFeature.Cabin, PosterFeature.Fireflies)),
        ("03-mountain-lake-boat", S(PosterScene.MountainLake, PosterTimeOfDay.Dusk, PosterMood.Calm, PosterPalette.MistValley, 303, PosterFeature.Boat, PosterFeature.Deer)),
        ("04-firefly-meadow-rabbit", S(PosterScene.FireflyMeadow, PosterTimeOfDay.Moonlit, PosterMood.Dreamy, PosterPalette.MoonMeadow, 404, PosterFeature.Rabbit, PosterFeature.Fireflies, PosterFeature.Windmill)),
        ("05-snowy-village-owl", S(PosterScene.SnowyVillage, PosterTimeOfDay.Twilight, PosterMood.Cozy, PosterPalette.SnowHush, 505, PosterFeature.Owl, PosterFeature.Snowfall)),
        ("06-desert-oasis-camel", S(PosterScene.DesertOasis, PosterTimeOfDay.Starlit, PosterMood.Curious, PosterPalette.EmberSky, 606, PosterFeature.Camel, PosterFeature.Tent)),
        ("07-island-cove-turtle", S(PosterScene.IslandCove, PosterTimeOfDay.Moonlit, PosterMood.Calm, PosterPalette.TidePool, 707, PosterFeature.Turtle, PosterFeature.Boat)),
        ("08-starry-hills-telescope", S(PosterScene.StarryHills, PosterTimeOfDay.Starlit, PosterMood.Magical, PosterPalette.StarfieldViolet, 808, PosterFeature.Telescope, PosterFeature.MilkyWay)),
        ("09-reef-turtle-fish", S(PosterScene.ReefAtDusk, PosterTimeOfDay.Twilight, PosterMood.Dreamy, PosterPalette.OceanDeep, 909, PosterFeature.Turtle, PosterFeature.Fish)),
        ("10-cloud-kingdom-castle", S(PosterScene.CloudKingdom, PosterTimeOfDay.Moonlit, PosterMood.Magical, PosterPalette.CloudDrift, 1010, PosterFeature.Castle, PosterFeature.Balloon)),
        ("11-town-rooftops-cat", S(PosterScene.TownRooftops, PosterTimeOfDay.Twilight, PosterMood.Cozy, PosterPalette.LanternGlow, 1111, PosterFeature.Cat, PosterFeature.Telescope)),
        ("12-jungle-dinosaur", S(PosterScene.JungleCanopy, PosterTimeOfDay.Moonlit, PosterMood.Curious, PosterPalette.PineForest, 1212, PosterFeature.Dinosaur, PosterFeature.Fireflies)),
    ];

    private static PosterSpec S(PosterScene scene, PosterTimeOfDay time, PosterMood mood, PosterPalette palette, int seed,
        params PosterFeature[] features) =>
        PosterSpecRules.Normalize(new PosterSpec { Scene = scene, TimeOfDay = time, Mood = mood, Palette = palette, Seed = seed, Features = [.. features] });

    public const int Size = 192;

    public static string GoldenDir => Path.Combine(TestKit.SourceDir(), "golden");

    public static IEnumerable<object[]> Names() => Cases.Select(c => new object[] { c.Name });

    [Theory]
    [MemberData(nameof(Names))]
    public void Poster_matches_its_golden(string name)
    {
        var spec = Cases.Single(c => c.Name == name).Spec;
        using var img = PosterEngine.RenderFlattened(spec, Size, Size);
        var path = Path.Combine(GoldenDir, name + ".png");
        if (Environment.GetEnvironmentVariable("STORYTIME_UPDATE_GOLDENS") == "1")
        {
            Directory.CreateDirectory(GoldenDir);
            File.WriteAllBytes(path, PosterEngine.EncodePng(img, fast: false));
            return;
        }
        Assert.True(File.Exists(path), $"missing golden {name}.png — run golden/regenerate.sh and review the images");
        using var golden = SKImage.FromEncodedData(path);
        Assert.Equal(Size, golden.Width);
        var a = TestKit.Pixels(img);
        var b = TestKit.Pixels(golden);
        var off = 0;
        var maxDelta = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var d = Math.Abs(a[i] - b[i]);
            maxDelta = Math.Max(maxDelta, d);
            if (d > 3) off++;
        }
        Assert.True(off <= a.Length * 0.005, $"{name}: {off} channel values differ by >3 (max {maxDelta}) — a visual change; regenerate and review if intended");
    }
}
