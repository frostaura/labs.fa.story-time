using System.Globalization;

namespace StoryTime.Core.Engines.Sherpa;

/// <summary>Punctuation class of an inter-sentence gap (derived from how the previous
/// sentence ends; Paragraph wins when the next sentence opens a new paragraph).</summary>
public enum GapClass
{
    Sentence,
    Emphatic,
    Ellipsis,
    EmDash,
    Paragraph,
}

/// <summary>
/// One planned inter-sentence silence. <see cref="BeforeIndex"/> is the sentence index the
/// gap precedes (matching SentenceTimestamp indices). Tags record every rule that shaped
/// the value, in application order, e.g. ["sent(0.55)", "conn(-40%)", "jit(+8%)"].
/// </summary>
public sealed record ProsodyGap
{
    public required int BeforeIndex { get; init; }
    public required GapClass Class { get; init; }
    public required double BaseSeconds { get; init; }
    public required double Seconds { get; init; }
    public required IReadOnlyList<string> Tags { get; init; }

    /// <summary>"sent(0.55) conn(-40%) jit(+8%) → 0.36s" — the harness report row core.</summary>
    public string Describe() =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{string.Join(" ", Tags)} → {Seconds:0.00}s");
}

/// <summary>
/// Content-aware prosody direction for bedtime narration (evolved from the constant-gap
/// ProsodyPlanner, founder feedback 2026-07-30: uniform pauses read as "one line at a
/// time"). Every inter-sentence pause is computed from narrative context — punctuation
/// class, connective flow, dialogue adjacency, dramatic openers, sentence length — with
/// deterministic per-gap jitter and an explicit anti-monotony pass. Same input → same
/// gaps, on every platform (pure integer hashing, no string.GetHashCode).
/// All values are seconds unless noted.
/// </summary>
public static class ProsodyDirector
{
    /// <summary>Median speaking speed (sherpa "speed"; &lt;1 is slower). Bedtime pace —
    /// slowed 2026-07-30 by founder ear ("could be a little slower").</summary>
    public const float BaseSpeed = 0.85f;
    /// <summary>Max deviation applied deterministically per sentence so pacing breathes.</summary>
    public const float SpeedJitter = 0.02f;

    /// <summary>Pause between clause chunks when an overlong sentence is split at a comma.</summary>
    public const double CommaPauseSeconds = 0.24;
    /// <summary>Soft tail of silence at the very end of the file.</summary>
    public const double FinalTailSeconds = 0.60;

    // ---- Base pause per punctuation class. Means sit slightly below the old constants
    // (sentence 0.68 → 0.55 etc.) because context modifiers now add the drama the old
    // flat values tried to bake in.
    public const double SentenceBaseSeconds = 0.55;
    public const double EmphaticBaseSeconds = 0.70;
    public const double EllipsisBaseSeconds = 0.85;
    /// <summary>Em-dash at a sentence end — a tiny held breath (new class 2026-07-30).</summary>
    public const double EmDashBaseSeconds = 0.62;
    public const double ParagraphBaseSeconds = 1.15;

    // ---- Context modifiers (multiplicative).
    /// <summary>Next sentence starts with a connective (And/Then/So/But/Now…): −40%.</summary>
    public const double ConnectiveFactor = 0.60;
    /// <summary>Both sentences are quoted dialogue — conversation flows: −30%.</summary>
    public const double DialogueFactor = 0.70;
    /// <summary>Next sentence is a dramatic opener (Suddenly/Just then…): +30% beat before it.</summary>
    public const double DramaticFactor = 1.30;
    /// <summary>Sentence-length modulation cap: short punchy → shorter after, long → longer breath.</summary>
    public const double LengthModMax = 0.15;
    /// <summary>Deterministic per-gap jitter cap (seeded hash, platform-stable).</summary>
    public const double JitterMax = 0.12;

    // ---- Clamps (per class family) and anti-monotony.
    public const double SentenceClampMin = 0.30;
    public const double SentenceClampMax = 0.95;
    public const double ParagraphClampMin = 0.85;
    public const double ParagraphClampMax = 1.50;
    /// <summary>No two adjacent gaps within this window of each other (anti-monotony).</summary>
    public const double MonotonyWindowSeconds = 0.025;

    /// <summary>Sentences longer than this are split at comma boundaries into clause chunks.</summary>
    public const int MaxChunkChars = 220;
    private const int MinClauseChars = 40;

