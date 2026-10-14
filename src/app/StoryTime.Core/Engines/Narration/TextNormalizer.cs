using System.Globalization;
using System.Text.RegularExpressions;

namespace StoryTime.Core.Engines.Narration;

/// <summary>
/// Turns a written story piece into the exact string handed to the speech voice. It never
/// changes WHERE the narration pauses (the segmenter and the pause map own that); it only
/// removes things voices read badly:
/// <list type="bullet">
/// <item>digits, ordinals, clock times and simple decimals become words;</item>
/// <item>the few abbreviations bedtime prose uses become words (Mr., Mrs., Dr., St.);</item>
/// <item>em/en dashes and semicolons become commas (a breath, not a hard stop);</item>
/// <item>quote marks are dropped (the segmenter already knows what is dialogue);</item>
/// <item>a trailing "…" becomes "," when the sentence continues in the next utterance
/// (continuing intonation) and "." when it ends one — the pause itself is spliced silence,
/// and the dots make some voices sing the last word.</item>
/// </list>
/// A dialogue fragment keeps its trailing comma ("Look," | whispered Pip): the comma's
/// continuing contour is what makes it sound like speech that leads into its tag (review
/// round 1 measured the "." version as flat and final).
/// '!' and '?' are KEPT everywhere: neural voices carry their contour, and founder feedback
/// (2026-10-14) was that flattened narration sounds bored. Deterministic and culture-invariant.
/// </summary>
public static partial class TextNormalizer
{
    /// <summary>Normalise one segment for speech.</summary>
    /// <param name="text">Raw segment text.</param>
    /// <param name="continues">True when the sentence continues in the next utterance (a
    /// mid-sentence "…" split, or dialogue followed by its tag).</param>
    public static string ForSpeech(string text, bool continues = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var s = text.Replace("...", "…").Replace(". . .", "…");

        // Quote marks (straight, curly, guillemets) — dialogue is already a separate segment.
        s = QuoteChars().Replace(s, string.Empty);
        // Leading/trailing single quotes used as dialogue marks (apostrophes inside words stay).
        s = EdgeSingleQuote().Replace(s, string.Empty);

        s = s.Replace('—', ',').Replace('–', ',').Replace(';', ',').Replace("&", " and ");
        s = Abbreviations(s);
        s = Numbers(s);

        s = MultiSpace().Replace(s, " ").Trim();
        s = SpaceBeforePunct().Replace(s, "$1");
        s = RepeatedCommas().Replace(s, ",");
        s = s.TrimStart(',', ' ');

        // Ellipsis → comma when the sentence continues, full stop when it ends; a mid-text
        // ellipsis (only when the segmenter could not split there) → comma.
        if (s.EndsWith('…')) s = s.TrimEnd('…', ' ') + (continues ? "," : ".");
        s = s.Replace("…", ",");
        s = RepeatedCommas().Replace(s, ",");

        if (s.Length == 0) return s;
        // ".," / ",." collisions from the substitutions above.
        s = s.Replace(",.", ".").Replace(".,", ".");
        return s.Trim();
    }

    private static string Abbreviations(string s)
    {
        s = Regex.Replace(s, @"\bMrs\.", "Missus");
        s = Regex.Replace(s, @"\bMr\.", "Mister");
        s = Regex.Replace(s, @"\bMs\.", "Miz");
        s = Regex.Replace(s, @"\bDr\.", "Doctor");
        // "St. Nicholas" → Saint; "Elm St." → Street.
        s = Regex.Replace(s, @"\bSt\.(?=\s+[A-Z])", "Saint");
        s = Regex.Replace(s, @"\bSt\.", "Street");
        return s;
    }

    private static string Numbers(string s)
    {
        // Clock times 7:30 → seven thirty; 7:05 → seven oh five; 7:00 → seven o'clock.
        s = ClockTime().Replace(s, m =>
        {
            var h = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var min = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            var hw = Cardinal(h);
            return min switch
            {
                0 => hw + " o'clock",
                < 10 => hw + " oh " + Cardinal(min),
                _ => hw + " " + Cardinal(min),
            };
        });
        // Ordinals 1st/2nd/3rd/4th…
        s = Ordinal().Replace(s, m => OrdinalWord(long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)));
        // Decimals 2.5 → two point five.
        s = Decimal().Replace(s, m =>
            Cardinal(long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)) + " point " +
            string.Join(' ', m.Groups[2].Value.Select(c => Cardinal(c - '0'))));
        // Integers, with optional thousands separators.
        s = Integer().Replace(s, m =>
        {
            var digits = m.Value.Replace(",", string.Empty);
            return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n < 1_000_000_000
                ? Cardinal(n)
                : string.Join(' ', digits.Select(c => Cardinal(c - '0')));
        });
        return s;
    }

    private static readonly string[] Ones =
        ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven",
         "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen"];
    private static readonly string[] Tens =
        ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];

    /// <summary>English cardinal words, 0 ≤ n &lt; 1e9 ("one hundred and five").</summary>
    public static string Cardinal(long n)
    {
        if (n < 0) return "minus " + Cardinal(-n);
        if (n < 20) return Ones[n];
        if (n < 100) return Tens[n / 10] + (n % 10 == 0 ? "" : "-" + Ones[n % 10]);
        if (n < 1000)
            return Ones[n / 100] + " hundred" + (n % 100 == 0 ? "" : " and " + Cardinal(n % 100));
        if (n < 1_000_000)
            return Cardinal(n / 1000) + " thousand" + Rest(n % 1000);
        return Cardinal(n / 1_000_000) + " million" + Rest(n % 1_000_000);

        static string Rest(long r) => r == 0 ? "" : r < 100 ? " and " + Cardinal(r) : " " + Cardinal(r);
    }

    /// <summary>English ordinal words ("first", "twenty-second").</summary>
    public static string OrdinalWord(long n)
    {
        var card = Cardinal(n);
        // Replace the last word's ending.
        var lastSep = Math.Max(card.LastIndexOf(' '), card.LastIndexOf('-'));
        var last = card[(lastSep + 1)..];
        var head = card[..(lastSep + 1)];
        var ord = last switch
        {
            "one" => "first",
            "two" => "second",
            "three" => "third",
            "five" => "fifth",
            "eight" => "eighth",
            "nine" => "ninth",
            "twelve" => "twelfth",
            _ when last.EndsWith('y') => last[..^1] + "ieth",
            _ => last + "th",
        };
        return head + ord;
    }

    [GeneratedRegex("[\"“”«»„]")]
    private static partial Regex QuoteChars();
    [GeneratedRegex(@"(^['‘]+|['‘’]+$|(?<=\s)['‘](?=\w)|(?<=[,.!?…])['’](?=\s|$))")]
    private static partial Regex EdgeSingleQuote();
    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpace();
    [GeneratedRegex(@"\s+([,.!?…])")]
    private static partial Regex SpaceBeforePunct();
    [GeneratedRegex(@"(,\s*){2,}")]
    private static partial Regex RepeatedCommas();
    [GeneratedRegex(@"\b(\d{1,2}):(\d{2})\b")]
    private static partial Regex ClockTime();
    [GeneratedRegex(@"\b(\d+)(?:st|nd|rd|th)\b")]
    private static partial Regex Ordinal();
    [GeneratedRegex(@"\b(\d+)\.(\d+)\b")]
    private static partial Regex Decimal();
    [GeneratedRegex(@"\b\d{1,3}(?:,\d{3})+\b|\b\d+\b")]
    private static partial Regex Integer();
}
