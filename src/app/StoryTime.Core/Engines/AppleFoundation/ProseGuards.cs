using System.Text.RegularExpressions;
using StoryTime.Core.Engines.Narration;

namespace StoryTime.Core.Engines.AppleFoundation;

/// <summary>Deterministic text guards around the on-device model, from the 2026-10-14 Mac lab:
/// <list type="bullet">
/// <item><see cref="Reframe"/> — positive reframing of outline beats before they reach a scene
/// prompt. Benign bedtime beats such as "afraid of the dark" or "fighting a giant with a
/// sword" tripped the default input guardrail; positive framing passed and reads gentler.</item>
/// <item><see cref="SplitRunOns"/> — cuts runaway sentences at clause joints.</item>
/// <item><see cref="StripRepeats"/> — scenes tended to open by repeating the previous scene's
/// last sentences verbatim, and stock lines ("Everything feels just right.") recurred across
/// scenes; repeats are removed after generation.</item>
/// </list>
/// Neither guard relaxes anything: the lexicon filter still runs on every output.</summary>
public static partial class ProseGuards
{
    private static readonly (Regex Pattern, string Replacement)[] Reframes =
    [
        (Word(@"(afraid|scared|frightened) of the dark"), "curious about the night"),
        (Word(@"(afraid|scared|frightened)"), "a little unsure"),
        (Word(@"the dark"), "the night"),
        (Word(@"darkness"), "the quiet night"),
        (Word(@"(got|gets|is|was|were|are) lost"), "$1 a little far from home"),
        (Word(@"fighting"), "playing"),
        (Word(@"fights"), "games"),
        (Word(@"fight"), "game"),
        (Word(@"battles?"), "friendly contest"),
        (Word(@"swords"), "walking sticks"),
        (Word(@"sword"), "walking stick"),
        (Word(@"monsters"), "shy creatures"),
        (Word(@"monster"), "shy creature"),
        (Word(@"giants"), "very tall friends"),
        (Word(@"giant"), "very tall friend"),
        (Word(@"ghosts"), "glowing friends"),
        (Word(@"ghost"), "glowing friend"),
        (Word(@"thunderstorms?|storms?"), "rain shower"),
        (Word(@"thunder"), "distant rumbles"),
        (Word(@"crying"), "sniffling"),
        (Word(@"cried"), "sniffled"),
        (Word(@"cry"), "sniffle"),
        (Word(@"chasing"), "following"),
        (Word(@"chased"), "followed"),
        (Word(@"chases?"), "follows"),
        (Word(@"hunting"), "searching"),
        (Word(@"hunts?"), "search"),
        (Word(@"dangerous"), "tricky"),
        (Word(@"danger"), "a small puzzle"),
    ];

