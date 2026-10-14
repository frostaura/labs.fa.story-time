using StoryTime.Contracts;
using StoryTime.Core.Abstractions;
using StoryTime.Core.Engines;
using StoryTime.Core.Pipeline;
using StoryTime.Core.Poster;
using StoryTime.Core.Services;
using Xunit;

namespace StoryTime.Core.Tests;

public class StoryPipelineTests
{
    private sealed class Harness : IAsyncDisposable
    {
        public TestEnv Env = null!;
        public FakeClock Clock = new();
        public InMemorySettingsStore Settings = new();
        public FakeTtsEngine Tts = new();
        public FakeNotificationService Notifications = new();
        public CooldownService Cooldown = null!;
        public IStoryEngine Engine = new TemplateStoryEngine(() => 42);
        public IDeviceCapabilities Capabilities = new DefaultDeviceCapabilities();

        public static async Task<Harness> CreateAsync(WeaveStrategy? forced = null)
        {
            var h = new Harness { Env = await TestEnv.CreateAsync() };
            h.Env.Options.ForcedWeaveStrategy = forced;
            h.Cooldown = new CooldownService(h.Settings, h.Clock, new StubEntitlements(h.Settings, h.Clock));
            return h;
        }

        public StoryPipeline Pipeline() => new(
            Engine, Tts, new PosterComposer(), new NullPosterRasterizer(),
            Env.Store, Settings, Cooldown, new SafetyFilter(), Notifications, Env.Options, Clock, Capabilities);

        public async ValueTask DisposeAsync() => await Env.DisposeAsync();
    }

    private sealed class FakeCapabilities : IDeviceCapabilities
    {
        public long PhysicalMemoryBytes { get; set; } = 8L * 1024 * 1024 * 1024;
        public bool IsLowMemoryDevice { get; set; }
        public int ProcessorCount { get; set; } = 8;
    }

    /// <summary>Wraps the template engine to observe/interfere with scene calls.</summary>
    private sealed class InterceptingEngine : IStoryEngine
    {
        private readonly IStoryEngine _inner;
        public int SceneCalls;
        public Action<int>? OnSceneCall;
        public Func<string, string>? SceneTransform;
        public Func<string, string>? RepairOverride;

        public InterceptingEngine(IStoryEngine inner) => _inner = inner;

        public string Name => _inner.Name;

        public Task<StoryOutline> GenerateOutlineAsync(ChildProfile profile, StoryBible? bible, int sceneCount, CancellationToken ct)
            => _inner.GenerateOutlineAsync(profile, bible, sceneCount, ct);

        public async Task<string> GenerateSceneAsync(ChildProfile profile, StoryOutline outline, SceneOutline scene, string previousTail, CancellationToken ct)
        {
            SceneCalls++;
            OnSceneCall?.Invoke(SceneCalls);
            var text = await _inner.GenerateSceneAsync(profile, outline, scene, previousTail, ct);
            return SceneTransform?.Invoke(text) ?? text;
        }

        public Task<string> PolishAsync(ChildProfile profile, string stitchedText, CancellationToken ct)
            => _inner.PolishAsync(profile, stitchedText, ct);

        public Task<PosterSpec> GeneratePosterSpecAsync(StoryOutline outline, CancellationToken ct)
            => _inner.GeneratePosterSpecAsync(outline, ct);

        public Task<string> RepairPassageAsync(ChildProfile profile, string passage, string reason, CancellationToken ct)
            => RepairOverride is not null ? Task.FromResult(RepairOverride(passage)) : _inner.RepairPassageAsync(profile, passage, reason, ct);
    }

    [Fact]
    public async Task Weave_ThreadsContentFilterLevelFromSettingsToEngines()
    {
        await using var h = await Harness.CreateAsync();
        h.Settings.ContentFilter = ContentFilterLevel.Braveish;
        var profile = TestEnv.Profile();
        Assert.Equal(ContentFilterLevel.Gentle, profile.ContentFilter);   // pre-weave default

        var story = await h.Pipeline().WeaveAsync(profile, null, CancellationToken.None);

        // The pipeline stamps the app-level setting onto the profile seam the engines read.
        Assert.Equal(ContentFilterLevel.Braveish, profile.ContentFilter);
        Assert.Equal(ContentFilterLevel.Braveish, profile.EffectiveContentFilter);
        Assert.NotEqual(StoryState.Failed, story.State);
    }

