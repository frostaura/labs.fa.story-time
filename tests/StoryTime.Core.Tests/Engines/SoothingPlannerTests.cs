using StoryTime.Core.Abstractions;
using StoryTime.Core.Engines.Narration;
using Xunit;

namespace StoryTime.Core.Tests.Engines;

public class NarrationSegmenterTests
{
    private static List<NarrationPiece> Seg(string text) => NarrationSegmenter.Segment(SentenceSplitter.SplitScript(text));

    [Fact]
    public void OnePiecePerPlainSentence()
    {
        var pieces = Seg("The fox slept by the stove, warm and small. The moon rose.");
        Assert.Equal(2, pieces.Count);
        Assert.All(pieces, p => Assert.Equal(SegmentKind.Narration, p.Kind));
        Assert.Contains(',', pieces[0].Raw);          // never split at commas
    }

    [Fact]
    public void DialogueAndTag_AreSeparatePieces()
    {
        var pieces = Seg("\"Sleep well, little owl,\" whispered Mama Bear.");
        Assert.Equal(2, pieces.Count);
        Assert.Equal(SegmentKind.Dialogue, pieces[0].Kind);
        Assert.Equal(SegmentKind.Tag, pieces[1].Kind);
        Assert.True(pieces[0].ClosesBeforeTag);
        Assert.Equal("whispered Mama Bear.", pieces[1].Raw);
    }

    [Fact]
    public void TagFirst_ThenDialogue()
    {
        var pieces = Seg("Mama smiled and said, “Sleep now.”");
        Assert.Equal([SegmentKind.Tag, SegmentKind.Dialogue], pieces.Select(p => p.Kind));
    }

    [Fact]
    public void SplitQuote_DialogueTagDialogue()
    {
        var pieces = Seg("\"Look,\" said Owl, \"the stars are out.\"");
        Assert.Equal([SegmentKind.Dialogue, SegmentKind.Tag, SegmentKind.Dialogue], pieces.Select(p => p.Kind));
    }

    [Fact]
    public void QuoteSpanningTwoSentences_StaysDialogue()
    {
        var pieces = Seg("\"It is late. Time for bed,\" said Mum.");
        Assert.Equal(SegmentKind.Dialogue, pieces[0].Kind);
        Assert.Equal(SegmentKind.Dialogue, pieces[1].Kind);
        Assert.Equal(SegmentKind.Tag, pieces[2].Kind);
    }

    [Fact]
    public void Ellipsis_SplitsASentence()
    {
        var pieces = Seg("She waited… and waited.");
        Assert.Equal(2, pieces.Count);
        Assert.True(pieces[0].EndsWithEllipsis);
        Assert.Equal(pieces[0].SentenceIndex, pieces[1].SentenceIndex);
    }

    [Fact]
    public void Apostrophes_DoNotOpenDialogue()
    {
        var pieces = Seg("Mila's lamp glowed. The owls' nest was quiet.");
        Assert.All(pieces, p => Assert.Equal(SegmentKind.Narration, p.Kind));
    }

    [Theory]
    [InlineData("Goodnight, Mila.", true)]
    [InlineData("Good night.", true)]
    [InlineData("\"Goodnight,\" said Owl.", true)]
    [InlineData("The night was good.", false)]
    public void GoodnightLines(string text, bool expected) => Assert.Equal(expected, NarrationSegmenter.IsGoodnightLine(text));
}

public class TextNormalizerTests
{
    [Theory]
    [InlineData("She had 3 red boats.", "She had three red boats.")]
    [InlineData("It was 7:30 at night.", "It was seven thirty at night.")]
    [InlineData("On the 2nd night.", "On the second night.")]
    [InlineData("Mr. Fox and Dr. Owl.", "Mister Fox and Doctor Owl.")]
    [InlineData("She paused — then smiled; the end.", "She paused, then smiled, the end.")]
    [InlineData("The lamp flickered…", "The lamp flickered.")]
    [InlineData("\"What a night!\"", "What a night!")]
    [InlineData("Is it time?", "Is it time?")]
    [InlineData("There were 1,250 stars.", "There were one thousand two hundred and fifty stars.")]
    public void ForSpeech(string input, string expected) => Assert.Equal(expected, TextNormalizer.ForSpeech(input));

    [Fact]
    public void DialogueBeforeTag_CommaBecomesFullStop()
        => Assert.Equal("Sleep well, little owl.", TextNormalizer.ForSpeech("\"Sleep well, little owl,\"", closesBeforeTag: true));

    [Fact]
    public void TagBeforeDialogue_KeepsItsComma()
        => Assert.Equal("Mama said,", TextNormalizer.ForSpeech("Mama said,"));

    [Fact]
    public void Exclamation_IsKeptInNarration()
        => Assert.Equal("The moon was so bright!", TextNormalizer.ForSpeech("The moon was so bright!"));

