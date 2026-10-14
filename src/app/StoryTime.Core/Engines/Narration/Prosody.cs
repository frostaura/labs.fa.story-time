using System.Globalization;
using StoryTime.Core.Abstractions;

namespace StoryTime.Core.Engines.Narration;

/// <summary>
/// The measured pause map (docs/research/voice-lab.md, 2026-10-14). Every pause is spliced
/// silence measured AFTER each utterance's own edge silence has been trimmed, because
/// <c>pre/postUtteranceDelay</c> are ignored when rendering to a buffer and SSML breaks land
/// differently on every voice. Spliced silence measured within ~80 ms of target on every voice.
/// Supersedes the content-aware ProsodyDirector (2026-07-30), whose jittered sub-second gaps
/// were tuned for a different engine.
/// </summary>
public static class PauseMap
{
    public const double SentenceSeconds = 0.650;
    public const double BeforeDialogueSeconds = 0.450;
    public const double DialogueTagSeconds = 0.250;
    public const double AfterTagSeconds = 0.600;
    public const double EllipsisSeconds = 0.750;
    public const double ParagraphSeconds = 1.400;
    /// <summary>Extrapolated in the lab (no measured scene breaks yet).</summary>
    public const double SceneSeconds = 2.200;
    public const double BeforeEndingSeconds = 1.800;
    public const double GoodnightPairSeconds = 1.100;
    public const double StoryHeadSeconds = 0.600;
    public const double StoryTailSeconds = 1.500;
    /// <summary>Of a window seam's pause, this much is carried at the END of the window
    /// before it (so its room tail rings out instead of being cut) and the rest at the head
    /// of the next. The two halves always sum to the seam pause.</summary>
    public const double SeamCarrySeconds = 0.500;
}

/// <summary>Why a pause has the length it has (one row of the plan report).</summary>
public enum PauseKind
{
    Head,
    Sentence,
    BeforeDialogue,
    DialogueTag,
    AfterTag,
    Ellipsis,
    Paragraph,
    Scene,
    BeforeEnding,
    GoodnightPair,
    WindowSeam,
}

/// <summary>Tunables of the SoothingNarrator recipe. Defaults are the founder-chosen
/// "expressive" recipe (2026-10-14, heard on Serena Premium): the lab recipe with base rate
/// 0.44, ±0.02 seeded per-sentence rate variation, dialogue at 0.48, and the ending ramping
/// 0.12 below base.</summary>
public sealed record SoothingOptions
{
    /// <summary>AVSpeechUtterance.rate for narration (0.5 is the system default).</summary>
    public float BaseRate { get; init; } = 0.44f;
    /// <summary>The final paragraph's rate ramps linearly from base down by this much.</summary>
    public float EndingRateDrop { get; init; } = 0.12f;
    /// <summary>Dialogue speaks this much faster than base (0.44 → 0.48).</summary>
    public float DialogueRateDelta { get; init; } = 0.04f;
    public float DialoguePitch { get; init; } = 1.08f;
    public double DialogueGainDb { get; init; } = -2.0;
    /// <summary>Gain at the last sentence of the ending (linear ramp from 0 dB).</summary>
    public double EndingGainDb { get; init; } = -6.0;
    public float GoodnightPitch { get; init; } = 0.97f;
    /// <summary>Dialogue tags ("…," whispered Pip) read a little lower and softer, as a
    /// storyteller does (review round 1: tags sat above the narration median).</summary>
    public float TagPitch { get; init; } = 0.95f;
    public double TagGainDb { get; init; } = -1.0;
    /// <summary>Deterministic per-sentence pitch variation (± this fraction, 5 steps) on
    /// narration and tags, so the contour does not settle into one register.</summary>
    public float PitchVariation { get; init; } = 0.03f;
    /// <summary>The slow-down and fade spread over at least this many utterances (extending
    /// back before the final paragraph when it is short), so the ending glides, not steps.</summary>
    public int EndingMinSegments { get; init; } = 5;
    /// <summary>Deterministic per-sentence rate variation: ± this much in steps of half of
    /// it, seeded by the sentence, so neighbouring sentences never share one metronomic tempo
    /// (founder feedback 2026-10-14: "monotone, as if the reader is bored"). Never applied
    /// to the ending, whose slow-down is deliberate.</summary>
    public float RateVariation { get; init; } = 0.02f;
    /// <summary>Ending = the last paragraph when it has at most this many sentences,
    /// otherwise the last <see cref="EndingFallbackSentences"/> sentences.</summary>
    public int EndingMaxSentences { get; init; } = 6;
    public int EndingFallbackSentences { get; init; } = 3;