    private static readonly string[] Connectives =
        ["and", "then", "so", "but", "now", "still", "soon", "next", "later"];

    private static readonly string[] DramaticOpeners =
        ["suddenly", "just then", "all at once", "all of a sudden", "but then", "at that very moment"];

    /// <summary>Deterministic speed for a sentence: BaseSpeed ± SpeedJitter in 0.01 steps.
    /// Stable across resume/replay (Knuth multiplicative hash of the index).</summary>
    public static float SpeedFor(int sentenceIndex)
    {
        var h = unchecked((uint)sentenceIndex * 2654435761u);
        var step = (int)((h >> 16) % 5) - 2;              // -2..+2
        return BaseSpeed + step * 0.01f;
    }

    /// <summary>
    /// Plan every inter-sentence gap for a narration script. Mirrors the synthesis loop
    /// exactly: whitespace-only sentences are skipped, a gap precedes each spoken sentence
    /// after the first, and a paragraph start wins over the previous terminator. The plan
    /// is a pure function of the script — the engine and the harness report call this
    /// independently and get identical numbers.
    /// </summary>
    public static IReadOnlyList<ProsodyGap> PlanGaps(Abstractions.NarrationScript script)
    {
        var sentences = script.SentenceList;
        var plans = new List<GapDraft>();
        var lastSpoken = -1;
        for (var i = 0; i < sentences.Count; i++)
        {
            var sentence = sentences[i].Trim();
            if (sentence.Length == 0) continue;
            if (lastSpoken >= 0)
                plans.Add(DraftGap(sentences[lastSpoken].Trim(), sentence, i, script.ParagraphStartSet.Contains(i)));
            lastSpoken = i;
        }

        // Anti-monotony, two explicit rules, both deterministic:
        //  (1) adjacent gaps must sit ≥ MonotonyWindowSeconds apart;
        //  (2) no run of 3 near-identical gaps (± the report's 40 ms window) — the third
        //      is pushed clear so the metronome can never re-emerge at a slow drift.
        for (var g = 1; g < plans.Count; g++)
        {
            var prev = plans[g - 1].Seconds;
            var dir = (JitterHash(plans[g].PrevSentence, plans[g].BeforeIndex) & 1) == 0 ? 1.0 : -1.0;

            if (Math.Abs(plans[g].Seconds - prev) < MonotonyWindowSeconds
                && !TryNudge(plans[g], prev, MonotonyWindowSeconds + 0.005, dir))
                TryNudge(plans[g], prev, MonotonyWindowSeconds + 0.005, -dir);

            var runBreak = ProsodyReport.NearIdenticalWindowSeconds + 0.005;
            if (g >= 2
                && Math.Abs(plans[g - 1].Seconds - plans[g - 2].Seconds) <= ProsodyReport.NearIdenticalWindowSeconds
                && Math.Abs(plans[g].Seconds - prev) <= ProsodyReport.NearIdenticalWindowSeconds
                && !TryNudge(plans[g], prev, runBreak, dir))
                TryNudge(plans[g], prev, runBreak, -dir);
        }

        return plans.Select(p => new ProsodyGap
        {
            BeforeIndex = p.BeforeIndex,
            Class = p.Class,
            BaseSeconds = p.BaseSeconds,
            Seconds = Math.Round(p.Seconds, 4),
            Tags = p.Tags,
        }).ToList();
    }

    /// <summary>Convenience: seconds-by-preceded-sentence-index lookup for the synthesis loop.</summary>
    public static Dictionary<int, double> GapSecondsByIndex(Abstractions.NarrationScript script)
        => PlanGaps(script).ToDictionary(gap => gap.BeforeIndex, gap => gap.Seconds);

    private sealed class GapDraft
    {
        public required int BeforeIndex { get; init; }
        public required GapClass Class { get; init; }
        public required double BaseSeconds { get; init; }
        public required string PrevSentence { get; init; }
        public double Seconds { get; set; }
        public List<string> Tags { get; } = [];
    }

