namespace StoryTime.Core.Engines.Narration;

/// <summary>AVSpeechSynthesisVoiceQuality, platform-neutral (0 = nothing usable installed).</summary>
public enum VoiceQualityTier { None = 0, Default = 1, Enhanced = 2, Premium = 3 }

public enum VoiceGender { Unspecified, Male, Female }

/// <summary>One installed system voice, as enumerated from the platform.</summary>
public sealed record InstalledVoice(
    string Identifier,
    string Name,
    string Language,
    VoiceQualityTier Quality,
    VoiceGender Gender = VoiceGender.Unspecified,
    bool IsNovelty = false,
    bool IsPersonal = false)
{
    /// <summary>"Ava (Premium)" → "Ava".</summary>
    public string BaseName
    {
        get
        {
            var n = Name.Trim();
            var cut = n.IndexOfAny([' ', '(']);
            return cut > 0 ? n[..cut] : n;
        }
    }

    /// <summary>A compact voice derived from a Siri voice (com.apple.ttsbundle.siri_*):
    /// renders to buffers like any compact voice and sounds markedly more natural than the
    /// classic compact set (device probe 2026-10-14).</summary>
    public bool IsSiriDerived => Identifier.Contains("ttsbundle.siri_", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Which voices a narrator may use. English only for v1; kept as data so a second
/// language is a policy, not a code change.</summary>
public sealed record VoiceLanguagePolicy(string LanguagePrefix = "en", string? PreferredRegion = null)
{
    public static VoiceLanguagePolicy English { get; } = new();

    public bool Accepts(string language) =>
        language.StartsWith(LanguagePrefix, StringComparison.OrdinalIgnoreCase)
        && (language.Length == LanguagePrefix.Length || language[LanguagePrefix.Length] is '-' or '_');
}

/// <summary>The one voice StoryTime requires (founder decision 2026-10-14: Serena Premium,
/// English (UK) — rated "expressive"; Ava, Zoe and the compact voices were "robotic").
/// Matched by name and quality, never by a hard-coded identifier.</summary>
public sealed record StorytellerRequirement(string Name = "Serena", VoiceQualityTier Quality = VoiceQualityTier.Premium,
    string PreferredLanguage = "en-GB")
{
    public static StorytellerRequirement Serena { get; } = new();

    /// <summary>Settings label, e.g. "Serena (Premium)".</summary>
    public string DisplayName => $"{Name} ({Quality})";

    public bool IsMetBy(InstalledVoice v) =>
        v.Quality >= Quality && string.Equals(v.BaseName, Name, StringComparison.OrdinalIgnoreCase);
}

/// <summary>The voice narration will use right now.</summary>
public sealed record StorytellerVoice(VoiceChoice Choice, bool MeetsRequirement, VoiceQualityTier BestTier);

/// <summary>
/// Ranks installed voices and picks the storyteller. Never hard-codes identifiers — they
/// change between OS builds — and works from quality, traits and names only.
/// <list type="bullet">
/// <item>Excluded: novelty voices, Personal Voice (cannot render to a buffer), the
/// <c>com.apple.eloquence</c> family, Siri NATURAL voices (not available to apps), and other
/// languages (per <see cref="VoiceLanguagePolicy"/>).</item>
/// <item>The required storyteller (Serena Premium) always wins. The rest rank as fallbacks
/// only — narration must still render if the voice was deleted while a story is being made — by tier (premium
/// 300, enhanced 200, default 0) + warmth + Siri-derived compact 30 (above Samantha/Moira)
/// + region match 20.</item>
/// </list>
/// </summary>
public static class VoiceRanker
{
    private static readonly Dictionary<string, int> Warmth = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Serena"] = 45, ["Ava"] = 40, ["Zoe"] = 35, ["Matilda"] = 30, ["Jamie"] = 25, ["Evan"] = 25,
        ["Nathan"] = 20, ["Karen"] = 20, ["Kate"] = 20, ["Stephanie"] = 15, ["Tessa"] = 15, ["Moira"] = 15,
        ["Samantha"] = 14, ["Daniel"] = 12, ["Allison"] = 12, ["Susan"] = 10, ["Joelle"] = 10, ["Noelle"] = 10,
        ["Oliver"] = 10, ["Tom"] = 8, ["Lee"] = 8,
    };

    public const int SiriDerivedBonus = 30;
    public const int RegionBonus = 20;
    public const int RequirementBonus = 1000;
    public const int SuperCompactPenalty = 40;

    public static bool IsEligible(InstalledVoice v, VoiceLanguagePolicy policy)
    {
        if (v.IsNovelty || v.IsPersonal) return false;
        var id = v.Identifier;
        if (id.StartsWith("com.apple.eloquence", StringComparison.OrdinalIgnoreCase)) return false;
        // Legacy MacinTalk voices (Fred, Ralph, Kathy…): macOS-only and robotic.
        if (id.StartsWith("com.apple.speech.synthesis.voice.", StringComparison.OrdinalIgnoreCase)) return false;
        if (id.Contains("siri", StringComparison.OrdinalIgnoreCase) && !v.IsSiriDerived) return false;
        if (v.Quality == VoiceQualityTier.None) return false;
        return policy.Accepts(v.Language);
    }

    public static int Score(InstalledVoice v, VoiceLanguagePolicy policy, StorytellerRequirement? requirement = null)
    {
        var score = v.Quality switch
        {
            VoiceQualityTier.Premium => 300,
            VoiceQualityTier.Enhanced => 200,
            _ => 0,
        };
        score += Warmth.GetValueOrDefault(v.BaseName);
        if (v.Quality == VoiceQualityTier.Default && v.IsSiriDerived) score += SiriDerivedBonus;
        // "super-compact" voices are an even smaller build than compact: rank them below it.
        if (v.Identifier.Contains("super-compact", StringComparison.OrdinalIgnoreCase)) score -= SuperCompactPenalty;
        if (policy.PreferredRegion is { Length: > 0 } region
            && v.Language.EndsWith(region, StringComparison.OrdinalIgnoreCase)) score += RegionBonus;
        if (requirement is not null && requirement.IsMetBy(v))
        {
            score += RequirementBonus;
            if (v.Language.Equals(requirement.PreferredLanguage, StringComparison.OrdinalIgnoreCase)) score += 1;
        }
        return score;
    }

    /// <summary>Eligible voices, best first; ties broken by identifier for determinism.</summary>
    public static List<InstalledVoice> Rank(IEnumerable<InstalledVoice> installed, VoiceLanguagePolicy? policy = null,
        StorytellerRequirement? requirement = null)
    {
        var p = policy ?? VoiceLanguagePolicy.English;
        var r = requirement ?? StorytellerRequirement.Serena;
        return installed.Where(v => IsEligible(v, p))
            .OrderByDescending(v => Score(v, p, r))
            .ThenBy(v => v.Identifier, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Pick the storyteller: the required voice when installed, otherwise the best
    /// fallback (narration still renders; the app routes the parent to the download step).
    /// Null when no eligible voice is installed at all.</summary>
    public static StorytellerVoice? Choose(IEnumerable<InstalledVoice> installed, VoiceLanguagePolicy? policy = null,
        StorytellerRequirement? requirement = null)
    {
        var r = requirement ?? StorytellerRequirement.Serena;
        var ranked = Rank(installed, policy, r);
        if (ranked.Count == 0) return null;
        return Build(ranked[0], ranked, r);
    }

    /// <summary>
    /// Launch-time re-validation of the persisted storyteller: kept while it is still
    /// installed, eligible and meets the requirement (stable narrator across launches even if
    /// the OS reorders voices); otherwise a fresh <see cref="Choose"/> (a deleted voice, or the
    /// required voice was just downloaded).
    /// </summary>
    public static StorytellerVoice? Revalidate(string? persistedId, IEnumerable<InstalledVoice> installed,
        VoiceLanguagePolicy? policy = null, StorytellerRequirement? requirement = null)
    {
        var r = requirement ?? StorytellerRequirement.Serena;
        var ranked = Rank(installed, policy, r);
        if (ranked.Count == 0) return null;
        var kept = ranked.FirstOrDefault(v => v.Identifier == persistedId && r.IsMetBy(v));
        return Build(kept ?? ranked[0], ranked, r);
    }

    private static StorytellerVoice Build(InstalledVoice primary, List<InstalledVoice> ranked, StorytellerRequirement r) => new(
        new VoiceChoice
        {
            VoiceId = primary.Identifier,
            DisplayName = primary.Name,
            Fallbacks = ranked.Where(v => v.Identifier != primary.Identifier).Take(3).Select(v => v.Identifier).ToList(),
        },
        r.IsMetBy(primary),
        ranked.Max(v => v.Quality));
}
