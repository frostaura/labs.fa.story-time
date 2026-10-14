using StoryTime.Contracts;
using StoryTime.Poster.Core;

namespace StoryTime.Poster.Spec;

/// <summary>Deterministic spec-from-story derivation — the poster path that needs no model
/// at all (TemplateStoryEngine, and the fallback whenever Foundation Models is unavailable
/// or returns nothing usable). Reads the outline's title, settings, characters and moral,
/// scores the twelve scenes by keyword evidence, and samples among the strongest so two
/// fox stories do not wear the same poster. Same outline → same spec, always.</summary>
public static class PosterSpecDeriver
{
    private sealed record Cue(string Word, (PosterScene Scene, double Weight)[] Scenes);

    /// <summary>Keyword → scene evidence. Primary weight 3, related scenes lower, so
    /// relevance wins but variety survives.</summary>
    private static readonly Cue[] SceneCues =
    [
        new("lighthouse", [(PosterScene.MoonlitSea, 4), (PosterScene.IslandCove, 1.5)]),
        new("harbor", [(PosterScene.MoonlitSea, 3), (PosterScene.TownRooftops, 1)]),
        new("harbour", [(PosterScene.MoonlitSea, 3), (PosterScene.TownRooftops, 1)]),
        new("ocean", [(PosterScene.MoonlitSea, 3), (PosterScene.IslandCove, 2), (PosterScene.ReefAtDusk, 1.5)]),
        new("sea", [(PosterScene.MoonlitSea, 3), (PosterScene.IslandCove, 1.5), (PosterScene.ReefAtDusk, 1)]),
        new("shore", [(PosterScene.MoonlitSea, 3), (PosterScene.IslandCove, 2)]),
        new("wave", [(PosterScene.MoonlitSea, 2), (PosterScene.IslandCove, 1.5)]),
        new("tide", [(PosterScene.IslandCove, 3), (PosterScene.MoonlitSea, 2), (PosterScene.ReefAtDusk, 1)]),
        new("shell", [(PosterScene.IslandCove, 2.5), (PosterScene.ReefAtDusk, 1.5)]),
        new("island", [(PosterScene.IslandCove, 4)]),
        new("cove", [(PosterScene.IslandCove, 3)]),
        new("beach", [(PosterScene.IslandCove, 3), (PosterScene.MoonlitSea, 1)]),
        new("boat", [(PosterScene.MoonlitSea, 2), (PosterScene.IslandCove, 2), (PosterScene.MountainLake, 1.5)]),
        new("sail", [(PosterScene.MoonlitSea, 2), (PosterScene.IslandCove, 2)]),
        new("reef", [(PosterScene.ReefAtDusk, 4)]),
        new("coral", [(PosterScene.ReefAtDusk, 4)]),
        new("underwater", [(PosterScene.ReefAtDusk, 4)]),
        new("otter", [(PosterScene.MoonlitSea, 1.5), (PosterScene.MountainLake, 1.5), (PosterScene.IslandCove, 1)]),
        new("fish", [(PosterScene.ReefAtDusk, 2)]),
        new("turtle", [(PosterScene.ReefAtDusk, 2.5), (PosterScene.IslandCove, 1.5)]),
        new("whale", [(PosterScene.MoonlitSea, 2), (PosterScene.ReefAtDusk, 2)]),
        new("forest", [(PosterScene.ForestClearing, 3), (PosterScene.MountainLake, 1), (PosterScene.JungleCanopy, 0.5)]),
        new("wood", [(PosterScene.ForestClearing, 2.5)]),
        new("glade", [(PosterScene.ForestClearing, 3)]),
        new("pine", [(PosterScene.ForestClearing, 2.5), (PosterScene.MountainLake, 1.5), (PosterScene.SnowyVillage, 0.5)]),
        new("cedar", [(PosterScene.ForestClearing, 2.5), (PosterScene.MountainLake, 1)]),
        new("fern", [(PosterScene.ForestClearing, 2.5), (PosterScene.JungleCanopy, 1)]),
        new("hollow", [(PosterScene.ForestClearing, 2)]),
        new("den", [(PosterScene.ForestClearing, 2), (PosterScene.FireflyMeadow, 1)]),
        new("moss", [(PosterScene.ForestClearing, 2)]),
        new("acorn", [(PosterScene.ForestClearing, 2), (PosterScene.FireflyMeadow, 1)]),
        new("cabin", [(PosterScene.ForestClearing, 2.5), (PosterScene.MountainLake, 1.5), (PosterScene.SnowyVillage, 1)]),
        new("bridge", [(PosterScene.ForestClearing, 1.5), (PosterScene.MountainLake, 1.5)]),
        new("mountain", [(PosterScene.MountainLake, 4), (PosterScene.SnowyVillage, 1)]),
        new("lake", [(PosterScene.MountainLake, 4)]),
        new("pond", [(PosterScene.MountainLake, 2.5), (PosterScene.FireflyMeadow, 1)]),
        new("river", [(PosterScene.MountainLake, 2.5), (PosterScene.ForestClearing, 1)]),
        new("valley", [(PosterScene.MountainLake, 2), (PosterScene.FireflyMeadow, 2)]),
        new("meadow", [(PosterScene.FireflyMeadow, 3.5), (PosterScene.StarryHills, 1)]),
        new("garden", [(PosterScene.FireflyMeadow, 3), (PosterScene.TownRooftops, 0.5)]),
        new("flower", [(PosterScene.FireflyMeadow, 2.5)]),
        new("willow", [(PosterScene.FireflyMeadow, 2), (PosterScene.MountainLake, 1)]),
        new("wish", [(PosterScene.FireflyMeadow, 1.5), (PosterScene.StarryHills, 1.5)]),
        new("firefl", [(PosterScene.FireflyMeadow, 3), (PosterScene.ForestClearing, 1)]),
        new("field", [(PosterScene.FireflyMeadow, 2)]),
        new("snow", [(PosterScene.SnowyVillage, 4), (PosterScene.MountainLake, 1)]),
        new("winter", [(PosterScene.SnowyVillage, 4)]),
        new("frost", [(PosterScene.SnowyVillage, 3)]),
        new("sled", [(PosterScene.SnowyVillage, 3)]),
        new("village", [(PosterScene.SnowyVillage, 2.5), (PosterScene.TownRooftops, 1.5)]),
        new("desert", [(PosterScene.DesertOasis, 4)]),
        new("dune", [(PosterScene.DesertOasis, 4)]),
        new("sand", [(PosterScene.DesertOasis, 2.5), (PosterScene.IslandCove, 1.5)]),
        new("oasis", [(PosterScene.DesertOasis, 4)]),
        new("camel", [(PosterScene.DesertOasis, 4)]),
        new("star", [(PosterScene.StarryHills, 3), (PosterScene.DesertOasis, 1), (PosterScene.MountainLake, 0.5)]),
        new("constellation", [(PosterScene.StarryHills, 3.5)]),
        new("comet", [(PosterScene.StarryHills, 3), (PosterScene.DesertOasis, 1)]),
        new("space", [(PosterScene.StarryHills, 3), (PosterScene.CloudKingdom, 1)]),
        new("moon", [(PosterScene.StarryHills, 1.5), (PosterScene.CloudKingdom, 1), (PosterScene.MoonlitSea, 0.5)]),
        new("telescope", [(PosterScene.StarryHills, 3), (PosterScene.TownRooftops, 1)]),
        new("hill", [(PosterScene.StarryHills, 2.5), (PosterScene.FireflyMeadow, 1)]),
        new("sky", [(PosterScene.CloudKingdom, 2), (PosterScene.StarryHills, 1.5)]),
        new("cloud", [(PosterScene.CloudKingdom, 4)]),
        new("feather", [(PosterScene.CloudKingdom, 2)]),
        new("wind", [(PosterScene.CloudKingdom, 1.5), (PosterScene.FireflyMeadow, 1)]),
        new("balloon", [(PosterScene.CloudKingdom, 3)]),
        new("castle", [(PosterScene.CloudKingdom, 2.5), (PosterScene.StarryHills, 1)]),
        new("dream", [(PosterScene.CloudKingdom, 1.5)]),
        new("town", [(PosterScene.TownRooftops, 3.5)]),
        new("roof", [(PosterScene.TownRooftops, 4)]),
        new("chimney", [(PosterScene.TownRooftops, 3), (PosterScene.SnowyVillage, 1)]),
        new("city", [(PosterScene.TownRooftops, 3)]),
        new("street", [(PosterScene.TownRooftops, 2.5)]),
        new("station", [(PosterScene.TownRooftops, 2.5), (PosterScene.StarryHills, 1)]),
        new("junction", [(PosterScene.TownRooftops, 2), (PosterScene.FireflyMeadow, 1), (PosterScene.StarryHills, 1)]),
        new("platform", [(PosterScene.TownRooftops, 2)]),
        new("train", [(PosterScene.StarryHills, 1.5), (PosterScene.FireflyMeadow, 1.5), (PosterScene.MountainLake, 1), (PosterScene.TownRooftops, 1)]),
        new("workshop", [(PosterScene.TownRooftops, 2.5)]),
        new("window", [(PosterScene.TownRooftops, 1.5), (PosterScene.FireflyMeadow, 1)]),
        new("lantern", [(PosterScene.TownRooftops, 1), (PosterScene.FireflyMeadow, 1), (PosterScene.ForestClearing, 1)]),
        new("jungle", [(PosterScene.JungleCanopy, 4)]),
        new("rainforest", [(PosterScene.JungleCanopy, 4)]),
        new("canopy", [(PosterScene.JungleCanopy, 3)]),
        new("vine", [(PosterScene.JungleCanopy, 3)]),
        new("parrot", [(PosterScene.JungleCanopy, 3)]),
        new("monkey", [(PosterScene.JungleCanopy, 3)]),
        new("sloth", [(PosterScene.JungleCanopy, 3)]),
        new("tropical", [(PosterScene.JungleCanopy, 2), (PosterScene.IslandCove, 2)]),
        new("dinosaur", [(PosterScene.JungleCanopy, 2), (PosterScene.FireflyMeadow, 1)]),
        new("humming", [(PosterScene.FireflyMeadow, 1.5), (PosterScene.MountainLake, 1)]),
        new("lullaby", [(PosterScene.TownRooftops, 1.5), (PosterScene.FireflyMeadow, 1)]),
    ];

