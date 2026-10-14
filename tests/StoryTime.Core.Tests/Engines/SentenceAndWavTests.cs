using StoryTime.Core.Abstractions;
using StoryTime.Core.Engines.Sherpa;
using Xunit;

namespace StoryTime.Core.Tests.Engines;

public class SentenceSplitterTests
{
    [Fact]
    public void Splits_OnTerminators()
    {
        var s = SentenceSplitter.Split("The fox slept. The moon rose! Was it late? It was.");
        Assert.Equal(4, s.Count);
        Assert.Equal("The moon rose!", s[1]);
    }

    [Fact]
    public void KeepsAbbreviationsTogether()
    {
        var s = SentenceSplitter.Split("Mr. Badger waved. Mrs. Owl smiled.");
        Assert.Equal(2, s.Count);
        Assert.Equal("Mr. Badger waved.", s[0]);
    }

    [Fact]
    public void ParagraphBreak_EndsASentence()
    {
        var s = SentenceSplitter.Split("A soft ending without a dot\n\nNext paragraph starts.");
        Assert.Equal(2, s.Count);
    }

    [Fact]
    public void AttachesClosingQuotes()
    {
        var s = SentenceSplitter.Split("\"Goodnight,\" said Pip. \"Sleep well!\" said Mo.");
        Assert.Equal(2, s.Count);
        Assert.EndsWith("said Pip.", s[0]);
        Assert.StartsWith("\"Sleep well!\"", s[1]);
    }

    [Fact]
    public void MergesTinyFragments()
    {
        var s = SentenceSplitter.Split("Oh! The fox curled up by the warm stove and slept.");
        Assert.Single(s);
    }

    [Fact]
    public void EmptyInput_YieldsNoSentences()
    {
        Assert.Empty(SentenceSplitter.Split("   \n  "));
    }

    [Fact]
    public void EmDash_BeforeCapital_IsASoftTerminator()
    {
        var s = SentenceSplitter.Split("The fox blinked twice — Every ripple smoothed away, soft and slow.");
        Assert.Equal(2, s.Count);
        Assert.Equal("The fox blinked twice —", s[0]);
        Assert.StartsWith("Every ripple", s[1]);
    }

    [Fact]
    public void EmDash_BeforeLowercase_StaysInline()
    {
        var s = SentenceSplitter.Split("They followed small clues — a thread of song, a friendly paw print.");
        Assert.Single(s);
    }

    [Fact]
    public void EmDash_BeforeQuote_IsASoftTerminator()
    {
        var s = SentenceSplitter.Split("The heron paused — \"Almost bedtime,\" she said.");
        Assert.Equal(2, s.Count);
        Assert.Equal("The heron paused —", s[0]);
    }
}

public class WavAudioTests
{
    [Fact]
    public void ToPcm16_ClampsAndScales()
    {
        var pcm = WavAudio.ToPcm16(new[] { 0f, 1f, -1f, 2f, -2f });
        Assert.Equal(0, pcm[0]);
        Assert.Equal(short.MaxValue, pcm[1]);
        Assert.Equal(-short.MaxValue, pcm[2]);
        Assert.Equal(short.MaxValue, pcm[3]); // clamped
        Assert.Equal(-short.MaxValue, pcm[4]); // clamped
    }

