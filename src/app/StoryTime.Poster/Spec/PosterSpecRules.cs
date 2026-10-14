using StoryTime.Contracts;

namespace StoryTime.Poster.Spec;

/// <summary>The spec contract's repair rules. Deterministic and idempotent
/// (Normalize(Normalize(s)) == Normalize(s)); anything a model can emit — undefined enum
/// values, duplicates, a whale in a desert, eight features — becomes a valid spec instead
/// of an exception. Repairs are conservative: a feature that cannot live in the scene is
/// DROPPED (never relocated into a scene the story did not ask for).</summary>
public static class PosterSpecRules
{
    public const int MaxFeatures = 3;

    private static readonly PosterScene[] AllScenes = Enum.GetValues<PosterScene>();

    private static PosterScene[] Except(params PosterScene[] excluded) => AllScenes.Except(excluded).ToArray();

    private static readonly PosterScene[] Land =
    [
        PosterScene.ForestClearing, PosterScene.MountainLake, PosterScene.FireflyMeadow, PosterScene.SnowyVillage,
        PosterScene.DesertOasis, PosterScene.StarryHills, PosterScene.TownRooftops, PosterScene.JungleCanopy,
    ];

    /// <summary>Feature → the scenes it may appear in. Published verbatim in
    /// docs/design/poster-engine.md ("Spec contract for the story engine").</summary>
    public static readonly IReadOnlyDictionary<PosterFeature, PosterScene[]> Compatibility =
        new Dictionary<PosterFeature, PosterScene[]>
        {
            [PosterFeature.Lighthouse] = [PosterScene.MoonlitSea, PosterScene.IslandCove],
            [PosterFeature.Cabin] = [PosterScene.ForestClearing, PosterScene.MountainLake, PosterScene.SnowyVillage, PosterScene.FireflyMeadow, PosterScene.StarryHills],
            [PosterFeature.Village] = [PosterScene.SnowyVillage, PosterScene.TownRooftops, PosterScene.FireflyMeadow, PosterScene.MountainLake, PosterScene.StarryHills, PosterScene.MoonlitSea],
            [PosterFeature.Castle] = [PosterScene.CloudKingdom, PosterScene.StarryHills, PosterScene.MountainLake, PosterScene.FireflyMeadow, PosterScene.SnowyVillage, PosterScene.TownRooftops],
            [PosterFeature.Windmill] = [PosterScene.FireflyMeadow, PosterScene.StarryHills],
            [PosterFeature.Tent] = [PosterScene.DesertOasis, PosterScene.StarryHills, PosterScene.ForestClearing, PosterScene.MountainLake, PosterScene.FireflyMeadow],
            [PosterFeature.Lantern] = [.. Land, PosterScene.IslandCove, PosterScene.MoonlitSea],
            [PosterFeature.Boat] = [PosterScene.MoonlitSea, PosterScene.MountainLake, PosterScene.IslandCove],
            [PosterFeature.Sailboat] = [PosterScene.MoonlitSea, PosterScene.IslandCove],
            [PosterFeature.Balloon] = [PosterScene.CloudKingdom, PosterScene.StarryHills, PosterScene.FireflyMeadow, PosterScene.MountainLake, PosterScene.DesertOasis, PosterScene.TownRooftops],
            [PosterFeature.Train] = [PosterScene.FireflyMeadow, PosterScene.StarryHills, PosterScene.MountainLake, PosterScene.SnowyVillage],
            [PosterFeature.Telescope] = [PosterScene.StarryHills, PosterScene.TownRooftops, PosterScene.DesertOasis],
            [PosterFeature.Fox] = [PosterScene.ForestClearing, PosterScene.FireflyMeadow, PosterScene.SnowyVillage, PosterScene.StarryHills, PosterScene.MountainLake, PosterScene.MoonlitSea],
            [PosterFeature.Rabbit] = [PosterScene.FireflyMeadow, PosterScene.ForestClearing, PosterScene.StarryHills, PosterScene.SnowyVillage],
            [PosterFeature.Owl] = [PosterScene.ForestClearing, PosterScene.JungleCanopy, PosterScene.SnowyVillage, PosterScene.FireflyMeadow, PosterScene.MountainLake],
            [PosterFeature.Deer] = [PosterScene.ForestClearing, PosterScene.MountainLake, PosterScene.FireflyMeadow, PosterScene.SnowyVillage],
            [PosterFeature.Bear] = [PosterScene.ForestClearing, PosterScene.MountainLake, PosterScene.StarryHills],
            [PosterFeature.Cat] = [PosterScene.TownRooftops, PosterScene.SnowyVillage],
            [PosterFeature.Hedgehog] = [PosterScene.ForestClearing, PosterScene.FireflyMeadow],
            [PosterFeature.Otter] = [PosterScene.MoonlitSea, PosterScene.MountainLake, PosterScene.IslandCove],
            [PosterFeature.Whale] = [PosterScene.MoonlitSea, PosterScene.IslandCove, PosterScene.ReefAtDusk],
            [PosterFeature.Turtle] = [PosterScene.ReefAtDusk, PosterScene.IslandCove],
            [PosterFeature.Fish] = [PosterScene.ReefAtDusk],
            [PosterFeature.Camel] = [PosterScene.DesertOasis],
            [PosterFeature.Dinosaur] = [PosterScene.FireflyMeadow, PosterScene.JungleCanopy, PosterScene.StarryHills, PosterScene.MountainLake],
            [PosterFeature.Birds] = Except(PosterScene.ReefAtDusk),
            [PosterFeature.Fireflies] = [PosterScene.ForestClearing, PosterScene.FireflyMeadow, PosterScene.JungleCanopy, PosterScene.MountainLake, PosterScene.StarryHills, PosterScene.DesertOasis],
            [PosterFeature.Flowers] = [PosterScene.FireflyMeadow, PosterScene.ForestClearing, PosterScene.StarryHills, PosterScene.MountainLake, PosterScene.JungleCanopy],
            [PosterFeature.Mushrooms] = [PosterScene.ForestClearing, PosterScene.FireflyMeadow, PosterScene.JungleCanopy],
            [PosterFeature.ShootingStar] = Except(PosterScene.ReefAtDusk),
            [PosterFeature.Aurora] = [PosterScene.SnowyVillage, PosterScene.MountainLake, PosterScene.StarryHills, PosterScene.MoonlitSea, PosterScene.ForestClearing, PosterScene.CloudKingdom],
            [PosterFeature.MilkyWay] = Except(PosterScene.ReefAtDusk),
            [PosterFeature.Clouds] = Except(PosterScene.ReefAtDusk),
            [PosterFeature.Snowfall] = [PosterScene.SnowyVillage, PosterScene.MountainLake, PosterScene.ForestClearing, PosterScene.TownRooftops],
        };

