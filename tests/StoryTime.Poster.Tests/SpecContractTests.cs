using StoryTime.Contracts;
using StoryTime.Poster.Core;
using StoryTime.Poster.Spec;
using Xunit;

namespace StoryTime.Poster.Tests;

/// <summary>The enum-only spec contract: lenient reading, idempotent repair, scene
/// compatibility, legacy upgrade, deterministic derivation from story metadata.</summary>
public class SpecContractTests
{
    [Fact]
    public void Normalize_is_idempotent_and_always_valid_for_garbage_input()
    {
        var r = new Rng(77);
        for (var i = 0; i < 3000; i++)
        {
            var spec = new PosterSpec
            {
                Scene = (PosterScene)r.Int(-3, 20),
                TimeOfDay = (PosterTimeOfDay)r.Int(-2, 8),
                Mood = (PosterMood)r.Int(-2, 10),
                Palette = (PosterPalette)r.Int(-2, 16),
                Features = Enumerable.Range(0, r.Int(0, 9)).Select(_ => (PosterFeature)r.Int(-3, 45)).ToList(),
                Seed = (int)r.NextULong(),
            };
            var n = PosterSpecRules.Normalize(spec);
            Assert.True(Enum.IsDefined(n.Scene));
            Assert.True(Enum.IsDefined(n.TimeOfDay));
            Assert.True(Enum.IsDefined(n.Mood));
            Assert.True(Enum.IsDefined(n.Palette));
            Assert.InRange(n.Features.Count, 0, PosterSpecRules.MaxFeatures);
            Assert.Equal(n.Features.Count, n.Features.Distinct().Count());
            Assert.All(n.Features, f => Assert.True(PosterSpecRules.IsCompatible(n.Scene, f)));
            Assert.True(n.Features.Count(PosterSpecRules.IsCreature) <= 1);
            Assert.True(PosterSpecRules.SpecEquals(n, PosterSpecRules.Normalize(n)));
            Assert.Equal(spec.Seed, n.Seed);
        }
        Assert.NotNull(PosterSpecRules.Normalize(null));
    }

    [Fact]
    public void Incompatible_features_are_dropped_not_relocated()
    {
        var n = PosterSpecRules.Normalize(new PosterSpec
        {
            Scene = PosterScene.DesertOasis,
            Features = [PosterFeature.Whale, PosterFeature.Camel, PosterFeature.Lighthouse, PosterFeature.Fox, PosterFeature.Tent],
        });
        Assert.Equal(PosterScene.DesertOasis, n.Scene);
        Assert.Equal([PosterFeature.Camel, PosterFeature.Tent], n.Features);
    }

    [Fact]
    public void Light_repairs_follow_the_sky()
    {
        var milky = PosterSpecRules.Normalize(new PosterSpec { Scene = PosterScene.StarryHills, TimeOfDay = PosterTimeOfDay.Moonlit, Features = [PosterFeature.MilkyWay] });
        Assert.Equal(PosterTimeOfDay.Starlit, milky.TimeOfDay);
        var reef = PosterSpecRules.Normalize(new PosterSpec { Scene = PosterScene.ReefAtDusk, TimeOfDay = PosterTimeOfDay.Starlit });
        Assert.Equal(PosterTimeOfDay.Twilight, reef.TimeOfDay);
    }

    [Fact]
    public void Json_is_by_name_and_lenient()
    {
        var spec = PosterSpecRules.Normalize(new PosterSpec
        {
            Scene = PosterScene.MoonlitSea, TimeOfDay = PosterTimeOfDay.Dusk, Mood = PosterMood.Cozy,
            Palette = PosterPalette.OceanDeep, Features = [PosterFeature.Lighthouse, PosterFeature.Boat], Seed = 12,
        });
        var json = PosterSpecJson.Serialize(spec);
        Assert.Contains("\"Scene\":\"MoonlitSea\"", json);
        Assert.Contains("\"Features\":[\"Lighthouse\",\"Boat\"]", json);
        Assert.True(PosterSpecRules.SpecEquals(spec, PosterSpecJson.Parse(json)!));

        // A model's stray values degrade, never throw: unknown names are repaired/dropped,
        // case and integers are accepted.
        const string messy = "{\"Scene\":\"moonlitsea\",\"TimeOfDay\":\"Noon\",\"Mood\":2,\"Palette\":\"Chartreuse\"," +
                             "\"Features\":[\"Lighthouse\",\"Dragon\",7,{\"x\":1},\"boat\"],\"Seed\":5}";
        var parsed = PosterSpecJson.Parse(messy)!;
        Assert.Equal(PosterScene.MoonlitSea, parsed.Scene);
        Assert.Equal(PosterTimeOfDay.Moonlit, parsed.TimeOfDay);
        Assert.Equal(PosterMood.Dreamy, parsed.Mood);
        Assert.Equal(PosterSpecRules.DefaultPalette(PosterScene.MoonlitSea), parsed.Palette);
        Assert.Equal([PosterFeature.Lighthouse, PosterFeature.Boat], parsed.Features);

        Assert.Null(PosterSpecJson.Parse("not json"));
        Assert.Null(PosterSpecJson.Parse(""));
    }

