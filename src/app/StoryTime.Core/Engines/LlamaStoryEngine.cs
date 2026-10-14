using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StoryTime.Contracts;
using StoryTime.Core.Abstractions;
using StoryTime.Core.Engines.Llama;

namespace StoryTime.Core.Engines;

/// <summary>Benchmark metrics from <see cref="LlamaStoryEngine.BenchmarkAsync"/>.</summary>
public sealed record LlamaBenchResult(int PromptTokens, int GeneratedTokens, double PromptTokensPerSecond, double GenerateTokensPerSecond);

/// <summary>Cumulative token accounting across an engine's lifetime (harness `weave
/// --timings` reads it after a run). CachedPromptTokens counts prompt tokens that were
/// already resident in the KV cache and did NOT need re-ingestion (KV prefix reuse).</summary>
public sealed class LlamaEngineStats
{
    public int Calls { get; internal set; }
    public int PromptTokens { get; internal set; }
    public int CachedPromptTokens { get; internal set; }
    public int GeneratedTokens { get; internal set; }

    internal void Record(Llama.LlamaGenerateResult r)
    {
        Calls++;
        PromptTokens += r.PromptTokens;
        CachedPromptTokens += r.CachedPromptTokens;
        GeneratedTokens += r.GeneratedTokens;
    }
}

