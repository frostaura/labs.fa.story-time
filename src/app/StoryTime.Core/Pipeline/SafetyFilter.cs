using System.Text.RegularExpressions;

namespace StoryTime.Core.Pipeline;

/// <summary>Deterministic kid-safety lexicon filter — one structural layer of the safety
/// stack (never weakened for throughput). Word-boundary matching avoids substring false
/// positives ("scarf" does not flag "scar"); a small phrase whitelist rescues benign
/// figurative uses ("treasure hunt").</summary>
public sealed class SafetyFilter
{
    // Curated: violence, fear, adult themes. Lower-case; matched at word boundaries.
    private static readonly string[] FlaggedTerms =
    [
        // violence
        "kill", "kills", "killed", "killing", "gun", "guns", "knife", "knives", "sword",
        "swords", "blood", "bloody", "stab", "stabbed", "shoot", "shot", "punch", "punched",
        "fight", "fights", "fighting", "murder", "weapon", "weapons", "war", "battle",
        "attack", "attacked", "hurt", "wound", "wounded", "smash", "smashed", "trap", "trapped",
        // fear / dread
        "scream", "screams", "screamed", "screaming", "terror", "terrified", "terrifying",
        "horror", "horrible", "nightmare", "nightmares", "demon", "demons", "devil", "ghost",
        "ghosts", "haunted", "monster", "monsters", "zombie", "zombies", "skeleton", "skeletons",
        "dread", "panic", "evil", "wicked", "curse", "cursed", "doom", "doomed",
        // death / harm
        "dead", "death", "die", "dies", "died", "dying", "grave", "graves", "coffin", "poison",
        "poisoned", "drown", "drowned", "drowning", "choke", "choked", "starve", "starving",
        // adult themes
        "beer", "wine", "whiskey", "drunk", "cigarette", "cigarettes", "smoking", "drug",
        "drugs", "naked", "gamble", "gambling", "hell", "damn", "stupid", "idiot", "hate",
        "hates", "hated",
    ];

    // Benign phrases that contain a flagged term; a match inside one of these is not flagged.
    private static readonly string[] WhitelistPhrases =
    [
        "dying to know", "ghost light", "war of tickles",
        "fight off a yawn", "monster of a yawn", "shot of moonlight",
    ];

    private static readonly Regex TermRegex = new(
        @"\b(" + string.Join("|", FlaggedTerms.Select(Regex.Escape)) + @")\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Returns the distinct flagged terms found in the text (empty = clean).</summary>
    public IReadOnlyList<string> Check(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var lowered = text.ToLowerInvariant();
        var whitelistedRanges = new List<(int Start, int End)>();
        foreach (var phrase in WhitelistPhrases)
        {
            var idx = 0;
            while ((idx = lowered.IndexOf(phrase, idx, StringComparison.Ordinal)) >= 0)
            {
                whitelistedRanges.Add((idx, idx + phrase.Length));
                idx += phrase.Length;
            }
        }

        var flagged = new List<string>();
        foreach (Match m in TermRegex.Matches(text))
        {
            var inWhitelist = whitelistedRanges.Any(r => m.Index >= r.Start && m.Index + m.Length <= r.End);
            if (inWhitelist) continue;
            var term = m.Value.ToLowerInvariant();
            if (!flagged.Contains(term)) flagged.Add(term);
        }
        return flagged;
    }

    public bool IsClean(string text) => Check(text).Count == 0;
}
