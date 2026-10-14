using StoryTime.Contracts;
using StoryTime.Core.Engines;
using StoryTime.Core.Pipeline;
using Xunit;

namespace StoryTime.Core.Tests;

public class TemplateStoryEngineTests
{
    private static TemplateStoryEngine Engine(int seed = 42) => new(() => seed);

    private static async Task<string> WeaveFullTextAsync(TemplateStoryEngine engine, ChildProfile profile, StoryBible? bible = null)
    {
        var outline = await engine.GenerateOutlineAsync(profile, bible, TemplateStoryEngine.SceneCount(profile.Length), CancellationToken.None);
        var scenes = new List<string>();
        foreach (var scene in outline.Scenes)
        {
            var tail = scenes.Count > 0 ? scenes[^1] : string.Empty;
            scenes.Add(await engine.GenerateSceneAsync(profile, outline, scene, tail, CancellationToken.None));
        }
        return await engine.PolishAsync(profile, string.Join("\n\n", scenes), CancellationToken.None);
    }

    private static int WordCount(string text) =>
        text.Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries).Length;

    [Theory]
    [InlineData(StoryLength.Five, 700)]
    [InlineData(StoryLength.Ten, 1400)]
    [InlineData(StoryLength.Fifteen, 2000)]
    public async Task WordCounts_WithinQuarterOfTarget(StoryLength length, int target)
    {
        var profile = TestEnv.Profile(length: length);
        var text = await WeaveFullTextAsync(Engine(), profile);
        var words = WordCount(text);
        Assert.InRange(words, (int)(target * 0.75), (int)(target * 1.25));
    }

    [Theory]
    [InlineData(StoryLength.Five, 5)]
    [InlineData(StoryLength.Ten, 8)]
    [InlineData(StoryLength.Fifteen, 12)]
    public async Task SceneCounts_MatchDesign(StoryLength length, int expected)
    {
        var profile = TestEnv.Profile(length: length);
        var outline = await Engine().GenerateOutlineAsync(profile, null, TemplateStoryEngine.SceneCount(length), CancellationToken.None);
        Assert.Equal(expected, outline.Scenes.Count);
    }

    [Fact]
    public async Task YoungerBand_UsesShorterSentences()
    {
        var young = TestEnv.Profile(age: 3);
        var older = TestEnv.Profile(age: 8);
        var youngText = await WeaveFullTextAsync(Engine(7), young);
        var olderText = await WeaveFullTextAsync(Engine(7), older);

        Assert.True(AvgSentenceLength(youngText) < AvgSentenceLength(olderText),
            $"3-4 avg {AvgSentenceLength(youngText):0.0} should be < 7-8 avg {AvgSentenceLength(olderText):0.0}");

        static double AvgSentenceLength(string text)
        {
            var sentences = StoryPipeline.SplitSentences(text);
            return sentences.Average(s => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        }
    }

    [Fact]
    public async Task SameSeed_ProducesIdenticalStory()
    {
        var profile = TestEnv.Profile();
        var a = await WeaveFullTextAsync(Engine(123), profile);
        var b = await WeaveFullTextAsync(Engine(123), profile);
        Assert.Equal(a, b);
    }

    [Fact]
    public async Task DifferentSeeds_ProduceDifferentStories()
    {
        var profile = TestEnv.Profile();
        var a = await WeaveFullTextAsync(Engine(1), profile);
        var b = await WeaveFullTextAsync(Engine(2), profile);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public async Task ChildName_IsWovenIn()
    {
        var profile = TestEnv.Profile();
        var text = await WeaveFullTextAsync(Engine(), profile);
        Assert.Contains(profile.Name, text);
    }

    [Fact]
    public async Task Story_AlwaysEndsWithCalmSleepyClose()
    {
        for (var seed = 0; seed < 8; seed++)
        {
            var text = await WeaveFullTextAsync(Engine(seed), TestEnv.Profile());
            Assert.EndsWith("Goodnight.", text.TrimEnd());
        }
    }

    [Fact]
    public async Task SeriesMode_ReusesBibleCharacters()
    {
        var profile = TestEnv.Profile();
        var bible = new StoryBible
        {
            EpisodeCount = 1,
            Characters = [new CharacterCard { Name = "Marlow", Kind = "otter", Trait = "gentle" }],
            LastEpisodeRecap = "The Lantern Fox — even small lights make the dark feel friendly.",
        };
        var engine = Engine(99);
        var outline = await engine.GenerateOutlineAsync(profile, bible, 5, CancellationToken.None);
        Assert.Contains(outline.Characters, c => c.Name == "Marlow");

        var text = await WeaveFullTextAsync(engine, profile, bible);
        Assert.Contains("Marlow", text);
    }

    [Fact]
    public async Task SafetyClean_ForAllInterestsAndBands()
    {
        var filter = new SafetyFilter();
        foreach (var interest in OnboardingInterests())
        {
            foreach (var age in new[] { 3, 5, 8 })
            {
                var profile = TestEnv.Profile(age: age);
                profile.Interests = [interest];
                var text = await WeaveFullTextAsync(Engine(interest.GetHashCode() ^ age), profile);
                var flags = filter.Check(text);
                Assert.True(flags.Count == 0, $"Interest '{interest}' age {age} flagged: {string.Join(",", flags)}");
            }
        }

        static string[] OnboardingInterests() =>
            ["Foxes", "Ocean", "Magic", "Space", "Forest", "Clouds", "Stars", "Music", "Trains", "Gardens"];
    }

    [Fact]
    public async Task SafetyClean_AcrossManySeeds()
    {
        var filter = new SafetyFilter();
        for (var seed = 0; seed < 12; seed++)
        {
            var text = await WeaveFullTextAsync(Engine(seed), TestEnv.Profile());
            Assert.True(filter.IsClean(text), $"Seed {seed} produced flagged text");
        }
    }

    [Fact]
    public async Task PosterSpec_IsDeterministicAndValid()
    {
        var profile = TestEnv.Profile();
        var engine = Engine(5);
        var outline = await engine.GenerateOutlineAsync(profile, null, 5, CancellationToken.None);
        var a = await engine.GeneratePosterSpecAsync(outline, CancellationToken.None);
        var b = await engine.GeneratePosterSpecAsync(outline, CancellationToken.None);

        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(a), System.Text.Json.JsonSerializer.Serialize(b));
        Assert.True(StoryTime.Poster.Spec.PosterSpecRules.SpecEquals(a, StoryTime.Poster.Spec.PosterSpecRules.Normalize(a)));
        Assert.InRange(a.Features.Count, 1, 3);
    }

    [Fact]
    public async Task Outline_HasTitleAndCharacters()
    {
        var outline = await Engine().GenerateOutlineAsync(TestEnv.Profile(), null, 5, CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(outline.Title));
        Assert.True(outline.Characters.Count >= 2);
        Assert.Equal("Aria", outline.Characters[0].Name);
    }

    // ---- Content filter level (additive 2026-07-30) ----

    // Titles are shape-diverse since 2026-07-31 and no longer identify the skeleton;
    // morals are 1:1 with skeletons and stable, so brave-ish detection keys on them.
    private static readonly string[] BraveishMoralMarkers =
        ["a little courage and a good friend can find anything",
         "a storm outside makes the inside twice as cozy",
         "a puzzle shared is already half solved"];

    private static bool IsBraveishOutline(StoryOutline outline) =>
        BraveishMoralMarkers.Any(m => outline.Moral.StartsWith(m, StringComparison.Ordinal));

    [Fact]
    public async Task Gentle_NeverDrawsBraveishSkeletons()
    {
        for (var seed = 0; seed < 60; seed++)
        {
            var outline = await Engine(seed).GenerateOutlineAsync(TestEnv.Profile(), null, 5, CancellationToken.None);
            Assert.False(IsBraveishOutline(outline), $"Seed {seed} drew a brave-ish skeleton at Gentle");
        }
    }

    [Fact]
    public async Task Braveish_DrawsGentlyAdventurousSkeletonsForSomeSeeds()
    {
        var braveHits = 0;
        for (var seed = 0; seed < 60; seed++)
        {
            var profile = TestEnv.Profile();
            profile.ContentFilter = ContentFilterLevel.Braveish;
            var outline = await Engine(seed).GenerateOutlineAsync(profile, null, 5, CancellationToken.None);
            if (IsBraveishOutline(outline)) braveHits++;
        }
        // 3 of 13 skeletons are brave-ish; over 60 seeds some (but not all) draws hit them.
        Assert.InRange(braveHits, 1, 59);
    }

    [Fact]
    public async Task Braveish_Age3To4_IsIdenticalToGentle()
    {
        for (var seed = 0; seed < 12; seed++)
        {
            var gentle = TestEnv.Profile(age: 3);
            var brave = TestEnv.Profile(age: 3);
            brave.ContentFilter = ContentFilterLevel.Braveish;
            var a = await WeaveFullTextAsync(Engine(seed), gentle);
            var b = await WeaveFullTextAsync(Engine(seed), brave);
            Assert.Equal(a, b);   // the youngest band tones Brave-ish down to Gentle exactly
        }
    }

    [Fact]
    public async Task Braveish_StoriesStaySafetyCleanAndEndGently()
    {
        var filter = new SafetyFilter();
        for (var seed = 0; seed < 20; seed++)
        {
            var profile = TestEnv.Profile(age: 7);
            profile.ContentFilter = ContentFilterLevel.Braveish;
            var text = await WeaveFullTextAsync(Engine(seed), profile);
            Assert.True(filter.IsClean(text), $"Brave-ish seed {seed} flagged: {string.Join(",", filter.Check(text))}");
            Assert.EndsWith("Goodnight.", text.TrimEnd());
        }
    }

    [Fact]
    public async Task Repair_ReplacesFlaggedTerms()
    {
        var filter = new SafetyFilter();
        var engine = Engine();
        const string passage = "The monster screamed and the ghost was terrifying.";
        var repaired = await engine.RepairPassageAsync(TestEnv.Profile(), passage, "monster, screamed, ghost", CancellationToken.None);
        Assert.True(filter.IsClean(repaired), $"Still flagged: {string.Join(",", filter.Check(repaired))}");
    }

    [Fact]
    public async Task Polish_AppendsGentleCloseWhenMissing()
    {
        var engine = Engine();
        var polished = await engine.PolishAsync(TestEnv.Profile(), "A tiny tale without an ending", CancellationToken.None);
        Assert.EndsWith("Goodnight.", polished.TrimEnd());
    }
}
