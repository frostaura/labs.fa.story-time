using System.Text.Json;
using StoryTime.Contracts;

namespace StoryTime.Core.Audio;

/// <summary>Seed context for the procedural ambient bed — everything the composer needs,
/// derived from the story's poster spec so the music matches the art's mood. Deterministic:
/// the same story always composes the same loop.</summary>
public sealed class AmbientContext
{
    public required PosterPalette Palette { get; init; }
    public string Mood { get; init; } = "calm";
    public required int Seed { get; init; }

    /// <summary>Derive the ambient context from a stored story. Falls back to NightHarbor /
    /// calm with the story id as seed when the poster spec is missing or unparsable, so every
    /// story — including template-engine ones — gets a bed.</summary>
    public static AmbientContext FromStory(StoryRecord story)
    {
        var palette = PosterPalette.NightHarbor;
        var mood = "calm";
        var seed = story.Id;
        if (!string.IsNullOrEmpty(story.PosterSpecJson))
        {
            // The parser reads current and retired spec contracts and never throws.
            var spec = StoryTime.Poster.Spec.PosterSpecJson.Parse(story.PosterSpecJson);
            if (spec is not null)
            {
                palette = spec.Palette;
                mood = spec.Mood.ToString().ToLowerInvariant();
                if (spec.Seed != 0) seed = spec.Seed;
            }
        }
        return new AmbientContext { Palette = palette, Mood = mood, Seed = seed };
    }
}
