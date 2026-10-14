using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using StoryTime.Contracts;
using StoryTime.Core.Abstractions;
using StoryTime.Core.Engines;

namespace StoryTime.Core.Pipeline;

/// <summary>Orchestrates the four weave stages (Outline → Scenes·stitch·polish → Poster →
/// Narration) with per-scene checkpointing to the story store, bounded safety repair,
/// teaser-first narration, and gentle failure. Two scheduling strategies (see architecture §6):
/// <list type="bullet">
/// <item><b>Overlapped</b> (default on ≥ 6 GB devices): polish runs per ~2-scene window as
/// scenes complete; narration consumes polished windows from a channel while later scenes
/// still generate; the poster runs after the final scene's LLM work in parallel with the
/// remaining narration windows (poster uses the LLM, narration uses the TTS — no contention).
/// LLM + TTS are co-resident by design. Progress is combined completion under the same
/// 5/40/5/50 weights; the reported Stage is the furthest stage started (monotone).</item>
/// <item><b>Sequential</b> (low-memory fallback, forced via StoryTimeOptions): the classic
/// order with whole-story polish; the LLM is never co-resident with active TTS work.</item>
/// </list></summary>
public sealed class StoryPipeline
{
    public const string GentleFailureMessage =
        "Something went wrong while making this story. Everything is safe — please try again in a moment.";

    /// <summary>Cumulative percent at which each stage completes (Outline 5, Scenes 45,
    /// Poster 50, Narration 100) — the same 5/40/5/50 weights the Report calls below use.
    /// Additive hook for the weave sheet's progress-bar tick marks; under the overlapped
    /// strategy percent is a combined weighted sum of all four stages, so these remain the
    /// stages' weight boundaries rather than strict time boundaries.</summary>
    public static readonly IReadOnlyList<int> StageBoundaryPercents = [5, 45, 50, 100];

    /// <summary>Scenes per polish/narration window on the overlapped path.</summary>
    public const int ScenesPerWindow = 2;

    /// <summary>Devices under this physical-memory floor fall back to the sequential
    /// strategy (co-resident text + speech engines need headroom; tuned for the previous
    /// bundled-model engines — re-measure once the Apple engines land).</summary>
    public const long OverlapMinPhysicalMemoryBytes = 6L * 1024 * 1024 * 1024;

    /// <summary>Overlap also needs CPU headroom: on a 4-core device the co-resident stages
    /// thrashed each other and overlapped measured SLOWER than sequential (16.6 vs 10.7 min,
    /// 2026-07-31, bundled-model engines). Six or more cores → overlap wins.</summary>
    public const int OverlapMinProcessorCount = 6;

    private const string OverlappedName = "overlapped";
    private const string SequentialName = "sequential";

    private const int MaxRepairAttempts = 2;

    /// <summary>Fixed scene-beat pause carried at the head of every narration window ≥ 1
    /// (the inter-window paragraph beat; windows are always paragraph boundaries).</summary>
    private const double InterWindowGapSeconds = Engines.Narration.ProsodyDirector.ParagraphBaseSeconds;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly IStoryEngine _engine;
    private readonly ITtsEngine _tts;
    private readonly IPosterComposer _composer;
    private readonly IPosterRasterizer _rasterizer;
    private readonly IStoryStore _store;
    private readonly ISettingsStore _settings;
    private readonly CooldownService _cooldown;
    private readonly SafetyFilter _safety;
    private readonly INotificationService _notifications;
    private readonly StoryTimeOptions _options;
    private readonly IClock _clock;
    private readonly IDeviceCapabilities _capabilities;

    private readonly object _progressGate = new();
    private int _lastPercent;

    /// <summary>Optional measurement sink (measurement runs, tests); null in the app.</summary>
    public WeaveTelemetry? Telemetry { get; set; }

    public StoryPipeline(
        IStoryEngine engine, ITtsEngine tts, IPosterComposer composer, IPosterRasterizer rasterizer,
        IStoryStore store, ISettingsStore settings, CooldownService cooldown, SafetyFilter safety,
        INotificationService notifications, StoryTimeOptions options, IClock clock,
        IDeviceCapabilities? capabilities = null)
    {
        _engine = engine;
        _tts = tts;
        _composer = composer;
        _rasterizer = rasterizer;
        _store = store;
        _settings = settings;
        _cooldown = cooldown;
        _safety = safety;
        _notifications = notifications;
        _options = options;
        _clock = clock;
        _capabilities = capabilities ?? new DefaultDeviceCapabilities();
    }