    private static GapDraft DraftGap(string prev, string next, int nextIndex, bool nextStartsParagraph)
    {
        var cls = nextStartsParagraph ? GapClass.Paragraph : ClassOf(prev);
        var baseSeconds = cls switch
        {
            GapClass.Paragraph => ParagraphBaseSeconds,
            GapClass.Ellipsis => EllipsisBaseSeconds,
            GapClass.Emphatic => EmphaticBaseSeconds,
            GapClass.EmDash => EmDashBaseSeconds,
            _ => SentenceBaseSeconds,
        };
        var draft = new GapDraft { BeforeIndex = nextIndex, Class = cls, BaseSeconds = baseSeconds, PrevSentence = prev, Seconds = baseSeconds };
        draft.Tags.Add(string.Create(CultureInfo.InvariantCulture, $"{ClassTag(cls)}({baseSeconds:0.00})"));

        // Dramatic opener outranks (and suppresses) the plain connective check — "But then"
        // is a beat, not a flow-through.
        if (IsDramaticOpener(next)) Apply(draft, DramaticFactor, "drama");
        else if (IsConnectiveStart(next)) Apply(draft, ConnectiveFactor, "conn");
        if (IsQuoted(prev) && IsQuoted(next)) Apply(draft, DialogueFactor, "dlg");

        var len = LengthFactor(WordCount(prev));
        if (Math.Abs(len - 1.0) >= 0.005) Apply(draft, len, "len");

        var jit = 1.0 + JitterFraction(prev, nextIndex);
        if (Math.Abs(jit - 1.0) >= 0.005) Apply(draft, jit, "jit");

        Clamp(draft);
        return draft;
    }

    private static void Apply(GapDraft draft, double factor, string rule)
    {
        draft.Seconds *= factor;
        draft.Tags.Add(string.Create(CultureInfo.InvariantCulture, $"{rule}({factor - 1.0:+0%;-0%})"));
    }

    private static void Clamp(GapDraft draft)
    {
        var (min, max) = ClampRange(draft.Class);
        var clamped = Math.Clamp(draft.Seconds, min, max);
        if (Math.Abs(clamped - draft.Seconds) > 1e-9)
        {
            draft.Tags.Add(string.Create(CultureInfo.InvariantCulture, $"clamp({clamped:0.00})"));
            draft.Seconds = clamped;
        }
    }

    /// <summary>Move a gap to exactly <paramref name="distance"/> from its neighbor on the
    /// given side, respecting the class clamp. Fails (returns false, no change) when the
    /// clamp pins the candidate back inside the separation window.</summary>
    private static bool TryNudge(GapDraft draft, double neighbor, double distance, double direction)
    {
        var (min, max) = ClampRange(draft.Class);
        var candidate = Math.Clamp(neighbor + direction * distance, min, max);
        if (Math.Abs(candidate - neighbor) < distance - 1e-9) return false;
        draft.Tags.Add(string.Create(CultureInfo.InvariantCulture, $"mono({candidate - draft.Seconds:+0.000;-0.000})"));
        draft.Seconds = candidate;
        return true;
    }

    private static (double Min, double Max) ClampRange(GapClass cls) =>
        cls == GapClass.Paragraph ? (ParagraphClampMin, ParagraphClampMax) : (SentenceClampMin, SentenceClampMax);

    private static string ClassTag(GapClass cls) => cls switch
    {
        GapClass.Paragraph => "para",
        GapClass.Ellipsis => "ellip",
        GapClass.Emphatic => "emph",
        GapClass.EmDash => "dash",
        _ => "sent",
    };

    /// <summary>Gap class from how a sentence ends (closing quotes/brackets ignored).</summary>
    public static GapClass ClassOf(string sentence)
    {
        var core = sentence.TrimEnd('"', '\'', '”', ')', ' ');
        if (core.Length == 0) return GapClass.Sentence;
        if (core.EndsWith("...", StringComparison.Ordinal) || core.EndsWith('…')) return GapClass.Ellipsis;
        if (core.EndsWith('—') || core.EndsWith("--", StringComparison.Ordinal)) return GapClass.EmDash;
        return core[^1] is '!' or '?' ? GapClass.Emphatic : GapClass.Sentence;
    }

