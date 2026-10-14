using System.Text.Json;
using System.Text.Json.Serialization;

namespace StoryTime.Contracts;

// ---------- Profile & settings ----------

public enum NarratorVoice { Ember, Willow, Sage }

public enum StoryMode { Series, OneShot }

/// <summary>Story length in minutes. 15 is Premium-gated.</summary>
public enum StoryLength { Five = 5, Ten = 10, Fifteen = 15 }

public enum AgeBand { Age3To4, Age5To6, Age7To8 }

/// <summary>Parent-facing content filter level (Settings → Content filters). The level ONLY
/// widens the allowed-theme palette handed to the story engines; every hard safety layer —
/// the absolute exclusions in the system prompts, the deterministic lexicon filter, the
/// bounded repair loop — is identical at every level, by construction (program invariant:
/// never weaken a layer). Additive, 2026-07-30.</summary>
public enum ContentFilterLevel
{
    /// <summary>Default. Soft wonder only — today's exact behavior, zero peril.</summary>
    Gentle,
    /// <summary>Gentle quests with tiny, always-resolved thrills, for older kids.
    /// For ages 3–4 this is automatically identical to Gentle (see ChildProfile.EffectiveContentFilter).</summary>
    Braveish,
}

public sealed class ChildProfile
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>3–8 inclusive.</summary>
    public int Age { get; set; } = 5;
    public List<string> Interests { get; set; } = [];
    public NarratorVoice Voice { get; set; } = NarratorVoice.Ember;
    public StoryLength Length { get; set; } = StoryLength.Ten;
    public StoryMode Mode { get; set; } = StoryMode.Series;

    public AgeBand Band => Age <= 4 ? AgeBand.Age3To4 : Age <= 6 ? AgeBand.Age5To6 : AgeBand.Age7To8;

    /// <summary>App-level content filter, stamped onto the profile at weave time by
    /// StoryPipeline so engines receive it through the same seam as age/interests.
    /// Source of truth is the settings store, not this transient copy. Additive, 2026-07-30.</summary>
    public ContentFilterLevel ContentFilter { get; set; } = ContentFilterLevel.Gentle;

    /// <summary>The level the engines actually apply: for ages 3–4, Brave-ish tones down
    /// automatically and is IDENTICAL to Gentle — the youngest band never gets even tiny
    /// thrills, regardless of the parent-facing setting.</summary>
    public ContentFilterLevel EffectiveContentFilter =>
        Band == AgeBand.Age3To4 ? ContentFilterLevel.Gentle : ContentFilter;

    /// <summary>The child's most recent story titles (newest first, ≤8), stamped onto the
    /// profile at weave time by StoryPipeline — same transient seam as ContentFilter — so
    /// the outline stage can steer NEW titles away from words and shapes the shelf already
    /// has. Source of truth is the story store, not this copy. Additive, 2026-07-31.</summary>
    public List<string> RecentTitles { get; set; } = [];
}

// ---------- Outline / story ----------

public sealed class CharacterCard
{
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;   // "fox", "child", "lighthouse keeper"…
    public string Trait { get; set; } = string.Empty;  // "curious", "sleepy"…
}

public sealed class SceneOutline
{
    public int Id { get; set; }
    public string Summary { get; set; } = string.Empty;
    public List<string> Beats { get; set; } = [];
}

public sealed class StoryOutline
{
    public string Title { get; set; } = string.Empty;
    public string Moral { get; set; } = string.Empty;
    public List<string> Settings { get; set; } = [];
    public List<CharacterCard> Characters { get; set; } = [];
    public List<SceneOutline> Scenes { get; set; } = [];
    /// <summary>Deterministic seed captured at outline time; scene/poster stages reuse it
    /// so an identical outline always weaves into an identical story. (Additive, Phase B.)</summary>
    public int Seed { get; set; }
}

/// <summary>Per-profile persistent world state for Series mode.</summary>
public sealed class StoryBible
{
    public List<CharacterCard> Characters { get; set; } = [];
    public List<string> Places { get; set; } = [];
    public string LastEpisodeRecap { get; set; } = string.Empty;
    public int EpisodeCount { get; set; }
}