    /// <summary>Keyword → featured element (the outline's characters and things).</summary>
    private static readonly (string Word, PosterFeature Feature)[] FeatureCues =
    [
        ("fox", PosterFeature.Fox), ("rabbit", PosterFeature.Rabbit), ("bunny", PosterFeature.Rabbit),
        ("hare", PosterFeature.Rabbit), ("owl", PosterFeature.Owl), ("deer", PosterFeature.Deer),
        ("fawn", PosterFeature.Deer), ("bear", PosterFeature.Bear), ("cat", PosterFeature.Cat),
        ("kitten", PosterFeature.Cat), ("hedgehog", PosterFeature.Hedgehog), ("otter", PosterFeature.Otter),
        ("whale", PosterFeature.Whale), ("turtle", PosterFeature.Turtle), ("fish", PosterFeature.Fish),
        ("camel", PosterFeature.Camel), ("dinosaur", PosterFeature.Dinosaur), ("songbird", PosterFeature.Birds),
        ("bird", PosterFeature.Birds), ("firefl", PosterFeature.Fireflies), ("lighthouse", PosterFeature.Lighthouse),
        ("cabin", PosterFeature.Cabin), ("cottage", PosterFeature.Cabin), ("village", PosterFeature.Village),
        ("castle", PosterFeature.Castle), ("windmill", PosterFeature.Windmill), ("tent", PosterFeature.Tent),
        ("lantern", PosterFeature.Lantern), ("sailboat", PosterFeature.Sailboat), ("boat", PosterFeature.Boat),
        ("balloon", PosterFeature.Balloon), ("train", PosterFeature.Train), ("telescope", PosterFeature.Telescope),
        ("flower", PosterFeature.Flowers), ("mushroom", PosterFeature.Mushrooms), ("comet", PosterFeature.ShootingStar),
        ("wish", PosterFeature.ShootingStar), ("aurora", PosterFeature.Aurora), ("cloud", PosterFeature.Clouds),
        ("snow", PosterFeature.Snowfall), ("constellation", PosterFeature.MilkyWay), ("moon moth", PosterFeature.MilkyWay),
    ];

