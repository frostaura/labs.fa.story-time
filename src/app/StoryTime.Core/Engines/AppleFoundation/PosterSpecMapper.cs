using StoryTime.Contracts;

namespace StoryTime.Core.Engines.AppleFoundation;

/// <summary>The ONLY place the Apple engine touches the poster contract: builds the guided
/// poster request and maps the model's small enum-only <see cref="FmPosterChoice"/> (Swift
/// <c>SEFMPosterChoice</c>) onto <see cref="PosterSpec"/>. Deliberately thin — layout is
/// fixed and the composer (PosterComposer.Validate) repairs anything out of zone, so a
/// change to the poster contract is re-pointed here and nowhere else.</summary>
public static class PosterSpecMapper
{
    public const string Instructions =
        "You are the art director for a calm children's bedtime-story poster. " +
        "Choose quiet, cozy night imagery that fits the story.";

    public static FmRequest Request(StoryOutline outline)
    {
        var place = outline.Settings.Count > 0 ? string.Join(", ", outline.Settings.Take(3)) : "a quiet meadow";
        var who = outline.Characters.Count > 0 ? string.Join(", ", outline.Characters.Take(3).Select(c => c.Kind)) : "a small animal";
        var prompt = $"Story: \"{outline.Title}\". Places: {place}. Characters: {who}. " +
                     "Pick the palette, sky light, far landscape, one main thing in front and one small extra touch " +
                     "that best suit this story. Choose Water only if the story has sea, lake or river; " +
                     "choose WhaleTail or Sailboat only with Water.";
        return new FmRequest
        {
            Instructions = Instructions,
            Prompt = prompt,
            Options = FmOptions.Threshold(0.9, outline.Seed, 0.5, 500),
        };
    }

    /// <summary>Maps the choice to a four-layer spec; unknown names fall back to safe defaults.</summary>
    public static PosterSpec Map(FmPosterChoice choice, int seed)
    {
        var rng = new Random(seed);
        var moonLeft = rng.Next(2) == 0;
        var moonX = moonLeft ? 0.3 : 0.7;
        var focalX = moonLeft ? 0.68 : 0.32;

        var sky = new List<PosterElement>
        {
            new() { Shape = Parse(choice.SkyLight, PosterShape.Moon), X = moonX, Y = 0.18, Scale = 0.16, Glow = 0.5 },
            new() { Shape = PosterShape.Stars, X = 0.5, Y = 0.3, Scale = 0.05, Density = 0.55 },
        };
        var accent = Parse(choice.Accent, (PosterShape?)null);
        if (accent is { } a)
            sky.Add(new PosterElement { Shape = a, X = 1 - moonX, Y = a == PosterShape.FireflyScatter ? 0.74 : 0.3, Scale = 0.14, Density = 0.5, Tint = 0.3 });

        var far = new List<PosterElement>();
        var mid = new List<PosterElement>();
        var landscape = Parse(choice.Landscape, PosterShape.Hills);
        var hasWater = landscape == PosterShape.Water;
        if (hasWater)
        {
            far.Add(new PosterElement { Shape = PosterShape.Hills, X = moonLeft ? 0.85 : 0.15, Y = 0.68, Scale = 0.7, Tint = 0.45 });
            mid.Add(new PosterElement { Shape = PosterShape.Water, X = 0.5, Y = 0.72, Scale = 0.9 });
        }
        else
        {
            far.Add(new PosterElement { Shape = landscape, X = moonLeft ? 0.7 : 0.3, Y = landscape == PosterShape.Mountain ? 0.62 : 0.76, Scale = 0.8, Tint = 0.35 });
            far.Add(new PosterElement { Shape = PosterShape.Hills, X = moonLeft ? 0.2 : 0.8, Y = 0.86, Scale = 0.7 });
        }

        var focal = Parse(choice.Focal, PosterShape.WindowGlow);
        if (focal is PosterShape.WhaleTail or PosterShape.Sailboat && !hasWater) focal = PosterShape.WindowGlow;
        var lit = focal is PosterShape.WindowGlow or PosterShape.Tent or PosterShape.Lantern;
        var fore = new List<PosterElement>
        {
            new() { Shape = focal, X = focalX, Y = hasWater ? 0.8 : 0.83, Scale = 0.24, Glow = lit ? 0.6 : 0, FlipX = !moonLeft },
        };

        return new PosterSpec
        {
            Palette = Enum.TryParse<PosterPalette>(choice.Palette, ignoreCase: true, out var p) ? p : PosterPalette.NightHarbor,
            Mood = string.IsNullOrWhiteSpace(choice.Mood) ? "calm" : choice.Mood,
            Seed = seed,
            Layers =
            [
                new PosterLayer { Depth = 0, Elements = sky },
                new PosterLayer { Depth = 1, Elements = far },
                new PosterLayer { Depth = 2, Elements = mid },
                new PosterLayer { Depth = 3, Elements = fore },
            ],
        };
    }

    private static PosterShape Parse(string name, PosterShape fallback) =>
        Enum.TryParse<PosterShape>(name, ignoreCase: true, out var s) ? s : fallback;

    private static PosterShape? Parse(string name, PosterShape? fallback) =>
        Enum.TryParse<PosterShape>(name, ignoreCase: true, out var s) ? s : fallback;
}