// ---------- Poster (spec contract v3 — see docs/design/poster-engine.md) ----------
//
// The story engine (Apple Foundation Models guided generation, or the deterministic
// template derivation) emits ONLY this small enum-only spec. It never emits geometry or
// drawing instructions: the procedural engine (src/app/StoryTime.Poster) owns composition,
// silhouettes, colour and layering. Every enum below is serialized by NAME and read
// leniently (case-insensitive names or integers; unknown values become "undefined" and are
// repaired by PosterSpecRules.Normalize), so a model that emits a stray value can never
// break story creation.

/// <summary>Curated palette ids (hue families). Names are stable: the ambient bed and the
/// background hum key on them too.</summary>
[JsonConverter(typeof(LenientEnumConverter<PosterPalette>))]
public enum PosterPalette
{
    NightHarbor, MoonMeadow, OceanDeep, PlumDusk, PineForest,
    CloudDrift, StarfieldViolet, LanternGlow, TidePool, SnowHush, EmberSky, MistValley
}

/// <summary>The closed scene vocabulary — one procedural "biome" each.</summary>
[JsonConverter(typeof(LenientEnumConverter<PosterScene>))]
public enum PosterScene
{
    MoonlitSea, ForestClearing, MountainLake, FireflyMeadow, SnowyVillage, DesertOasis,
    IslandCove, StarryHills, ReefAtDusk, CloudKingdom, TownRooftops, JungleCanopy
}

/// <summary>Bedtime light, from the last warm glow to deep starlight.</summary>
[JsonConverter(typeof(LenientEnumConverter<PosterTimeOfDay>))]
public enum PosterTimeOfDay { Dusk, Twilight, Moonlit, Starlit }

/// <summary>Emotional temperature; shifts warmth, haze, chroma and density, never content.</summary>
[JsonConverter(typeof(LenientEnumConverter<PosterMood>))]
public enum PosterMood { Calm, Cozy, Dreamy, Magical, Sleepy, Curious }

/// <summary>Featured elements. At most three per spec; scene compatibility is enforced by
/// PosterSpecRules (an incompatible feature is dropped, never drawn in the wrong place).</summary>
[JsonConverter(typeof(LenientEnumConverter<PosterFeature>))]
public enum PosterFeature
{
    // structures & vehicles
    Lighthouse, Cabin, Village, Castle, Windmill, Tent, Lantern, Boat, Sailboat, Balloon, Train, Telescope,
    // creatures (one focal creature per poster)
    Fox, Rabbit, Owl, Deer, Bear, Cat, Hedgehog, Otter, Whale, Turtle, Fish, Camel, Dinosaur,
    // ambient life & sky
    Birds, Fireflies, Flowers, Mushrooms, ShootingStar, Aurora, MilkyWay, Clouds, Snowfall,
}

/// <summary>Poster spec contract v3: enum-only. Same (spec) → same pixels, everywhere.</summary>
public sealed class PosterSpec
{
    public const int CurrentVersion = 3;

    public int Version { get; set; } = CurrentVersion;
    public PosterScene Scene { get; set; }
    public PosterTimeOfDay TimeOfDay { get; set; } = PosterTimeOfDay.Moonlit;
    public PosterMood Mood { get; set; }
    public PosterPalette Palette { get; set; }
    /// <summary>0–3 featured elements, most important first (the first creature is the focal).</summary>
    public List<PosterFeature> Features { get; set; } = [];
    /// <summary>Deterministic variation seed (ridges, stars, placement, poses).</summary>
    public int Seed { get; set; }
}

/// <summary>Enum converter that writes names and reads names (any case) or integers, mapping
/// anything unknown to an undefined value (-1) instead of throwing — the poster rules then
/// repair it. A malformed model value must degrade a poster, never fail a story.</summary>
public sealed class LenientEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    private static T Undefined => (T)Enum.ToObject(typeof(T), -1);

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                var text = reader.GetString();
                return !string.IsNullOrWhiteSpace(text)
                       && !char.IsDigit(text.Trim()[0]) && text.Trim()[0] != '-'
                       && Enum.TryParse<T>(text.Trim(), ignoreCase: true, out var named)
                       && Enum.IsDefined(named)
                    ? named
                    : Undefined;
            case JsonTokenType.Number when reader.TryGetInt32(out var number):
                var value = (T)Enum.ToObject(typeof(T), number);
                return Enum.IsDefined(value) ? value : Undefined;
            default:
                reader.Skip();
                return Undefined;
        }
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        if (Enum.IsDefined(value)) writer.WriteStringValue(value.ToString());
        else writer.WriteNumberValue(Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture));
    }
}