/// <summary>
/// Real on-device story engine: llama.cpp (pinned b10189) running Qwen3-1.7B Q4_K_M.
/// JSON stages (outline, poster spec) decode under GBNF grammars from StoryTime.Contracts;
/// prose stages are free text. Model path comes from IModelProvider; the model is loaded
/// lazily on first use and kept mmap'd for the engine's lifetime. Dispose before starting
/// TTS on-device (memory ceiling discipline, architecture §11).
/// </summary>
public sealed class LlamaStoryEngine : IStoryEngine, IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IModelProvider _models;
    private readonly ILogger<LlamaStoryEngine> _log;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private readonly Poster.PosterComposer _composer = new();
    private readonly Poster.CompositionCritic _critic = new();
    private readonly TemplateStoryEngine _templateArtist = new();
    private LlamaRunner? _runner;

    /// <summary>Optional per-token-batch progress hook (tokens generated in the current call).</summary>
    public IProgress<int>? TokenProgress { get; set; }

    /// <summary>Fixed seed for reproducible runs (harness --seed); 0 = nondeterministic.</summary>
    public uint Seed { get; set; }

    /// <summary>Cumulative token accounting for this engine instance (measurement only).</summary>
    public LlamaEngineStats Stats { get; } = new();

    /// <summary>KV prefix reuse across calls (default on). The context stays alive for the
    /// whole weave and each call re-decodes only the tokens that diverge from the previous
    /// call's prompt — the shared system prompt (and, per stage, the shared outline/story
    /// preamble) is served from cache. Off → every call clears memory (pre-cache behavior).</summary>
    public bool KvPrefixCache { get; set; } = true;

    public LlamaStoryEngine(IModelProvider models, ILogger<LlamaStoryEngine>? log = null)
    {
        _models = models;
        _log = log ?? NullLogger<LlamaStoryEngine>.Instance;
    }

    public string Name => "llama-qwen3";

    private async Task<LlamaRunner> GetRunnerAsync(CancellationToken ct)
    {
        if (_runner is not null) return _runner;
        await _initGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_runner is not null) return _runner;
            var availability = await _models.GetAvailabilityAsync(ct).ConfigureAwait(false);
            if (!availability.LlmReady || availability.LlmPath is null)
                throw new InvalidOperationException("LlamaStoryEngine selected but LLM model is not available (IModelProvider.LlmReady == false)");
            _log.LogInformation("Loading GGUF model (mmap): {Path}", availability.LlmPath);
            _runner = new LlamaRunner(availability.LlmPath);
            return _runner;
        }
        finally
        {
            _initGate.Release();
        }
    }

    // ---------------- IStoryEngine ----------------

    public async Task<StoryOutline> GenerateOutlineAsync(ChildProfile profile, StoryBible? bible, int sceneCount, CancellationToken ct)
    {
        // One title shape per weave, rotated by seed (harness --seed keeps it reproducible;
        // production Seed==0 draws freely) and nudged along by shelf size so consecutive
        // nights lean toward different shapes. See LlamaPrompts.TitleShapeHints.
        var hintIndex = (int)((Seed != 0 ? Seed : (uint)Random.Shared.Next(0, int.MaxValue))
                              % (uint)LlamaPrompts.TitleShapeHints.Length);
        hintIndex = (hintIndex + profile.RecentTitles.Count) % LlamaPrompts.TitleShapeHints.Length;
        var user = LlamaPrompts.OutlineUser(profile, bible, sceneCount, LlamaPrompts.TitleShapeHints[hintIndex]);
        var outline = await GenerateJsonAsync<StoryOutline>(profile.Band, profile.EffectiveContentFilter, user, GrammarStore.Outline, maxTokens: 1400, ct).ConfigureAwait(false);
        if (outline.Scenes.Count == 0)
            throw new InvalidOperationException("Outline generation produced no scenes");
        return outline;
    }

    public async Task<string> GenerateSceneAsync(ChildProfile profile, StoryOutline outline, SceneOutline scene, string previousTail, CancellationToken ct)
    {
        var user = LlamaPrompts.SceneUser(profile, outline, scene, previousTail);
        var text = await GenerateTextAsync(profile.Band, profile.EffectiveContentFilter, user, temperature: 0.8f, maxTokens: 800, ct).ConfigureAwait(false);
        return text.Trim();
    }

    public async Task<string> PolishAsync(ChildProfile profile, string stitchedText, CancellationToken ct)
    {
        // Polish the whole story in one pass; budget scales with input length.
        var maxTokens = Math.Min(4096, Math.Max(1200, stitchedText.Length / 2));
        var text = await GenerateTextAsync(profile.Band, profile.EffectiveContentFilter, LlamaPrompts.PolishUser(profile, stitchedText), 0.5f, maxTokens, ct).ConfigureAwait(false);
        var polished = text.Trim();
        // Guard: a polish pass that lost most of the story is worse than no polish.
        return polished.Length >= stitchedText.Length / 2 ? polished : stitchedText;
    }

    /// <summary>Window-scoped polish (overlapped pipeline): same budgets and same
    /// half-length guard as PolishAsync, window-aware prompt (middle windows must not
    /// close the story; only the final window writes the goodnight).</summary>
    public async Task<string> PolishWindowAsync(ChildProfile profile, string windowText, string previousTail, bool isFinalWindow, CancellationToken ct)
    {
        var maxTokens = Math.Min(4096, Math.Max(1200, windowText.Length / 2));
        var user = LlamaPrompts.PolishWindowUser(profile, windowText, previousTail, isFinalWindow);
        var text = await GenerateTextAsync(profile.Band, profile.EffectiveContentFilter, user, 0.5f, maxTokens, ct).ConfigureAwait(false);
        var polished = text.Trim();
        return polished.Length >= windowText.Length / 2 ? polished : windowText;
    }

    /// <summary>Poster stage, multi-candidate: TWO seeded LLM formations (cheap ~350-token
    /// JSON calls, run sequentially on the already-loaded model) plus the template engine's
    /// formation as a deterministic safety net. Every candidate is validated by the
    /// composer, then the CompositionCritic — pure math, no model — picks the winner.
    /// Poster art has no theme palette to widen — always the Gentle system prompt.</summary>
    public async Task<PosterSpec> GeneratePosterSpecAsync(StoryOutline outline, CancellationToken ct)
    {
        var user = LlamaPrompts.PosterUser(outline);
        var candidates = new List<(string Name, PosterSpec Validated)>(3);
        for (var sample = 0; sample < 2; sample++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var spec = await GenerateJsonAsync<PosterSpec>(AgeBand.Age5To6, ContentFilterLevel.Gentle, user,
                    GrammarStore.Poster, maxTokens: 700, ct, seedOffset: (uint)(sample * 61)).ConfigureAwait(false);
                candidates.Add(($"llm-{sample + 1}", _composer.Validate(spec)));
            }
            catch (InvalidOperationException ex)
            {
                // A failed sample is recoverable — the other sample and the template
                // candidate still compete; the poster stage never sinks the weave.
                _log.LogWarning("Poster sample {Sample} failed: {Message}", sample + 1, ex.Message);
            }
        }
        candidates.Add(("template", _composer.Validate(
            await _templateArtist.GeneratePosterSpecAsync(outline, ct).ConfigureAwait(false))));

        var (winner, scores) = _critic.Judge(candidates, outline);
        foreach (var s in scores) _log.LogInformation("poster critic: {Score}", s.ToString());
        _log.LogInformation("poster critic winner: {Name}", candidates[winner].Name);
        return candidates[winner].Validated;
    }

    public async Task<string> RepairPassageAsync(ChildProfile profile, string passage, string reason, CancellationToken ct)
    {
        // Safety repair always runs under the strictest (Gentle) theme palette — the repair
        // pass is part of the safety stack and never inherits the widened level.
        var text = await GenerateTextAsync(profile.Band, ContentFilterLevel.Gentle, LlamaPrompts.RepairUser(profile, passage, reason), 0.6f, 700, ct).ConfigureAwait(false);
        var repaired = text.Trim();
        return repaired.Length > 0 ? repaired : passage;
    }

    /// <summary>Raw generation benchmark (harness `bench`): fixed prose prompt, n tokens max.</summary>
    public async Task<LlamaBenchResult> BenchmarkAsync(int maxTokens, CancellationToken ct)
    {
        var runner = await GetRunnerAsync(ct).ConfigureAwait(false);
        var prompt = LlamaPrompts.Chat(LlamaPrompts.SystemPrompt(AgeBand.Age5To6),
            "Tell a gentle little story about a sleepy fox who finds a warm lantern in the snow.");
        var result = await Task.Run(() => runner.Generate(prompt, new LlamaGenerateOptions
        {
            Temperature = 0.8f,
            MaxTokens = maxTokens,
            Seed = Seed,
            ReuseKvPrefix = KvPrefixCache,
        }, ct), ct).ConfigureAwait(false);
        Stats.Record(result);
        return new LlamaBenchResult(result.PromptTokens, result.GeneratedTokens,
            result.PromptTokensPerSecond, result.GenerateTokensPerSecond);
    }

    // ---------------- internals ----------------

    private async Task<string> GenerateTextAsync(AgeBand band, ContentFilterLevel level, string user, float temperature, int maxTokens, CancellationToken ct)
    {
        var runner = await GetRunnerAsync(ct).ConfigureAwait(false);
        var prompt = LlamaPrompts.Chat(LlamaPrompts.SystemPrompt(band, level), user);
        var result = await Task.Run(() => runner.Generate(prompt, new LlamaGenerateOptions
        {
            Temperature = temperature,
            MaxTokens = maxTokens,
            Seed = Seed,
            Progress = TokenProgress,
            ReuseKvPrefix = KvPrefixCache,
        }, ct), ct).ConfigureAwait(false);
        Stats.Record(result);
        _log.LogInformation("llama gen: {Gen} tok @ {Tps:F1} tok/s (prompt {P} tok, {Cached} cached, @ {Pps:F0} tok/s)",
            result.GeneratedTokens, result.GenerateTokensPerSecond, result.PromptTokens, result.CachedPromptTokens, result.PromptTokensPerSecond);
        return result.Text;
    }

    private async Task<T> GenerateJsonAsync<T>(AgeBand band, ContentFilterLevel level, string user, string grammar, int maxTokens, CancellationToken ct, uint seedOffset = 0)
    {
        var runner = await GetRunnerAsync(ct).ConfigureAwait(false);
        var prompt = LlamaPrompts.Chat(LlamaPrompts.SystemPrompt(band, level), user);

        Exception? lastError = null;
        for (var attempt = 0; attempt < 2; attempt++) // robust parse with ONE retry on failure
        {
            ct.ThrowIfCancellationRequested();
            var result = await Task.Run(() => runner.Generate(prompt, new LlamaGenerateOptions
            {
                Temperature = 0.4f, // JSON stages run cool
                MaxTokens = maxTokens,
                Grammar = grammar,
                Seed = Seed != 0 ? Seed + seedOffset + (uint)attempt : 0,
                Progress = TokenProgress,
                ReuseKvPrefix = KvPrefixCache,
            }, ct), ct).ConfigureAwait(false);
            Stats.Record(result);
            try
            {
                var parsed = TryParseJson<T>(result.Text);
                _log.LogInformation("llama json ({Type}): {Gen} tok @ {Tps:F1} tok/s",
                    typeof(T).Name, result.GeneratedTokens, result.GenerateTokensPerSecond);
                return parsed;
            }
            catch (JsonException ex)
            {
                lastError = ex;
                _log.LogWarning("JSON parse failed for {Type} (attempt {Attempt}): {Message}", typeof(T).Name, attempt + 1, ex.Message);
            }
        }
        throw new InvalidOperationException($"Grammar-constrained JSON generation failed twice for {typeof(T).Name}", lastError);
    }

    internal static T TryParseJson<T>(string raw)
    {
        // Grammar guarantees shape, but guard against truncation / stray leading text anyway.
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) throw new JsonException("No JSON object found in output");
        var json = raw[start..(end + 1)];
        return JsonSerializer.Deserialize<T>(json, JsonOpts) ?? throw new JsonException("Deserialized to null");
    }

    public void Dispose()
    {
        _runner?.Dispose();
        _runner = null;
        _initGate.Dispose();
    }
}