    /// <summary>Creatures that can be the poster's focal subject (one per poster).</summary>
    public static bool IsCreature(PosterFeature f) => f is PosterFeature.Fox or PosterFeature.Rabbit
        or PosterFeature.Owl or PosterFeature.Deer or PosterFeature.Bear or PosterFeature.Cat
        or PosterFeature.Hedgehog or PosterFeature.Otter or PosterFeature.Whale or PosterFeature.Turtle
        or PosterFeature.Camel or PosterFeature.Dinosaur;

    public static bool IsStructure(PosterFeature f) => f is PosterFeature.Lighthouse or PosterFeature.Cabin
        or PosterFeature.Village or PosterFeature.Castle or PosterFeature.Windmill or PosterFeature.Tent
        or PosterFeature.Lantern or PosterFeature.Telescope;

    public static bool IsVehicle(PosterFeature f) => f is PosterFeature.Boat or PosterFeature.Sailboat
        or PosterFeature.Balloon or PosterFeature.Train;

    public static bool IsCompatible(PosterScene scene, PosterFeature feature) =>
        Compatibility.TryGetValue(feature, out var scenes) && scenes.Contains(scene);

    /// <summary>The scene a legacy or undefined spec falls back to, keyed by palette (the
    /// one field every historic spec carries).</summary>
    public static PosterScene SceneForPalette(PosterPalette palette) => palette switch
    {
        PosterPalette.NightHarbor => PosterScene.MoonlitSea,
        PosterPalette.OceanDeep => PosterScene.MoonlitSea,
        PosterPalette.TidePool => PosterScene.IslandCove,
        PosterPalette.MoonMeadow => PosterScene.FireflyMeadow,
        PosterPalette.PineForest => PosterScene.ForestClearing,
        PosterPalette.SnowHush => PosterScene.SnowyVillage,
        PosterPalette.CloudDrift => PosterScene.CloudKingdom,
        PosterPalette.StarfieldViolet => PosterScene.StarryHills,
        PosterPalette.LanternGlow => PosterScene.TownRooftops,
        PosterPalette.EmberSky => PosterScene.DesertOasis,
        PosterPalette.PlumDusk => PosterScene.StarryHills,
        PosterPalette.MistValley => PosterScene.MountainLake,
        _ => PosterScene.StarryHills,
    };