    /// <summary>True when the sentence opens with a flow-through connective (And/Then/So…).</summary>
    public static bool IsConnectiveStart(string sentence)
    {
        var word = FirstWord(sentence);
        return Connectives.Contains(word, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>True when the sentence opens with a dramatic beat (Suddenly/Just then…).</summary>
    public static bool IsDramaticOpener(string sentence)
    {
        var stripped = StripLeadingQuotes(sentence);
        foreach (var opener in DramaticOpeners)
        {
            if (stripped.Length < opener.Length) continue;
            if (!stripped.StartsWith(opener, StringComparison.OrdinalIgnoreCase)) continue;
            if (stripped.Length == opener.Length || !char.IsLetter(stripped[opener.Length])) return true;
        }
        return false;
    }

    /// <summary>True when the sentence reads as quoted dialogue (opens with a quote mark).</summary>
    public static bool IsQuoted(string sentence)
    {
        var t = sentence.TrimStart();
        return t.Length > 0 && t[0] is '"' or '“' or '\'' or '‘';
    }

    /// <summary>Length modulation from the previous sentence's word count: short punchy →
    /// shorter pause, long → slightly longer breath, capped at ±LengthModMax.</summary>
    public static double LengthFactor(int prevWordCount)
        => 1.0 + Math.Clamp((prevWordCount - 11) * 0.02, -LengthModMax, LengthModMax);

    /// <summary>Deterministic per-gap jitter in [−JitterMax, +JitterMax], 1% steps.
    /// FNV-1a over the previous sentence's UTF-16 units mixed with the gap position —
    /// stable across platforms and app restarts (players seek off these timestamps).</summary>
    public static double JitterFraction(string prevSentence, int nextIndex)
        => ((int)(JitterHash(prevSentence, nextIndex) % 25) - 12) / 100.0;

    private static uint JitterHash(string prevSentence, int nextIndex)
    {
        var h = 2166136261u;                              // FNV-1a
        foreach (var c in prevSentence) h = (h ^ c) * 16777619u;
        return h ^ unchecked((uint)nextIndex * 2654435761u);
    }

    private static string FirstWord(string sentence)
    {
        var s = StripLeadingQuotes(sentence);
        var end = 0;
        while (end < s.Length && char.IsLetter(s[end])) end++;
        return s[..end];
    }

    private static string StripLeadingQuotes(string sentence)
        => sentence.TrimStart(' ', '"', '“', '\'', '‘');

    private static int WordCount(string sentence)
        => sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>Split an overlong sentence at comma boundaries into clause chunks the model
    /// narrates separately (joined with CommaPauseSeconds). Normal sentences pass through.</summary>
    public static List<string> ChunkSentence(string sentence)
    {
        if (sentence.Length <= MaxChunkChars) return [sentence];
        var chunks = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var part in sentence.Split(','))
        {
            if (current.Length > 0)
            {
                current.Append(',');
                if (current.Length + part.Length + 1 > MaxChunkChars && current.Length >= MinClauseChars)
                {
                    chunks.Add(current.ToString().Trim());
                    current.Clear();
                }
            }
            current.Append(part);
        }
        if (current.Length > 0) chunks.Add(current.ToString().Trim());
        return chunks.Count == 0 ? [sentence] : chunks;
    }
}

/// <summary>Pacing metrics over a gap plan — the measurement half of the 2026-07-30
/// "measure it" mandate. Used by the harness `tts --report` table and pinned by unit
/// tests (CV ≥ 0.25, longest near-identical run ≤ 2 on the acceptance fixture).</summary>
public static class ProsodyReport
{
    /// <summary>Two consecutive gaps within this window count as "near-identical".</summary>
    public const double NearIdenticalWindowSeconds = 0.040;

    public sealed record Summary
    {
        public required int GapCount { get; init; }
        public required double MeanSeconds { get; init; }
        public required double StdSeconds { get; init; }
        /// <summary>Coefficient of variation (std/mean) — the anti-metronome number.</summary>
        public required double CV { get; init; }
        public required double MinSeconds { get; init; }
        public required double MaxSeconds { get; init; }
        /// <summary>Longest run of consecutive gaps in which every adjacent pair differs
        /// by ≤ NearIdenticalWindowSeconds. 1 = every neighbor differs audibly.</summary>
        public required int LongestNearIdenticalRun { get; init; }
        /// <summary>Fraction of gaps carrying each rule tag (conn, dlg, drama, len, jit, clamp, mono).</summary>
        public required IReadOnlyDictionary<string, double> RuleShare { get; init; }
    }