    [Fact]
    public void WriteThenReadDuration_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tw-wav-{Guid.NewGuid():N}.wav");
        try
        {
            // 24000 samples @ 24kHz mono = exactly 1 second, split over two chunks (concat math).
            WavAudio.Write(path, [new short[9000], new short[15000]], 24000);
            Assert.Equal(1.0, WavAudio.ReadDurationSeconds(path), 3);
            Assert.Equal(44 + 24000 * 2, new FileInfo(path).Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Concat_JoinsParts_ByteExactDurations()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tw-concat-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var a = Path.Combine(dir, "a.wav");
            var b = Path.Combine(dir, "b.wav");
            var joined = Path.Combine(dir, "joined.wav");
            WavAudio.Write(a, [new short[24000]], 24000);          // 1.0 s
            WavAudio.Write(b, [new short[12000], new short[6000]], 24000); // 0.75 s
            var duration = WavAudio.Concat(joined, [a, b]);
            Assert.Equal(1.75, duration, 6);
            Assert.Equal(1.75, WavAudio.ReadDurationSeconds(joined), 6);
            Assert.Equal(44 + (24000 + 18000) * 2, new FileInfo(joined).Length);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Concat_RejectsSampleRateMismatch()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tw-concat-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var a = Path.Combine(dir, "a.wav");
            var b = Path.Combine(dir, "b.wav");
            WavAudio.Write(a, [new short[100]], 24000);
            WavAudio.Write(b, [new short[100]], 22050);
            Assert.Throws<InvalidOperationException>(() => WavAudio.Concat(Path.Combine(dir, "j.wav"), [a, b]));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TimestampMath_AccumulatesLikeTheEngine_IncludingPlannedGaps()
    {
        // Mirror of SherpaTtsEngine accumulation with the ProsodyDirector plan: sentences
        // of 1.0s and 0.5s with the planned (content-aware, varied) gap between them —
        // timestamps include exactly the inserted gap, so player seeks stay accurate.
        const int rate = 24000;
        var script = SentenceSplitter.SplitScript("The fox slept by the warm stove. The moon rose over the pier.");
        var plan = ProsodyDirector.GapSecondsByIndex(script);
        var gapSeconds = plan[1];
        Assert.InRange(gapSeconds, ProsodyDirector.SentenceClampMin, ProsodyDirector.SentenceClampMax);

        var pause = (int)(gapSeconds * rate);
        long cursor = 0;
        var s1 = new short[rate];        // 1.0 s
        var start1 = cursor / (double)rate; cursor += s1.Length;
        var end1 = cursor / (double)rate; cursor += pause;
        var s2 = new short[rate / 2];    // 0.5 s
        var start2 = cursor / (double)rate; cursor += s2.Length;
        var end2 = cursor / (double)rate;

        Assert.Equal(0.0, start1, 3);
        Assert.Equal(1.0, end1, 3);
        Assert.Equal(1.0 + gapSeconds, start2, 3);
        Assert.Equal(1.5 + gapSeconds, end2, 3);
    }
}

public class ProsodyDirectorTests
{
    /// <summary>The 12+-sentence acceptance fixture: mixed dialogue, connectives, a
    /// dramatic opener, an ellipsis, an em-dash break, and a paragraph break.</summary>
    private const string Fixture =
        "Down by the old wooden pier, a small red fox tucked her nose beneath her tail and watched the slow water. " +
        "The evening was warm. \"Is it bedtime already?\" asked the fox. \"Almost, little one,\" said the heron. " +
        "And the water rocked the boats gently, like cradles. Then a hush settled over the harbor. " +
        "The lanterns blinked on, one by one… Suddenly a silver fish leapt and drew one bright arc. " +
        "The fox blinked twice — Every ripple smoothed away, soft and slow.\n\n" +
        "But then the night grew calm again. So the fox curled deeper into her warm fur. " +
        "The stars came out to keep watch. Goodnight, little fox.";

    private static IReadOnlyList<ProsodyGap> PlanFixture() =>
        ProsodyDirector.PlanGaps(SentenceSplitter.SplitScript(Fixture));

    [Fact]
    public void SpeedFor_IsDeterministic_AndWithinJitterBand()
    {
        for (var i = 0; i < 200; i++)
        {
            var s = ProsodyDirector.SpeedFor(i);
            Assert.Equal(ProsodyDirector.SpeedFor(i), s);                       // stable
            Assert.InRange(s, ProsodyDirector.BaseSpeed - ProsodyDirector.SpeedJitter - 1e-6,
                              ProsodyDirector.BaseSpeed + ProsodyDirector.SpeedJitter + 1e-6);
        }
        // The jitter must actually vary — pacing is supposed to breathe.
        Assert.True(Enumerable.Range(0, 50).Select(ProsodyDirector.SpeedFor).Distinct().Count() >= 3);
    }

    [Theory]
    [InlineData("The fox slept.", GapClass.Sentence)]
    [InlineData("The moon rose!", GapClass.Emphatic)]
    [InlineData("Was it late?", GapClass.Emphatic)]
    [InlineData("And then…", GapClass.Ellipsis)]
    [InlineData("Off to sleep...", GapClass.Ellipsis)]
    [InlineData("\"Goodnight!\"", GapClass.Emphatic)]
    [InlineData("She held her breath —", GapClass.EmDash)]
    [InlineData("She held her breath--", GapClass.EmDash)]
    public void ClassOf_FollowsPunctuation(string sentence, GapClass expected)
        => Assert.Equal(expected, ProsodyDirector.ClassOf(sentence));

    [Fact]
    public void PlanGaps_IsFullyDeterministic()
    {
        var a = PlanFixture();
        var b = PlanFixture();
        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Seconds, b[i].Seconds);
            Assert.Equal(a[i].Tags, b[i].Tags);
            Assert.Equal(a[i].Class, b[i].Class);
        }
    }

