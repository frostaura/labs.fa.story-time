using StoryTime.Contracts;
using StoryTime.Core.Abstractions;
using StoryTime.Core.Engines;
using StoryTime.Core.Engines.Prompts;
using StoryTime.Core.Pipeline;
using StoryTime.Core.Poster;
using StoryTime.Core.Services;
using Xunit;

namespace StoryTime.Core.Tests;

/// <summary>Founder title/prose quality pass (2026-07-31): titles must be shape-diverse and
/// non-echoing; template prose must flow (long/short rhythm), not read as stitched short
/// sentences. Thresholds are the ProseMetrics/TitleMetrics acceptance bars.</summary>
public class ProseAndTitleQualityTests
{
    private static TemplateStoryEngine Engine(int seed) => new(() => seed);

    private static async Task<string> WeaveFullTextAsync(TemplateStoryEngine engine, ChildProfile profile)
    {
        var outline = await engine.GenerateOutlineAsync(profile, null, TemplateStoryEngine.SceneCount(profile.Length), CancellationToken.None);
        var scenes = new List<string>();
        foreach (var scene in outline.Scenes)
            scenes.Add(await engine.GenerateSceneAsync(profile, outline, scene, scenes.Count > 0 ? scenes[^1] : string.Empty, CancellationToken.None));
        return await engine.PolishAsync(profile, string.Join("\n\n", scenes), CancellationToken.None);
    }

    // ---------------- titles ----------------

    private static async Task<List<string>> TitlesAcrossSeedsAsync(int count, bool feedRecents = true)
    {
        var profile = TestEnv.Profile();
        var titles = new List<string>();
        for (var seed = 1; seed <= count; seed++)
        {
            var outline = await Engine(seed * 7919).GenerateOutlineAsync(profile, null, 5, CancellationToken.None);
            titles.Add(outline.Title);
            if (feedRecents) profile.RecentTitles = [.. titles];
        }
        return titles;
    }