    public static Summary Summarize(IReadOnlyList<ProsodyGap> gaps)
    {
        if (gaps.Count == 0)
            return new Summary
            {
                GapCount = 0, MeanSeconds = 0, StdSeconds = 0, CV = 0, MinSeconds = 0, MaxSeconds = 0,
                LongestNearIdenticalRun = 0, RuleShare = new Dictionary<string, double>(),
            };

        var seconds = gaps.Select(g => g.Seconds).ToArray();
        var mean = seconds.Average();
        var std = Math.Sqrt(seconds.Sum(s => (s - mean) * (s - mean)) / seconds.Length);

        var longestRun = 1;
        var run = 1;
        for (var i = 1; i < seconds.Length; i++)
        {
            run = Math.Abs(seconds[i] - seconds[i - 1]) <= NearIdenticalWindowSeconds ? run + 1 : 1;
            longestRun = Math.Max(longestRun, run);
        }

        string[] rules = ["conn", "dlg", "drama", "len", "jit", "clamp", "mono"];
        var share = rules.ToDictionary(
            r => r,
            r => gaps.Count(g => g.Tags.Any(t => t.StartsWith(r + "(", StringComparison.Ordinal))) / (double)gaps.Count);

        return new Summary
        {
            GapCount = gaps.Count,
            MeanSeconds = mean,
            StdSeconds = std,
            CV = std / mean,
            MinSeconds = seconds.Min(),
            MaxSeconds = seconds.Max(),
            LongestNearIdenticalRun = longestRun,
            RuleShare = share,
        };
    }
}

/// <summary>
/// Sample-domain shaping for narration chunks: silence trimming so the prosody gaps are
/// what we insert (not what the model happened to emit), short fades that kill joint
/// clicks, whole-story loudness normalization, and the cozy mastering chain (founder
/// voice-character pass 2026-07-31: "warm, cozy… almost too loud, too high-pitched" —
/// pitch-down via duration-compensated resampling, high-shelf de-brightening, low-shelf
/// warmth, and RMS-targeted bedtime loudness instead of peak-maxing).
/// </summary>
public static class AudioShaper
{
    /// <summary>Samples below this are treated as silence when trimming (~ -50 dBFS).</summary>
    public const float SilenceThreshold = 0.0032f;
    /// <summary>Silence kept before the first audible sample.</summary>
    public const double KeepLeadSeconds = 0.020;
    /// <summary>Silence kept after the last audible sample.</summary>
    public const double KeepTailSeconds = 0.045;
    /// <summary>Linear fade-in applied to every chunk.</summary>
    public const double FadeInSeconds = 0.012;
    /// <summary>Linear fade-out applied to every chunk.</summary>
    public const double FadeOutSeconds = 0.028;
    /// <summary>Legacy normalization target: -1 dBFS (used when CozyMastering is off).</summary>
    public const float TargetPeak = 0.891f;
    /// <summary>Never amplify by more than this (avoid boosting noise on quiet voices).</summary>
    public const float MaxGain = 3.0f;

    // ---- Cozy mastering chain (founder voice-character pass 2026-07-31). Each element is
    // a tunable const; CozyMastering is the master switch (harness `tts --no-mastering`
    // renders the legacy chain for A/B). Applies to every voice, every render — narration,
    // teaser, and cached voice samples (VoiceSampleVersion bumped so caches regenerate).

    /// <summary>Master on/off for the cozy chain (pitch shift + shelves + RMS loudness).</summary>
    public static bool CozyMastering { get; set; } = true;

    /// <summary>Pitch shift in semitones (negative = down). −0.6 st takes the ring off the
    /// voice without making it sound slowed-down or male.</summary>
    public const double PitchSemitones = -0.6;
    /// <summary>Resample step 2^(st/12) ≈ 0.9659: reading the rendered audio at this step
    /// stretches it by 1/step and lowers pitch by the same factor. The engine compensates
    /// pacing by rendering at speed/PitchRatio (see <see cref="RenderSpeed"/>), so net
    /// duration is unchanged (±2%) and only the pitch moves.</summary>
    public static readonly double PitchRatio = Math.Pow(2.0, PitchSemitones / 12.0);

    /// <summary>High-shelf cut that softens the bright sentence-ending ring.</summary>
    public const double HighShelfHz = 4000.0;
    public const double HighShelfDb = -3.5;
    /// <summary>Gentle low-shelf warmth under the voice's chest register.</summary>
    public const double LowShelfHz = 200.0;
    public const double LowShelfDb = 1.5;