    [Fact]
    public void PlanGaps_FixtureShape_IsTheExpectedMix()
    {
        var gaps = PlanFixture();
        Assert.Equal(13, gaps.Count); // 14 sentences (em-dash splits one) → 13 gaps

        // Dialogue pair: "Is it bedtime already?"… / "Almost, little one,"…
        var dlg = Assert.Single(gaps, g => g.Tags.Any(t => t.StartsWith("dlg(")));
        Assert.Equal(3, dlg.BeforeIndex);

        // Connectives: And / So (Then at index 5 also; "But then" upgrades to drama).
        Assert.Contains(gaps, g => g.BeforeIndex == 4 && g.Tags.Any(t => t.StartsWith("conn(")));
        Assert.Contains(gaps, g => g.BeforeIndex == 5 && g.Tags.Any(t => t.StartsWith("conn(")));

        // Dramatic opener after the ellipsis: gap rides the ellipsis class, boosted.
        var drama = gaps.Single(g => g.BeforeIndex == 7);
        Assert.Equal(GapClass.Ellipsis, drama.Class);
        Assert.Contains(drama.Tags, t => t.StartsWith("drama("));

        // Em-dash held breath before "Every ripple…".
        Assert.Equal(GapClass.EmDash, gaps.Single(g => g.BeforeIndex == 9).Class);

        // Paragraph beat before "But then…": paragraph class wins, drama still applies.
        var para = gaps.Single(g => g.BeforeIndex == 10);
        Assert.Equal(GapClass.Paragraph, para.Class);
        Assert.Contains(para.Tags, t => t.StartsWith("drama("));
    }

    [Fact]
    public void PlanGaps_AllClampsHold()
    {
        foreach (var g in PlanFixture())
        {
            var (min, max) = g.Class == GapClass.Paragraph
                ? (ProsodyDirector.ParagraphClampMin, ProsodyDirector.ParagraphClampMax)
                : (ProsodyDirector.SentenceClampMin, ProsodyDirector.SentenceClampMax);
            Assert.InRange(g.Seconds, min, max);
        }
    }

    [Fact]
    public void PlanGaps_Acceptance_CvAndRunLength()
    {
        var summary = ProsodyReport.Summarize(PlanFixture());
        Assert.True(summary.CV >= 0.25, $"CV {summary.CV:0.000} must be ≥ 0.25 — pacing may not be a metronome");
        Assert.True(summary.LongestNearIdenticalRun <= 2,
            $"Longest near-identical run {summary.LongestNearIdenticalRun} must be ≤ 2");
    }

    [Fact]
    public void PlanGaps_AntiMonotony_AdjacentGapsNeverNearIdentical()
    {
        // Worst case: the same plain sentence over and over — bases identical, only
        // jitter and the explicit anti-monotony rules separate the gaps.
        var text = string.Join(" ", Enumerable.Repeat("The fox slept by the stove.", 10));
        var gaps = ProsodyDirector.PlanGaps(SentenceSplitter.SplitScript(text));
        Assert.Equal(9, gaps.Count);
        for (var i = 1; i < gaps.Count; i++)
            Assert.True(Math.Abs(gaps[i].Seconds - gaps[i - 1].Seconds) >= ProsodyDirector.MonotonyWindowSeconds - 1e-9,
                $"gaps {i - 1} and {i} are {Math.Abs(gaps[i].Seconds - gaps[i - 1].Seconds) * 1000:0.0}ms apart");

        var summary = ProsodyReport.Summarize(gaps);
        Assert.True(summary.LongestNearIdenticalRun <= 2, $"run {summary.LongestNearIdenticalRun} on identical input");
    }

    [Fact]
    public void PlanGaps_ConnectiveShortensAgainstBase()
    {
        var gaps = ProsodyDirector.PlanGaps(SentenceSplitter.SplitScript("The fox curled up by the stove. And the moon rose."));
        var g = Assert.Single(gaps);
        Assert.Contains(g.Tags, t => t.StartsWith("conn("));
        Assert.True(g.Seconds < g.BaseSeconds, "connective flow-through must land below the class base");
    }