    /// <summary>The palette a scene wears when the spec's palette is undefined.</summary>
    public static PosterPalette DefaultPalette(PosterScene scene) => scene switch
    {
        PosterScene.MoonlitSea => PosterPalette.NightHarbor,
        PosterScene.ForestClearing => PosterPalette.PineForest,
        PosterScene.MountainLake => PosterPalette.MistValley,
        PosterScene.FireflyMeadow => PosterPalette.MoonMeadow,
        PosterScene.SnowyVillage => PosterPalette.SnowHush,
        PosterScene.DesertOasis => PosterPalette.EmberSky,
        PosterScene.IslandCove => PosterPalette.TidePool,
        PosterScene.StarryHills => PosterPalette.StarfieldViolet,
        PosterScene.ReefAtDusk => PosterPalette.OceanDeep,
        PosterScene.CloudKingdom => PosterPalette.CloudDrift,
        PosterScene.TownRooftops => PosterPalette.LanternGlow,
        PosterScene.JungleCanopy => PosterPalette.PineForest,
        _ => PosterPalette.NightHarbor,
    };

    /// <summary>Repairs any spec into a valid one. Never throws; never returns null.</summary>
    public static PosterSpec Normalize(PosterSpec? spec)
    {
        spec ??= new PosterSpec();
        var palette = Enum.IsDefined(spec.Palette) ? spec.Palette : (PosterPalette)(-1);
        var scene = Enum.IsDefined(spec.Scene)
            ? spec.Scene
            : SceneForPalette(Enum.IsDefined(palette) ? palette : PosterPalette.StarfieldViolet);
        if (!Enum.IsDefined(palette)) palette = DefaultPalette(scene);
        var time = Enum.IsDefined(spec.TimeOfDay) ? spec.TimeOfDay : PosterTimeOfDay.Moonlit;
        var mood = Enum.IsDefined(spec.Mood) ? spec.Mood : PosterMood.Calm;

        var features = new List<PosterFeature>(MaxFeatures);
        var hasCreature = false;
        foreach (var f in spec.Features ?? [])
        {
            if (features.Count == MaxFeatures) break;
            if (!Enum.IsDefined(f) || features.Contains(f) || !IsCompatible(scene, f)) continue;
            if (IsCreature(f))
            {
                if (hasCreature) continue; // one focal creature — a second makes a petting zoo
                hasCreature = true;
            }
            features.Add(f);
        }

        // Light repairs: a Milky Way cannot survive a full moon or dusk, aurora needs dark.
        if (features.Contains(PosterFeature.MilkyWay) && time is PosterTimeOfDay.Moonlit or PosterTimeOfDay.Dusk)
            time = PosterTimeOfDay.Starlit;
        if (features.Contains(PosterFeature.Aurora) && time == PosterTimeOfDay.Dusk)
            time = PosterTimeOfDay.Twilight;
        // Underwater "time" is the light at the surface: dusk and twilight read; deep night
        // underwater would be a black rectangle, so it is pinned to twilight.
        if (scene == PosterScene.ReefAtDusk && time is PosterTimeOfDay.Starlit)
            time = PosterTimeOfDay.Twilight;

        return new PosterSpec
        {
            Version = PosterSpec.CurrentVersion,
            Scene = scene,
            TimeOfDay = time,
            Mood = mood,
            Palette = palette,
            Features = features,
            Seed = spec.Seed,
        };
    }

    /// <summary>Value equality for specs (tests, caches).</summary>
    public static bool SpecEquals(PosterSpec a, PosterSpec b) =>
        a.Version == b.Version && a.Scene == b.Scene && a.TimeOfDay == b.TimeOfDay && a.Mood == b.Mood
        && a.Palette == b.Palette && a.Seed == b.Seed && a.Features.SequenceEqual(b.Features);

    /// <summary>Stable cache key for rendered layers of a normalized spec.</summary>
    public static string CacheKey(PosterSpec spec)
    {
        var n = Normalize(spec);
        return $"v{PosterEngineInfo.ArtVersion}-{(int)n.Scene}-{(int)n.TimeOfDay}-{(int)n.Mood}-{(int)n.Palette}-" +
               $"{string.Join('.', n.Features.Select(f => (int)f))}-{n.Seed}";
    }
}

/// <summary>Engine identity, kept dependency-free so Core can stamp thumbnails with it.</summary>
public static class PosterEngineInfo
{
    /// <summary>Bump when the engine's output changes enough that cached art must re-render.
    /// v9: the procedural parallax engine (spec contract v3) replaced the shape-formation
    /// composer (v8). v10: the picture-book pass — illustrated creatures, layouts, framing,
    /// warm focal lights, broader palettes.</summary>
    public const int ArtVersion = 10;
}
