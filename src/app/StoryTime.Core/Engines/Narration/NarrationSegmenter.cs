namespace StoryTime.Core.Engines.Narration;

/// <summary>What a narration segment is, for pausing and performance.</summary>
public enum SegmentKind
{
    /// <summary>Plain storytelling.</summary>
    Narration,
    /// <summary>Text inside quotation marks.</summary>
    Dialogue,
    /// <summary>Narration sharing a sentence with dialogue ("…," whispered Mum).</summary>
    Tag,
}

/// <summary>One piece of a sentence that becomes exactly one utterance.</summary>
public sealed record NarrationPiece
{
    public required int SentenceIndex { get; init; }
    public required SegmentKind Kind { get; init; }
    /// <summary>The written text of the piece (quotes included), for classification.</summary>
    public required string Raw { get; init; }
    /// <summary>The piece ends in "…" — the next piece (or sentence) waits the ellipsis pause.</summary>
    public required bool EndsWithEllipsis { get; init; }
    /// <summary>A dialogue piece immediately followed by its tag in the same sentence.</summary>
    public required bool ClosesBeforeTag { get; init; }
    /// <summary>Last piece of its sentence.</summary>
    public required bool IsLastInSentence { get; init; }
}

/// <summary>
/// Splits narration sentences into utterance pieces per the measured recipe
/// (docs/research/voice-lab.md): one utterance per sentence; a dialogue quote and its tag are
/// separate pieces; "…" splits a sentence; commas NEVER split (voices already breathe there
/// for 150–300 ms). Quote state carries across sentences of the same paragraph, so a quote
/// that spans two sentences stays dialogue in both. Deterministic.
/// </summary>
public static class NarrationSegmenter
{
    public static List<NarrationPiece> Segment(Abstractions.NarrationScript script)
    {
        var pieces = new List<NarrationPiece>();
        var quote = QuoteState.None;
        for (var i = 0; i < script.SentenceList.Count; i++)
        {
            if (script.ParagraphStartSet.Contains(i)) quote = QuoteState.None;
            var sentence = script.SentenceList[i].Trim();
            if (sentence.Length == 0) continue;
            var runs = QuoteRuns(sentence, ref quote);
            var hasDialogue = runs.Any(r => r.Quoted);

            var sentencePieces = new List<NarrationPiece>();
            for (var r = 0; r < runs.Count; r++)
            {
                var kind = runs[r].Quoted ? SegmentKind.Dialogue : hasDialogue ? SegmentKind.Tag : SegmentKind.Narration;
                var nextIsTag = r + 1 < runs.Count && !runs[r + 1].Quoted && runs[r].Quoted;
                var parts = SplitAtEllipses(runs[r].Text);
                for (var p = 0; p < parts.Count; p++)
                {
                    var lastPart = p == parts.Count - 1;
                    sentencePieces.Add(new NarrationPiece
                    {
                        SentenceIndex = i,
                        Kind = kind,
                        Raw = parts[p],
                        EndsWithEllipsis = EndsWithEllipsis(parts[p]),
                        ClosesBeforeTag = lastPart && nextIsTag,
                        IsLastInSentence = false,
                    });
                }
            }
            if (sentencePieces.Count == 0) continue;
            sentencePieces[^1] = sentencePieces[^1] with { IsLastInSentence = true };
            pieces.AddRange(sentencePieces);
        }
        return pieces;
    }

    private enum QuoteState { None, Double, Single }

    private readonly record struct Run(string Text, bool Quoted);

    /// <summary>Split a sentence into quoted / unquoted runs. Quote characters stay with the
    /// quoted run. Runs without a letter or digit are folded into a neighbour.</summary>
    private static List<Run> QuoteRuns(string s, ref QuoteState state)
    {
        var runs = new List<Run>();
        var current = new System.Text.StringBuilder();
        var currentQuoted = state != QuoteState.None;

        void Flush(bool quotedNext)
        {
            if (current.Length > 0)
            {
                var text = current.ToString().Trim();
                if (text.Length > 0) runs.Add(new Run(text, currentQuoted));
                current.Clear();
            }
            currentQuoted = quotedNext;
        }

        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            var prev = i > 0 ? s[i - 1] : ' ';
            var next = i + 1 < s.Length ? s[i + 1] : ' ';

            if (state == QuoteState.None)
            {
                var opensDouble = c is '"' or '“' or '«';
                var opensSingle = c == '‘' || (c == '\'' && (i == 0 || char.IsWhiteSpace(prev)) && char.IsLetter(next));
                if (opensDouble || opensSingle)
                {
                    Flush(quotedNext: true);
                    state = opensDouble ? QuoteState.Double : QuoteState.Single;
                    current.Append(c);
                    continue;
                }
                current.Append(c);
            }
            else
            {
                current.Append(c);
                var closesDouble = state == QuoteState.Double && c is '"' or '”' or '»';
                // A single quote closes dialogue only when no letter follows (so "don't" and
                // "Mila's" inside the quote never end it).
                var closesSingle = state == QuoteState.Single && c is '\'' or '’' && !char.IsLetter(next);
                if (closesDouble || closesSingle)
                {
                    state = QuoteState.None;
                    Flush(quotedNext: false);
                }
            }
        }
        Flush(currentQuoted);

        // Fold punctuation-only runs (a stray "." after a closing quote) into the previous run.
        var folded = new List<Run>();
        foreach (var run in runs)
        {
            if (!run.Text.Any(char.IsLetterOrDigit))
            {
                if (folded.Count > 0) folded[^1] = folded[^1] with { Text = folded[^1].Text + run.Text };
                continue;
            }
            folded.Add(run);
        }
        return folded;
    }

    /// <summary>Split after every "…" / "..." that is followed by more words.</summary>
    public static List<string> SplitAtEllipses(string text)
    {
        var parts = new List<string>();
        var t = text.Replace("...", "…");
        var start = 0;
        for (var i = 0; i < t.Length; i++)
        {
            if (t[i] != '…') continue;
            var end = i + 1;
            // keep closing quotes / punctuation glued to the ellipsis
            while (end < t.Length && t[end] is '"' or '”' or '\'' or '’' or ')' or ',' or '!' or '?') end++;
            var rest = t[end..];
            if (!rest.Any(char.IsLetterOrDigit)) break;
            var piece = t[start..end].Trim();
            if (piece.Any(char.IsLetterOrDigit)) parts.Add(piece);
            start = end;
            i = end - 1;
        }
        var tail = t[start..].Trim();
        if (tail.Any(char.IsLetterOrDigit)) parts.Add(tail);
        else if (parts.Count > 0 && tail.Length > 0) parts[^1] += tail;
        return parts;
    }

    private static bool EndsWithEllipsis(string piece)
    {
        var core = piece.TrimEnd('"', '”', '\'', '’', ')', ' ', ',', '!', '?');
        return core.EndsWith('…') || core.EndsWith("...", StringComparison.Ordinal);
    }

    /// <summary>"Goodnight, Mila." / "Good night." / "Night night." / "Sleep tight."</summary>
    public static bool IsGoodnightLine(string text)
    {
        var t = text.TrimStart(' ', '"', '“', '\'', '‘').ToLowerInvariant();
        return t.StartsWith("goodnight", StringComparison.Ordinal)
               || t.StartsWith("good night", StringComparison.Ordinal)
               || t.StartsWith("night night", StringComparison.Ordinal)
               || t.StartsWith("night-night", StringComparison.Ordinal)
               || t.StartsWith("sleep tight", StringComparison.Ordinal);
    }
}