    [Fact]
    public void Legacy_formation_specs_upgrade_with_their_palette_seed_and_motifs()
    {
        // v2: numeric palette, free-string mood, positioned shapes (by name and by number).
        const string v2 = "{\"Palette\":0,\"Mood\":\"calm\",\"Seed\":31337,\"Layers\":[" +
                          "{\"Depth\":0,\"Elements\":[{\"Shape\":\"Moon\",\"X\":0.3}]}," +
                          "{\"Depth\":2,\"Elements\":[{\"Shape\":7},{\"Shape\":\"Boat\"}]}," +
                          "{\"Depth\":3,\"Elements\":[{\"Shape\":\"FoxSilhouette\"}]}]}";
        var spec = PosterSpecJson.Parse(v2)!;
        Assert.Equal(PosterScene.MoonlitSea, spec.Scene);
        Assert.Equal(PosterPalette.NightHarbor, spec.Palette);
        Assert.Equal(PosterTimeOfDay.Moonlit, spec.TimeOfDay);
        Assert.Equal(31337, spec.Seed);
        Assert.Contains(PosterFeature.Lighthouse, spec.Features);
        Assert.Contains(PosterFeature.Boat, spec.Features);
        Assert.Contains(PosterFeature.Fox, spec.Features);
    }

    [Fact]
    public void Derivation_is_deterministic_and_reads_the_story()
    {
        PosterSpec D(string title, string setting, string kind, int seed = 3) =>
            PosterSpecDeriver.Derive(title, [setting], [kind], "kindness helps", seed);

        Assert.True(PosterSpecRules.SpecEquals(D("The Lantern Fox", "the fern hollow", "fox"), D("The Lantern Fox", "the fern hollow", "fox")));

        var sea = D("The Lighthouse Keeper's Song", "the silver shore", "otter");
        Assert.Contains(sea.Scene, new[] { PosterScene.MoonlitSea, PosterScene.IslandCove, PosterScene.MountainLake });
        Assert.Equal(PosterScene.SnowyVillage, D("A Winter Hush", "the snowy village", "owl").Scene);
        Assert.Equal(PosterScene.DesertOasis, D("Sand and Stars", "the desert oasis", "camel").Scene);
        Assert.Contains(PosterFeature.Fox, D("The Lantern Fox", "the fern hollow", "fox").Features);
        // Short cues are whole words: "garden" must not read as a fox "den".
        Assert.False(PosterSpecDeriver.Has("the walled garden", "den"));
        Assert.True(PosterSpecDeriver.Has("two foxes", "fox"));

        // Every derived spec is already normal.
        for (var seed = 0; seed < 200; seed++)
        {
            var s = D("Story " + seed, seed % 2 == 0 ? "the pine glade" : "the cloud meadow", seed % 3 == 0 ? "hedgehog" : "cloud lamb", seed);
            Assert.True(PosterSpecRules.SpecEquals(s, PosterSpecRules.Normalize(s)));
        }
    }

    [Fact]
    public void Every_feature_has_a_home_and_every_scene_has_features()
    {
        foreach (var f in Enum.GetValues<PosterFeature>())
            Assert.NotEmpty(PosterSpecRules.Compatibility[f]);
        foreach (var s in TestKit.Scenes)
            Assert.True(Enum.GetValues<PosterFeature>().Count(f => PosterSpecRules.IsCompatible(s, f)) >= 3, s.ToString());
    }

    [Fact]
    public void Contract_doc_lists_every_enum_value()
    {
        var doc = File.ReadAllText(Path.Combine(TestKit.SourceDir(), "../../docs/design/poster-engine.md"));
        foreach (var name in Enum.GetNames<PosterScene>().Concat(Enum.GetNames<PosterFeature>())
                     .Concat(Enum.GetNames<PosterTimeOfDay>()).Concat(Enum.GetNames<PosterMood>()).Concat(Enum.GetNames<PosterPalette>()))
            Assert.Contains($"`{name}`", doc);
    }
}