    [Fact]
    public async Task Weave_AtBraveish_StillPassesTheSafetyFilter()
    {
        await using var h = await Harness.CreateAsync();
        h.Settings.ContentFilter = ContentFilterLevel.Braveish;
        var story = await h.Pipeline().WeaveAsync(TestEnv.Profile(age: 7), null, CancellationToken.None);
        Assert.True(new SafetyFilter().IsClean(story.FullText),
            "Brave-ish weave produced text the (level-independent) safety filter flags");
    }

    [Fact]
    public async Task Weave_CompletesWithApprovalStateWhenApprovalOn()
    {
        await using var h = await Harness.CreateAsync();
        h.Settings.ApprovalStep = true;
        var story = await h.Pipeline().WeaveAsync(TestEnv.Profile(), null, CancellationToken.None);
        Assert.Equal(StoryState.AwaitingApproval, story.State);
        Assert.False(string.IsNullOrWhiteSpace(story.FullText));
        Assert.False(string.IsNullOrWhiteSpace(story.Title));
    }

    [Fact]
    public async Task Weave_CompletesReadyWhenApprovalOff()
    {
        await using var h = await Harness.CreateAsync();
        h.Settings.ApprovalStep = false;
        var story = await h.Pipeline().WeaveAsync(TestEnv.Profile(), null, CancellationToken.None);
        Assert.Equal(StoryState.Ready, story.State);
    }

    [Fact]
    public async Task Progress_IsMonotonicAndStagesInOrder()
    {
        await using var h = await Harness.CreateAsync();
        var reports = new List<WeaveProgress>();
        await h.Pipeline().WeaveAsync(TestEnv.Profile(), new SyncProgress<WeaveProgress>(reports.Add), CancellationToken.None);

        Assert.NotEmpty(reports);
        for (var i = 1; i < reports.Count; i++)
        {
            Assert.True(reports[i].Percent >= reports[i - 1].Percent, "percent must never decrease");
            Assert.True(reports[i].Stage >= reports[i - 1].Stage, "stages must run in order");
        }
        Assert.Equal(WeaveStage.Done, reports[^1].Stage);
        Assert.Equal(100, reports[^1].Percent);
    }

    [Fact]
    public async Task Narration_IsTeaserFirst()
    {
        // Overlapped (default on this dev machine): teaser renders FIRST, then the story
        // arrives as per-window parts whose sentence counts sum to the full split.
        await using var h = await Harness.CreateAsync();
        var story = await h.Pipeline().WeaveAsync(TestEnv.Profile(), null, CancellationToken.None);

        Assert.Equal(3, h.Tts.SynthesizeCallSentenceCounts[0]);                       // teaser: first ~3 sentences
        Assert.Contains("teaser", h.Tts.OutputPaths[0]);
        var narrated = h.Tts.SynthesizeCallSentenceCounts.Skip(1).Sum();
        Assert.Equal(StoryPipeline.SplitSentences(story.FullText).Count, narrated);   // windows cover the whole story
        Assert.All(h.Tts.OutputPaths.Skip(1), p => Assert.Contains("-part", p));
    }

    [Fact]
    public async Task Narration_IsTeaserFirst_Sequential()
    {
        // The forced-sequential path keeps the classic two-call shape exactly.
        await using var h = await Harness.CreateAsync(WeaveStrategy.Sequential);
        await h.Pipeline().WeaveAsync(TestEnv.Profile(), null, CancellationToken.None);
        Assert.Equal(2, h.Tts.SynthesizeCallSentenceCounts.Count);
        Assert.Equal(3, h.Tts.SynthesizeCallSentenceCounts[0]);
        Assert.True(h.Tts.SynthesizeCallSentenceCounts[1] > 3);
        Assert.Contains("teaser", h.Tts.OutputPaths[0]);
    }

