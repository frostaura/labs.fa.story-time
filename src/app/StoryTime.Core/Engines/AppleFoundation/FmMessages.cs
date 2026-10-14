using System.Text.Json;
using System.Text.Json.Serialization;

namespace StoryTime.Core.Engines.AppleFoundation;

/// <summary>Sampling options sent with every request (Swift <c>SEFMOptions</c>).</summary>
public sealed record FmOptions
{
    /// <summary>"greedy", "topK" or "threshold".</summary>
    public string? Sampling { get; init; }
    public int? TopK { get; init; }
    public double? ProbabilityThreshold { get; init; }
    public ulong? Seed { get; init; }
    public double? Temperature { get; init; }
    public int? MaximumResponseTokens { get; init; }

    public static FmOptions Threshold(double p, int seed, double temperature, int maxTokens) => new()
    { Sampling = "threshold", ProbabilityThreshold = p, Seed = SeedOf(seed), Temperature = temperature, MaximumResponseTokens = maxTokens };

    public static FmOptions TopKSampling(int k, int seed, double temperature, int maxTokens) => new()
    { Sampling = "topK", TopK = k, Seed = SeedOf(seed), Temperature = temperature, MaximumResponseTokens = maxTokens };

    public static FmOptions Greedy(int maxTokens) => new() { Sampling = "greedy", MaximumResponseTokens = maxTokens };

    private static ulong SeedOf(int seed) => unchecked((uint)seed);
}

/// <summary>One stage request (Swift <c>SEFMRequest</c>): instructions open a fresh session,
/// prompt is the single turn.</summary>
public sealed record FmRequest
{
    public required string Instructions { get; init; }
    public required string Prompt { get; init; }
    public required FmOptions Options { get; init; }
    /// <summary>Outline only: exact scene count for the guided schema.</summary>
    public int? SceneCount { get; init; }
    /// <summary>null (default guardrails) or "permissive" — a ladder step for plain-String
    /// stages only; the output still passes the lexicon filter like every other output.</summary>
    public string? Guardrails { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, FmJson.Options);
}

/// <summary>Guided outline as generated (field order = generation order).</summary>
public sealed class FmOutline
{
    public string Plan { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public List<string> Settings { get; set; } = [];
    public List<FmCharacter> Characters { get; set; } = [];
    public List<FmScene> Scenes { get; set; } = [];
    public string Theme { get; set; } = string.Empty;
    public List<string> Bible { get; set; } = [];
}

public sealed class FmCharacter
{
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Trait { get; set; } = string.Empty;
}

public sealed class FmScene
{
    public string Summary { get; set; } = string.Empty;
    public List<string> Beats { get; set; } = [];
}

public sealed class FmDigest
{
    public string Summary { get; set; } = string.Empty;
    public List<string> NewFacts { get; set; } = [];
}

public sealed class FmText
{
    public string Text { get; set; } = string.Empty;
}

/// <summary>Guided poster choice (Swift <c>SEFMPosterChoice</c>): poster spec contract v3
/// as enum NAMES, without the seed. Mapped and repaired by PosterSpecMapper.</summary>
public sealed class FmPosterChoice
{
    public string Scene { get; set; } = string.Empty;
    public string TimeOfDay { get; set; } = string.Empty;
    public string Mood { get; set; } = string.Empty;
    public string Palette { get; set; } = string.Empty;
    public List<string> Features { get; set; } = [];
}

public static class FmJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Deserializes a bridge result; malformed JSON is a <see cref="FmErrorCode.Parse"/> failure.</summary>
    public static T Read<T>(string json) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options)
                   ?? throw new FoundationModelsException(FmErrorCode.Parse, "empty result");
        }
        catch (JsonException ex)
        {
            throw new FoundationModelsException(FmErrorCode.Parse, ex.Message);
        }
    }
}
