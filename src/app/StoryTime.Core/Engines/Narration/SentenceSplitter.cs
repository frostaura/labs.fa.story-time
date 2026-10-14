using System.Text;

namespace StoryTime.Core.Engines.Narration;

/// <summary>
/// Splits story prose into narration sentences for per-sentence TTS synthesis.
/// Deliberately simple and deterministic: terminators . ! ? … (with closing quotes attached),
/// an em-dash acts as a soft terminator when what follows starts a new sentence
/// (capital letter or opening quote),
/// abbreviation-safe for the tiny set that plausibly appears in bedtime prose,
/// and short-fragment merging so the TTS never receives a 1-2 word "sentence".
/// </summary>
public static class SentenceSplitter
{
    private static readonly string[] Abbreviations = ["Mr.", "Mrs.", "Ms.", "Dr.", "St."];

    public static List<string> Split(string text) => SplitScript(text).SentenceList;

    /// <summary>
    /// Paragraph-aware split: sentences plus the set of indices that open a new paragraph
    /// (any newline-separated block boundary). The TTS engine turns paragraph starts into
    /// longer scene-beat pauses; index 0 is never included.
    /// </summary>
    public static Abstractions.NarrationScript SplitScript(string text)
    {
        var sentences = new List<string>();
        var paragraphStarts = new HashSet<int>();
        var sceneStarts = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(text))
            return new Abstractions.NarrationScript { SentenceList = sentences, ParagraphStartSet = paragraphStarts, SceneStartSet = sceneStarts };

        var normalized = text.Replace("\r\n", "\n").Replace('’', '\'');
        var pendingScene = false;
        var blankRun = 0;
        foreach (var block in normalized.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(block))
            {
                // Two or more blank lines between paragraphs mark a scene change.
                if (++blankRun >= 2) pendingScene = true;
                continue;
            }
            blankRun = 0;
            if (IsSceneMarker(block))
            {
                pendingScene = true;               // "***" / "⁂" / "~": never spoken
                continue;
            }
            var paragraph = SplitParagraph(block);
            if (paragraph.Count == 0) continue;
            if (sentences.Count > 0)
            {
                paragraphStarts.Add(sentences.Count);
                if (pendingScene) sceneStarts.Add(sentences.Count);
            }
            pendingScene = false;
            sentences.AddRange(paragraph);
        }
        return new Abstractions.NarrationScript { SentenceList = sentences, ParagraphStartSet = paragraphStarts, SceneStartSet = sceneStarts };
    }

    /// <summary>A line made only of break glyphs (*, ⁂, ~, #, •, ·) and spaces.</summary>
    public static bool IsSceneMarker(string line)
    {
        var t = line.Trim();
        if (t.Length == 0) return false;
        foreach (var c in t)
            if (c is not ('*' or '⁂' or '~' or '#' or '•' or '·' or ' ')) return false;
        return true;
    }

    private static List<string> SplitParagraph(string normalized)
    {
        var sentences = new List<string>();
        var current = new StringBuilder();

        for (var i = 0; i < normalized.Length; i++)
        {
            var c = normalized[i];
            current.Append(c);
            if (c == '—')
            {
                // Em-dash is a soft terminator ONLY when the next non-space char opens a
                // new sentence (capital or quote): "held her breath — And then…" splits;
                // "clues — a thread of song" stays one sentence.
                var k = i + 1;
                while (k < normalized.Length && normalized[k] == ' ') k++;
                if (k < normalized.Length && (char.IsUpper(normalized[k]) || normalized[k] is '"' or '“' or '\'' or '‘'))
                    FlushIfAny(current, sentences);
                continue;
            }
            if (c is '.' or '!' or '?' or '…')
            {
                if (c == '.' && EndsWithAbbreviation(current)) continue;
                // attach closing quote / bracket to the sentence
                while (i + 1 < normalized.Length && normalized[i + 1] is '"' or '\'' or '”' or ')')
                {
                    current.Append(normalized[++i]);
                }
                // dialogue attribution continues the sentence: "Sleep well!" said Mo.
                var j = i + 1;
                while (j < normalized.Length && normalized[j] == ' ') j++;
                if (j < normalized.Length && char.IsLower(normalized[j])) continue;
                FlushIfAny(current, sentences);
            }
        }
        FlushIfAny(current, sentences);

        return MergeShortFragments(sentences);
    }

    private static bool EndsWithAbbreviation(StringBuilder sb)
    {
        var s = sb.ToString();
        return Abbreviations.Any(a => s.EndsWith(a, StringComparison.Ordinal));
    }

    private static void FlushIfAny(StringBuilder current, List<string> sentences)
    {
        var s = current.ToString().Trim();
        current.Clear();
        if (s.Length > 0) sentences.Add(s);
    }

    private static List<string> MergeShortFragments(List<string> sentences)
    {
        const int minChars = 6;
        var merged = new List<string>();
        foreach (var s in sentences)
        {
            if (merged.Count > 0 && (s.Length < minChars || merged[^1].Length < minChars))
                merged[^1] = merged[^1] + " " + s;
            else
                merged.Add(s);
        }
        return merged;
    }
}