    /// <summary>The strategy a fresh weave would run with on this device right now.</summary>
    public WeaveStrategy PickStrategy() =>
        _options.ForcedWeaveStrategy
        ?? (_capabilities.IsLowMemoryDevice
            || _capabilities.PhysicalMemoryBytes < OverlapMinPhysicalMemoryBytes
            || _capabilities.ProcessorCount < OverlapMinProcessorCount
            ? WeaveStrategy.Sequential
            : WeaveStrategy.Overlapped);

    /// <summary>Resumes a checkpointed weave, or returns null when none is pending.</summary>
    public async Task<StoryRecord?> ResumeAsync(ChildProfile profile, IProgress<WeaveProgress>? progress, CancellationToken ct)
    {
        var checkpoint = await _store.GetCheckpointAsync();
        if (checkpoint is null) return null;
        var story = await _store.GetStoryAsync(checkpoint.StoryId);
        if (story is null || story.State != StoryState.Weaving)
        {
            await _store.SaveCheckpointAsync(null);
            return null;
        }
        return await RunAsync(profile, story, checkpoint, progress, ct);
    }

    public async Task<StoryRecord> WeaveAsync(ChildProfile profile, IProgress<WeaveProgress>? progress, CancellationToken ct)
    {
        var bible = profile.Mode == StoryMode.Series ? await _store.GetBibleAsync(profile.Id) : null;
        var story = new StoryRecord
        {
            ProfileId = profile.Id,
            Mode = profile.Mode,
            Voice = profile.Voice,
            State = StoryState.Weaving,
            EpisodeNumber = profile.Mode == StoryMode.Series ? (bible?.EpisodeCount ?? 0) + 1 : 0,
            RecapLine = profile.Mode == StoryMode.Series && bible is { EpisodeCount: > 0 }
                ? $"Previously: {bible.LastEpisodeRecap}"
                : string.Empty,
            CreatedUtc = _clock.UtcNow,
        };
        await _store.SaveStoryAsync(story);

        // The strategy is decided ONCE per weave and stamped into the checkpoint — a resume
        // (possibly after a settings/device change) always continues the same schedule.
        var checkpoint = new WeaveCheckpoint
        {
            StoryId = story.Id,
            Stage = WeaveStage.Outline,
            Strategy = PickStrategy() == WeaveStrategy.Overlapped ? OverlappedName : SequentialName,
        };
        await _store.SaveCheckpointAsync(checkpoint);

        return await RunAsync(profile, story, checkpoint, progress, ct);
    }