    [Fact]
    public async Task Completion_StartsCooldown_ClearsCheckpoint_Notifies()
    {
        await using var h = await Harness.CreateAsync();
        await h.Pipeline().WeaveAsync(TestEnv.Profile(), null, CancellationToken.None);
        Assert.True(h.Cooldown.IsActive);
        Assert.Null(await h.Env.Store.GetCheckpointAsync());
        Assert.Equal(1, h.Notifications.ShownCount);
    }

    [Fact]
    public async Task Completion_RespectsNotificationToggle()
    {
        await using var h = await Harness.CreateAsync();
        h.Settings.StoryReadyNotifications = false;
        await h.Pipeline().WeaveAsync(TestEnv.Profile(), null, CancellationToken.None);
        Assert.Equal(0, h.Notifications.ShownCount);
    }

    [Fact]
    public async Task SeriesMode_UpdatesBibleAndEpisodeNumbers()
    {
        await using var h = await Harness.CreateAsync();
        var profile = TestEnv.Profile(StoryMode.Series);
        profile.Id = 0;
        await h.Env.Store.SaveProfileAsync(profile);

        var first = await h.Pipeline().WeaveAsync(profile, null, CancellationToken.None);
        Assert.Equal(1, first.EpisodeNumber);
        Assert.Equal(string.Empty, first.RecapLine);

        var bible = await h.Env.Store.GetBibleAsync(profile.Id);
        Assert.NotNull(bible);
        Assert.Equal(1, bible.EpisodeCount);
        Assert.NotEmpty(bible.LastEpisodeRecap);
        Assert.True(bible.Characters.Count >= 2);

        h.Cooldown.Clear();
        var second = await h.Pipeline().WeaveAsync(profile, null, CancellationToken.None);
        Assert.Equal(2, second.EpisodeNumber);
        Assert.StartsWith("Previously: ", second.RecapLine);

        bible = await h.Env.Store.GetBibleAsync(profile.Id);
        Assert.Equal(2, bible!.EpisodeCount);
    }

    [Fact]
    public async Task OneShotMode_SkipsBible()
    {
        await using var h = await Harness.CreateAsync();
        var profile = TestEnv.Profile(StoryMode.OneShot);
        await h.Env.Store.SaveProfileAsync(profile);
        var story = await h.Pipeline().WeaveAsync(profile, null, CancellationToken.None);
        Assert.Equal(0, story.EpisodeNumber);
        Assert.Null(await h.Env.Store.GetBibleAsync(profile.Id));
    }

    [Fact]
    public async Task Cancellation_LeavesCheckpointForResume()
    {
        await using var h = await Harness.CreateAsync();
        var cts = new CancellationTokenSource();
        var engine = new InterceptingEngine(new TemplateStoryEngine(() => 42))
        {
            OnSceneCall = calls => { if (calls == 3) cts.Cancel(); },
        };
        h.Engine = engine;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            h.Pipeline().WeaveAsync(TestEnv.Profile(), null, cts.Token));

        var checkpoint = await h.Env.Store.GetCheckpointAsync();
        Assert.NotNull(checkpoint);
        Assert.Equal(WeaveStage.Scenes, checkpoint.Stage);
        Assert.Equal(2, checkpoint.SceneIndex); // two scenes committed before the cancel

