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

// ---------- Poster ----------

/// <summary>Curated palette ids — the LLM may only pick from these.</summary>
public enum PosterPalette
{
    NightHarbor, MoonMeadow, OceanDeep, PlumDusk, PineForest,
    CloudDrift, StarfieldViolet, LanternGlow, TidePool, SnowHush, EmberSky, MistValley
}

/// <summary>Scene vocabulary for poster formations. Grouped by role; the composer
/// knows each shape's category (scatter / sky / terrain / grounded / water-bound / focal)
/// and repairs accordingly. v1 shapes stay first so stored specs keep deserializing.</summary>
public enum PosterShape
{
    // ---- v1 set (stored art depends on these names) ----
    Moon, Stars, Hills, Water, Cloud, Pine, FoxSilhouette, Lighthouse, Boat, WindowGlow,
    // ---- celestial ----
    Crescent, StarCluster, ShootingStar,
    // ---- sky / atmosphere ----
    MistBand, Aurora, Snowfall, FireflyScatter,
    // ---- terrain masses ----
    Mountain, ForestRow, Path,
    // ---- flora ----
    RoundTree, Reeds, FlowerScatter,
    // ---- structures ----
    Sailboat, Lantern, Tent,
    // ---- creatures ----
    Rabbit, Owl, BirdFlock, DeerSilhouette, WhaleTail,
}

public sealed class PosterElement
{
    public PosterShape Shape { get; set; }
    /// <summary>0..1 normalized; composer clamps.</summary>
    public double X { get; set; }
    public double Y { get; set; }
    public double Scale { get; set; } = 0.3;
    /// <summary>Stars/cloud scatter density 0..1 where applicable.</summary>
    public double Density { get; set; }
    /// <summary>Atmospheric depth tint 0..1: 0 = the layer's natural color, 1 = fully faded
    /// into the sky tone (distance haze). Additive v2 field; absent in stored v1 specs.</summary>
    public double Tint { get; set; }
    /// <summary>Soft radial glow strength 0..1 rendered behind the shape (moon halo,
    /// lantern, lit window, tent, fireflies). Additive v2 field.</summary>
    public double Glow { get; set; }
    /// <summary>Mirror the shape horizontally (creatures face left/right). Additive v2 field.</summary>
    public bool FlipX { get; set; }
}

public sealed class PosterLayer
{
    /// <summary>0 = far background … 3 = foreground.</summary>
    public int Depth { get; set; }
    public List<PosterElement> Elements { get; set; } = [];
}

public sealed class PosterSpec
{
    public PosterPalette Palette { get; set; }
    public string Mood { get; set; } = "calm";
    /// <summary>Exactly 4, back→front. Composer validates.</summary>
    public List<PosterLayer> Layers { get; set; } = [];
    /// <summary>Deterministic scatter seed so same spec → same pixels.</summary>
    public int Seed { get; set; }
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