    private async Task<StoryRecord> RunAsync(ChildProfile profile, StoryRecord story, WeaveCheckpoint checkpoint,
        IProgress<WeaveProgress>? progress, CancellationToken ct)
    {
        // Thread the content filter level to the engines through the profile seam (both
        // weave and resume funnel through here). The level only widens the allowed-theme
        // palette; the SafetyFilter below runs identically at every level.
        profile.ContentFilter = _settings.ContentFilter;

        // Thread the child's recent titles through the same seam (title-variety negatives,
        // additive 2026-07-31): the outline stage steers new titles away from the words and
        // shapes already on this child's shelf. Failed weaves and untitled rows are noise.
        var recentStories = await _store.GetStoriesAsync(profile.Id);
        profile.RecentTitles = recentStories
            .Where(s => s.Id != story.Id && s.State != StoryState.Failed && !string.IsNullOrWhiteSpace(s.Title))
            .OrderByDescending(s => s.CreatedUtc)
            .Select(s => s.Title)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        _lastPercent = 0;
        var swTotal = Stopwatch.StartNew();
        // A checkpoint written before the strategy field existed resumes sequentially —
        // exactly the schedule it was created under.
        var overlapped = checkpoint.Strategy == OverlappedName;
        if (Telemetry is not null) Telemetry.Strategy = overlapped ? OverlappedName : SequentialName;
        try
        {
            // ---- Stage 1 · Outline (weight 5) ----
            var stageStart = swTotal.Elapsed;
            StoryOutline outline;
            var bible = profile.Mode == StoryMode.Series ? await _store.GetBibleAsync(profile.Id) : null;
            if (checkpoint.Stage == WeaveStage.Outline || string.IsNullOrEmpty(checkpoint.OutlineJson))
            {
                Report(progress, WeaveStage.Outline, 0, 0);
                outline = await _engine.GenerateOutlineAsync(profile, bible, TemplateStoryEngine.SceneCount(profile.Length), ct);
                story.Title = outline.Title;
                await _store.SaveStoryAsync(story);
                checkpoint.Stage = WeaveStage.Scenes;
                checkpoint.SceneIndex = 0;
                checkpoint.OutlineJson = JsonSerializer.Serialize(outline, JsonOpts);
                checkpoint.CompletedScenesJson = "[]";
                await _store.SaveCheckpointAsync(checkpoint);
            }
            else
            {
                outline = JsonSerializer.Deserialize<StoryOutline>(checkpoint.OutlineJson, JsonOpts)
                          ?? throw new InvalidOperationException("Corrupt outline checkpoint.");
                story.Title = outline.Title;
            }
            Report(progress, WeaveStage.Outline, 5, 1);
            if (Telemetry is not null) Telemetry.OutlineSeconds = (swTotal.Elapsed - stageStart).TotalSeconds;

            if (overlapped)
                await RunOverlappedStagesAsync(profile, story, checkpoint, outline, progress, swTotal, ct);
            else
                await RunSequentialStagesAsync(profile, story, checkpoint, outline, progress, swTotal, ct);

            // ---- Completion ----
            story.State = _settings.ApprovalStep ? StoryState.AwaitingApproval : StoryState.Ready;
            await _store.SaveStoryAsync(story);

            if (profile.Mode == StoryMode.Series)
                await UpdateBibleAsync(profile, outline, ct);

            await _store.SaveCheckpointAsync(null);
            _cooldown.Start();
            if (Telemetry is not null) Telemetry.TotalSeconds = swTotal.Elapsed.TotalSeconds;
            Report(progress, WeaveStage.Done, 100, 1);

            if (_settings.StoryReadyNotifications)
                await _notifications.ShowStoryReadyAsync(profile.Name, story.Title);

            return story;
        }
        catch (OperationCanceledException)
        {
            // Checkpoint already persisted along the way; the weave resumes on next start.
            throw;
        }
        catch (SafetyException)
        {
            await FailGentlyAsync(story);
            return story;
        }
        catch (Exception)
        {
            await FailGentlyAsync(story);
            return story;
        }
    }

    // ================================================================
    // Sequential strategy — the classic stage order (low-memory path)
    // ================================================================

