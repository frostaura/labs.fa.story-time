using System.Text.Json;
using StoryTime.Contracts;

namespace StoryTime.Poster.Spec;

/// <summary>Reads and writes stored poster specs. Reads both the current enum-only contract
/// (v3) and the retired shape-formation contract (v2: Palette + Mood string + Layers of
/// positioned shapes), upgrading the latter so every story already on a device keeps a
/// poster: the palette survives, the scene comes from the palette, and recognisable motifs
/// (lighthouse, fox, boat, …) become featured elements. Never throws.</summary>
public static class PosterSpecJson
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static string Serialize(PosterSpec spec) => JsonSerializer.Serialize(PosterSpecRules.Normalize(spec), Options);

    /// <summary>Parses any stored spec. Returns null only when the text is empty or not JSON.</summary>
    public static PosterSpec? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var root = doc.RootElement;
            var isLegacy = root.TryGetProperty("Layers", out _) && !root.TryGetProperty("Scene", out _);
            return isLegacy ? UpgradeLegacy(root) : PosterSpecRules.Normalize(JsonSerializer.Deserialize<PosterSpec>(json, Options));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly Dictionary<string, PosterFeature> LegacyShapes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Lighthouse"] = PosterFeature.Lighthouse, ["Boat"] = PosterFeature.Boat, ["Sailboat"] = PosterFeature.Sailboat,
        ["FoxSilhouette"] = PosterFeature.Fox, ["Rabbit"] = PosterFeature.Rabbit, ["Owl"] = PosterFeature.Owl,
        ["DeerSilhouette"] = PosterFeature.Deer, ["WhaleTail"] = PosterFeature.Whale, ["WindowGlow"] = PosterFeature.Cabin,
        ["Lantern"] = PosterFeature.Lantern, ["Tent"] = PosterFeature.Tent, ["FireflyScatter"] = PosterFeature.Fireflies,
        ["Aurora"] = PosterFeature.Aurora, ["Snowfall"] = PosterFeature.Snowfall, ["ShootingStar"] = PosterFeature.ShootingStar,
        ["BirdFlock"] = PosterFeature.Birds, ["FlowerScatter"] = PosterFeature.Flowers, ["Cloud"] = PosterFeature.Clouds,
    };

    // v2 shape enum order, for specs stored with numeric shapes (the v2 default).
    private static readonly string[] LegacyShapeOrder =
    [
        "Moon", "Stars", "Hills", "Water", "Cloud", "Pine", "FoxSilhouette", "Lighthouse", "Boat", "WindowGlow",
        "Crescent", "StarCluster", "ShootingStar", "MistBand", "Aurora", "Snowfall", "FireflyScatter",
        "Mountain", "ForestRow", "Path", "RoundTree", "Reeds", "FlowerScatter", "Sailboat", "Lantern", "Tent",
        "Rabbit", "Owl", "BirdFlock", "DeerSilhouette", "WhaleTail",
    ];

    private static PosterSpec UpgradeLegacy(JsonElement root)
    {
        var palette = (PosterPalette)(-1);
        if (root.TryGetProperty("Palette", out var p))
        {
            if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n) && Enum.IsDefined((PosterPalette)n)) palette = (PosterPalette)n;
            else if (p.ValueKind == JsonValueKind.String && Enum.TryParse<PosterPalette>(p.GetString(), true, out var named)) palette = named;
        }
        var seed = root.TryGetProperty("Seed", out var s) && s.TryGetInt32(out var sv) ? sv : 0;
        var mood = PosterMood.Calm;
        if (root.TryGetProperty("Mood", out var m) && m.ValueKind == JsonValueKind.String
            && Enum.TryParse<PosterMood>(m.GetString(), true, out var parsedMood) && Enum.IsDefined(parsedMood))
            mood = parsedMood;

        var shapes = new List<string>();
        var hasMoon = false;
        var hasCrescent = false;
        if (root.TryGetProperty("Layers", out var layers) && layers.ValueKind == JsonValueKind.Array)
        {
            foreach (var layer in layers.EnumerateArray())
            {
                if (!layer.TryGetProperty("Elements", out var els) || els.ValueKind != JsonValueKind.Array) continue;
                foreach (var el in els.EnumerateArray())
                {
                    if (!el.TryGetProperty("Shape", out var shape)) continue;
                    var name = shape.ValueKind switch
                    {
                        JsonValueKind.String => shape.GetString() ?? string.Empty,
                        JsonValueKind.Number when shape.TryGetInt32(out var i) && i >= 0 && i < LegacyShapeOrder.Length => LegacyShapeOrder[i],
                        _ => string.Empty,
                    };
                    hasMoon |= name == "Moon";
                    hasCrescent |= name == "Crescent";
                    shapes.Add(name);
                }
            }
        }

        var scene = PosterSpecRules.SceneForPalette(Enum.IsDefined(palette) ? palette : PosterPalette.NightHarbor);
        if (shapes.Contains("Lighthouse") || shapes.Contains("Sailboat") || shapes.Contains("WhaleTail")) scene = PosterScene.MoonlitSea;
        else if (shapes.Contains("Mountain") && shapes.Contains("Water")) scene = PosterScene.MountainLake;
        else if (shapes.Contains("Snowfall")) scene = PosterScene.SnowyVillage;
        else if (shapes.Contains("Tent") && !shapes.Contains("Water")) scene = PosterScene.StarryHills;

        var features = shapes.Where(LegacyShapes.ContainsKey).Select(n => LegacyShapes[n]).Distinct().ToList();
        return PosterSpecRules.Normalize(new PosterSpec
        {
            Scene = scene,
            Palette = palette,
            Mood = mood,
            TimeOfDay = hasMoon ? PosterTimeOfDay.Moonlit : hasCrescent ? PosterTimeOfDay.Starlit : PosterTimeOfDay.Twilight,
            Features = features,
            Seed = seed,
        });
    }
}