    [Fact]
    public void PlanGaps_SkipsEmptySentences_LikeTheEngine()
    {
        var script = new NarrationScript
        {
            SentenceList = ["The fox slept.", "   ", "The moon rose."],
            ParagraphStartSet = [],
        };
        var g = Assert.Single(ProsodyDirector.PlanGaps(script));
        Assert.Equal(2, g.BeforeIndex); // gap precedes the spoken sentence, not the blank
    }

    [Theory]
    [InlineData("And the fox slept.", true)]
    [InlineData("Then they walked home.", true)]
    [InlineData("So it was settled.", true)]
    [InlineData("Andy waved goodnight.", false)]  // prefix, not the word
    [InlineData("The fox slept.", false)]
    public void IsConnectiveStart_MatchesWholeFirstWord(string sentence, bool expected)
        => Assert.Equal(expected, ProsodyDirector.IsConnectiveStart(sentence));

    [Theory]
    [InlineData("Suddenly the fish leapt.", true)]
    [InlineData("Just then, a light blinked on.", true)]
    [InlineData("All at once the garden went still.", true)]
    [InlineData("But then the night grew calm.", true)]
    [InlineData("Suddenlyish is not a word.", false)]
    [InlineData("The fox slept.", false)]
    public void IsDramaticOpener_MatchesOpeners(string sentence, bool expected)
        => Assert.Equal(expected, ProsodyDirector.IsDramaticOpener(sentence));

    [Fact]
    public void LengthFactor_ShortSentencesShorten_LongOnesBreathe()
    {
        Assert.True(ProsodyDirector.LengthFactor(4) < 1.0);
        Assert.Equal(1.0, ProsodyDirector.LengthFactor(11), 6);
        Assert.True(ProsodyDirector.LengthFactor(20) > 1.0);
        Assert.Equal(1.0 - ProsodyDirector.LengthModMax, ProsodyDirector.LengthFactor(1), 6);   // floor
        Assert.Equal(1.0 + ProsodyDirector.LengthModMax, ProsodyDirector.LengthFactor(40), 6);  // cap
    }

    [Fact]
    public void JitterFraction_IsBounded_Deterministic_AndVaries()
    {
        var values = new HashSet<double>();
        for (var i = 0; i < 60; i++)
        {
            var j = ProsodyDirector.JitterFraction("The fox slept by the stove.", i);
            Assert.Equal(ProsodyDirector.JitterFraction("The fox slept by the stove.", i), j);
            Assert.InRange(j, -ProsodyDirector.JitterMax, ProsodyDirector.JitterMax);
            values.Add(j);
        }
        Assert.True(values.Count >= 5, "per-gap jitter must actually vary");
    }

    [Fact]
    public void ChunkSentence_PassesNormalSentencesThrough()
    {
        var s = "A calm, ordinary sentence.";
        Assert.Equal([s], ProsodyDirector.ChunkSentence(s));
    }

    [Fact]
    public void ChunkSentence_SplitsOverlongAtCommas()
    {
        var clause = string.Join(" ", Enumerable.Repeat("gently drifting over the quiet hills", 3));
        var s = $"{clause}, {clause}, {clause}.";
        var chunks = ProsodyDirector.ChunkSentence(s);
        Assert.True(chunks.Count > 1);
        Assert.Equal(s.Length, chunks.Sum(c => c.Length) + (chunks.Count - 1)); // nothing lost (spaces at joins trimmed)
    }
}

public class ProsodyReportTests
{
    private static ProsodyGap Gap(int index, double seconds, params string[] tags) => new()
    {
        BeforeIndex = index,
        Class = GapClass.Sentence,
        BaseSeconds = ProsodyDirector.SentenceBaseSeconds,
        Seconds = seconds,
        Tags = tags.Length > 0 ? tags : ["sent(0.55)"],
    };

    [Fact]
    public void Summarize_ComputesMeanStdCvMinMax()
    {
        var s = ProsodyReport.Summarize([Gap(1, 0.40), Gap(2, 0.60), Gap(3, 0.80)]);
        Assert.Equal(3, s.GapCount);
        Assert.Equal(0.60, s.MeanSeconds, 6);
        Assert.Equal(Math.Sqrt(0.08 / 3), s.StdSeconds, 6);
        Assert.Equal(s.StdSeconds / s.MeanSeconds, s.CV, 6);
        Assert.Equal(0.40, s.MinSeconds, 6);
        Assert.Equal(0.80, s.MaxSeconds, 6);
    }

