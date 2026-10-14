using StoryTime.Core.Engines.Narration;

namespace StoryTime.Core.Engines;

/// <summary>Deterministic prose-rhythm metrics (founder discipline: metrics, not vibes —
/// 2026-07-31 title/prose quality pass). Sentence segmentation is the canonical
/// SentenceSplitter, so these numbers describe exactly what the narrator will read.
/// Pinned by unit tests.</summary>
public static class ProseMetrics
{
    /// <summary>A sentence under this many words counts as "short" for rhythm metrics.</summary>
    public const int ShortSentenceWords = 8;

    public sealed record Report(
        int SentenceCount,
        double AvgWords,
        double StdWords,
        double CV,
        double ShortShare,
        int LongestShortRun,
        double DistinctWordRatio);

    public static Report Analyze(string text)
    {
        var sentences = SentenceSplitter.Split(text ?? string.Empty);
        if (sentences.Count == 0) return new Report(0, 0, 0, 0, 0, 0, 0);

        var lengths = sentences.Select(WordCount).ToList();
        var avg = lengths.Average();
        var std = Math.Sqrt(lengths.Average(l => (l - avg) * (l - avg)));
        var cv = avg > 0 ? std / avg : 0;

        var shortCount = 0;
        var longestRun = 0;
        var run = 0;
        foreach (var l in lengths)
        {
            if (l < ShortSentenceWords)
            {
                shortCount++;
                run++;
                longestRun = Math.Max(longestRun, run);
            }
            else
            {
                run = 0;
            }
        }

        var words = Words(text!);
        var distinctRatio = words.Count == 0 ? 0 : words.Distinct(StringComparer.OrdinalIgnoreCase).Count() / (double)words.Count;

        return new Report(sentences.Count, avg, std, cv, shortCount / (double)sentences.Count, longestRun, distinctRatio);
    }

    private static int WordCount(string sentence) =>
        sentence.Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries).Length;

    private static List<string> Words(string text) =>
        text.Split([' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim('.', ',', '!', '?', ';', ':', '"', '\'', '…', '—', '(', ')'))
            .Where(w => w.Length > 0)
            .ToList();
}

/// <summary>Title similarity + shape classification for the title-variety guard
/// (TemplateStoryEngine redraw, unit thresholds).</summary>
public static class TitleMetrics
{
    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "of", "and", "at", "in", "on", "to", "for", "with", "who", "how", "one",
    };

    /// <summary>Content tokens: lowercased alphanumeric words minus stopwords, so
    /// "The Lantern Fox" vs "A Lantern for Pip" compare on lantern/fox/pip only.</summary>
    public static IReadOnlyList<string> Tokens(string title) =>
        (title ?? string.Empty)
            .Split([' ', '-', '—', ',', '.', '!', '?', ':', ';', '"', '\''], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.ToLowerInvariant())
            .Where(t => t.Length > 0 && !Stopwords.Contains(t))
            .ToList();

    /// <summary>Jaccard similarity over content tokens (0 = disjoint, 1 = identical sets).</summary>
    public static double TokenJaccard(string a, string b)
    {
        var ta = Tokens(a).ToHashSet();
        var tb = Tokens(b).ToHashSet();
        if (ta.Count == 0 && tb.Count == 0) return 1;
        if (ta.Count == 0 || tb.Count == 0) return 0;
        var intersect = ta.Intersect(tb).Count();
        return intersect / (double)(ta.Count + tb.Count - intersect);
    }

    /// <summary>Coarse grammatical shape of a title — enough classes to prove the pool is
    /// not "The Adjective Noun" forever. Order of checks matters (question outranks all).</summary>
    public static string ShapeOf(string title)
    {
        var t = (title ?? string.Empty).Trim();
        if (t.Length == 0) return "Empty";
        var words = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (t.EndsWith('?')) return "Question";
        if (t.StartsWith("How ", StringComparison.OrdinalIgnoreCase)) return "How";
        if (t.Contains(" and the ", StringComparison.OrdinalIgnoreCase)
            || t.Contains(" and a ", StringComparison.OrdinalIgnoreCase)) return "AndPairing";
        if (words.Length <= 2 && !t.StartsWith("The ", StringComparison.OrdinalIgnoreCase)) return "Evocative";
        if (words[0].EndsWith("ing", StringComparison.OrdinalIgnoreCase)) return "Doing";
        if (t.Contains(" at the ", StringComparison.OrdinalIgnoreCase)
            || t.Contains(" in the ", StringComparison.OrdinalIgnoreCase)
            || t.Contains(" by the ", StringComparison.OrdinalIgnoreCase)) return "NamePlace";
        if (t.StartsWith("The ", StringComparison.OrdinalIgnoreCase)) return "TheOpening";
        if (t.StartsWith("One ", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("Goodnight", StringComparison.OrdinalIgnoreCase)) return "NightTurn";
        return "Other";
    }
}