    /// <summary>Lay the ambient pad under the voice.</summary>
    public bool Pad { get; init; } = true;
    /// <summary>Extra lead-in, pad only, before the head pause (story start only).</summary>
    public double PadLeadSeconds { get; init; } = 3.0;
    /// <summary>Music-only tail after the story tail pause (story end only).</summary>
    public double PadTailSeconds { get; init; } = 5.0;
    public double PadFadeInSeconds { get; init; } = 3.0;
    public double PadFadeOutSeconds { get; init; } = 5.0;
    public double HeadSeconds { get; init; } = PauseMap.StoryHeadSeconds;
    public double TailSeconds { get; init; } = PauseMap.StoryTailSeconds;

    /// <summary>Voice-sample profile: the full chain, with a short lead and tail so a ▶ tap
    /// answers in under a second.</summary>
    public static SoothingOptions Sample => new()
    {
        PadLeadSeconds = 0.3,
        PadFadeInSeconds = 0.6,
        PadTailSeconds = 1.5,
        PadFadeOutSeconds = 1.8,
        HeadSeconds = 0.3,
        TailSeconds = 0.6,
    };
}

/// <summary>One utterance of the plan: what to say, how, and the silence before it.</summary>
public sealed record PlannedSegment
{
    public required int Index { get; init; }
    public required int SentenceIndex { get; init; }
    public required SegmentKind Kind { get; init; }
    /// <summary>Written text (for reports).</summary>
    public required string Source { get; init; }
    /// <summary>Exactly what the voice is given.</summary>
    public required string Spoken { get; init; }
    public required double PauseBeforeSeconds { get; init; }
    public required PauseKind PauseBefore { get; init; }
    public required float Rate { get; init; }
    public required float Pitch { get; init; }
    /// <summary>Performance gain applied after mastering (dialogue −2 dB, ending ramp).</summary>
    public required double GainDb { get; init; }
    /// <summary>The ending-ramp share of <see cref="GainDb"/>. Excluded from the loudness
    /// reference so the normaliser cannot undo the bedtime fade.</summary>
    public required double EndingGainDb { get; init; }
    public required bool InEnding { get; init; }
}

/// <summary>A complete, serialisable narration plan for one script (a story, a window of
/// one, a teaser or a voice sample). Pure data — the Mac voice-lab harness renders the same
/// plan the app renders.</summary>
public sealed record NarrationPlan
{
    public required List<PlannedSegment> Segments { get; init; }
    /// <summary>Sentence count of the source script (timestamps are emitted for every one).</summary>
    public required int SentenceCount { get; init; }
    /// <summary>Silence (pad only) before the first segment.</summary>
    public required double HeadSeconds { get; init; }
    public required PauseKind HeadKind { get; init; }
    /// <summary>Silence (pad only) after the last segment.</summary>
    public required double TailSeconds { get; init; }
    public required bool Pad { get; init; }
    public required double PadFadeInSeconds { get; init; }
    public required double PadFadeOutSeconds { get; init; }
    public required double TimelineOffsetSeconds { get; init; }

    /// <summary>Planned silence total (head, gaps, tail), seconds.</summary>
    public double PlannedSilenceSeconds => HeadSeconds + TailSeconds + Segments.Skip(1).Sum(s => s.PauseBeforeSeconds);
}