    [Theory]
    [InlineData(0, "zero")]
    [InlineData(21, "twenty-one")]
    [InlineData(105, "one hundred and five")]
    [InlineData(2000, "two thousand")]
    public void Cardinal(long n, string expected) => Assert.Equal(expected, TextNormalizer.Cardinal(n));

    [Theory]
    [InlineData(1, "first")]
    [InlineData(12, "twelfth")]
    [InlineData(20, "twentieth")]
    [InlineData(23, "twenty-third")]
    public void Ordinal(long n, string expected) => Assert.Equal(expected, TextNormalizer.OrdinalWord(n));
}

public class SoothingPlannerTests
{
    private const string Story =
        "The little fox curled up by the fire. The wind hummed outside.\n\n" +
        "\"Are you sleepy?\" asked Owl. \"A little,\" said Fox. She yawned… and smiled.\n\n\n" +
        "Morning was far away. The stars kept watch.\n\n" +
        "And so the forest slept. Goodnight, little fox. Goodnight.";

    private static NarrationPlan PlanStory(SoothingOptions? o = null) =>
        SoothingPlanner.Plan(SentenceSplitter.SplitScript(Story), o ?? new SoothingOptions { RateVariation = 0 });

    private static PlannedSegment Find(NarrationPlan plan, string startsWith) =>
        plan.Segments.First(s => s.Source.TrimStart('"').StartsWith(startsWith, StringComparison.Ordinal));

    [Fact]
    public void PauseMap_ExactMilliseconds()
    {
        var plan = PlanStory();
        Assert.Equal(0.650, Find(plan, "The wind").PauseBeforeSeconds, 6);                 // sentence
        Assert.Equal(PauseKind.Paragraph, Find(plan, "Are you").PauseBefore);
        Assert.Equal(1.400, Find(plan, "Are you").PauseBeforeSeconds, 6);                  // paragraph
        Assert.Equal(0.250, Find(plan, "asked Owl").PauseBeforeSeconds, 6);                // dialogue → tag
        Assert.Equal(0.600, Find(plan, "A little").PauseBeforeSeconds, 6);                 // after the tag
        Assert.Equal(0.600, Find(plan, "She yawned").PauseBeforeSeconds, 6);               // after the tag
        Assert.Equal(0.750, Find(plan, "and smiled").PauseBeforeSeconds, 6);               // ellipsis
        Assert.Equal(2.200, Find(plan, "Morning").PauseBeforeSeconds, 6);                  // scene (blank-line run)
        Assert.Equal(1.800, Find(plan, "And so").PauseBeforeSeconds, 6);                   // before the ending
        var pair = plan.Segments.Last();
        Assert.Equal("Goodnight.", pair.Spoken);
        Assert.Equal(1.100, pair.PauseBeforeSeconds, 6);                                   // goodnight pair
    }

    [Fact]
    public void BeforeDialogue_Is450()
    {
        var plan = SoothingPlanner.Plan(SentenceSplitter.SplitScript("The owl looked down. \"Hello there.\""));
        Assert.Equal(PauseKind.BeforeDialogue, plan.Segments[1].PauseBefore);
        Assert.Equal(0.450, plan.Segments[1].PauseBeforeSeconds, 6);
    }

    [Fact]
    public void HeadAndTail_WithAndWithoutPad()
    {
        var withPad = PlanStory();
        Assert.Equal(0.6 + 3.0, withPad.HeadSeconds, 6);
        Assert.Equal(1.5 + 5.0, withPad.TailSeconds, 6);
        Assert.Equal(3.0, withPad.PadFadeInSeconds, 6);
        Assert.Equal(5.0, withPad.PadFadeOutSeconds, 6);

        var noPad = PlanStory(new SoothingOptions { Pad = false, RateVariation = 0 });
        Assert.Equal(0.6, noPad.HeadSeconds, 6);
        Assert.Equal(1.5, noPad.TailSeconds, 6);
    }

    [Fact]
    public void Dialogue_Parameters()
    {
        var d = Find(PlanStory(), "Are you");
        Assert.Equal(SegmentKind.Dialogue, d.Kind);
        Assert.Equal(0.48f, d.Rate, 4);
        Assert.Equal(1.08f, d.Pitch, 4);
        Assert.Equal(-2.0, d.GainDb, 6);
        var tag = Find(PlanStory(), "asked Owl");
        Assert.Equal(0.44f, tag.Rate, 4);
        Assert.Equal(0.0, tag.GainDb, 6);
    }

    [Fact]
    public void Ending_RampsRateAndGainLinearly_AndGoodnightPitch()
    {
        var plan = PlanStory();
        var ending = plan.Segments.Where(s => s.InEnding).ToList();
        Assert.Equal(3, ending.Count);
        Assert.Equal(0.44f, ending[0].Rate, 4);
        Assert.Equal(0.38f, ending[1].Rate, 4);
        Assert.Equal(0.32f, ending[2].Rate, 4);
        Assert.Equal(0.0, ending[0].GainDb, 6);
        Assert.Equal(-3.0, ending[1].GainDb, 6);
        Assert.Equal(-6.0, ending[2].GainDb, 6);
        Assert.Equal(-6.0, ending[2].EndingGainDb, 6);
        Assert.Equal(0.97f, ending[1].Pitch, 4);
        Assert.Equal(0.97f, ending[2].Pitch, 4);
        Assert.Equal(1.0f, ending[0].Pitch, 4);
        Assert.All(plan.Segments.Where(s => !s.InEnding), s => Assert.Equal(0.0, s.EndingGainDb));
    }