    private static Regex Word(string pattern) =>
        new($@"\b{pattern}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Rewrites tripwire phrasing in plan text (titles, summaries, beats, notes) to
    /// positive framing. A leading capital is preserved.</summary>
    public static string Reframe(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var result = text;
        foreach (var (pattern, replacement) in Reframes)
        {
            result = pattern.Replace(result, m =>
            {
                var r = m.Result(replacement);
                return char.IsUpper(m.Value[0]) && r.Length > 0 ? char.ToUpperInvariant(r[0]) + r[1..] : r;
            });
        }
        return result;
    }

    private static readonly (Regex Pattern, string Replacement)[] HouseWords =
    [
        (Word(@"weaving"), "spinning"),
        (Word(@"weaves"), "spins"),
        (Word(@"weaved|wove|woven"), "spun"),
        (Word(@"weave"), "spin"),
        (Word(@"weavers?"), "spinner"),
        (Word(@"looming"), "rising"),
        (Word(@"loomed"), "rose"),
        (Word(@"looms"), "spinning wheels"),
        (Word(@"loom"), "spinning wheel"),
    ];

    /// <summary>House vocabulary: the founder keeps a small set of words out of the product
    /// entirely, story text included. The model uses them freely ("voices weaving a quiet
    /// song"), so model output is rewritten with plain equivalents. A leading capital is kept.</summary>
    public static string HouseVocabulary(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var result = text;
        foreach (var (pattern, replacement) in HouseWords)
        {
            result = pattern.Replace(result, m =>
                char.IsUpper(m.Value[0]) ? char.ToUpperInvariant(replacement[0]) + replacement[1..] : replacement);
        }
        return result;
    }

    [GeneratedRegex(@"[^\p{L}\p{N} ]")]
    private static partial Regex NonWord();

    public static string Normalize(string sentence) =>
        string.Join(' ', NonWord().Replace(sentence.ToLowerInvariant(), " ").Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Removes (1) opening sentences that repeat or near-repeat the previous scene's
    /// tail (5-gram overlap ≥ 0.6, checked on the first four sentences), and (2) any sentence
    /// that exactly repeats a stock line already used in this story. Returns the cleaned text
    /// and the fraction of words removed.</summary>
    public static (string Text, double RemovedFraction) StripRepeats(string text, string previousTail, IReadOnlyCollection<string> usedStockLines)
    {
        var paragraphs = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        var tailSentences = SentenceSplitter.Split(previousTail).Select(Normalize).Where(s => s.Length > 0).ToList();
        var tailGrams = tailSentences.SelectMany(s => Grams(s, 5)).ToHashSet();
        var stock = usedStockLines.ToHashSet();

        var totalWords = 0;
        var removedWords = 0;
        var sentenceIndex = 0;
        var kept = new List<string>();
        foreach (var paragraph in paragraphs)
        {
            var keptSentences = new List<string>();
            foreach (var sentence in SentenceSplitter.Split(paragraph))
            {
                var words = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                totalWords += words;
                var norm = Normalize(sentence);
                var repeatsTail = sentenceIndex < 4 && norm.Length > 0 &&
                    (tailSentences.Contains(norm) || Overlap(norm, tailGrams) >= 0.6);
                var repeatsStock = stock.Contains(norm);
                sentenceIndex++;
                if (repeatsTail || repeatsStock)
                {
                    removedWords += words;
                    continue;
                }
                keptSentences.Add(sentence);
            }
            if (keptSentences.Count > 0) kept.Add(string.Join(' ', keptSentences));
        }
        return (string.Join("\n\n", kept), totalWords == 0 ? 0 : removedWords / (double)totalWords);
    }

    [GeneratedRegex(@",\s+(and|while|so|until)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex ClauseJoint();

    /// <summary>Splits run-on sentences. The age-banded instructions ask for long, flowing
    /// sentences, and the on-device model takes that to the extreme — the Mac and simulator
    /// labs produced whole 160-word scenes as ONE sentence, which narration cannot pace.
    /// A sentence longer than <paramref name="maxWords"/> is cut at ", and / while / so /
    /// until" joints once the current piece holds at least 18 words; words are never changed
    /// except the joint itself and the next capital.</summary>
    public static string SplitRunOns(string text, int maxWords = 45)
    {
        var paragraphs = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        for (var p = 0; p < paragraphs.Length; p++)
        {
            var sentences = SentenceSplitter.Split(paragraphs[p]);
            var rebuilt = new List<string>();
            foreach (var sentence in sentences)
            {
                if (WordCount(sentence) <= maxWords) { rebuilt.Add(sentence); continue; }
                var pieces = new List<string>();
                var start = 0;
                foreach (Match joint in ClauseJoint().Matches(sentence))
                {
                    var head = sentence[start..joint.Index];
                    var tail = sentence[(joint.Index + joint.Length)..];
                    if (WordCount(head) < 18 || WordCount(tail) < 8) continue;
                    pieces.Add(Capitalize(head.Trim().TrimEnd(',', ';')) + ".");
                    start = joint.Index + joint.Length;
                    if (WordCount(sentence[start..]) <= maxWords) break;
                }
                pieces.Add(Capitalize(sentence[start..].Trim()));
                rebuilt.AddRange(pieces);
            }
            paragraphs[p] = string.Join(' ', rebuilt);
        }
        return string.Join("\n\n", paragraphs);
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static int WordCount(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>Short sentences (≤8 words) — the stock lines a small model tends to recycle.</summary>
    public static IEnumerable<string> StockLines(string text) =>
        SentenceSplitter.Split(text)
            .Where(s => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 8)
            .Select(Normalize)
            .Where(s => s.Length > 0);

    private static IEnumerable<string> Grams(string normalized, int n)
    {
        var w = normalized.Split(' ');
        for (var i = 0; i + n <= w.Length; i++) yield return string.Join(' ', w, i, n);
    }

    private static double Overlap(string normalized, HashSet<string> tailGrams)
    {
        var grams = Grams(normalized, 5).ToList();
        if (grams.Count == 0 || tailGrams.Count == 0) return 0;
        return grams.Count(tailGrams.Contains) / (double)grams.Count;
    }
}
