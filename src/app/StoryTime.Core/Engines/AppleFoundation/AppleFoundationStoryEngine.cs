using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StoryTime.Contracts;
using StoryTime.Core.Abstractions;
using StoryTime.Core.Engines.Narration;
using StoryTime.Core.Engines.Prompts;
using StoryTime.Core.Pipeline;

namespace StoryTime.Core.Engines.AppleFoundation;

/// <summary>Tuning for <see cref="AppleFoundationStoryEngine"/>.</summary>
public sealed class AppleFoundationOptions
{
    /// <summary>Optional per-scene polish pass (never whole-story). A failed or suspicious
    /// polish always keeps the original scene text. Off by default: in the 2026-10-14 Mac lab
    /// the polish pass added ~30 s to a five-scene story, merged sentences into run-ons and
    /// appended recycled closing lines (docs/research/genai-lab-fm-shim.md).</summary>
    public bool PolishScenes { get; init; }

    /// <summary>Recipe rule 7: once MORE than this many scenes fell back to templates, the
    /// rest of the story is rendered from templates, driven by the outline.</summary>
    public int MaxSceneFallbacks { get; init; } = 2;

    /// <summary>Wait used for rate-limit/timeout retries when the OS gives no reset date.</summary>
    public TimeSpan DefaultRetryWait { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Upper bound on any single rate-limit wait.</summary>
    public TimeSpan MaxRetryWait { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>A diagnostics record (parents and diagnostics only — never shown to a child).</summary>
public sealed record AppleEngineEvent(DateTime Utc, string Stage, string What, FmErrorCode? Code, string? Detail);

/// <summary>Story engine on Apple's on-device Foundation Models (iOS 26+), through the
/// <see cref="IFoundationModelsBridge"/> shim. Follows docs/research/foundation-models.md:
/// <list type="bullet">
/// <item>Outline: guided, exact scene count, plus a short story bible.</item>
/// <item>Scenes: streamed prose, each in a FRESH session carrying only the running summary
/// (≤120 words, built from per-scene digests), the last two sentences, the character cards,
/// the story notes and the beat. A token-budget guard trims summary, then notes — never the beat.</item>
/// <item>Digest: guided, greedy, after each scene. Polish: optional, per scene, never whole-story.
/// Poster: guided enum choice mapped by <see cref="PosterSpecMapper"/>. Repair: model rewrite,
/// deterministic template repair when the rewrite is not clean.</item>
/// <item>Failure ladder per stage: context overflow → trim and retry once; guardrail or
/// refusal → re-run, then the safer phrasing at temperature 0.5, then the template scene;
/// lexicon or refusal-text hit on output → treated as guardrail, at most two model attempts;
/// rate limit / timeout → wait (reset date or default) and retry, up to twice; unavailable,
/// assets unavailable or unsupported locale → templates for the whole story; more than
/// <see cref="AppleFoundationOptions.MaxSceneFallbacks"/> fallen-back scenes → templates for
/// the rest of the story.</item>
/// </list>
/// Every existing safety layer stays: the instructions are StoryPrompts.SystemPrompt
/// unchanged, every output passes the <see cref="SafetyFilter"/> lexicon here, and the
/// pipeline's own filter + repair loop still checks every passage after this engine.</summary>
public sealed partial class AppleFoundationStoryEngine : IStoryEngine
{
    private const int SafetyMarginTokens = 200;
    private const int MaxRuns = 4;
    private const int MaxEvents = 64;

    private readonly IFoundationModelsBridge _bridge;
    private readonly SafetyFilter _safety;
    private readonly Func<int> _seedProvider;
    private readonly AppleFoundationOptions _options;
    private readonly ILogger _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TemplateStoryEngine _template = new();

    private readonly object _gate = new();
    private readonly List<StoryRun> _runs = [];
    private readonly LinkedList<AppleEngineEvent> _events = new();

    public AppleFoundationStoryEngine(
        IFoundationModelsBridge bridge,
        SafetyFilter safety,
        Func<int>? seedProvider = null,
        AppleFoundationOptions? options = null,
        ILogger<AppleFoundationStoryEngine>? logger = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _bridge = bridge;
        _safety = safety;
        _seedProvider = seedProvider ?? (() => Random.Shared.Next());
        _options = options ?? new AppleFoundationOptions();
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _delay = delay ?? Task.Delay;
    }

    public string Name => "AppleFoundation";

    /// <summary>Recent ladder events, newest last (diagnostics; refusal explanations live here).</summary>
    public IReadOnlyList<AppleEngineEvent> RecentEvents
    {
        get { lock (_events) return _events.ToList(); }
    }

    // =====================================================================
    // Outline
    // =====================================================================

    public async Task<StoryOutline> GenerateOutlineAsync(ChildProfile profile, StoryBible? bible, int sceneCount, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var seed = _seedProvider();
        var availability = _bridge.GetAvailability();
        var run = NewRun(seed, availability.ContextSize);

        if (!availability.IsAvailable)
        {
            Record("outline", $"model unavailable ({availability.Reason}); whole story from templates", null, null);
            return await TemplateOutlineAsync(run, profile, bible, sceneCount, ct);
        }

        var interests = FoundationPrompts.SafeInterests(profile, _safety);
        var hint = StoryPrompts.TitleShapeHints[(int)((uint)seed % (uint)StoryPrompts.TitleShapeHints.Length)];
        var instructions = FoundationPrompts.Instructions(profile);

        var (outline, outcome) = await LadderAsync(
            "outline", allowPermissive: false,
            build: attempt => BudgetAsync(run, 350, attempt.Trim, 1, trim => new FmRequest
            {
                Instructions = instructions,
                Prompt = attempt.Step == Step.Safer
                    ? $"Plan a calm bedtime story for {FoundationPrompts.SafeName(profile)}, who loves " +
                      $"{(interests.Count > 0 ? string.Join(", ", interests) : "cozy animals")}, in exactly {sceneCount} gentle scenes. " +
                      "The last scene ends with everyone asleep."
                    : FoundationPrompts.Outline(profile, interests, trim > 0 ? null : bible, sceneCount, hint),
                Options = FmOptions.Threshold(0.9, Vary(seed, attempt.Step), attempt.Step == Step.Safer ? 0.5 : 0.7, 900),
                SceneCount = sceneCount,
            }),
            invoke: (request, token) => _bridge.GenerateOutlineAsync(request.ToJson(), token),
            accept: json => MapOutline(json, profile, sceneCount, seed, run),
            ct);

        if (outcome != Outcome.Model || outline is null)
        {
            Record("outline", "outline fell back; whole story from templates", null, null);
            return await TemplateOutlineAsync(run, profile, bible, sceneCount, ct);
        }
        lock (run) run.Outline = outline;
        return outline;
    }

    private async Task<StoryOutline> TemplateOutlineAsync(StoryRun run, ChildProfile profile, StoryBible? bible, int sceneCount, CancellationToken ct)
    {
        var outline = await new TemplateStoryEngine(() => run.Seed).GenerateOutlineAsync(profile, bible, sceneCount, ct);
        lock (run)
        {
            run.TemplateWhole = true;
            run.Outline = outline;
        }
        return outline;
    }

    private StoryOutline? MapOutline(string json, ChildProfile profile, int sceneCount, int seed, StoryRun run)
    {
        var fm = FmJson.Read<FmOutline>(json);
        if (fm.Scenes.Count != sceneCount || string.IsNullOrWhiteSpace(fm.Title) || fm.Scenes.Any(s => string.IsNullOrWhiteSpace(s.Summary)))
            throw new FoundationModelsException(FmErrorCode.Parse, $"outline shape invalid ({fm.Scenes.Count}/{sceneCount} scenes)");

        var allText = string.Join(" ", new[] { fm.Title, fm.Theme }
            .Concat(fm.Settings)
            .Concat(fm.Characters.SelectMany(c => new[] { c.Name, c.Kind, c.Trait }))
            .Concat(fm.Scenes.SelectMany(s => s.Beats.Prepend(s.Summary)))
            .Concat(fm.Bible));
        if (!_safety.IsClean(allText) || LooksLikeRefusal(fm.Title)) return null;

        var childName = FoundationPrompts.SafeName(profile);
        var characters = new List<CharacterCard> { new() { Name = childName, Kind = "child", Trait = "curious" } };
        foreach (var c in fm.Characters)
        {
            var name = c.Name.Trim();
            if (name.Length == 0 || name.Equals(childName, StringComparison.OrdinalIgnoreCase)) continue;
            if (characters.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
            characters.Add(new CharacterCard
            {
                Name = name,
                Kind = string.IsNullOrWhiteSpace(c.Kind) ? "friend" : c.Kind.Trim().ToLowerInvariant(),
                Trait = c.Trait.Trim().ToLowerInvariant(),
            });
            if (characters.Count == 5) break;
        }

        var outline = new StoryOutline
        {
            Title = ProseGuards.Reframe(CleanTitle(fm.Title)),
            Moral = ProseGuards.Reframe(fm.Theme.Trim().TrimEnd('.')),
            Seed = seed,
            Settings = fm.Settings.Select(s => s.Trim()).Where(s => s.Length > 0).Take(3).ToList(),
            Characters = characters,
            // Positive reframing BEFORE the beats reach scene prompts (lab: "afraid of the dark"
            // and "fighting a giant with a sword" tripped the input guardrail).
            Scenes = fm.Scenes.Select((s, i) => new SceneOutline
            {
                Id = i,
                Summary = ProseGuards.Reframe(s.Summary.Trim()),
                Beats = s.Beats.Select(b => ProseGuards.Reframe(b.Trim())).Where(b => b.Length > 0).ToList(),
            }).ToList(),
        };
        if (outline.Settings.Count == 0) outline.Settings.Add("a quiet meadow");

        lock (run)
        {
            run.Bible.Clear();
            foreach (var line in fm.Bible.Select(b => ProseGuards.Reframe(b.Trim())).Where(b => b.Length > 0).Take(8))
                run.Bible.Add(line);
        }
        return outline;
    }

    // =====================================================================
    // Scenes
    // =====================================================================

    public async Task<string> GenerateSceneAsync(ChildProfile profile, StoryOutline outline, SceneOutline scene, string previousTail, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var run = RunFor(outline);

        bool templateWhole;
        lock (run) templateWhole = run.TemplateWhole;
        if (!templateWhole && !_bridge.GetAvailability().IsAvailable)
        {
            Record($"scene {scene.Id}", "model became unavailable; rest of story from templates", null, null);
            lock (run) run.TemplateWhole = templateWhole = true;
        }
        if (templateWhole) return await TemplateSceneAsync(run, profile, outline, scene, previousTail, ct);

        var context = BuildSceneContext(run, profile, outline, scene, previousTail);
        var instructions = FoundationPrompts.Instructions(profile);
        var sceneSeed = HashCombine(outline.Seed, scene.Id + 1);

        var (text, outcome) = await LadderAsync(
            $"scene {scene.Id}", allowPermissive: true,
            build: attempt => BudgetAsync(run, 0, attempt.Trim, 3, trim => new FmRequest
            {
                Instructions = instructions,
                Prompt = attempt.Step == Step.Safer ? FoundationPrompts.SceneSafer(context) : FoundationPrompts.Scene(context, trim),
                Options = FmOptions.Threshold(0.9, Vary(sceneSeed, attempt.Step), attempt.Step == Step.Safer ? 0.5 : 0.7, 600),
                Guardrails = attempt.Step == Step.Permissive ? "permissive" : null,
            }),
            invoke: StreamSceneTextAsync,
            accept: raw => AcceptScene(run, raw, context),
            ct);

        if (outcome == Outcome.WholeStory)
        {
            lock (run) run.TemplateWhole = true;
            return await TemplateSceneAsync(run, profile, outline, scene, previousTail, ct);
        }
        if (outcome == Outcome.Fallback || text is null)
        {
            lock (run)
            {
                run.FallbackScenes++;
                if (run.FallbackScenes > _options.MaxSceneFallbacks)
                {
                    run.TemplateWhole = true;
                    Record($"scene {scene.Id}", $"{run.FallbackScenes} scenes fell back; rest of story from templates", null, null);
                }
            }
            return await TemplateSceneAsync(run, profile, outline, scene, previousTail, ct);
        }

        lock (run)
        {
            run.SceneTexts[scene.Id] = text;
            run.TemplateScenes.Remove(scene.Id);
            foreach (var line in ProseGuards.StockLines(text)) run.StockLines.Add(line);
        }
        await DigestAsync(run, scene, text, ct);
        return text;
    }

    private async Task<string> TemplateSceneAsync(StoryRun run, ChildProfile profile, StoryOutline outline, SceneOutline scene,
        string previousTail, CancellationToken ct)
    {
        var text = await _template.GenerateSceneAsync(profile, outline, scene, previousTail, ct);
        lock (run)
        {
            run.SceneTexts[scene.Id] = text;
            run.TemplateScenes.Add(scene.Id);
            run.Digests[scene.Id] = scene.Summary;
        }
        return text;
    }

    private FoundationPrompts.SceneContext BuildSceneContext(StoryRun run, ChildProfile profile, StoryOutline outline,
        SceneOutline scene, string previousTail)
    {
        lock (run)
        {
            var summaries = new List<string>();
            foreach (var s in outline.Scenes.Where(s => s.Id < scene.Id))
                summaries.Add(run.Digests.TryGetValue(s.Id, out var d) ? d : s.Summary);
            var summary = ProseGuards.Reframe(
                FoundationPrompts.CapWords(string.Join(" ", summaries.Select(EnsureSentence)), FoundationPrompts.SummaryWordCap));

            var last = run.SceneTexts.TryGetValue(scene.Id - 1, out var prev) ? LastSentences(prev, 2) : previousTail;
            var perScene = TemplateStoryEngine.TargetWords(profile.Length) / Math.Max(1, outline.Scenes.Count);
            var target = Math.Clamp((int)Math.Round(perScene / 10.0) * 10, 120, 260);

            return new FoundationPrompts.SceneContext
            {
                Profile = profile,
                Outline = outline,
                Scene = scene,
                Summary = summary,
                Bible = run.Bible.Take(FoundationPrompts.BibleLineCap).Select(ProseGuards.Reframe).ToList(),
                LastSentences = last,
                TargetWords = target,
                AvoidLines = run.SceneTexts.Values.SelectMany(t => SentenceSplitter.Split(t))
                    .Where(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 8)
                    .Reverse().Distinct().Take(6).ToList(),
            };
        }
    }

    /// <summary>Streams one scene. Deltas are watched as they arrive: a refusal opening or a
    /// lexicon hit aborts the stream early (no point finishing a passage that will be
    /// rejected), and the partial text is handed to the acceptance check, which rejects it.</summary>
    private async Task<string> StreamSceneTextAsync(FmRequest request, CancellationToken ct)
    {
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sb = new StringBuilder();
        var gate = new object();
        var flagged = false;

        void OnDelta(string delta)
        {
            lock (gate)
            {
                if (flagged) return;
                sb.Append(delta);
                var soFar = sb.ToString();
                if ((soFar.Length <= 300 && LooksLikeRefusal(soFar)) || !_safety.IsClean(soFar))
                {
                    flagged = true;
                    abort.Cancel();
                }
            }
        }

        try
        {
            var json = await _bridge.StreamSceneAsync(request.ToJson(), OnDelta, abort.Token);
            return FmJson.Read<FmText>(json).Text;
        }
        catch (Exception ex) when (IsCancellation(ex) && !ct.IsCancellationRequested && FlaggedUnderLock())
        {
            // Never hand the partial text on: cleaning would trim the flagged half-sentence
            // and could make a rejected passage look acceptable.
            throw new ContentRejectedException();
        }

        bool FlaggedUnderLock()
        {
            lock (gate) return flagged;
        }
    }

    private async Task DigestAsync(StoryRun run, SceneOutline scene, string sceneText, CancellationToken ct)
    {
        var digest = scene.Summary;
        var facts = new List<string>();
        try
        {
            var request = await BudgetAsync(run, 80, 0, 0, _ => new FmRequest
            {
                Instructions = FoundationPrompts.DigestInstructions,
                Prompt = FoundationPrompts.Digest(sceneText),
                Options = FmOptions.Greedy(200),
            });
            var fm = FmJson.Read<FmDigest>(await _bridge.DigestSceneAsync(request.ToJson(), ct));
            if (!string.IsNullOrWhiteSpace(fm.Summary) && _safety.IsClean(fm.Summary) && !LooksLikeRefusal(fm.Summary))
                digest = fm.Summary.Trim();
            facts.AddRange(fm.NewFacts.Select(f => f.Trim()).Where(f => f.Length is > 0 and <= 80 && _safety.IsClean(f)).Take(3));
        }
        catch (FoundationModelsException ex) when (ex.Code == FmErrorCode.Cancelled || ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch (FoundationModelsException ex)
        {
            Record($"digest {scene.Id}", "digest failed; using the outline summary", ex.Code, ex.Message);
        }

        lock (run)
        {
            run.Digests[scene.Id] = digest;
            foreach (var fact in facts)
            {
                if (run.Bible.Any(b => b.Equals(fact, StringComparison.OrdinalIgnoreCase))) continue;
                run.Bible.Add(fact);
            }
            while (run.Bible.Count > FoundationPrompts.BibleLineCap) run.Bible.RemoveAt(0);
        }
    }

    // =====================================================================
    // Polish (per scene, never whole-story)
    // =====================================================================

    /// <summary>Sequential pipeline's whole-story polish, done scene by scene.</summary>
    public Task<string> PolishAsync(ChildProfile profile, string stitchedText, CancellationToken ct) =>
        PolishWindowAsync(profile, stitchedText, string.Empty, isFinalWindow: true, ct);

    public async Task<string> PolishWindowAsync(ChildProfile profile, string windowText, string previousTail, bool isFinalWindow, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var run = LatestRun();
        if (run is null)
        {
            // No live run (a resumed weave reached polish with no scenes from this process):
            // normalize only; never a whole-story model call.
            var normalized = NormalizeParagraphs(windowText);
            return isFinalWindow ? EnsureGentleClose(normalized, profile) : normalized;
        }
        if (IsTemplateWhole(run))
        {
            var text = await ReRenderModelScenesAsync(run, profile, windowText, ct);
            return await _template.PolishWindowAsync(profile, text, previousTail, isFinalWindow, ct);
        }

        var chunks = SplitIntoChunks(run, windowText);
        var available = _bridge.GetAvailability().IsAvailable;
        var polished = new List<string>(chunks.Count);
        var tail = previousTail;
        for (var i = 0; i < chunks.Count; i++)
        {
            var isLast = i == chunks.Count - 1;
            var chunk = chunks[i];
            var result = _options.PolishScenes && available
                ? await PolishChunkAsync(run, profile, chunk, tail, isFinalWindow && isLast, ct)
                : chunk;
            polished.Add(result);
            tail = LastSentences(result, 2);
        }

        var joined = NormalizeParagraphs(string.Join("\n\n", polished));
        return isFinalWindow ? EnsureGentleClose(joined, profile) : joined;
    }

    private async Task<string> PolishChunkAsync(StoryRun run, ChildProfile profile, string chunk, string previousTail, bool isFinal, CancellationToken ct)
    {
        var request = new FmRequest
        {
            Instructions = FoundationPrompts.Instructions(profile),
            Prompt = StoryPrompts.PolishWindowUser(profile, chunk, previousTail, isFinal),
            Options = FmOptions.TopKSampling(20, run.Seed, 0.3, 600),
        };
        if (!await FitsAsync(run, request, 0)) return chunk;

        var originalWords = WordCount(chunk);
        var lastGuardrail = false;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            lastGuardrail = false;
            try
            {
                var text = CleanProse(FmJson.Read<FmText>(await _bridge.PolishSceneAsync(request.ToJson(), ct)).Text);
                var ratio = originalWords == 0 ? 0 : WordCount(text) / (double)originalWords;
                if (ratio is >= 0.75 and <= 1.25 && _safety.IsClean(text) && !LooksLikeRefusal(text)) return text;
                Record("polish", "polish output rejected; keeping the scene as written", null, $"ratio {ratio:0.00}");
            }
            catch (FoundationModelsException ex) when (ex.Code == FmErrorCode.Cancelled || ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }
            catch (FoundationModelsException ex)
            {
                Record("polish", "polish failed; keeping the scene as written", ex.Code, ex.Explanation ?? ex.Message);
                if (ex.Code is not (FmErrorCode.Guardrail or FmErrorCode.Refusal or FmErrorCode.Parse)) break;
                lastGuardrail = ex.Code == FmErrorCode.Guardrail;
            }
            // Second try: varied seed, and permissive guardrails if the default ones tripped
            // (the output is lexicon-checked above either way).
            request = request with
            {
                Options = FmOptions.TopKSampling(20, Vary(run.Seed, Step.Rerun), 0.3, 600),
                Guardrails = lastGuardrail ? "permissive" : null,
            };
        }
        return chunk;
    }

    /// <summary>Splits a window back into the scenes this engine produced (exact matches, in
    /// order); otherwise into paragraph groups of roughly one scene each.</summary>
    private static List<string> SplitIntoChunks(StoryRun run, string windowText)
    {
        List<string> sceneTexts;
        lock (run) sceneTexts = run.SceneTexts.Values.ToList();

        var found = new List<(int Index, string Text)>();
        foreach (var t in sceneTexts)
        {
            var idx = windowText.IndexOf(t, StringComparison.Ordinal);
            if (idx >= 0) found.Add((idx, t));
        }
        found.Sort((a, b) => a.Index.CompareTo(b.Index));
        var covered = found.Sum(f => f.Text.Length);
        var nonSpace = windowText.Count(c => !char.IsWhiteSpace(c));
        if (found.Count > 0 && found.Sum(f => f.Text.Count(c => !char.IsWhiteSpace(c))) == nonSpace && covered <= windowText.Length)
            return found.Select(f => f.Text).ToList();

        var chunks = new List<string>();
        var current = new StringBuilder();
        foreach (var paragraph in windowText.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.Length > 0 && WordCount(current.ToString()) + WordCount(paragraph) > 260)
            {
                chunks.Add(current.ToString());
                current.Clear();
            }
            if (current.Length > 0) current.Append("\n\n");
            current.Append(paragraph.Trim());
        }
        if (current.Length > 0) chunks.Add(current.ToString());
        return chunks;
    }

    /// <summary>Rule 7 for the window path: once the story switched to templates, model-written
    /// scenes that are still unpolished in this window are re-rendered from templates too, so the
    /// remaining story reads in one voice. Scenes already polished and narrated stay as they are.</summary>
    private async Task<string> ReRenderModelScenesAsync(StoryRun run, ChildProfile profile, string windowText, CancellationToken ct)
    {
        List<KeyValuePair<int, string>> modelScenes;
        StoryOutline? outline;
        lock (run)
        {
            outline = run.Outline;
            modelScenes = run.SceneTexts.Where(kv => !run.TemplateScenes.Contains(kv.Key)).ToList();
        }
        if (outline is null || modelScenes.Count == 0) return windowText;

        var text = windowText;
        foreach (var (id, sceneText) in modelScenes)
        {
            if (!text.Contains(sceneText, StringComparison.Ordinal)) continue;
            var scene = outline.Scenes.FirstOrDefault(s => s.Id == id);
            if (scene is null) continue;
            var replacement = await _template.GenerateSceneAsync(profile, outline, scene, string.Empty, ct);
            text = text.Replace(sceneText, replacement, StringComparison.Ordinal);
        }
        return text;
    }

    // =====================================================================
    // Poster and repair
    // =====================================================================

    public async Task<PosterSpec> GeneratePosterSpecAsync(StoryOutline outline, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var run = FindRun(outline.Seed);
        var templateWhole = run is not null && IsTemplateWhole(run);
        if (templateWhole || !_bridge.GetAvailability().IsAvailable)
            return await _template.GeneratePosterSpecAsync(outline, ct);

        try
        {
            var request = PosterSpecMapper.Request(outline);
            var json = await _bridge.GeneratePosterAsync(request.ToJson(), ct);
            return PosterSpecMapper.Map(FmJson.Read<FmPosterChoice>(json), HashCombine(outline.Seed, 104729));
        }
        catch (FoundationModelsException ex) when (ex.Code == FmErrorCode.Cancelled || ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch (FoundationModelsException ex)
        {
            Record("poster", "poster fell back to the template formation", ex.Code, ex.Explanation ?? ex.Message);
            return await _template.GeneratePosterSpecAsync(outline, ct);
        }
    }

    public async Task<string> RepairPassageAsync(ChildProfile profile, string passage, string reason, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var run = LatestRun();
        if (run is null || IsTemplateWhole(run) || !_bridge.GetAvailability().IsAvailable)
            return await _template.RepairPassageAsync(profile, passage, reason, ct);

        var request = new FmRequest
        {
            Instructions = FoundationPrompts.Instructions(profile),
            Prompt = StoryPrompts.RepairUser(profile, passage, reason),
            Options = FmOptions.Threshold(0.9, Vary(run.Seed, Step.Safer), 0.5, 900),
        };
        if (await FitsAsync(run, request, 0))
        {
            // Default guardrails first; a guardrail trip earns one permissive retry (the
            // output is lexicon-checked like any other).
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var text = CleanProse(FmJson.Read<FmText>(await _bridge.PolishSceneAsync(request.ToJson(), ct)).Text);
                    if (WordCount(text) > 0 && _safety.IsClean(text) && !LooksLikeRefusal(text)) return text;
                    Record("repair", "model repair not clean; using deterministic repair", null, reason);
                    break;
                }
                catch (FoundationModelsException ex) when (ex.Code == FmErrorCode.Cancelled || ct.IsCancellationRequested)
                {
                    throw new OperationCanceledException(ct);
                }
                catch (FoundationModelsException ex)
                {
                    Record("repair", "model repair failed", ex.Code, ex.Explanation ?? ex.Message);
                    if (ex.Code != FmErrorCode.Guardrail) break;
                    request = request with { Guardrails = "permissive" };
                }
            }
        }
        return await _template.RepairPassageAsync(profile, passage, reason, ct);
    }

    // =====================================================================
    // Failure ladder
    // =====================================================================

    internal enum Step { Normal, Rerun, Permissive, Safer }

    private enum Outcome { Model, Fallback, WholeStory }

    /// <summary>A streamed passage was aborted mid-way on a lexicon or refusal hit.</summary>
    private sealed class ContentRejectedException : Exception;

    private readonly record struct Attempt(Step Step, int Trim);

    private async Task<(T? Value, Outcome Outcome)> LadderAsync<T>(
        string stage, bool allowPermissive,
        Func<Attempt, Task<FmRequest>> build,
        Func<FmRequest, CancellationToken, Task<string>> invoke,
        Func<string, T?> accept,
        CancellationToken ct) where T : class
    {
        var step = Step.Normal;
        var trim = 0;
        var contextRetried = false;
        var waits = 0;
        var contentHit = false;
        var modelAttempts = 0;

        for (var guard = 0; guard < 10; guard++)
        {
            ct.ThrowIfCancellationRequested();
            var request = await build(new Attempt(step, trim));
            modelAttempts++;
            try
            {
                var raw = await invoke(request, ct);
                var value = accept(raw);
                if (value is not null) return (value, Outcome.Model);

                contentHit = true;
                Record(stage, "output failed the lexicon/refusal check (treated as guardrail)", FmErrorCode.Guardrail, null);
                if (modelAttempts >= 2 || !TryAdvance(ref step, allowPermissive)) return (null, Outcome.Fallback);
            }
            catch (ContentRejectedException)
            {
                contentHit = true;
                Record(stage, "stream aborted on a lexicon/refusal hit (treated as guardrail)", FmErrorCode.Guardrail, null);
                if (modelAttempts >= 2 || !TryAdvance(ref step, allowPermissive)) return (null, Outcome.Fallback);
            }
            catch (Exception ex) when (IsCancellation(ex))
            {
                ct.ThrowIfCancellationRequested();
                throw new OperationCanceledException("Foundation Models request cancelled.", ex);
            }
            catch (FoundationModelsException ex)
            {
                Record(stage, Describe(ex.Code), ex.Code, ex.Explanation ?? ex.Message);
                switch (ex.Code)
                {
                    case FmErrorCode.ContextExceeded:
                        if (contextRetried) return (null, Outcome.Fallback);
                        contextRetried = true;
                        trim++;
                        break;

                    case FmErrorCode.Guardrail:
                    case FmErrorCode.Refusal:
                        if (contentHit && modelAttempts >= 2) return (null, Outcome.Fallback);
                        if (!TryAdvance(ref step, allowPermissive)) return (null, Outcome.Fallback);
                        break;

                    case FmErrorCode.RateLimited:
                    case FmErrorCode.Timeout:
                    case FmErrorCode.Concurrent:
                        if (waits >= 2) return (null, Outcome.Fallback);
                        waits++;
                        await _delay(WaitFor(ex), ct);
                        break;

                    case FmErrorCode.UnavailableDevice:
                    case FmErrorCode.UnavailableNotEnabled:
                    case FmErrorCode.UnavailableModelNotReady:
                    case FmErrorCode.UnsupportedLocale:
                    case FmErrorCode.AssetsUnavailable:
                        return (null, Outcome.WholeStory);

                    default:
                        // Parse / unknown: one plain re-run, then down the ladder.
                        if (!TryAdvance(ref step, allowPermissive)) return (null, Outcome.Fallback);
                        break;
                }
            }
        }
        return (null, Outcome.Fallback);
    }

    /// <summary>Normal → plain re-run → (String stages) same prompt under permissive guardrails
    /// → safer phrasing → give up. The permissive step comes from the 2026-10-14 Mac lab: the
    /// default guardrail tripped input-side on benign scene prompts and the same prompt passed
    /// under permissive guardrails; that output still goes through the lexicon filter.</summary>
    private static bool TryAdvance(ref Step step, bool allowPermissive)
    {
        switch (step)
        {
            case Step.Normal: step = Step.Rerun; return true;
            case Step.Rerun: step = allowPermissive ? Step.Permissive : Step.Safer; return true;
            case Step.Permissive: step = Step.Safer; return true;
            default: return false;
        }
    }

    private TimeSpan WaitFor(FoundationModelsException ex)
    {
        if (ex.ResetDate is { } reset)
        {
            var wait = reset - DateTimeOffset.UtcNow;
            if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
            return wait > _options.MaxRetryWait ? _options.MaxRetryWait : wait;
        }
        return _options.DefaultRetryWait;
    }

    private static string Describe(FmErrorCode code) => code switch
    {
        FmErrorCode.ContextExceeded => "context window exceeded",
        FmErrorCode.Guardrail => "guardrail violation",
        FmErrorCode.Refusal => "model refused",
        FmErrorCode.RateLimited => "rate limited",
        FmErrorCode.Timeout => "timed out",
        FmErrorCode.Concurrent => "concurrent request",
        FmErrorCode.Parse => "could not parse the result",
        FmErrorCode.AssetsUnavailable => "model assets unavailable",
        FmErrorCode.UnsupportedLocale => "unsupported language or locale",
        _ => code.ToString(),
    };

    // =====================================================================
    // Token budget
    // =====================================================================

    /// <summary>Builds the request at the lowest trim level whose input fits
    /// <c>contextSize − maxResponseTokens − 200 − schemaReserve</c>.</summary>
    private async Task<FmRequest> BudgetAsync(StoryRun run, int schemaReserve, int startTrim, int maxTrim, Func<int, FmRequest> make)
    {
        var trim = Math.Min(startTrim, maxTrim);
        var request = make(trim);
        while (!await FitsAsync(run, request, schemaReserve) && trim < maxTrim)
        {
            trim++;
            request = make(trim);
        }
        return request;
    }

    private async Task<bool> FitsAsync(StoryRun run, FmRequest request, int schemaReserve)
    {
        var budget = run.ContextSize - (request.Options.MaximumResponseTokens ?? 600) - SafetyMarginTokens - schemaReserve;
        var text = request.Instructions + "\n" + request.Prompt;
        var estimate = EstimateTokens(text);
        if (estimate <= budget * 3 / 4) return true;
        var exact = await _bridge.CountTokensAsync(text, CancellationToken.None);
        return (exact ?? estimate) <= budget;
    }

    /// <summary>Conservative estimate (~3.5 characters per token for English prose).</summary>
    internal static int EstimateTokens(string text) => (int)Math.Ceiling(text.Length / 3.5);

    // =====================================================================
    // Runs (per-story state between pipeline calls)
    // =====================================================================

    private sealed class StoryRun
    {
        public required int Seed { get; init; }
        public required int ContextSize { get; init; }
        public StoryOutline? Outline { get; set; }
        public bool TemplateWhole { get; set; }
        public int FallbackScenes { get; set; }
        public List<string> Bible { get; } = [];
        public SortedDictionary<int, string> Digests { get; } = new();
        public SortedDictionary<int, string> SceneTexts { get; } = new();
        public HashSet<int> TemplateScenes { get; } = [];
        /// <summary>Normalized short sentences already used (per-story banned-repetition list).</summary>
        public HashSet<string> StockLines { get; } = [];
    }

    private StoryRun NewRun(int seed, int contextSize)
    {
        var run = new StoryRun { Seed = seed, ContextSize = Math.Max(contextSize, FoundationModelsAvailability.ContextFloor) };
        lock (_gate)
        {
            _runs.RemoveAll(r => r.Seed == seed);
            _runs.Add(run);
            while (_runs.Count > MaxRuns) _runs.RemoveAt(0);
        }
        return run;
    }

    private StoryRun? FindRun(int seed)
    {
        lock (_gate) return _runs.LastOrDefault(r => r.Seed == seed);
    }

    private StoryRun? LatestRun()
    {
        lock (_gate) return _runs.Count == 0 ? null : _runs[^1];
    }

    /// <summary>The run for this outline; a resumed weave (app restarted mid-story) gets a
    /// fresh run whose "story so far" is rebuilt from the outline's scene summaries.</summary>
    private StoryRun RunFor(StoryOutline outline)
    {
        var run = FindRun(outline.Seed);
        if (run is not null)
        {
            lock (run) run.Outline ??= outline;
            return run;
        }
        var availability = _bridge.GetAvailability();
        run = NewRun(outline.Seed, availability.ContextSize);
        lock (run) run.Outline = outline;
        return run;
    }

    private static bool IsTemplateWhole(StoryRun run)
    {
        lock (run) return run.TemplateWhole;
    }

    private void Record(string stage, string what, FmErrorCode? code, string? detail)
    {
        _logger.LogInformation("AppleFoundation {Stage}: {What} ({Code}) {Detail}", stage, what, code, detail);
        lock (_events)
        {
            _events.AddLast(new AppleEngineEvent(DateTime.UtcNow, stage, what, code, detail));
            while (_events.Count > MaxEvents) _events.RemoveFirst();
        }
    }

    // =====================================================================
    // Text helpers
    // =====================================================================

    [GeneratedRegex(@"^\s*[""“]?\s*(I'm sorry|I am sorry|Sorry,|I apologi[sz]e|I can't|I cannot|I can not|I won't|I will not|I'm unable|I am unable|I'm not able|As an AI|As a language model)",
        RegexOptions.IgnoreCase)]
    private static partial Regex RefusalOpening();

    [GeneratedRegex(@"\b(as an AI|language model|I can't help with|I cannot help with|I'm not able to help)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RefusalAnywhere();

    [GeneratedRegex(@"^\s*(#+\s.*|\*\*?\s*(scene|title|chapter)\b.*|(scene|chapter)\s+\d+\s*[:.\-].*|title\s*:.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExtraBlankLines();

    [GeneratedRegex(@"\b(sleep|asleep|sleepy|goodnight|good night|dream|dreams|drift|drifted|drifting|snug|slumber)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SleepWords();

    /// <summary>Refusal text in String generation looks like ordinary output (recipe), so it
    /// is matched by its opening and by tell-tale phrases anywhere.</summary>
    internal static bool LooksLikeRefusal(string text) =>
        RefusalOpening().IsMatch(text) || RefusalAnywhere().IsMatch(text);

    /// <summary>Scene acceptance: clean, strip repeats of the previous tail and of this story's
    /// stock lines, then the length / refusal / lexicon checks. A scene that was mostly
    /// repetition is rejected so the ladder regenerates it.</summary>
    private string? AcceptScene(StoryRun run, string raw, FoundationPrompts.SceneContext context)
    {
        HashSet<string> stock;
        lock (run) stock = [.. run.StockLines];
        var (stripped, removed) = ProseGuards.StripRepeats(CleanProse(raw), context.LastSentences, stock);
        if (removed > 0.4) return null;
        return AcceptProse(stripped, context.TargetWords);
    }

    private string? AcceptProse(string raw, int targetWords)
    {
        var text = CleanProse(raw);
        if (WordCount(text) < Math.Max(30, targetWords * 3 / 10)) return null;
        if (LooksLikeRefusal(text) || !_safety.IsClean(text)) return null;
        return text;
    }

    /// <summary>Strips headings and markdown, normalizes spacing, and drops a trailing
    /// half-sentence left by the response-token cap.</summary>
    internal static string CleanProse(string raw)
    {
        var text = HeadingLine().Replace(raw.Replace("\r\n", "\n"), string.Empty);
        text = text.Replace("**", string.Empty).Replace("__", string.Empty);
        text = Spaces().Replace(text, " ");
        text = NormalizeParagraphs(text);
        var trimmed = text.TrimEnd();
        if (trimmed.Length > 0 && !".!?…\"”’'".Contains(trimmed[^1]))
        {
            var cut = trimmed.LastIndexOfAny(['.', '!', '?', '…']);
            if (cut > trimmed.Length * 6 / 10)
            {
                var end = cut + 1;
                while (end < trimmed.Length && trimmed[end] is '"' or '”' or '’' or '\'') end++;
                trimmed = trimmed[..end];
            }
        }
        return trimmed.Trim();
    }

    private static string NormalizeParagraphs(string text)
    {
        var paragraphs = ExtraBlankLines().Replace(text.Replace("\r\n", "\n"), "\n\n")
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(p => string.Join(' ', p.Split(['\n', ' '], StringSplitOptions.RemoveEmptyEntries)))
            .Where(p => p.Length > 0);
        return string.Join("\n\n", paragraphs);
    }

    /// <summary>Design covenant: the story's last paragraph is a soft goodnight. The model is
    /// asked for one; if its closing paragraph carries no sleep at all, the template engine's
    /// gentle close is appended.</summary>
    private static string EnsureGentleClose(string text, ChildProfile profile)
    {
        var paragraphs = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        if (paragraphs.Length > 0 && SleepWords().IsMatch(paragraphs[^1])) return text;
        return text + "\n\nAnd with that, the night grew soft and still. " +
               $"{FoundationPrompts.SafeName(profile)} closed both eyes, breathed slowly, and drifted into a calm, cozy sleep. Goodnight.";
    }

    private static string CleanTitle(string title)
    {
        var t = title.Trim().Trim('"', '“', '”', '*', '#', '\'').Trim();
        return t.Length > 60 ? t[..60].TrimEnd() : t;
    }

    private static string LastSentences(string text, int count)
    {
        var sentences = SentenceSplitter.Split(text);
        return string.Join(" ", sentences.Skip(Math.Max(0, sentences.Count - count)));
    }

    private static string EnsureSentence(string s)
    {
        var t = s.Trim();
        return t.Length == 0 || ".!?…".Contains(t[^1]) ? t : t + ".";
    }

    private static int WordCount(string text) =>
        text.Split([' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).Length;

    private static bool IsCancellation(Exception ex) =>
        ex is OperationCanceledException || ex is FoundationModelsException { Code: FmErrorCode.Cancelled };

    /// <summary>A re-run must not reuse the exact seed — a seeded re-run of the same prompt
    /// would reproduce the same rejected output.</summary>
    private static int Vary(int seed, Step step) => step == Step.Normal ? seed : HashCombine(seed, 7919 * (int)step);

    private static int HashCombine(int a, int b)
    {
        unchecked { return a * 486187739 + b; }
    }
}