    [Fact]
    public void Summarize_FindsLongestNearIdenticalRun()
    {
        // 0.50, 0.53, 0.55 pairwise within 40ms → run of 3; then a clear jump.
        var s = ProsodyReport.Summarize([Gap(1, 0.50), Gap(2, 0.53), Gap(3, 0.55), Gap(4, 0.90)]);
        Assert.Equal(3, s.LongestNearIdenticalRun);

        var spread = ProsodyReport.Summarize([Gap(1, 0.30), Gap(2, 0.50), Gap(3, 0.70)]);
        Assert.Equal(1, spread.LongestNearIdenticalRun);
    }

    [Fact]
    public void Summarize_CountsRuleShares()
    {
        var s = ProsodyReport.Summarize(
        [
            Gap(1, 0.35, "sent(0.55)", "conn(-40%)", "jit(+4%)"),
            Gap(2, 0.60, "sent(0.55)", "jit(-2%)"),
        ]);
        Assert.Equal(0.5, s.RuleShare["conn"], 6);
        Assert.Equal(1.0, s.RuleShare["jit"], 6);
        Assert.Equal(0.0, s.RuleShare["drama"], 6);
    }

    [Fact]
    public void Describe_ReadsLikeTheReportRow()
    {
        var g = Gap(1, 0.36, "sent(0.55)", "conn(-40%)", "jit(+8%)");
        Assert.Equal("sent(0.55) conn(-40%) jit(+8%) → 0.36s", g.Describe());
    }
}

public class AudioShaperTests
{
    [Fact]
    public void ShapeChunk_TrimsEdgeSilence_AndFades()
    {
        const int rate = 24000;
        var samples = new float[rate]; // 1s: 0.4s silence, 0.2s tone, 0.4s silence
        for (var i = (int)(0.4 * rate); i < (int)(0.6 * rate); i++) samples[i] = 0.5f;
        var shaped = AudioShaper.ShapeChunk(samples, rate);

        // Kept: tone (0.2s) + lead margin + tail margin, well under the original 1s.
        var expected = 0.2 + AudioShaper.KeepLeadSeconds + AudioShaper.KeepTailSeconds;
        Assert.InRange(shaped.Length / (double)rate, expected - 0.01, expected + 0.01);
        // The cut points land inside kept silence and are faded to ~zero — no edge clicks.
        Assert.True(Math.Abs(shaped[0]) < 0.001f);
        Assert.True(Math.Abs(shaped[^1]) < 0.001f);
        // The tone plateau survives untouched.
        Assert.Equal(0.5f, shaped.Max(), 3);
    }

    [Fact]
    public void ShapeChunk_AllSilence_YieldsEmpty()
        => Assert.Empty(AudioShaper.ShapeChunk(new float[1000], 24000));

    [Theory]
    [InlineData(0.891f, 1.0f)]   // already at target → unity
    [InlineData(2.0f, 0.4455f)]  // hot input → attenuated to -1 dBFS
    [InlineData(0.05f, 3.0f)]    // very quiet → gain clamped at MaxGain
    public void NormalizationGain_TargetsMinusOneDb(float peak, float expected)
        => Assert.Equal(expected, AudioShaper.NormalizationGain(peak), 3);
}

public class NarrationScriptTests
{
    [Fact]
    public void SplitScript_MarksParagraphStarts()
    {
        var script = SentenceSplitter.SplitScript("The fox slept. The moon rose.\n\nA new morning came. Birds sang.");
        Assert.Equal(4, script.SentenceList.Count);
        Assert.Equal([2], script.ParagraphStartSet.Order().ToArray());
    }

    [Fact]
    public void SplitScript_FirstSentence_IsNeverAParagraphStart()
    {
        var script = SentenceSplitter.SplitScript("\n\nOnly paragraph here. Second sentence.");
        Assert.DoesNotContain(0, script.ParagraphStartSet);
    }

    [Fact]
    public void Take_KeepsOnlyParagraphStartsInRange()
    {
        var script = SentenceSplitter.SplitScript("One long sentence here.\n\nSecond paragraph opens. And continues.\n\nThird begins.");
        var teaser = script.Take(2);
        Assert.Equal(2, teaser.SentenceList.Count);
        Assert.Equal([1], teaser.ParagraphStartSet.Order().ToArray());
    }

    [Fact]
    public void SplitScript_Sentences_MatchFlatSplit()
    {
        const string text = "The fox slept. The moon rose!\n\nA new day. It was bright.";
        Assert.Equal(SentenceSplitter.Split(text), SentenceSplitter.SplitScript(text).SentenceList);
    }
}