/// <summary>
/// Builds the SoothingNarrator plan: segmentation, the measured pause map, and per-segment
/// rate / pitch / gain. Pure and deterministic — the same script always yields the same plan.
/// </summary>
public static class SoothingPlanner
{
    public static NarrationPlan Plan(NarrationScript script, SoothingOptions? options = null)
    {
        var o = options ?? new SoothingOptions();
        var pieces = NarrationSegmenter.Segment(script);

        var storyStart = script.LeadingSilenceSeconds <= 0;
        var storyEnds = script.AppendFinalTail && !script.IsExcerpt;
        var (endingStart, endingAtParagraph) = storyEnds ? FindEnding(script, o) : (int.MaxValue, false);

        // Ending pieces, for the linear ramps: the final paragraph, extended back to at least
        // EndingMinSegments utterances so a short ending still glides rather than steps.
        var firstEnding = pieces.FindIndex(p => p.SentenceIndex >= endingStart);
        if (firstEnding >= 0) firstEnding = Math.Max(0, Math.Min(firstEnding, pieces.Count - o.EndingMinSegments));
        var endingCount = firstEnding < 0 ? 0 : pieces.Count - firstEnding;

        var segments = new List<PlannedSegment>(pieces.Count);
        var dialogueSentences = pieces.Where(p => p.Kind == SegmentKind.Dialogue).Select(p => p.SentenceIndex).ToHashSet();
        for (var k = 0; k < pieces.Count; k++)
        {
            var piece = pieces[k];
            var (pause, kind) = k == 0 ? (0.0, PauseKind.Head) : PauseBetween(pieces[k - 1], piece, script, endingStart, endingAtParagraph, dialogueSentences);

            var inEnding = firstEnding >= 0 && k >= firstEnding;
            var rampT = !inEnding ? 0.0 : endingCount <= 1 ? 0.5 : (k - firstEnding) / (double)(endingCount - 1);
            var sentence = script.SentenceList[piece.SentenceIndex];

            float rate;
            var pitch = 1.0f;
            var gainDb = 0.0;
            switch (piece.Kind)
            {
                case SegmentKind.Dialogue:
                    // Dialogue speaks at a fixed, slightly quicker rate (0.48), never jittered.
                    rate = o.BaseRate + o.DialogueRateDelta;
                    pitch *= o.DialoguePitch;
                    gainDb += o.DialogueGainDb;
                    break;
                case SegmentKind.Tag:
                    // Tags sit at a fixed, lower pitch: they recede behind the dialogue.
                    rate = o.BaseRate + Variation(sentence, piece.SentenceIndex, o.RateVariation);
                    pitch *= o.TagPitch;
                    gainDb += o.TagGainDb;
                    break;
                default:
                    rate = o.BaseRate + Variation(sentence, piece.SentenceIndex, o.RateVariation);
                    pitch *= 1 + PitchVariation(sentence, piece.SentenceIndex, o.PitchVariation);
                    break;
            }
            var endingDb = 0.0;
            if (inEnding)
            {
                // The ending replaces the jitter with a steady glide down from base.
                var glide = (float)(o.EndingRateDrop * rampT);
                rate = (piece.Kind == SegmentKind.Dialogue ? o.BaseRate + o.DialogueRateDelta : o.BaseRate) - glide;
                if (piece.Kind != SegmentKind.Dialogue) pitch = piece.Kind == SegmentKind.Tag ? o.TagPitch : 1.0f;
                endingDb = o.EndingGainDb * rampT;
                gainDb += endingDb;
            }
            if (NarrationSegmenter.IsGoodnightLine(piece.Raw)) pitch *= o.GoodnightPitch;
            rate = Math.Clamp(rate, 0.0f, 1.0f);
            pitch = Math.Clamp(pitch, 0.5f, 2.0f);

            segments.Add(new PlannedSegment
            {
                Index = k,
                SentenceIndex = piece.SentenceIndex,
                Kind = piece.Kind,
                Source = piece.Raw,
                Spoken = TextNormalizer.ForSpeech(piece.Raw, continues: !piece.IsLastInSentence),
                PauseBeforeSeconds = pause,
                PauseBefore = kind,
                Rate = (float)Math.Round(rate, 4),
                Pitch = (float)Math.Round(pitch, 4),
                GainDb = Clean(gainDb),
                EndingGainDb = Clean(endingDb),
                InEnding = inEnding,
            });
        }
        // Drop anything that normalised to nothing (a lone glyph) — keep the pause of the
        // first dropped piece's successor unchanged.
        segments = segments.Where(s => s.Spoken.Any(char.IsLetterOrDigit)).Select((s, i) => s with { Index = i }).ToList();

        // A window that continues the story carries SeamCarrySeconds of the seam pause as its
        // tail; the next window's head is the seam pause minus that carry.
        var head = storyStart
            ? o.HeadSeconds + (o.Pad ? o.PadLeadSeconds : 0)
            : Math.Max(0, script.LeadingSilenceSeconds - PauseMap.SeamCarrySeconds);
        var tail = script.AppendFinalTail ? o.TailSeconds + (o.Pad ? o.PadTailSeconds : 0) : PauseMap.SeamCarrySeconds;

        return new NarrationPlan
        {
            Segments = segments,
            SentenceCount = script.SentenceList.Count,
            HeadSeconds = head,
            HeadKind = storyStart ? PauseKind.Head : PauseKind.WindowSeam,
            TailSeconds = tail,
            Pad = o.Pad,
            PadFadeInSeconds = storyStart && o.Pad ? o.PadFadeInSeconds : 0,
            PadFadeOutSeconds = script.AppendFinalTail && o.Pad ? o.PadFadeOutSeconds : 0,
            TimelineOffsetSeconds = script.TimelineOffsetSeconds,
        };
    }