        var story = await h.Env.Store.GetStoryAsync(checkpoint.StoryId);
        Assert.Equal(StoryState.Weaving, story!.State);
        Assert.False(h.Cooldown.IsActive);
    }

    [Fact]
    public async Task Resume_ContinuesFromCheckpointWithoutRedoingScenes()
    {
        await using var h = await Harness.CreateAsync();
        var cts = new CancellationTokenSource();
        var interceptor = new InterceptingEngine(new TemplateStoryEngine(() => 42))
        {
            OnSceneCall = calls => { if (calls == 3) cts.Cancel(); },
        };
        h.Engine = interceptor;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            h.Pipeline().WeaveAsync(TestEnv.Profile(), null, cts.Token));

        // Fresh pipeline (app restart): resume must finish and only weave the remaining scenes.
        var resumedEngine = new InterceptingEngine(new TemplateStoryEngine(() => 42));
        h.Engine = resumedEngine;
        var story = await h.Pipeline().ResumeAsync(TestEnv.Profile(), null, CancellationToken.None);

        Assert.NotNull(story);
        Assert.True(story.State is StoryState.Ready or StoryState.AwaitingApproval);
        Assert.Equal(3, resumedEngine.SceneCalls); // 5 scenes total, 2 checkpointed
        Assert.Null(await h.Env.Store.GetCheckpointAsync());
    }

    [Fact]
    public async Task Resume_WithNoCheckpointReturnsNull()
    {
        await using var h = await Harness.CreateAsync();
        Assert.Null(await h.Pipeline().ResumeAsync(TestEnv.Profile(), null, CancellationToken.None));
    }

    [Fact]
    public async Task UnrepairableFlaggedScene_FailsGentlyWithoutCooldown()
    {
        await using var h = await Harness.CreateAsync();
        h.Engine = new InterceptingEngine(new TemplateStoryEngine(() => 42))
        {
            SceneTransform = text => text + " Then a terrifying monster screamed.",
            RepairOverride = passage => passage, // repair never helps
        };

        var story = await h.Pipeline().WeaveAsync(TestEnv.Profile(), null, CancellationToken.None);
        Assert.Equal(StoryState.Failed, story.State);
        Assert.Equal(StoryPipeline.GentleFailureMessage, story.FullText);
        Assert.False(h.Cooldown.IsActive);
        Assert.Null(await h.Env.Store.GetCheckpointAsync());
        Assert.Equal(0, h.Notifications.ShownCount);
    }

    [Fact]
    public async Task RepairableFlaggedScene_IsRepairedAndSucceeds()
    {
        await using var h = await Harness.CreateAsync();
        var appended = false;
        h.Engine = new InterceptingEngine(new TemplateStoryEngine(() => 42))
        {
            // Flag exactly one scene; the template engine's own repair map fixes it.
            SceneTransform = text =>
            {
                if (appended) return text;
                appended = true;
                return text + " A monster watched kindly.";
            },
        };

        var story = await h.Pipeline().WeaveAsync(TestEnv.Profile(), null, CancellationToken.None);
        Assert.NotEqual(StoryState.Failed, story.State);
        Assert.DoesNotContain("monster", story.FullText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("shadow puppet", story.FullText);
    }

    [Fact]
    public async Task PosterStage_PersistsValidatedSpec()
    {
        await using var h = await Harness.CreateAsync();
        var story = await h.Pipeline().WeaveAsync(TestEnv.Profile(), null, CancellationToken.None);
        Assert.False(string.IsNullOrEmpty(story.PosterSpecJson));
        var spec = StoryTime.Poster.Spec.PosterSpecJson.Parse(story.PosterSpecJson);
        Assert.NotNull(spec);
        Assert.Equal(PosterSpec.CurrentVersion, spec.Version);
        // Persisted specs are already normalized (contract v3, enum-only, compatible features).
        Assert.True(StoryTime.Poster.Spec.PosterSpecRules.SpecEquals(spec, StoryTime.Poster.Spec.PosterSpecRules.Normalize(spec)));
    }

    [Fact]
    public async Task Narration_PersistsManifestData()
    {
        await using var h = await Harness.CreateAsync();
        var story = await h.Pipeline().WeaveAsync(TestEnv.Profile(), null, CancellationToken.None);
        Assert.True(story.DurationSeconds > 0);
        Assert.False(string.IsNullOrEmpty(story.AudioPath));
        Assert.True(File.Exists(story.AudioPath));
        Assert.False(string.IsNullOrEmpty(story.TimestampsJson));
    }

    // ================= overlapped-strategy tests (perf pass 2026-07-31) =================

    [Fact]
    public void WindowPlan_PairsScenesWithRemainderTail()
    {
        Assert.Equal(new List<(int, int)> { (0, 2), (2, 2), (4, 1) }, StoryPipeline.WindowPlan(5));
        Assert.Equal(new List<(int, int)> { (0, 2), (2, 2), (4, 2), (6, 2) }, StoryPipeline.WindowPlan(8));
        Assert.Equal(new List<(int, int)> { (0, 1) }, StoryPipeline.WindowPlan(1));
    }

    [Fact]
    public async Task Overlap_NarrationStartsBeforeLastSceneCompletes()
    {
        await using var h = await Harness.CreateAsync(WeaveStrategy.Overlapped);
        var narrationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Tts.OnSynthesizeStart = _ => narrationStarted.TrySetResult();

        // The LAST scene call (5 of 5) blocks until narration has started. Under the
        // overlapped schedule window 1 was polished and handed to the TTS long before —
        // so this returns true quickly; under a sequential schedule it would time out.
        var startedBeforeLastScene = false;
        h.Engine = new InterceptingEngine(new TemplateStoryEngine(() => 42))
        {
            OnSceneCall = calls =>
            {
                if (calls == 5) startedBeforeLastScene = narrationStarted.Task.Wait(TimeSpan.FromSeconds(15));
            },
        };

        var story = await h.Pipeline().WeaveAsync(TestEnv.Profile(), null, CancellationToken.None);
        Assert.NotEqual(StoryState.Failed, story.State);
        Assert.True(startedBeforeLastScene, "narration must begin while later scenes are still generating");
    }

    [Fact]
    public async Task Overlap_FullTextSplit_MatchesAppendedTimestampsExactly()
    {
        await using var h = await Harness.CreateAsync(WeaveStrategy.Overlapped);
        var story = await h.Pipeline().WeaveAsync(TestEnv.Profile(), null, CancellationToken.None);

        var sentences = StoryPipeline.SplitSentences(story.FullText);
        var timestamps = System.Text.Json.JsonSerializer.Deserialize<List<SentenceTimestamp>>(story.TimestampsJson)!;

        // Players re-split FullText and align by count — global indices must be 0..n-1 in
        // order and times must be monotonic across window seams.
        Assert.Equal(sentences.Count, timestamps.Count);
        for (var i = 0; i < timestamps.Count; i++)
        {
            Assert.Equal(i, timestamps[i].Index);
            Assert.True(timestamps[i].EndSeconds >= timestamps[i].StartSeconds);
            if (i > 0)
                Assert.True(timestamps[i].StartSeconds >= timestamps[i - 1].EndSeconds - 1e-9,
                    $"timestamp {i} moved backwards across a window seam");
        }
        Assert.True(File.Exists(story.AudioPath));
        Assert.Equal(story.DurationSeconds,
            StoryTime.Core.Engines.Narration.WavAudio.ReadDurationSeconds(story.AudioPath), 2);
        // Part files are working files — cleaned up after assembly.
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(story.AudioPath)!, "*-part*.wav"));
    }

    [Fact]
    public async Task Overlap_MiddleWindows_NeverEndTheStory()
    {
        await using var h = await Harness.CreateAsync(WeaveStrategy.Overlapped);
        var story = await h.Pipeline().WeaveAsync(TestEnv.Profile(), null, CancellationToken.None);
        // Exactly one gentle close, at the very end (the template polish appends it only
        // on the final window; a goodnight mid-story means a middle window closed it).
        var occurrences = story.FullText.Split("Goodnight.").Length - 1;
        Assert.Equal(1, occurrences);
        Assert.EndsWith("Goodnight.", story.FullText.TrimEnd());
    }

    [Fact]
    public async Task Overlap_CheckpointsWindowBoundary_AndResumesMidOverlap()
    {
        await using var h = await Harness.CreateAsync(WeaveStrategy.Overlapped);
        var cts = new CancellationTokenSource();
        var tts = h.Tts;
        h.Engine = new InterceptingEngine(new TemplateStoryEngine(() => 42))
        {
            OnSceneCall = calls =>
            {
                if (calls != 3) return;
                // Scene 3 (window 2's first): wait until window 1's narration part landed,
                // then cancel — a genuinely mid-overlap kill (scenes ahead of narration).
                SpinWait.SpinUntil(() => tts.OutputPaths.Any(p => p.Contains("-part0")), TimeSpan.FromSeconds(15));
                cts.Cancel();
            },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            h.Pipeline().WeaveAsync(TestEnv.Profile(), null, cts.Token));

        var checkpoint = await h.Env.Store.GetCheckpointAsync();
        Assert.NotNull(checkpoint);
        Assert.Equal("overlapped", checkpoint.Strategy);
        Assert.Equal(2, checkpoint.SceneIndex);                                  // scenes 1–2 committed
        var polished = System.Text.Json.JsonSerializer.Deserialize<List<string>>(checkpoint.PolishedWindowsJson)!;
        Assert.Single(polished);                                                 // window 1 polished
        Assert.True(checkpoint.TeaserDone);
        var parts = System.Text.Json.JsonSerializer.Deserialize<List<NarrationPart>>(checkpoint.NarrationPartsJson)!;
        Assert.Single(parts);                                                    // window 1 narrated
        Assert.True(File.Exists(parts[0].AudioPath));

        // Fresh pipeline (app restart): only the remaining scenes are generated, the
        // narrated window is NOT re-synthesized, and the assembled story is consistent.
        var resumedEngine = new InterceptingEngine(new TemplateStoryEngine(() => 42));
        h.Engine = resumedEngine;
        var resumedTts = new FakeTtsEngine();
        h.Tts = resumedTts;
        var story = await h.Pipeline().ResumeAsync(TestEnv.Profile(), null, CancellationToken.None);

        Assert.NotNull(story);
        Assert.True(story.State is StoryState.Ready or StoryState.AwaitingApproval);
        Assert.Equal(3, resumedEngine.SceneCalls);                               // scenes 3–5 only
        Assert.DoesNotContain(resumedTts.OutputPaths, p => p.Contains("teaser"));
        Assert.DoesNotContain(resumedTts.OutputPaths, p => p.Contains("-part0.wav"));
        Assert.Contains(resumedTts.OutputPaths, p => p.Contains("-part1.wav"));
        Assert.Null(await h.Env.Store.GetCheckpointAsync());

        var sentences = StoryPipeline.SplitSentences(story.FullText);
        var timestamps = System.Text.Json.JsonSerializer.Deserialize<List<SentenceTimestamp>>(story.TimestampsJson)!;
        Assert.Equal(sentences.Count, timestamps.Count);
        Assert.True(File.Exists(story.AudioPath));
    }

    [Fact]
    public async Task Overlap_Progress_IsMonotonic_AndNarrationRowStartsEarly()
    {
        await using var h = await Harness.CreateAsync(WeaveStrategy.Overlapped);
        var narrationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Tts.OnSynthesizeStart = _ => narrationStarted.TrySetResult();
        h.Engine = new InterceptingEngine(new TemplateStoryEngine(() => 42))
        {
            // Hold the last scene until narration is live so the overlap window is real.
            OnSceneCall = calls => { if (calls == 5) narrationStarted.Task.Wait(TimeSpan.FromSeconds(15)); },
        };

        var reports = new List<WeaveProgress>();
        await h.Pipeline().WeaveAsync(TestEnv.Profile(), new SyncProgress<WeaveProgress>(reports.Add), CancellationToken.None);

        for (var i = 1; i < reports.Count; i++)
        {
            Assert.True(reports[i].Percent >= reports[i - 1].Percent, "percent must never decrease under overlap");
            Assert.True(reports[i].Stage >= reports[i - 1].Stage, "reported stage must stay monotone under overlap");
        }
        Assert.Equal(WeaveStage.Done, reports[^1].Stage);
        Assert.Equal(100, reports[^1].Percent);
        // The Narration row goes "weaving…" while the Scenes stage is still short of its
        // 45% boundary — impossible on the sequential path, definitional for overlap.
        Assert.Contains(reports, r => r.Stage == WeaveStage.Narration && r.Percent < 45);
    }

    [Fact]
    public async Task SequentialFallback_IsPickedOnLowMemoryDevices()
    {
        await using var h = await Harness.CreateAsync();               // no forced strategy
        h.Capabilities = new FakeCapabilities { PhysicalMemoryBytes = 4L * 1024 * 1024 * 1024 };
        Assert.Equal(WeaveStrategy.Sequential, h.Pipeline().PickStrategy());

        h.Capabilities = new FakeCapabilities { IsLowMemoryDevice = true };
        Assert.Equal(WeaveStrategy.Sequential, h.Pipeline().PickStrategy());

        // 4-core device: co-residency thrashes → sequential is faster.
        h.Capabilities = new FakeCapabilities { ProcessorCount = 4 };
        Assert.Equal(WeaveStrategy.Sequential, h.Pipeline().PickStrategy());

        h.Capabilities = new FakeCapabilities();                       // 8 GB, 8 cores, not low-RAM
        Assert.Equal(WeaveStrategy.Overlapped, h.Pipeline().PickStrategy());

        // And a low-memory weave actually runs the classic two-call narration shape.
        h.Capabilities = new FakeCapabilities { PhysicalMemoryBytes = 4L * 1024 * 1024 * 1024 };
        var story = await h.Pipeline().WeaveAsync(TestEnv.Profile(), null, CancellationToken.None);
        Assert.NotEqual(StoryState.Failed, story.State);
        Assert.Equal(2, h.Tts.SynthesizeCallSentenceCounts.Count);
        Assert.DoesNotContain(h.Tts.OutputPaths, p => p.Contains("-part"));
    }

    [Fact]
    public async Task LegacyCheckpoint_WithoutStrategy_ResumesSequentially()
    {
        await using var h = await Harness.CreateAsync(WeaveStrategy.Sequential);
        var cts = new CancellationTokenSource();
        h.Engine = new InterceptingEngine(new TemplateStoryEngine(() => 42))
        {
            OnSceneCall = calls => { if (calls == 3) cts.Cancel(); },
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            h.Pipeline().WeaveAsync(TestEnv.Profile(), null, cts.Token));

        // Simulate a checkpoint written by a pre-overlap build: no strategy field.
        var checkpoint = await h.Env.Store.GetCheckpointAsync();
        checkpoint!.Strategy = string.Empty;
        await h.Env.Store.SaveCheckpointAsync(checkpoint);

        h.Engine = new InterceptingEngine(new TemplateStoryEngine(() => 42));
        var story = await h.Pipeline().ResumeAsync(TestEnv.Profile(), null, CancellationToken.None);
        Assert.NotNull(story);
        Assert.True(story.State is StoryState.Ready or StoryState.AwaitingApproval);
        Assert.DoesNotContain(h.Tts.OutputPaths, p => p.Contains("-part"));      // sequential narration
    }

    [Fact]
    public async Task Overlap_ProducerFailure_FailsGently_WithoutHanging()
    {
        await using var h = await Harness.CreateAsync(WeaveStrategy.Overlapped);
        h.Engine = new InterceptingEngine(new TemplateStoryEngine(() => 42))
        {
            SceneTransform = text => text + " Then a terrifying monster screamed.",
            RepairOverride = passage => passage, // repair never helps
        };

        var story = await h.Pipeline().WeaveAsync(TestEnv.Profile(), null, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30));                               // a hang here is the bug
        Assert.Equal(StoryState.Failed, story.State);
        Assert.Equal(StoryPipeline.GentleFailureMessage, story.FullText);
        Assert.False(h.Cooldown.IsActive);
        Assert.Null(await h.Env.Store.GetCheckpointAsync());
    }

    /// <summary>IProgress that reports synchronously (Progress&lt;T&gt; posts to a sync
    /// context and loses ordering in tests).</summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public SyncProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }
}