    private async Task RunSequentialStagesAsync(ChildProfile profile, StoryRecord story, WeaveCheckpoint checkpoint,
        StoryOutline outline, IProgress<WeaveProgress>? progress, Stopwatch swTotal, CancellationToken ct)
    {
        // ---- Stage 2 · Scenes, stitch, polish (weight 40) ----
        var stageStart = swTotal.Elapsed;
        string polished;
        if (checkpoint.Stage == WeaveStage.Scenes)
        {
            var scenes = JsonSerializer.Deserialize<List<string>>(
                string.IsNullOrEmpty(checkpoint.CompletedScenesJson) ? "[]" : checkpoint.CompletedScenesJson, JsonOpts) ?? [];
            for (var i = checkpoint.SceneIndex; i < outline.Scenes.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var tail = scenes.Count > 0 ? Tail(scenes[^1]) : string.Empty;
                var text = await GenerateSafeSceneAsync(profile, outline, outline.Scenes[i], tail, ct);
                scenes.Add(text);

                checkpoint.SceneIndex = i + 1;
                checkpoint.CompletedScenesJson = JsonSerializer.Serialize(scenes, JsonOpts);
                await _store.SaveCheckpointAsync(checkpoint);

                var fraction = (i + 1) / (double)outline.Scenes.Count;
                Report(progress, WeaveStage.Scenes, 5 + (int)(fraction * 38), fraction);
            }

            var stitched = string.Join("\n\n", scenes);
            polished = await _engine.PolishAsync(profile, stitched, ct);
            polished = await EnsureSafeAsync(profile, polished, ct);

            story.FullText = polished;
            await _store.SaveStoryAsync(story);
            checkpoint.Stage = WeaveStage.Poster;
            await _store.SaveCheckpointAsync(checkpoint);
        }
        else
        {
            polished = story.FullText;
        }
        Report(progress, WeaveStage.Scenes, 45, 1);
        if (Telemetry is not null) Telemetry.ScenesSeconds = (swTotal.Elapsed - stageStart).TotalSeconds;

        // ---- Stage 3 · Poster (weight 5) ----
        stageStart = swTotal.Elapsed;
        if (checkpoint.Stage == WeaveStage.Poster)
        {
            await RenderPosterAsync(story, outline, ct);
            checkpoint.Stage = WeaveStage.Narration;
            checkpoint.SentenceIndex = 0;
            await _store.SaveCheckpointAsync(checkpoint);
        }
        Report(progress, WeaveStage.Poster, 50, 1);
        if (Telemetry is not null) Telemetry.PosterSeconds = (swTotal.Elapsed - stageStart).TotalSeconds;

        // ---- Stage 4 · Narration, teaser first (weight 50) ----
        stageStart = swTotal.Elapsed;
        // Paragraph-aware script: prosody engines render paragraph breaks as scene-beat pauses.
        var script = Engines.Narration.SentenceSplitter.SplitScript(polished);

        var teaserPath = Path.Combine(_options.MediaDirectory, $"story-{story.Id}-teaser.wav");
        var teaser = await _tts.SynthesizeAsync(script.Take(3), profile.Voice, teaserPath,
            new DelegateProgress(f => Report(progress, WeaveStage.Narration, 50 + (int)(f * 5), f * 0.1)), ct);
        story.TeaserPath = teaser.AudioPath;
        await _store.SaveStoryAsync(story);

        var audioPath = Path.Combine(_options.MediaDirectory, $"story-{story.Id}.wav");
        var manifest = await _tts.SynthesizeAsync(script, profile.Voice, audioPath,
            new DelegateProgress(f => Report(progress, WeaveStage.Narration, 55 + (int)(f * 44), 0.1 + f * 0.9)), ct);

        story.AudioPath = manifest.AudioPath;
        story.DurationSeconds = manifest.DurationSeconds;
        story.TimestampsJson = JsonSerializer.Serialize(manifest.Timestamps, JsonOpts);
        if (Telemetry is not null)
        {
            Telemetry.NarrationSeconds = (swTotal.Elapsed - stageStart).TotalSeconds;
            Telemetry.NarrationAudioSeconds = manifest.DurationSeconds;
        }
    }

    // ================================================================
    // Overlapped strategy — producer (LLM) / consumer (TTS) over windows
    // ================================================================

    /// <summary>Scene index ranges of the polish/narration windows: pairs of
    /// <see cref="ScenesPerWindow"/>, last window takes the remainder.</summary>
    internal static List<(int Start, int Count)> WindowPlan(int sceneCount)
    {
        var windows = new List<(int, int)>();
        for (var start = 0; start < sceneCount; start += ScenesPerWindow)
            windows.Add((start, Math.Min(ScenesPerWindow, sceneCount - start)));
        return windows;
    }

    private sealed class OverlapState
    {
        public required int SceneUnitsTotal { get; init; }  // scenes + one polish unit per window
        public required int WindowCount { get; init; }
        public int SceneUnitsDone;
        public bool PosterDone;
        public double Narration01;
        public WeaveStage Furthest = WeaveStage.Scenes;
    }

    /// <summary>Combined-completion progress for the overlapped path. Percent is the
    /// weighted sum of all four stages' fractions (monotone — every term only grows);
    /// Stage is the furthest stage that has STARTED (monotone), so the weave sheet's
    /// row derivation (before → done, current → weaving, after → waiting) stays sensible:
    /// the Narration row flips to "weaving…" the moment its first window starts.</summary>
    private void ReportOverlap(IProgress<WeaveProgress>? progress, OverlapState s, WeaveStage stage)
    {
        lock (s)
        {
            if (stage > s.Furthest) s.Furthest = stage;
            var scenes01 = Math.Min(1.0, s.SceneUnitsDone / (double)s.SceneUnitsTotal);
            var percent = 5
                + (int)(40 * scenes01)
                + (s.PosterDone ? 5 : 0)
                + (int)(50 * Math.Min(1.0, s.Narration01));
            var stageFraction = s.Furthest switch
            {
                WeaveStage.Scenes => scenes01,
                WeaveStage.Poster => s.PosterDone ? 1 : 0,
                WeaveStage.Narration => Math.Min(1.0, s.Narration01),
                _ => 1,
            };
            Report(progress, s.Furthest, percent, stageFraction);
        }
    }

