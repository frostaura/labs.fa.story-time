using StoryTime.Core.Abstractions;

namespace StoryTime.Harness;

/// <summary>
/// Desktop model resolution for headless runs: $STORYTIME_MODEL_ROOT (default ~/StoryTimeModels),
/// populated by scripts/fetch-models.sh. $STORYTIME_LLM can point at an alternate GGUF
/// (e.g. the Qwen3-0.6B low-RAM fallback tier).
/// </summary>
internal sealed class DesktopModelProvider : IModelProvider
{
    public static string ModelRoot =>
        Environment.GetEnvironmentVariable("STORYTIME_MODEL_ROOT")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "StoryTimeModels");

    public Task<ModelAvailability> GetAvailabilityAsync(CancellationToken ct)
    {
        var llm = Environment.GetEnvironmentVariable("STORYTIME_LLM")
                  ?? Path.Combine(ModelRoot, "Qwen3-1.7B-Q4_K_M.gguf");
        // TTS bundle preference: $STORYTIME_TTS_DIR override → kokoro-multi-lang-v1_0 (fp32
        // quality tier) → kokoro-int8-multi-lang-v1_0 (low-storage tier) → legacy v0_19.
        var ttsOverride = Environment.GetEnvironmentVariable("STORYTIME_TTS_DIR");
        var ttsDir = ttsOverride is not null
            ? (Path.IsPathRooted(ttsOverride) ? ttsOverride : Path.Combine(ModelRoot, ttsOverride))
            : new[] { "kokoro-multi-lang-v1_0", "kokoro-int8-multi-lang-v1_0", "kokoro-en-v0_19" }
                .Select(d => Path.Combine(ModelRoot, d))
                .FirstOrDefault(TtsComplete) ?? Path.Combine(ModelRoot, "kokoro-multi-lang-v1_0");
        var ttsReady = TtsComplete(ttsDir);
        return Task.FromResult(new ModelAvailability
        {
            LlmReady = File.Exists(llm),
            LlmPath = File.Exists(llm) ? llm : null,
            TtsReady = ttsReady,
            TtsDirectory = ttsReady ? ttsDir : null,
        });
    }

    private static bool TtsComplete(string dir) =>
        (File.Exists(Path.Combine(dir, "model.onnx")) || File.Exists(Path.Combine(dir, "model.int8.onnx")))
        && File.Exists(Path.Combine(dir, "voices.bin"))
        && File.Exists(Path.Combine(dir, "tokens.txt"))
        && Directory.Exists(Path.Combine(dir, "espeak-ng-data"));
}