    /// <summary>Scene-appropriate extras the derivation may add (seeded) when the story
    /// itself names fewer than two featured elements.</summary>
    private static readonly Dictionary<PosterScene, PosterFeature[]> SceneExtras = new()
    {
        [PosterScene.MoonlitSea] = [PosterFeature.Lighthouse, PosterFeature.Sailboat, PosterFeature.Boat, PosterFeature.Whale, PosterFeature.Clouds, PosterFeature.Birds],
        [PosterScene.ForestClearing] = [PosterFeature.Cabin, PosterFeature.Fireflies, PosterFeature.Mushrooms, PosterFeature.Owl, PosterFeature.Deer, PosterFeature.Tent],
        [PosterScene.MountainLake] = [PosterFeature.Boat, PosterFeature.Cabin, PosterFeature.Deer, PosterFeature.Fireflies, PosterFeature.Aurora, PosterFeature.Tent],
        [PosterScene.FireflyMeadow] = [PosterFeature.Fireflies, PosterFeature.Rabbit, PosterFeature.Flowers, PosterFeature.Windmill, PosterFeature.Fox, PosterFeature.Cabin],
        [PosterScene.SnowyVillage] = [PosterFeature.Snowfall, PosterFeature.Village, PosterFeature.Fox, PosterFeature.Owl, PosterFeature.Aurora, PosterFeature.Cat],
        [PosterScene.DesertOasis] = [PosterFeature.Camel, PosterFeature.Tent, PosterFeature.Lantern, PosterFeature.Telescope, PosterFeature.Fireflies],
        [PosterScene.IslandCove] = [PosterFeature.Boat, PosterFeature.Sailboat, PosterFeature.Turtle, PosterFeature.Lighthouse, PosterFeature.Lantern],
        [PosterScene.StarryHills] = [PosterFeature.Telescope, PosterFeature.Tent, PosterFeature.Windmill, PosterFeature.Rabbit, PosterFeature.Fox, PosterFeature.Balloon],
        [PosterScene.ReefAtDusk] = [PosterFeature.Turtle, PosterFeature.Fish, PosterFeature.Whale],
        [PosterScene.CloudKingdom] = [PosterFeature.Castle, PosterFeature.Balloon, PosterFeature.Birds, PosterFeature.Aurora],
        [PosterScene.TownRooftops] = [PosterFeature.Cat, PosterFeature.Telescope, PosterFeature.Balloon, PosterFeature.Castle],
        [PosterScene.JungleCanopy] = [PosterFeature.Fireflies, PosterFeature.Owl, PosterFeature.Mushrooms, PosterFeature.Dinosaur, PosterFeature.Flowers],
    };

