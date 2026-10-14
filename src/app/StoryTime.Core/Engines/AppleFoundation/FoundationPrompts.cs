using System.Text;
using System.Text.RegularExpressions;
using StoryTime.Contracts;
using StoryTime.Core.Engines.Prompts;

namespace StoryTime.Core.Engines.AppleFoundation;

/// <summary>Stage prompts for the on-device Apple model, sized for a 4,096-token session.
/// Instructions are always <see cref="StoryPrompts.SystemPrompt"/> unchanged — the age-banded
/// role line plus every absolute rule — so the model-side safety layer is identical to every
/// other engine's. Only profile-derived, sanitized values reach a prompt: the child's name
/// and the parent-picked interests. A child never types into a prompt.</summary>
public static partial class FoundationPrompts
{
    /// <summary>Running "story so far" cap (recipe: ≤120 words).</summary>
    public const int SummaryWordCap = 120;

    /// <summary>Story-notes cap (recipe: bible ≤10 lines).</summary>
    public const int BibleLineCap = 10;

    public static string Instructions(ChildProfile profile) =>
        StoryPrompts.SystemPrompt(profile.Band, profile.EffectiveContentFilter);

    // ---------------- sanitization ----------------

    [GeneratedRegex(@"[^\p{L}\p{M}' \-]")]
    private static partial Regex NameNoise();

    [GeneratedRegex(@"[^\p{L}\p{M}' \-]")]
    private static partial Regex InterestNoise();

    /// <summary>Letters, spaces, hyphens and apostrophes only; ≤24 characters.</summary>
    public static string SafeName(ChildProfile profile)
    {
        var cleaned = NameNoise().Replace(profile.Name ?? string.Empty, string.Empty).Trim();
        if (cleaned.Length > 24) cleaned = cleaned[..24].Trim();
        return cleaned.Length == 0 ? "Little One" : cleaned;
    }

    /// <summary>Up to five interests, letters only, each ≤24 characters, lexicon-clean.</summary>
    public static IReadOnlyList<string> SafeInterests(ChildProfile profile, Pipeline.SafetyFilter safety)
    {
        var list = new List<string>();
        foreach (var raw in profile.Interests)
        {
            var cleaned = InterestNoise().Replace(raw ?? string.Empty, string.Empty).Trim();
            if (cleaned.Length == 0 || cleaned.Length > 24 || !safety.IsClean(cleaned)) continue;
            if (!list.Contains(cleaned, StringComparer.OrdinalIgnoreCase)) list.Add(cleaned);
            if (list.Count == 5) break;
        }
        return list;
    }

    // ---------------- outline ----------------

    public static string Outline(ChildProfile profile, IReadOnlyList<string> interests, StoryBible? bible,
        int sceneCount, string titleShapeHint)
    {
        var name = SafeName(profile);
        var sb = new StringBuilder();
        sb.Append($"Plan a bedtime story for {name}, age {profile.Age}, who loves ")
          .Append(interests.Count > 0 ? string.Join(", ", interests) : "cozy animals and the night sky")
          .Append($". The story has exactly {sceneCount} scenes. ");
        sb.Append($"{name} is the main character; add one to three gentle companions. ");
        if (bible is { EpisodeCount: > 0 })
        {
            sb.Append("This is the next episode of a gentle series. Keep these characters: ")
              .Append(string.Join("; ", bible.Characters.Where(c => c.Kind != "child").Take(3).Select(c => $"{c.Name} the {c.Trait} {c.Kind}")))
              .Append(". Familiar places: ").Append(string.Join(", ", bible.Places.Take(3))).Append(". ");
            if (!string.IsNullOrWhiteSpace(bible.LastEpisodeRecap))
                sb.Append("Last time: ").Append(bible.LastEpisodeRecap).Append(' ');
        }
        sb.Append("The title is built only from this story's own characters, places and things. ");
        sb.Append("Shape the title as: ").Append(titleShapeHint).Append(". Never shape it as \"The <adjective> <noun>\". ");
        if (profile.RecentTitles.Count > 0)
        {
            sb.Append("Recent titles were: ")
              .Append(string.Join("; ", profile.RecentTitles.Take(5).Select(t => $"\"{t}\"")))
              .Append(". Use fresh words and a different shape. ");
        }
        sb.Append("Each scene is one calm, small step. The final scene settles everyone safely for sleep.");
        return sb.ToString();
    }

    // ---------------- scene ----------------

    /// <summary>Everything a scene prompt may carry. Trim order under budget pressure:
    /// summary first, then the story notes, then the character cards — never the beat.</summary>
    public sealed record SceneContext
    {
        public required ChildProfile Profile { get; init; }
        public required StoryOutline Outline { get; init; }
        public required SceneOutline Scene { get; init; }
        public required string Summary { get; init; }
        public required IReadOnlyList<string> Bible { get; init; }
        public required string LastSentences { get; init; }
        public required int TargetWords { get; init; }
        /// <summary>Stock lines already used in this story (do-not-reuse list).</summary>
        public IReadOnlyList<string> AvoidLines { get; init; } = [];
    }