    [Fact]
    public void Teaser_GetsNoEnding_ButKeepsHeadAndTail()
    {
        var teaser = SoothingPlanner.Plan(SentenceSplitter.SplitScript(Story).Take(3));
        Assert.DoesNotContain(teaser.Segments, s => s.InEnding);
        Assert.Equal(3.6, teaser.HeadSeconds, 6);
        Assert.Equal(6.5, teaser.TailSeconds, 6);
    }

    [Fact]
    public void WindowSeams_SplitTheScenePauseAcrossTheJoin()
    {
        var script = SentenceSplitter.SplitScript(Story);
        var middle = new NarrationScript
        {
            SentenceList = script.SentenceList,
            ParagraphStartSet = script.ParagraphStartSet,
            LeadingSilenceSeconds = PauseMap.SceneSeconds,
            AppendFinalTail = false,
            TimelineOffsetSeconds = 42,
        };
        var plan = SoothingPlanner.Plan(middle);
        Assert.Equal(PauseMap.SceneSeconds - PauseMap.SeamCarrySeconds, plan.HeadSeconds, 6);
        Assert.Equal(PauseMap.SeamCarrySeconds, plan.TailSeconds, 6);
        Assert.Equal(PauseKind.WindowSeam, plan.HeadKind);
        Assert.Equal(0, plan.PadFadeInSeconds);
        Assert.Equal(0, plan.PadFadeOutSeconds);
        Assert.Equal(42, plan.TimelineOffsetSeconds);
        Assert.DoesNotContain(plan.Segments, s => s.InEnding);   // a middle window never ends the story
        // The window before (no tail) + the window after (head) sum to exactly the scene pause.
        Assert.Equal(PauseMap.SceneSeconds, plan.HeadSeconds + PauseMap.SeamCarrySeconds, 6);
    }

    [Fact]
    public void RateVariation_IsDeterministic_Bounded_AndNotFlat()
    {
        var a = SoothingPlanner.Plan(SentenceSplitter.SplitScript(Story));
        var b = SoothingPlanner.Plan(SentenceSplitter.SplitScript(Story));
        Assert.Equal(a.Segments.Select(s => s.Rate), b.Segments.Select(s => s.Rate));
        var narration = a.Segments.Where(s => s.Kind != SegmentKind.Dialogue && !s.InEnding).Select(s => s.Rate).ToList();
        Assert.All(narration, r => Assert.InRange(r, 0.4199f, 0.4601f));
        var many = Enumerable.Range(0, 40).Select(i => SoothingPlanner.Variation($"Sentence {i} of the night.", i, 0.02f)).ToList();
        Assert.Equal(5, many.Distinct().Count());                               // −0.02, −0.01, 0, +0.01, +0.02
        Assert.All(many, v => Assert.InRange(v, -0.02001f, 0.02001f));
    }

    [Fact]
    public void SpokenText_IsNormalised_AndQuotesStripped()
    {
        var plan = PlanStory();
        Assert.Equal("A little.", Find(plan, "A little").Spoken);
        Assert.Equal("She yawned.", Find(plan, "She yawned").Spoken);
        Assert.DoesNotContain(plan.Segments, s => s.Spoken.Contains('"'));
    }

    [Fact]
    public void EmptyScript_PlansNothing()
    {
        var plan = SoothingPlanner.Plan(SentenceSplitter.SplitScript(""));
        Assert.Empty(plan.Segments);
        Assert.Equal(0, plan.SentenceCount);
    }

    [Fact]
    public void SceneMarkers_AreNeverSpoken_AndMarkScenes()
    {
        var script = SentenceSplitter.SplitScript("The day ended.\n\n* * *\n\nNight came softly.");
        Assert.Equal(2, script.SentenceList.Count);
        Assert.Contains(1, script.SceneStartSet);
        Assert.Contains(1, script.ParagraphStartSet);
        Assert.Equal(PauseKind.Scene, SoothingPlanner.Plan(script).Segments[1].PauseBefore);
    }

    [Fact]
    public void SingleSentenceStory_EndsOnTheRampMidpoint()
    {
        var plan = SoothingPlanner.Plan(SentenceSplitter.SplitScript("The fox slept."), new SoothingOptions { RateVariation = 0 });
        Assert.True(plan.Segments[0].InEnding);
        Assert.Equal(0.38f, plan.Segments[0].Rate, 4);
        Assert.Equal(-3.0, plan.Segments[0].GainDb, 6);
    }
}
