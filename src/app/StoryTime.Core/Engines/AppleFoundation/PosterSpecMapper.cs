using StoryTime.Contracts;
using StoryTime.Core.Engines.Prompts;
using StoryTime.Poster.Core;
using StoryTime.Poster.Spec;

namespace StoryTime.Core.Engines.AppleFoundation;

/// <summary>The ONLY place the Apple engine touches the poster contract: builds the guided
/// poster request and maps the model's enum-name <see cref="FmPosterChoice"/> (Swift
/// <c>SEFMPosterChoice</c>) onto spec contract v3 (docs/design/poster-engine.md). Names are
/// parsed leniently (any case; anything unknown stays undefined) and the result always goes
/// through <see cref="PosterSpecRules.Normalize"/>, which repairs undefined values and drops
/// features that do not fit the scene — a stray model value degrades a poster, never a story.</summary>
public static class PosterSpecMapper
{
    public const string Instructions =
        "You choose the picture for a calm children's bedtime-story poster. " +
        "Pick the quiet night place and the few things in it that best fit the story.";

    public static FmRequest Request(StoryOutline outline) => new()
    {
        Instructions = Instructions,
        Prompt = StoryPrompts.PosterUser(outline),
        Options = FmOptions.Threshold(0.9, outline.Seed, 0.5, 500),
    };

    /// <summary>The spec seed for a story: derived from the outline seed exactly as
    /// <see cref="PosterSpecDeriver"/> derives it, so the model path and the template path
    /// vary a poster the same way for the same story.</summary>
    public static int SeedFor(StoryOutline outline) =>
        (int)(Hash.Combine((ulong)(uint)outline.Seed, 104729) & 0x7FFFFFFF);

    /// <summary>Maps the choice to a normalized v3 spec with the given seed.</summary>
    public static PosterSpec Map(FmPosterChoice choice, int seed)
    {
        var spec = new PosterSpec
        {
            Scene = Parse<PosterScene>(choice.Scene),
            TimeOfDay = Parse<PosterTimeOfDay>(choice.TimeOfDay),
            Mood = Parse<PosterMood>(choice.Mood),
            Palette = Parse<PosterPalette>(choice.Palette),
            Features = (choice.Features ?? []).Select(Parse<PosterFeature>).ToList(),
            Seed = seed,
        };
        return PosterSpecRules.Normalize(spec);
    }

    /// <summary>Name (any case) → value; integers and unknown names become undefined (-1)
    /// so Normalize repairs them. Integers are refused because the schema only offers names.</summary>
    private static T Parse<T>(string? name) where T : struct, Enum =>
        !string.IsNullOrWhiteSpace(name) && !char.IsDigit(name.Trim()[0]) && name.Trim()[0] != '-'
        && Enum.TryParse<T>(name.Trim(), ignoreCase: true, out var value) && Enum.IsDefined(value)
            ? value
            : (T)(object)(-1);
}