    /// <param name="trim">0 = full; 1 = summary and notes halved; 2 = no summary, notes
    /// halved again; 3 = no summary, no notes, lead characters only.</param>
    public static string Scene(SceneContext c, int trim = 0)
    {
        var outline = c.Outline;
        var isFinal = outline.Scenes.Count > 0 && c.Scene.Id >= outline.Scenes[^1].Id;
        var sb = new StringBuilder();
        sb.Append($"Story: \"{outline.Title}\". ");

        var cards = trim >= 3 ? outline.Characters.Take(2) : outline.Characters.Take(5);
        sb.Append("Characters: ").Append(string.Join("; ", cards.Select(Card))).Append(". ");

        var bible = trim switch { 0 => c.Bible, 1 => Half(c.Bible), 2 => Half(Half(c.Bible)), _ => [] };
        if (bible.Count > 0) sb.Append("Story notes: ").Append(string.Join("; ", bible)).Append(". ");

        var summary = trim switch { 0 => c.Summary, 1 => HalfWords(c.Summary), _ => string.Empty };
        if (!string.IsNullOrWhiteSpace(summary)) sb.Append("The story so far: ").Append(summary.Trim()).Append(' ');

        sb.Append($"Now write scene {c.Scene.Id + 1} of {outline.Scenes.Count}: ").Append(c.Scene.Summary.Trim()).Append(' ');
        if (c.Scene.Beats.Count > 0) sb.Append("Touch these beats in order: ").Append(string.Join("; ", c.Scene.Beats)).Append(". ");
        if (!string.IsNullOrWhiteSpace(c.LastSentences))
            sb.Append("For context only, the previous part ended: \"").Append(c.LastSentences.Trim())
              .Append("\" Do not repeat or reword those sentences; begin this scene with something new. ");
        if (trim < 2 && c.AvoidLines.Count > 0)
            sb.Append("Do not reuse these lines: ").Append(string.Join("; ", c.AvoidLines.Take(6).Select(l => $"\"{l}\""))).Append(". ");
        // Lab 2026-10-14: asked only for "flowing prose", the model wrote whole scenes as ONE
        // 160-word sentence, and every middle scene ended with someone drifting off to sleep.
        var sentences = Math.Max(5, c.TargetWords / (c.Profile.Band == AgeBand.Age3To4 ? 9 : 18));
        sb.Append($"Write about {c.TargetWords} words of flowing prose in about {sentences} sentences, ");
        sb.Append(c.Profile.Band == AgeBand.Age3To4 ? "each short and simple, " : "varied in length and each under 35 words, ");
        sb.Append("with two or three gentle sensory details. ");
        sb.Append("Do not include the title, scene numbers or headings. ");
        sb.Append(isFinal
            ? "This is the final scene: wind everything down and end with everyone cozy, safe and drifting toward sleep."
            : "This scene is in the middle of the story: nobody falls asleep, dreams or says goodnight yet. " +
              "End it softly and contentedly, never on a cliffhanger.");
        return sb.ToString();
    }

    /// <summary>The pre-tested safer phrasing (ladder step after a re-run): the beat and the
    /// lead characters only, framed positively. Run at temperature 0.5. The instructions —
    /// and with them every absolute rule — are unchanged.</summary>
    public static string SceneSafer(SceneContext c)
    {
        var outline = c.Outline;
        var isFinal = outline.Scenes.Count > 0 && c.Scene.Id >= outline.Scenes[^1].Id;
        var leads = string.Join(" and ", outline.Characters.Take(2).Select(ch => ch.Name));
        return $"Write a calm, cozy part of a bedtime story about {leads}. In this part, {c.Scene.Summary.Trim().TrimEnd('.')}. " +
               $"Use about {c.TargetWords} words of warm, gentle prose with no headings. " +
               (isFinal ? "End with everyone snug and falling asleep." : "End the part peacefully.");
    }

    // ---------------- digest / polish / repair ----------------

    public const string DigestInstructions =
        "You keep short, kind notes for a children's bedtime storyteller. " +
        "Summaries are calm, factual and gentle.";

    public static string Digest(string sceneText) =>
        "Summarize this part of a bedtime story for the storyteller's notes, and list any new names, places or objects.\n\n" + sceneText;

    // ---------------- helpers ----------------

    public static string Card(CharacterCard c) =>
        string.IsNullOrWhiteSpace(c.Trait) ? $"{c.Name} the {c.Kind}" : $"{c.Name} the {c.Trait} {c.Kind}";

    private static IReadOnlyList<string> Half(IReadOnlyList<string> lines) => lines.Take(lines.Count / 2).ToList();

    public static string HalfWords(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Skip(words.Length - words.Length / 2));
    }

    /// <summary>Keeps the most recent <paramref name="cap"/> words (the newest events matter most).</summary>
    public static string CapWords(string text, int cap)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length <= cap ? string.Join(' ', words) : string.Join(' ', words.Skip(words.Length - cap));
    }
}