    /// <summary>Pause before <paramref name="s"/> given the piece before it. Precedence:
    /// scene &gt; ending &gt; paragraph &gt; goodnight beat &gt; ellipsis &gt; after-tag &gt;
    /// before-dialogue &gt; sentence. Inside one sentence: ellipsis, else dialogue↔tag.</summary>
    private static (double Seconds, PauseKind Kind) PauseBetween(NarrationPiece p, NarrationPiece s, NarrationScript script,
        int endingStart, bool endingAtParagraph, HashSet<int> dialogueSentences)
    {
        if (p.SentenceIndex == s.SentenceIndex)
            return p.EndsWithEllipsis ? (PauseMap.EllipsisSeconds, PauseKind.Ellipsis) : (PauseMap.DialogueTagSeconds, PauseKind.DialogueTag);

        var i = s.SentenceIndex;
        if (script.SceneStartSet.Contains(i)) return (PauseMap.SceneSeconds, PauseKind.Scene);
        if (i == endingStart && endingAtParagraph) return (PauseMap.BeforeEndingSeconds, PauseKind.BeforeEnding);
        if (script.ParagraphStartSet.Contains(i)) return (PauseMap.ParagraphSeconds, PauseKind.Paragraph);
        // Goodnight lines get their own held beat: between the pair, and before the first
        // one at the very end of the story (review round 1: a plain 650 ms ran straight in).
        if (NarrationSegmenter.IsGoodnightLine(script.SentenceList[i])
            && (NarrationSegmenter.IsGoodnightLine(script.SentenceList[p.SentenceIndex]) || i >= endingStart))
            return (PauseMap.GoodnightPairSeconds, PauseKind.GoodnightPair);
        if (p.EndsWithEllipsis) return (PauseMap.EllipsisSeconds, PauseKind.Ellipsis);
        if (p.Kind == SegmentKind.Tag && dialogueSentences.Contains(p.SentenceIndex)) return (PauseMap.AfterTagSeconds, PauseKind.AfterTag);
        if (s.Kind == SegmentKind.Dialogue) return (PauseMap.BeforeDialogueSeconds, PauseKind.BeforeDialogue);
        return (PauseMap.SentenceSeconds, PauseKind.Sentence);
    }

    /// <summary>Rounded to 3 decimals, with negative zero normalised away.</summary>
    private static double Clean(double db)
    {
        var r = Math.Round(db, 3);
        return r == 0 ? 0 : r;
    }

    /// <summary>First sentence of the ending and whether it opens a paragraph.</summary>
    private static (int Start, bool AtParagraph) FindEnding(NarrationScript script, SoothingOptions o)
    {
        var count = script.SentenceList.Count;
        if (count == 0) return (int.MaxValue, false);
        var lastParagraph = script.ParagraphStartSet.Count == 0 ? 0 : script.ParagraphStartSet.Max();
        if (lastParagraph > 0 && count - lastParagraph <= o.EndingMaxSentences) return (lastParagraph, true);
        return (Math.Max(0, count - o.EndingFallbackSentences), false);
    }

    /// <summary>Deterministic per-sentence rate offset in {−v, −v/2, 0, +v/2, +v} from an
    /// FNV-1a hash of the sentence text and its index (platform-stable, no GetHashCode).</summary>
    public static float Variation(string sentence, int index, float max)
    {
        if (max <= 0) return 0;
        var h = 2166136261u;
        foreach (var c in sentence) h = (h ^ c) * 16777619u;
        h ^= unchecked((uint)index * 2654435761u);
        h = (h ^ (h >> 15)) * 2246822519u;
        var step = (int)((h >> 13) % 5) - 2;          // −2..+2
        return step * max / 2f;
    }

    /// <summary>Deterministic per-sentence pitch offset, never zero: ±max/2 or ±max, so no
    /// narration line sits exactly on the base register (review round 2).</summary>
    public static float PitchVariation(string sentence, int index, float max)
    {
        if (max <= 0) return 0;
        var step = Variation(sentence, index + 7919, 2f);   // −2..+2 in steps of 1
        if (step == 0) step = (index & 1) == 0 ? 1f : -1f;
        return step * max / 2f;
    }

    /// <summary>One plan row per segment: "[12] s3 Dialogue +0.45s(BeforeDialogue) r0.42 p1.08 -2.0dB | text".</summary>
    public static string Describe(NarrationPlan plan) =>
        string.Join('\n', plan.Segments.Select(s => string.Create(CultureInfo.InvariantCulture,
            $"[{s.Index}] s{s.SentenceIndex} {s.Kind} +{s.PauseBeforeSeconds:0.000}s({s.PauseBefore}) r{s.Rate:0.000} p{s.Pitch:0.00} {s.GainDb:+0.0;-0.0}dB | {s.Spoken}")));
}