    [Fact]
    public async Task TemplateTitles_ThirtySeeds_AreAllDistinct()
    {
        var titles = await TitlesAcrossSeedsAsync(30);
        Assert.Equal(30, titles.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task TemplateTitles_ThirtySeeds_LowMeanPairwiseJaccard()
    {
        var titles = await TitlesAcrossSeedsAsync(30);
        var pairs = new List<double>();
        for (var i = 0; i < titles.Count; i++)
            for (var j = i + 1; j < titles.Count; j++)
                pairs.Add(TitleMetrics.TokenJaccard(titles[i], titles[j]));
        Assert.True(pairs.Average() < 0.35, $"Mean pairwise Jaccard {pairs.Average():0.000} ≥ 0.35 — titles converge");
    }

    [Fact]
    public async Task TemplateTitles_ThirtySeeds_AtLeastFourShapes()
    {
        var titles = await TitlesAcrossSeedsAsync(30);
        var shapes = titles.Select(TitleMetrics.ShapeOf).Distinct().ToList();
        Assert.True(shapes.Count >= 4, $"Only {shapes.Count} title shapes: {string.Join(", ", shapes)}");
    }

    [Fact]
    public async Task TemplateTitles_NotAlwaysTheOpening()
    {
        var titles = await TitlesAcrossSeedsAsync(30);
        var theShare = titles.Count(t => t.StartsWith("The ", StringComparison.Ordinal)) / (double)titles.Count;
        Assert.True(theShare < 0.5, $"{theShare:P0} of titles still open with \"The\"");
    }

    [Fact]
    public async Task TemplateTitles_SimilarityGuard_RedrawsAgainstRecentTitle()
    {
        // Same seed twice: unguarded draw first, then with that exact title as a negative.
        var profile = TestEnv.Profile();
        var first = (await Engine(4242).GenerateOutlineAsync(profile, null, 5, CancellationToken.None)).Title;

        profile.RecentTitles = [first];
        var second = (await Engine(4242).GenerateOutlineAsync(profile, null, 5, CancellationToken.None)).Title;

        Assert.NotEqual(first, second);
        Assert.True(TitleMetrics.TokenJaccard(first, second) <= 0.5,
            $"Redraw \"{second}\" still shares >50% tokens with \"{first}\"");
    }

    [Fact]
    public async Task TemplateTitles_AreDeterministicPerSeedAndRecents()
    {
        var a = (await Engine(77).GenerateOutlineAsync(TestEnv.Profile(), null, 5, CancellationToken.None)).Title;
        var b = (await Engine(77).GenerateOutlineAsync(TestEnv.Profile(), null, 5, CancellationToken.None)).Title;
        Assert.Equal(a, b);
    }

    // ---------------- prose rhythm ----------------

    [Theory]
    [InlineData(6)]
    [InlineData(8)]
    public async Task TemplateProse_OlderBands_FlowLikeAStoryteller(int age)
    {
        for (var seed = 1; seed <= 5; seed++)
        {
            var text = await WeaveFullTextAsync(Engine(seed * 31), TestEnv.Profile(age: age));
            var r = ProseMetrics.Analyze(text);
            Assert.True(r.AvgWords >= 20, $"Age {age} seed {seed}: avg {r.AvgWords:0.0} < 20 words — founder floor (raised 2026-07-31)");
            Assert.True(r.CV >= 0.3, $"Age {age} seed {seed}: CV {r.CV:0.00} < 0.3 — monotone rhythm");
        }
    }

    [Fact]
    public async Task TemplateProse_YoungBand_KeepsGentleShortSentences()
    {
        for (var seed = 1; seed <= 5; seed++)
        {
            var text = await WeaveFullTextAsync(Engine(seed * 31), TestEnv.Profile(age: 3));
            var r = ProseMetrics.Analyze(text);
            Assert.True(r.AvgWords <= 9.5, $"Age 3 seed {seed}: avg {r.AvgWords:0.0} > 9.5 — too long for toddlers");
        }
    }

    [Fact]
    public void ProseMetrics_ComputesKnownValues()
    {
        var r = ProseMetrics.Analyze("One two three. One two three four five six seven eight nine ten.");
        Assert.Equal(2, r.SentenceCount);
        Assert.Equal(6.5, r.AvgWords, 3);
        Assert.Equal(0.5, r.ShortShare, 3);   // "One two three." is under 8 words
        Assert.Equal(1, r.LongestShortRun);
        Assert.True(r.CV > 0.5);
    }

    [Fact]
    public void TitleMetrics_JaccardAndShapes()
    {
        Assert.Equal(1.0, TitleMetrics.TokenJaccard("The Lantern Fox", "Lantern Fox"), 3);
        Assert.Equal(0.0, TitleMetrics.TokenJaccard("The Lantern Fox", "Goodnight, Tide Pools"), 3);
        Assert.Equal("Question", TitleMetrics.ShapeOf("Who Lit the Little Lantern?"));
        Assert.Equal("How", TitleMetrics.ShapeOf("How the Moon Learned to Hum"));
        Assert.Equal("AndPairing", TitleMetrics.ShapeOf("Wren and the Paper Boat"));
        Assert.Equal("TheOpening", TitleMetrics.ShapeOf("The Lantern Fox"));
        Assert.Equal("Evocative", TitleMetrics.ShapeOf("Thistledown"));
        Assert.Equal("Doing", TitleMetrics.ShapeOf("Counting Stars with Pip"));
    }

    // ---------------- recent-title threading (pipeline seam) ----------------

    private sealed class TitleCapturingEngine : IStoryEngine
    {
        private readonly TemplateStoryEngine _inner = new(() => 42);
        public List<string>? SeenRecentTitles;

        public string Name => "capture";

        public Task<StoryOutline> GenerateOutlineAsync(ChildProfile profile, StoryBible? bible, int sceneCount, CancellationToken ct)
        {
            SeenRecentTitles = [.. profile.RecentTitles];
            return _inner.GenerateOutlineAsync(profile, bible, sceneCount, ct);
        }

        public Task<string> GenerateSceneAsync(ChildProfile profile, StoryOutline outline, SceneOutline scene, string previousTail, CancellationToken ct)
            => _inner.GenerateSceneAsync(profile, outline, scene, previousTail, ct);
        public Task<string> PolishAsync(ChildProfile profile, string stitchedText, CancellationToken ct)
            => _inner.PolishAsync(profile, stitchedText, ct);
        public Task<PosterSpec> GeneratePosterSpecAsync(StoryOutline outline, CancellationToken ct)
            => _inner.GeneratePosterSpecAsync(outline, ct);
        public Task<string> RepairPassageAsync(ChildProfile profile, string passage, string reason, CancellationToken ct)
            => _inner.RepairPassageAsync(profile, passage, reason, ct);
    }

    [Fact]
    public async Task Pipeline_ThreadsRecentShelfTitles_ToTheOutlineStage()
    {
        await using var env = await TestEnv.CreateAsync();
        var profile = TestEnv.Profile();
        await env.Store.SaveProfileAsync(profile);

        // Shelf history: two good stories, one failed (must be excluded), newest first.
        var t0 = new DateTime(2026, 7, 1, 19, 0, 0, DateTimeKind.Utc);
        await env.Store.SaveStoryAsync(new StoryRecord { ProfileId = profile.Id, Title = "The Lantern Fox", State = StoryState.Ready, CreatedUtc = t0 });
        await env.Store.SaveStoryAsync(new StoryRecord { ProfileId = profile.Id, Title = "Goodnight, Tide Pools", State = StoryState.Ready, CreatedUtc = t0.AddDays(1) });
        await env.Store.SaveStoryAsync(new StoryRecord { ProfileId = profile.Id, Title = "A story that wandered off", State = StoryState.Failed, CreatedUtc = t0.AddDays(2) });

        var engine = new TitleCapturingEngine();
        var clock = new FakeClock();
        var settings = new InMemorySettingsStore();
        var pipeline = new StoryPipeline(
            engine, new FakeTtsEngine(), new PosterComposer(), new NullPosterRasterizer(),
            env.Store, settings, new CooldownService(settings, clock, new StoreEntitlements(settings, clock)),
            new SafetyFilter(), new FakeNotificationService(), env.Options, clock);

        var story = await pipeline.WeaveAsync(profile, null, CancellationToken.None);

        Assert.NotEqual(StoryState.Failed, story.State);
        Assert.NotNull(engine.SeenRecentTitles);
        Assert.Equal(["Goodnight, Tide Pools", "The Lantern Fox"], engine.SeenRecentTitles);
    }

    // ---------------- prompt deltas (pin the founder-pass wording) ----------------

    [Fact]
    public void OutlinePrompt_InjectsRecentTitlesAsNegatives()
    {
        var profile = TestEnv.Profile();
        profile.RecentTitles = ["The Lantern Fox", "The Softest Sound"];
        var prompt = StoryPrompts.OutlineUser(profile, null, 5);
        Assert.Contains("\"The Lantern Fox\"; \"The Softest Sound\"", prompt);
        Assert.Contains("do not reuse their distinctive words", prompt);
        Assert.Contains("do not repeat their shapes", prompt);
    }

    [Fact]
    public void OutlinePrompt_UsesShapeHint_NoLiteralExampleTitles_AndSkipsNegativesWhenShelfEmpty()
    {
        var prompt = StoryPrompts.OutlineUser(TestEnv.Profile(), null, 5, StoryPrompts.TitleShapeHints[0]);
        Assert.Contains("Vary the title's shape", prompt);
        Assert.Contains("Tonight, shape the title as: " + StoryPrompts.TitleShapeHints[0], prompt);
        Assert.Contains("Never shape it as \"The <adjective> <noun>\"", prompt);
        Assert.DoesNotContain("recent stories were titled", prompt);
        // A small model copies literal example titles verbatim (observed 2026-07-31), so
        // the prompt must never carry one.
        Assert.DoesNotContain("Tide Pools", prompt);
        Assert.DoesNotContain("Thistledown", prompt);
    }

    [Fact]
    public void TitleShapeHints_CoverAtLeastFiveDistinctShapes()
    {
        Assert.True(StoryPrompts.TitleShapeHints.Length >= 5);
        Assert.Equal(StoryPrompts.TitleShapeHints.Length, StoryPrompts.TitleShapeHints.Distinct().Count());
    }

    [Fact]
    public void SystemPrompts_CarryFlowingProseGuidance_PerBand()
    {
        Assert.Contains("Never write three short sentences in a row", StoryPrompts.SystemPrompt(AgeBand.Age5To6));
        Assert.Contains("Never write three short sentences in a row", StoryPrompts.SystemPrompt(AgeBand.Age7To8));
        // 3–4 keeps short sentences with only a gentle stretch — no flowing mandate.
        var young = StoryPrompts.SystemPrompt(AgeBand.Age3To4);
        Assert.Contains("short sentences of five to ten words", young);
        Assert.DoesNotContain("Never write three short sentences in a row", young);
    }

    [Fact]
    public void ScenePrompt_AsksForSensoryDetailAndGentleSurprise()
    {
        var profile = TestEnv.Profile();
        var outline = new StoryOutline { Title = "T", Moral = "m", Scenes = [new SceneOutline { Id = 1, Summary = "s" }] };
        var prompt = StoryPrompts.SceneUser(profile, outline, outline.Scenes[0], string.Empty);
        Assert.Contains("concrete sensory details", prompt);
        Assert.Contains("gentle surprise", prompt);
    }

    [Fact]
    public void PolishPrompt_MergesChoppyRuns_AndKeepsPacingPunctuation()
    {
        var prompt = StoryPrompts.PolishUser(TestEnv.Profile(), "text");
        Assert.Contains("merge them into one longer flowing sentence", prompt);
        Assert.Contains("ellipsis", prompt);        // pacing channel stays
        Assert.Contains("em-dash", prompt);
    }
}