    /// <summary>Bedtime peak ceiling: −6 dBFS (was −1 dBFS peak-maxing).</summary>
    public const float CozyTargetPeak = 0.501f;
    /// <summary>Perceived-loudness target: −26 dBFS RMS over voiced audio (gaps excluded).</summary>
    public const double CozyTargetRmsDb = -26.0;

    /// <summary>Sherpa speed that compensates the resampler's time-stretch: rendering at
    /// speed/PitchRatio is 1/PitchRatio faster, the resample stretches by 1/PitchRatio,
    /// net pacing unchanged. Identity when the chain is off.</summary>
    public static float RenderSpeed(float prosodySpeed)
        => CozyMastering ? (float)(prosodySpeed / PitchRatio) : prosodySpeed;

    /// <summary>Cozy mastering for one raw model chunk: pitch-down resample, then the two
    /// shelves. Pass-through (same reference) when the chain is off or the chunk is empty.
    /// Runs BEFORE <see cref="ShapeChunk"/> so trims/fades see the final signal.</summary>
    public static float[] MasterChunk(float[] samples, int sampleRate)
    {
        if (!CozyMastering || samples.Length == 0) return samples;
        var shifted = Resample(samples, PitchRatio);
        Biquad.HighShelf(sampleRate, HighShelfHz, HighShelfDb).Process(shifted);
        Biquad.LowShelf(sampleRate, LowShelfHz, LowShelfDb).Process(shifted);
        return shifted;
    }

    /// <summary>Deterministic Catmull-Rom resampler: output[i] = x(i·step). step &lt; 1 →
    /// longer output → lower pitch at the same playback rate.</summary>
    public static float[] Resample(float[] x, double step)
    {
        if (x.Length < 2) return x;
        var outLen = (int)Math.Floor((x.Length - 1) / step) + 1;
        var y = new float[outLen];
        for (var i = 0; i < outLen; i++)
        {
            var p = i * step;
            var i1 = (int)p;
            var t = p - i1;
            var i0 = Math.Max(i1 - 1, 0);
            var i2 = Math.Min(i1 + 1, x.Length - 1);
            var i3 = Math.Min(i1 + 2, x.Length - 1);
            double s0 = x[i0], s1 = x[i1], s2 = x[i2], s3 = x[i3];
            // Catmull-Rom cubic interpolation.
            var a = -0.5 * s0 + 1.5 * s1 - 1.5 * s2 + 0.5 * s3;
            var b = s0 - 2.5 * s1 + 2.0 * s2 - 0.5 * s3;
            var c = -0.5 * s0 + 0.5 * s2;
            y[i] = (float)(((a * t + b) * t + c) * t + s1);
        }
        return y;
    }

    /// <summary>Whole-story gain: RMS-targeted loudness with the −6 dBFS peak ceiling when
    /// the cozy chain is on; legacy −1 dBFS peak normalization when off.</summary>
    public static float StoryGain(IEnumerable<float[]?> chunks)
    {
        float peak = 0f;
        double sumSquares = 0;
        long n = 0;
        foreach (var chunk in chunks)
        {
            if (chunk is null) continue;
            foreach (var s in chunk)
            {
                var a = Math.Abs(s);
                if (a > peak) peak = a;
                sumSquares += (double)s * s;
                n++;
            }
        }
        if (!CozyMastering) return NormalizationGain(peak);
        if (peak <= 0f || n == 0) return 1f;
        var rms = Math.Sqrt(sumSquares / n);
        var gain = (float)(Math.Pow(10.0, CozyTargetRmsDb / 20.0) / rms);
        gain = Math.Min(gain, CozyTargetPeak / peak); // headroom ceiling always wins
        return Math.Min(gain, MaxGain);
    }

    /// <summary>Trim model-emitted edge silence down to small fixed margins, then apply
    /// fade-in/out. Returns a new array (or the input when nothing changed).</summary>
    public static float[] ShapeChunk(float[] samples, int sampleRate)
    {
        if (samples.Length == 0) return samples;

        var first = 0;
        while (first < samples.Length && Math.Abs(samples[first]) < SilenceThreshold) first++;
        var last = samples.Length - 1;
        while (last >= first && Math.Abs(samples[last]) < SilenceThreshold) last--;
        if (last < first) return [];                     // all-silence chunk

        var lead = (int)(KeepLeadSeconds * sampleRate);
        var tail = (int)(KeepTailSeconds * sampleRate);
        var start = Math.Max(0, first - lead);
        var end = Math.Min(samples.Length - 1, last + tail);

        var shaped = new float[end - start + 1];
        Array.Copy(samples, start, shaped, 0, shaped.Length);

        var fadeIn = Math.Min((int)(FadeInSeconds * sampleRate), shaped.Length / 2);
        for (var i = 0; i < fadeIn; i++) shaped[i] *= (i + 1) / (float)(fadeIn + 1);
        var fadeOut = Math.Min((int)(FadeOutSeconds * sampleRate), shaped.Length / 2);
        for (var i = 0; i < fadeOut; i++) shaped[^(i + 1)] *= (i + 1) / (float)(fadeOut + 1);
        return shaped;
    }

