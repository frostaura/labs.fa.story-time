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
            try
            {
                var spec = JsonSerializer.Deserialize<PosterSpec>(story.PosterSpecJson);
                if (spec is not null)
                {
                    palette = Enum.IsDefined(spec.Palette) ? spec.Palette : PosterPalette.NightHarbor;
                    mood = string.IsNullOrWhiteSpace(spec.Mood) ? "calm" : spec.Mood;
                    if (spec.Seed != 0) seed = spec.Seed;
                }
            }
            catch (JsonException) { /* fall back to defaults */ }
        }
        return new AmbientContext { Palette = palette, Mood = mood, Seed = seed };
    }
}