// ---------- Narration ----------

public sealed class SentenceTimestamp
{
    public int Index { get; set; }
    public double StartSeconds { get; set; }
    public double EndSeconds { get; set; }
}

public sealed class NarrationManifest
{
    public string AudioPath { get; set; } = string.Empty;      // full story m4a/wav
    public string TeaserPath { get; set; } = string.Empty;     // ~20s teaser
    public double DurationSeconds { get; set; }
    public List<SentenceTimestamp> Timestamps { get; set; } = [];
}

// ---------- Stored story ----------

public enum StoryState { Weaving, AwaitingApproval, Ready, Failed }

public sealed class StoryRecord
{
    public int Id { get; set; }
    public int ProfileId { get; set; }
    public string Title { get; set; } = string.Empty;
    public StoryMode Mode { get; set; }
    public int EpisodeNumber { get; set; }                     // 0 for one-shots
    public string RecapLine { get; set; } = string.Empty;      // "Previously: …"
    public string FullText { get; set; } = string.Empty;
    public string PosterSpecJson { get; set; } = string.Empty;
    public string ThumbnailPath { get; set; } = string.Empty;
    public string AudioPath { get; set; } = string.Empty;
    public string TeaserPath { get; set; } = string.Empty;
    public string TimestampsJson { get; set; } = string.Empty;
    public double DurationSeconds { get; set; }
    public NarratorVoice Voice { get; set; }
    public StoryState State { get; set; }
    public bool IsFavorite { get; set; }
    public DateTime CreatedUtc { get; set; }
}

// ---------- Weave progress ----------

public enum WeaveStage { Outline, Scenes, Poster, Narration, Done }

public sealed class WeaveProgress
{
    public WeaveStage Stage { get; set; }
    /// <summary>0..100 across the whole weave.</summary>
    public int Percent { get; set; }
    /// <summary>0..1 within the current stage.</summary>
    public double StageFraction { get; set; }
}

/// <summary>One narrated window of the overlapped pipeline: a part WAV plus its
/// window-local sentence timestamps. Persisted inside WeaveCheckpoint.NarrationPartsJson
/// so resume can keep already-rendered audio; parts are concatenated (and their
/// timestamps offset) into the final story WAV at completion. Additive, 2026-07-31.</summary>
public sealed class NarrationPart
{
    public string AudioPath { get; set; } = string.Empty;
    public double DurationSeconds { get; set; }
    /// <summary>Sentence count of this window's script (timestamp indices are 0-based
    /// within the window; global index = window-local index + sum of previous counts).</summary>
    public int SentenceCount { get; set; }
    public List<SentenceTimestamp> Timestamps { get; set; } = [];
}

/// <summary>Persisted checkpoint so a weave survives app restarts.
/// Fields from Strategy down are additive (2026-07-31) for the overlapped pipeline:
/// a checkpoint written by older builds deserializes with Strategy == "" and resumes
/// on the sequential path exactly as before.</summary>
public sealed class WeaveCheckpoint
{
    public int StoryId { get; set; }
    public WeaveStage Stage { get; set; }
    public int SceneIndex { get; set; }
    public int SentenceIndex { get; set; }
    public string OutlineJson { get; set; } = string.Empty;
    public string CompletedScenesJson { get; set; } = string.Empty;

    /// <summary>"" (legacy/sequential) or "overlapped". A resume always continues on the
    /// strategy the weave started with — window bookkeeping only exists for overlapped.</summary>
    public string Strategy { get; set; } = string.Empty;
    /// <summary>JSON list of polished window texts (window = ~2 scenes), in order.</summary>
    public string PolishedWindowsJson { get; set; } = string.Empty;
    /// <summary>JSON list of NarrationPart for windows fully narrated to part WAVs.</summary>
    public string NarrationPartsJson { get; set; } = string.Empty;
    public bool TeaserDone { get; set; }
    public bool PosterDone { get; set; }
}
