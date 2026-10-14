using System.Runtime.InteropServices;
using StoryTime.Contracts;
using StoryTime.Core.Abstractions;
using StoryTime.Core.Engines;
using StoryTime.Core.Engines.Sherpa;
using Xunit;

namespace StoryTime.Core.Tests.Engines;

/// <summary>
/// Model-dependent integration tests. They self-skip (early-return, logged via Trait)
/// when ~/StoryTimeModels or ~/StoryTimeNative is absent, so CI without the big
/// binaries stays green. (Plain xunit has no dynamic skip without an extra package,
/// and this project's csproj is owned by another workstream — early-return is the
/// deliberate approximation of a skippable fact.)
/// </summary>
public class ModelDependentTests
{
    private static string ModelRoot =>
        Environment.GetEnvironmentVariable("STORYTIME_MODEL_ROOT")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "StoryTimeModels");

    private static string NativeRoot =>
        Environment.GetEnvironmentVariable("STORYTIME_NATIVE_ROOT")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "StoryTimeNative");

    private sealed class TestModelProvider : IModelProvider
    {
        public Task<ModelAvailability> GetAvailabilityAsync(CancellationToken ct)
        {
            var llm = Path.Combine(ModelRoot, "Qwen3-1.7B-Q4_K_M.gguf");
            var tts = Path.Combine(ModelRoot, "kokoro-en-v0_19");
            return Task.FromResult(new ModelAvailability
            {
                LlmReady = File.Exists(llm),
                LlmPath = File.Exists(llm) ? llm : null,
                TtsReady = Directory.Exists(tts),
                TtsDirectory = Directory.Exists(tts) ? tts : null,
            });
        }
    }

    private static bool _resolverHooked;

    /// <summary>Tests run without Native.props, so map DllImport("llama") to the built dylib.</summary>
    private static void HookLlamaResolver()
    {
        if (_resolverHooked) return;
        _resolverHooked = true;
        var dylib = Path.Combine(NativeRoot, "osx-arm64", "libllama.dylib");
        NativeLibrary.SetDllImportResolver(typeof(LlamaStoryEngine).Assembly,
            (name, _, _) => name == "llama" && File.Exists(dylib) ? NativeLibrary.Load(dylib) : IntPtr.Zero);
    }

    [Fact]
    [Trait("Category", "ModelDependent")]
    public async Task Kokoro_SynthesizesRealAudio_WithMonotonicTimestamps()
    {
        if (!Directory.Exists(Path.Combine(ModelRoot, "kokoro-en-v0_19"))) return; // skip: models absent

        using var tts = new SherpaTtsEngine(new TestModelProvider());
        var outPath = Path.Combine(Path.GetTempPath(), $"tw-test-{Guid.NewGuid():N}.wav");
        try
        {
            var manifest = await tts.SynthesizeAsync(
                ["The fox curled up.", "The moon rose gently."],
                NarratorVoice.Ember, outPath, null, CancellationToken.None);

            Assert.True(File.Exists(outPath));
            Assert.True(manifest.DurationSeconds > 1.0, $"duration {manifest.DurationSeconds}");
            Assert.Equal(2, manifest.Timestamps.Count);
            Assert.True(manifest.Timestamps[0].EndSeconds <= manifest.Timestamps[1].StartSeconds);
            Assert.Equal(manifest.DurationSeconds, WavAudio.ReadDurationSeconds(outPath), 1);
        }
        finally
        {
            File.Delete(outPath);
        }
    }

    /// <summary>KV prefix-cache correctness (perf pass 2026-07-31): generating the SAME
    /// prompt+seed with the prefix cache warm (shared system-prompt prefix resident from a
    /// previous call, llama_memory_seq_rm of the divergent tail) must produce the SAME text
    /// as a fresh full-clear generation. Sampling is chain-per-call, so any divergence
    /// would mean the reused KV entries corrupt the logits.</summary>
    [Fact]
    [Trait("Category", "ModelDependent")]
    public void Qwen_KvPrefixReuse_MatchesFreshGeneration()
    {
        var llm = Path.Combine(ModelRoot, "Qwen3-1.7B-Q4_K_M.gguf");
        if (!File.Exists(llm)) return; // skip: models absent
        if (!File.Exists(Path.Combine(NativeRoot, "osx-arm64", "libllama.dylib"))) return; // skip: natives absent
        HookLlamaResolver();

        using var runner = new StoryTime.Core.Engines.Llama.LlamaRunner(llm);
        var system = StoryTime.Core.Engines.Llama.LlamaPrompts.SystemPrompt(AgeBand.Age5To6);
        var warmPrompt = StoryTime.Core.Engines.Llama.LlamaPrompts.Chat(system,
            "Tell a gentle little story about a sleepy fox who finds a warm lantern in the snow.");
        var testPrompt = StoryTime.Core.Engines.Llama.LlamaPrompts.Chat(system,
            "Tell a gentle little story about a small boat rocking on a calm evening sea.");

        StoryTime.Core.Engines.Llama.LlamaGenerateResult Generate(bool reuse) =>
            runner.Generate(testPrompt, new StoryTime.Core.Engines.Llama.LlamaGenerateOptions
            {
                Temperature = 0.8f,
                MaxTokens = 48,
                Seed = 4242,
                ReuseKvPrefix = reuse,
            }, CancellationToken.None);

        // Warm the cache with a DIFFERENT prompt sharing the system prefix…
        runner.Generate(warmPrompt, new StoryTime.Core.Engines.Llama.LlamaGenerateOptions
        {
            Temperature = 0.8f, MaxTokens = 16, Seed = 7,
        }, CancellationToken.None);

        // …then generate with the cache (prefix must actually hit)…
        var cached = Generate(reuse: true);
        Assert.True(cached.CachedPromptTokens > 0, "expected a shared-prefix cache hit");
        Assert.True(cached.CachedPromptTokens < cached.PromptTokens);

        // …and fresh (full memory clear). Identical text or the cache corrupts sampling.
        var fresh = Generate(reuse: false);
        Assert.Equal(0, fresh.CachedPromptTokens);
        Assert.Equal(fresh.Text, cached.Text);
    }

    [Fact]
    [Trait("Category", "ModelDependent")]
    public async Task Qwen_Generates_UnderTheEngine()
    {
        if (!File.Exists(Path.Combine(ModelRoot, "Qwen3-1.7B-Q4_K_M.gguf"))) return; // skip: models absent
        if (!File.Exists(Path.Combine(NativeRoot, "osx-arm64", "libllama.dylib"))) return; // skip: natives absent
        HookLlamaResolver();

        using var engine = new LlamaStoryEngine(new TestModelProvider()) { Seed = 7 };
        var result = await engine.BenchmarkAsync(24, CancellationToken.None);
        Assert.True(result.GeneratedTokens > 0);
        Assert.True(result.GenerateTokensPerSecond > 0);
    }
}