    private static readonly Dictionary<PosterScene, PosterPalette[]> ScenePalettes = new()
    {
        [PosterScene.MoonlitSea] = [PosterPalette.NightHarbor, PosterPalette.OceanDeep, PosterPalette.StarfieldViolet, PosterPalette.MistValley],
        [PosterScene.ForestClearing] = [PosterPalette.PineForest, PosterPalette.MoonMeadow, PosterPalette.MistValley, PosterPalette.PlumDusk],
        [PosterScene.MountainLake] = [PosterPalette.MistValley, PosterPalette.OceanDeep, PosterPalette.PlumDusk, PosterPalette.PineForest],
        [PosterScene.FireflyMeadow] = [PosterPalette.MoonMeadow, PosterPalette.PlumDusk, PosterPalette.TidePool, PosterPalette.LanternGlow],
        [PosterScene.SnowyVillage] = [PosterPalette.SnowHush, PosterPalette.NightHarbor, PosterPalette.StarfieldViolet],
        [PosterScene.DesertOasis] = [PosterPalette.EmberSky, PosterPalette.PlumDusk, PosterPalette.LanternGlow, PosterPalette.StarfieldViolet],
        [PosterScene.IslandCove] = [PosterPalette.TidePool, PosterPalette.OceanDeep, PosterPalette.NightHarbor],
        [PosterScene.StarryHills] = [PosterPalette.StarfieldViolet, PosterPalette.PlumDusk, PosterPalette.NightHarbor, PosterPalette.MoonMeadow],
        [PosterScene.ReefAtDusk] = [PosterPalette.OceanDeep, PosterPalette.TidePool, PosterPalette.StarfieldViolet],
        [PosterScene.CloudKingdom] = [PosterPalette.CloudDrift, PosterPalette.PlumDusk, PosterPalette.StarfieldViolet],
        [PosterScene.TownRooftops] = [PosterPalette.LanternGlow, PosterPalette.NightHarbor, PosterPalette.PlumDusk, PosterPalette.EmberSky],
        [PosterScene.JungleCanopy] = [PosterPalette.PineForest, PosterPalette.TidePool, PosterPalette.MoonMeadow],
    };