    private async Task RunOverlappedStagesAsync(ChildProfile profile, StoryRecord story, WeaveCheckpoint checkpoint,
        StoryOutline outline, IProgress<WeaveProgress>? progress, Stopwatch swTotal, CancellationToken ct)
    {
        var windows = WindowPlan(outline.Scenes.Count);
        var state = new OverlapState
        {
            SceneUnitsTotal = outline.Scenes.Count + windows.Count,
            WindowCount = windows.Count,
        };

        // ---- resume bookkeeping (all fields additive on WeaveCheckpoint) ----
        var scenes = Deserialize<List<string>>(checkpoint.CompletedScenesJson) ?? [];
        var polishedWindows = Deserialize<List<string>>(checkpoint.PolishedWindowsJson) ?? [];
        var parts = Deserialize<List<NarrationPart>>(checkpoint.NarrationPartsJson) ?? [];
        state.SceneUnitsDone = scenes.Count + polishedWindows.Count;
        state.PosterDone = checkpoint.PosterDone;
        state.Narration01 = parts.Count / (double)windows.Count;

        // One writer gate: producer and consumer both persist checkpoint/story rows;
        // sqlite writes and the shared mutable checkpoint must not interleave.
        using var ioGate = new SemaphoreSlim(1, 1);
        async Task PersistAsync(Func<Task> save)
        {
            await ioGate.WaitAsync(CancellationToken.None);
            try { await save(); }
            finally { ioGate.Release(); }
        }

        var channel = Channel.CreateUnbounded<(int Window, string Text)>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        // Replay windows already polished but not yet narrated (resume mid-overlap).
        for (var w = parts.Count; w < polishedWindows.Count; w++)
            channel.Writer.TryWrite((w, polishedWindows[w]));

        using var failureCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var innerCt = failureCts.Token;

        var scenesSpanEnd = TimeSpan.Zero;
        var posterSpan = TimeSpan.Zero;
        var narrationStart = TimeSpan.MinValue;
        var narrationEnd = TimeSpan.Zero;

        // ---------------- producer: scenes → window polish → poster ----------------
        async Task ProduceAsync()
        {
            try
            {
                await ProduceCoreAsync();
            }
            catch (Exception ex)
            {
                // The consumer parks on the channel — a dying producer MUST fail the
                // channel or the weave would hang instead of failing gently.
                channel.Writer.TryComplete(ex);
                throw;
            }
        }

        async Task ProduceCoreAsync()
        {
            var scenesStart = swTotal.Elapsed;
            for (var w = polishedWindows.Count; w < windows.Count; w++)
            {
                var (start, count) = windows[w];
                for (var i = start; i < start + count; i++)
                {
                    if (i < scenes.Count) continue; // scene survived a previous run
                    innerCt.ThrowIfCancellationRequested();
                    var tail = scenes.Count > 0 ? Tail(scenes[^1]) : string.Empty;
                    var text = await GenerateSafeSceneAsync(profile, outline, outline.Scenes[i], tail, innerCt);
                    scenes.Add(text);
                    await PersistAsync(async () =>
                    {
                        checkpoint.SceneIndex = i + 1;
                        checkpoint.CompletedScenesJson = JsonSerializer.Serialize(scenes, JsonOpts);
                        await _store.SaveCheckpointAsync(checkpoint);
                    });
                    Interlocked.Increment(ref state.SceneUnitsDone);
                    ReportOverlap(progress, state, WeaveStage.Scenes);
                }

                innerCt.ThrowIfCancellationRequested();
                var stitched = string.Join("\n\n", scenes.Skip(start).Take(count));
                var previousTail = w == 0 ? string.Empty : Tail(polishedWindows[w - 1]);
                var polishedWindow = await _engine.PolishWindowAsync(profile, stitched, previousTail, w == windows.Count - 1, innerCt);
                polishedWindow = await EnsureSafeAsync(profile, polishedWindow, innerCt);
                polishedWindows.Add(polishedWindow);
                await PersistAsync(async () =>
                {
                    story.FullText = string.Join("\n\n", polishedWindows);
                    await _store.SaveStoryAsync(story);
                    checkpoint.PolishedWindowsJson = JsonSerializer.Serialize(polishedWindows, JsonOpts);
                    if (w == windows.Count - 1) checkpoint.Stage = WeaveStage.Narration;
                    await _store.SaveCheckpointAsync(checkpoint);
                });
                channel.Writer.TryWrite((w, polishedWindow));
                Interlocked.Increment(ref state.SceneUnitsDone);
                ReportOverlap(progress, state, WeaveStage.Scenes);
            }
            scenesSpanEnd = swTotal.Elapsed;
            if (Telemetry is not null) Telemetry.ScenesSeconds = (scenesSpanEnd - scenesStart).TotalSeconds;

            // Text is complete — narration can drain to the end while the poster runs.
            channel.Writer.TryComplete();

            // ---- Poster, off the critical path (LLM busy here, TTS narrating in parallel) ----
            if (!checkpoint.PosterDone)
            {
                var posterStart = swTotal.Elapsed;
                await RenderPosterAsync(story, outline, innerCt, persist: PersistAsync);
                await PersistAsync(async () =>
                {
                    checkpoint.PosterDone = true;
                    await _store.SaveCheckpointAsync(checkpoint);
                });
                posterSpan = swTotal.Elapsed - posterStart;
                lock (state) state.PosterDone = true;
                ReportOverlap(progress, state, WeaveStage.Poster);
            }
            else
            {
                lock (state) state.PosterDone = true;
            }
        }

        // ---------------- consumer: teaser first, then window narration ----------------
        async Task ConsumeAsync()
        {
            await foreach (var (w, text) in channel.Reader.ReadAllAsync(innerCt))
            {
                if (narrationStart == TimeSpan.MinValue) narrationStart = swTotal.Elapsed;
                var isLast = w == windows.Count - 1;
                var script = Engines.Narration.SentenceSplitter.SplitScript(text);
                var windowScript = new NarrationScript
                {
                    SentenceList = script.SentenceList,
                    ParagraphStartSet = script.ParagraphStartSet,
                    LeadingSilenceSeconds = w > 0 ? InterWindowGapSeconds : 0,
                    AppendFinalTail = isLast,
                };

                void ReportWindow(double inner)
                {
                    lock (state) state.Narration01 = (w + Math.Clamp(inner, 0, 1)) / windows.Count;
                    ReportOverlap(progress, state, WeaveStage.Narration);
                }

                if (w == 0 && !checkpoint.TeaserDone)
                {
                    // Teaser = the first ~3 sentences of window 1, rendered FIRST as its own
                    // file so approval can start while the rest of the story still weaves.
                    var teaserPath = Path.Combine(_options.MediaDirectory, $"story-{story.Id}-teaser.wav");
                    var teaser = await _tts.SynthesizeAsync(script.Take(3), profile.Voice, teaserPath,
                        new DelegateProgress(f => ReportWindow(0.15 * f)), innerCt);
                    await PersistAsync(async () =>
                    {
                        story.TeaserPath = teaser.AudioPath;
                        await _store.SaveStoryAsync(story);
                        checkpoint.TeaserDone = true;
                        await _store.SaveCheckpointAsync(checkpoint);
                    });
                }

                var teaserShare = w == 0 ? 0.15 : 0.0;
                var partPath = Path.Combine(_options.MediaDirectory, $"story-{story.Id}-part{w}.wav");
                var manifest = await _tts.SynthesizeAsync(windowScript, profile.Voice, partPath,
                    new DelegateProgress(f => ReportWindow(teaserShare + (1 - teaserShare) * f)), innerCt);
                parts.Add(new NarrationPart
                {
                    AudioPath = manifest.AudioPath,
                    DurationSeconds = manifest.DurationSeconds,
                    SentenceCount = windowScript.SentenceList.Count,
                    Timestamps = manifest.Timestamps,
                });
                await PersistAsync(async () =>
                {
                    checkpoint.NarrationPartsJson = JsonSerializer.Serialize(parts, JsonOpts);
                    await _store.SaveCheckpointAsync(checkpoint);
                });
                ReportWindow(1);
            }
            narrationEnd = swTotal.Elapsed;
        }

        var producer = Task.Run(ProduceAsync, CancellationToken.None);
        var consumer = Task.Run(ConsumeAsync, CancellationToken.None);
        try
        {
            // React to the FIRST failure: cancel the other side instead of letting it run
            // on (WhenAll alone would wait for both regardless of the early fault).
            var first = await Task.WhenAny(producer, consumer);
            if (!first.IsCompletedSuccessfully) failureCts.Cancel();
            await Task.WhenAll(producer, consumer);
        }
        catch
        {
            // One side failed (or the user cancelled): stop the other side, then rethrow
            // the REAL failure — a TTS error must not masquerade as a cancellation just
            // because the producer observed the linked token first.
            failureCts.Cancel();
            try { await Task.WhenAll(producer, consumer); } catch { /* surfaced below */ }
            ct.ThrowIfCancellationRequested();
            var real = new[] { producer, consumer }
                .Where(t => t.Exception is not null)
                .SelectMany(t => t.Exception!.InnerExceptions)
                .FirstOrDefault(e => e is not OperationCanceledException);
            if (real is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(real);
            throw;
        }

        // ---------------- assembly: concat parts, offset timestamps ----------------
        var audioPath = Path.Combine(_options.MediaDirectory, $"story-{story.Id}.wav");
        var partPaths = parts.Select(p => p.AudioPath).ToList();
        double totalDuration;
        if (partPaths.All(File.Exists))
        {
            totalDuration = Engines.Narration.WavAudio.Concat(audioPath, partPaths);
        }
        else if (File.Exists(audioPath))
        {
            // Crash landed between concat and checkpoint-clear on a previous run: the full
            // WAV exists and part metadata still carries the exact timeline.
            totalDuration = parts.Sum(p => p.DurationSeconds);
        }
        else
        {
            throw new InvalidOperationException("Narration parts missing and no assembled audio present.");
        }

        // Global sentence indices/timestamps: window-local manifests appended in window
        // order, index offset by cumulative sentence count, time by cumulative duration.
        var timestamps = new List<SentenceTimestamp>();
        var indexOffset = 0;
        var timeOffset = 0.0;
        foreach (var part in parts)
        {
            foreach (var t in part.Timestamps)
            {
                timestamps.Add(new SentenceTimestamp
                {
                    Index = t.Index + indexOffset,
                    StartSeconds = t.StartSeconds + timeOffset,
                    EndSeconds = t.EndSeconds + timeOffset,
                });
            }
            indexOffset += part.SentenceCount;
            timeOffset += part.DurationSeconds;
        }

        story.AudioPath = audioPath;
        story.DurationSeconds = totalDuration;
        story.TimestampsJson = JsonSerializer.Serialize(timestamps, JsonOpts);
        await _store.SaveStoryAsync(story);

        foreach (var partPath in partPaths.Where(File.Exists))
        {
            try { File.Delete(partPath); }
            catch (IOException) { /* best-effort; part files are outside the store's cleanup */ }
        }

        if (Telemetry is not null)
        {
            Telemetry.PosterSeconds = posterSpan.TotalSeconds;
            Telemetry.NarrationSeconds = narrationStart == TimeSpan.MinValue
                ? 0
                : (narrationEnd - narrationStart).TotalSeconds;
            Telemetry.NarrationAudioSeconds = totalDuration;
        }
    }

    /// <summary>Poster spec generation + validation + thumbnail rasterization (shared by
    /// both strategies; the overlapped path passes its serialized persist gate).</summary>
    private async Task RenderPosterAsync(StoryRecord story, StoryOutline outline, CancellationToken ct,
        Func<Func<Task>, Task>? persist = null)
    {
        var spec = _composer.Validate(await _engine.GeneratePosterSpecAsync(outline, ct));
        var specJson = StoryTime.Poster.Spec.PosterSpecJson.Serialize(spec);
        var thumbPath = Path.Combine(_options.MediaDirectory, $"story-{story.Id}-thumb-v{Poster.PosterComposer.ArtVersion}.png");
        var renderedPath = await _rasterizer.FlattenToPngAsync(spec, thumbPath, 512, ct);
        Func<Task> save = async () =>
        {
            story.PosterSpecJson = specJson;
            story.ThumbnailPath = renderedPath;
            await _store.SaveStoryAsync(story);
        };
        if (persist is null) await save();
        else await persist(save);
    }

    private static T? Deserialize<T>(string json) where T : class =>
        string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<T>(json, JsonOpts);

    private async Task FailGentlyAsync(StoryRecord story)
    {
        story.State = StoryState.Failed;
        if (string.IsNullOrEmpty(story.Title)) story.Title = "A story that wandered off";
        story.FullText = GentleFailureMessage;
        await _store.SaveStoryAsync(story);
        await _store.SaveCheckpointAsync(null);
        // No cooldown on failure — the parent may retry immediately.
    }

    /// <summary>Scene generation with the layered safety loop: lexicon check → ≤2 repair
    /// passes → one scene regeneration → gentle failure.</summary>
    private async Task<string> GenerateSafeSceneAsync(ChildProfile profile, StoryOutline outline, SceneOutline scene,
        string previousTail, CancellationToken ct)
    {
        var text = await _engine.GenerateSceneAsync(profile, outline, scene, previousTail, ct);
        if (_safety.IsClean(text)) return text;

        var repaired = await RepairAsync(profile, text, ct);
        if (repaired is not null) return repaired;

        // One regeneration, then a final repair pass on it.
        var regenerated = await _engine.GenerateSceneAsync(profile, outline, scene, previousTail, ct);
        if (_safety.IsClean(regenerated)) return regenerated;
        regenerated = await RepairAsync(profile, regenerated, ct);
        return regenerated ?? throw new SafetyException();
    }

    /// <summary>Bounded repair loop; returns clean text or null when still flagged.</summary>
    private async Task<string?> RepairAsync(ChildProfile profile, string text, CancellationToken ct)
    {
        var current = text;
        for (var attempt = 0; attempt < MaxRepairAttempts; attempt++)
        {
            var flags = _safety.Check(current);
            if (flags.Count == 0) return current;
            current = await _engine.RepairPassageAsync(profile, current, string.Join(", ", flags), ct);
        }
        return _safety.IsClean(current) ? current : null;
    }

    private async Task<string> EnsureSafeAsync(ChildProfile profile, string text, CancellationToken ct)
    {
        if (_safety.IsClean(text)) return text;
        return await RepairAsync(profile, text, ct) ?? throw new SafetyException();
    }

    private async Task UpdateBibleAsync(ChildProfile profile, StoryOutline outline, CancellationToken ct)
    {
        var bible = await _store.GetBibleAsync(profile.Id) ?? new StoryBible();
        bible.EpisodeCount += 1;

        foreach (var character in outline.Characters)
        {
            if (!bible.Characters.Any(c => c.Name.Equals(character.Name, StringComparison.OrdinalIgnoreCase)))
                bible.Characters.Add(character);
        }
        foreach (var place in outline.Settings)
        {
            if (!bible.Places.Contains(place)) bible.Places.Add(place);
        }

        // Bible writes pass the same safety filter (program invariant).
        var recap = $"{outline.Title} — {outline.Moral}.";
        if (!_safety.IsClean(recap))
            recap = await RepairAsync(profile, recap, ct) ?? "a gentle adventure, happily concluded.";
        bible.LastEpisodeRecap = recap;

        await _store.SaveBibleAsync(profile.Id, bible);
    }

    private void Report(IProgress<WeaveProgress>? progress, WeaveStage stage, int percent, double stageFraction)
    {
        lock (_progressGate)
        {
            // Monotonic guarantee: percent never moves backwards (the overlapped path also
            // funnels every report through here, so cross-thread reports stay ordered).
            if (percent < _lastPercent) percent = _lastPercent;
            _lastPercent = percent;
            progress?.Report(new WeaveProgress
            {
                Stage = stage,
                Percent = Math.Clamp(percent, 0, 100),
                StageFraction = Math.Clamp(stageFraction, 0, 1),
            });
        }
    }

    /// <summary>Canonical sentence segmentation — MUST stay in lockstep with the narration
    /// path (players re-split FullText and align to stored timestamps by count). The
    /// overlapped path preserves this: windows are joined with a blank line, and the
    /// splitter treats paragraph blocks independently, so split(join(windows)) is exactly
    /// the concatenation of the per-window splits the narration used.</summary>
    public static List<string> SplitSentences(string text)
        => Engines.Narration.SentenceSplitter.Split(text);

    private static string Tail(string sceneText)
    {
        var sentences = SplitSentences(sceneText);
        return sentences.Count == 0 ? string.Empty : sentences[^1];
    }

    private sealed class SafetyException : Exception;

    /// <summary>Synchronous IProgress — Progress&lt;T&gt; posts asynchronously and can
    /// deliver narration sub-progress after Done, breaking monotonic ordering.</summary>
    private sealed class DelegateProgress : IProgress<double>
    {
        private readonly Action<double> _handler;
        public DelegateProgress(Action<double> handler) => _handler = handler;
        public void Report(double value) => _handler(value);
    }
}