    /// <summary>Peak over a set of chunks (silence chunks may be null).</summary>
    public static float Peak(IEnumerable<float[]?> chunks)
    {
        var peak = 0f;
        foreach (var chunk in chunks)
        {
            if (chunk is null) continue;
            foreach (var s in chunk)
            {
                var a = Math.Abs(s);
                if (a > peak) peak = a;
            }
        }
        return peak;
    }

    /// <summary>Gain that lands the story at TargetPeak, clamped to MaxGain (and always
    /// attenuates when over target so nothing ever clips).</summary>
    public static float NormalizationGain(float peak) =>
        peak <= 0f ? 1f : Math.Min(TargetPeak / peak, MaxGain);
}

/// <summary>
/// One RBJ (Audio-EQ-Cookbook) biquad section, direct form I, double-precision state.
/// Pure C#, deterministic — shelf filters for the cozy mastering chain. Coefficients are
/// normalized by a0; slope S = 1 (gentle, guaranteed-stable shelves).
/// </summary>
public sealed class Biquad
{
    private readonly double _b0, _b1, _b2, _a1, _a2;

    private Biquad(double b0, double b1, double b2, double a0, double a1, double a2)
    {
        _b0 = b0 / a0; _b1 = b1 / a0; _b2 = b2 / a0;
        _a1 = a1 / a0; _a2 = a2 / a0;
    }

    public static Biquad HighShelf(int sampleRate, double freqHz, double gainDb)
    {
        var (a, w0, alpha) = Prelude(sampleRate, freqHz, gainDb);
        var cos = Math.Cos(w0);
        var sq = 2.0 * Math.Sqrt(a) * alpha;
        return new Biquad(
            a * ((a + 1) + (a - 1) * cos + sq),
            -2.0 * a * ((a - 1) + (a + 1) * cos),
            a * ((a + 1) + (a - 1) * cos - sq),
            (a + 1) - (a - 1) * cos + sq,
            2.0 * ((a - 1) - (a + 1) * cos),
            (a + 1) - (a - 1) * cos - sq);
    }

    public static Biquad LowShelf(int sampleRate, double freqHz, double gainDb)
    {
        var (a, w0, alpha) = Prelude(sampleRate, freqHz, gainDb);
        var cos = Math.Cos(w0);
        var sq = 2.0 * Math.Sqrt(a) * alpha;
        return new Biquad(
            a * ((a + 1) - (a - 1) * cos + sq),
            2.0 * a * ((a - 1) - (a + 1) * cos),
            a * ((a + 1) - (a - 1) * cos - sq),
            (a + 1) + (a - 1) * cos + sq,
            -2.0 * ((a - 1) + (a + 1) * cos),
            (a + 1) + (a - 1) * cos - sq);
    }

    private static (double A, double W0, double Alpha) Prelude(int sampleRate, double freqHz, double gainDb)
    {
        var a = Math.Pow(10.0, gainDb / 40.0);
        var w0 = 2.0 * Math.PI * freqHz / sampleRate;
        var alpha = Math.Sin(w0) / 2.0 * Math.Sqrt((a + 1.0 / a) * (1.0 / 1.0 - 1.0) + 2.0); // S = 1
        return (a, w0, alpha);
    }

    /// <summary>Filter in place (fresh state per call — chunks are independent).</summary>
    public void Process(float[] samples)
    {
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            double x0 = samples[i];
            var y0 = _b0 * x0 + _b1 * x1 + _b2 * x2 - _a1 * y1 - _a2 * y2;
            samples[i] = (float)y0;
            x2 = x1; x1 = x0;
            y2 = y1; y1 = y0;
        }
    }
}