    /// <summary>Derives a normalized spec from story metadata. <paramref name="seed"/> is
    /// the outline seed; the spec seed is derived from it so poster variation is stable.</summary>
    public static PosterSpec Derive(StoryOutline outline) =>
        Derive(outline.Title, outline.Settings, outline.Characters.Select(c => c.Kind), outline.Moral, outline.Seed);

    public static PosterSpec Derive(string title, IEnumerable<string> settings, IEnumerable<string> characterKinds,
        string moral, int seed)
    {
        var rng = new Rng(seed).Fork("poster-derive");
        var settingText = string.Join(" | ", settings).ToLowerInvariant();
        var kindsText = string.Join(" | ", characterKinds).ToLowerInvariant();
        var titleText = (title ?? string.Empty).ToLowerInvariant();
        var moralText = (moral ?? string.Empty).ToLowerInvariant();
        var all = $"{titleText} | {settingText} | {kindsText} | {moralText}";

        // ---- Featured elements named by the story ----
        var named = new List<PosterFeature>();
        foreach (var (word, feature) in FeatureCues)
        {
            if ((Has(kindsText, word) || Has(titleText, word) || Has(settingText, word)) && !named.Contains(feature))
                named.Add(feature);
        }
        // Creatures first: the hero of the story is the poster's focal subject.
        named = named.OrderBy(f => PosterSpecRules.IsCreature(f) ? 0 : 1).ToList();
        var hero = named.FirstOrDefault(PosterSpecRules.IsCreature, (PosterFeature)(-1));

        // ---- Scene: keyword evidence (settings weigh most), hero compatibility, seeded pick ----
        var scores = Enum.GetValues<PosterScene>().ToDictionary(s => s, _ => 0.0);
        foreach (var cue in SceneCues)
        {
            var w = (Has(settingText, cue.Word) ? 1.0 : 0) + (Has(titleText, cue.Word) ? 0.6 : 0)
                    + (Has(kindsText, cue.Word) ? 0.5 : 0) + (Has(moralText, cue.Word) ? 0.25 : 0);
            if (w <= 0) continue;
            foreach (var (cueScene, weight) in cue.Scenes) scores[cueScene] += weight * w;
        }
        if (Enum.IsDefined(hero))
            foreach (var s in PosterSpecRules.Compatibility[hero]) scores[s] += 2.0;

        var max = scores.Values.Max();
        PosterScene scene;
        if (max <= 0)
        {
            // No evidence at all: a gentle default set, seeded.
            scene = rng.Pick([PosterScene.FireflyMeadow, PosterScene.StarryHills, PosterScene.ForestClearing,
                PosterScene.MoonlitSea, PosterScene.MountainLake, PosterScene.CloudKingdom]);
        }
        else
        {
            // Sample among scenes within 60% of the best, weighted by score² — relevance
            // dominates, close runners-up still get their nights.
            var pool = scores.Where(kv => kv.Value >= max * 0.6).OrderBy(kv => kv.Key).ToList();
            var total = pool.Sum(kv => kv.Value * kv.Value);
            var pick = rng.Next() * total;
            scene = pool[^1].Key;
            foreach (var kv in pool)
            {
                pick -= kv.Value * kv.Value;
                if (pick <= 0) { scene = kv.Key; break; }
            }
        }

        // ---- Features: the story's own first, then seeded scene extras (1–3 total) ----
        var features = named.Where(f => PosterSpecRules.IsCompatible(scene, f)).ToList();
        var target = 1 + rng.Int(3); // 1..3
        var extras = SceneExtras[scene].ToList();
        rng.Shuffle(extras);
        foreach (var extra in extras)
        {
            if (features.Count >= target) break;
            if (features.Contains(extra)) continue;
            if (PosterSpecRules.IsCreature(extra) && features.Any(PosterSpecRules.IsCreature)) continue;
            features.Add(extra);
        }
        // Shooting stars are an occasional delight, never a fixture (founder, 2026-07-31).
        if (features.Contains(PosterFeature.ShootingStar) && !rng.Chance(0.35))
            features.Remove(PosterFeature.ShootingStar);

        // ---- Time of day: keyword cues, else a seeded bedtime distribution ----
        PosterTimeOfDay time;
        if (ContainsAny(all, "sunset", "dusk", "evening", "golden")) time = PosterTimeOfDay.Dusk;
        else if (ContainsAny(all, "constellation", "comet", "galaxy", "milky")) time = PosterTimeOfDay.Starlit;
        else
        {
            var r = rng.Next();
            time = r < 0.38 ? PosterTimeOfDay.Moonlit
                 : r < 0.62 ? PosterTimeOfDay.Starlit
                 : r < 0.84 ? PosterTimeOfDay.Twilight
                 : PosterTimeOfDay.Dusk;
        }

        // ---- Mood: the moral's register, else seeded among the calm three ----
        PosterMood mood;
        if (ContainsAny(all, "magic", "wish", "sparkl", "glimmer", "sprite", "enchant")) mood = PosterMood.Magical;
        else if (ContainsAny(all, "home", "warm", "tea", "blanket", "family", "together", "cozy", "cosy")) mood = PosterMood.Cozy;
        else if (ContainsAny(all, "dream", "drowsy", "sleep", "hush", "lullaby", "quiet")) mood = rng.Chance(0.5) ? PosterMood.Dreamy : PosterMood.Sleepy;
        else if (ContainsAny(all, "find", "explor", "discover", "puzzle", "curious", "brave", "journey")) mood = PosterMood.Curious;
        else mood = rng.Pick([PosterMood.Calm, PosterMood.Cozy, PosterMood.Dreamy]);

        // ---- Palette: a scene-appropriate family, nudged by warm/cool words ----
        var palettes = ScenePalettes[scene];
        var palette = palettes[rng.Int(palettes.Length)];
        if (ContainsAny(all, "ember", "copper", "honey", "lamplight") && scene is PosterScene.TownRooftops or PosterScene.DesertOasis)
            palette = rng.Chance(0.5) ? PosterPalette.LanternGlow : PosterPalette.EmberSky;

        return PosterSpecRules.Normalize(new PosterSpec
        {
            Scene = scene,
            TimeOfDay = time,
            Mood = mood,
            Palette = palette,
            Features = features,
            Seed = (int)(Hash.Combine((ulong)(uint)seed, 104729) & 0x7FFFFFFF),
        });
    }

    private static bool ContainsAny(string text, params string[] words) => words.Any(w => Has(text, w));

    /// <summary>Word-aware match: a cue matches tokens that START with it ("star" → "stars",
    /// "starlit"), short cues (≤3 letters) only whole words or plurals ("den" never matches
    /// "garden", "sea" never "season"), and multi-word cues as phrases.</summary>
    internal static bool Has(string text, string cue)
    {
        if (cue.Contains(' ')) return text.Contains(cue, StringComparison.Ordinal);
        var start = 0;
        while (start < text.Length)
        {
            while (start < text.Length && !char.IsLetter(text[start])) start++;
            var end = start;
            while (end < text.Length && char.IsLetter(text[end])) end++;
            if (end > start)
            {
                var token = text.AsSpan(start, end - start);
                if (cue.Length <= 3
                        ? token.SequenceEqual(cue) || (token.StartsWith(cue) && (token[cue.Length..].SequenceEqual("s") || token[cue.Length..].SequenceEqual("es")))
                        : token.StartsWith(cue))
                    return true;
            }
            start = end;
        }
        return false;
    }
}
