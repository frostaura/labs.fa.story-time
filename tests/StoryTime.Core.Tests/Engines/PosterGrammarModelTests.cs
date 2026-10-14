using StoryTime.Contracts;
using StoryTime.Core.Abstractions;
using StoryTime.Core.Engines;
using Xunit;

namespace StoryTime.Core.Tests.Engines;

/// <summary>
/// Model-dependent verification that the v2 poster formation grammar actually PARSES and
/// SAMPLES under the pinned llama.cpp (b10189) — static string checks in GrammarTests can
/// not catch a GBNF construct the native parser rejects (e.g. bounded repetition quirks).
/// Self-skips (early-return) when ~/StoryTimeModels or ~/StoryTimeNative is absent,
/// same convention as ModelDependentTests.
/// </summary>
public class PosterGrammarModelTests
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
            return Task.FromResult(new ModelAvailability
            {
                LlmReady = File.Exists(llm),
                LlmPath = File.Exists(llm) ? llm : null,
            });
        }
    }

    /// <summary>NativeLibrary.SetDllImportResolver may be called ONCE per assembly, and
    /// ModelDependentTests owns that single registration. Reuse its (idempotent) hook via
    /// reflection so both test classes can run in the same process in any order.</summary>
    private static void HookLlamaResolver() =>
        typeof(ModelDependentTests)
            .GetMethod("HookLlamaResolver", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, null);

    [Fact]
    [Trait("Category", "ModelDependent")]
    public async Task PosterFormation_GeneratesUnderTheV2Grammar_AndSurvivesTheComposer()
    {
        if (!File.Exists(Path.Combine(ModelRoot, "Qwen3-1.7B-Q4_K_M.gguf"))) return; // skip: models absent
        if (!File.Exists(Path.Combine(NativeRoot, "osx-arm64", "libllama.dylib"))) return; // skip: natives absent
        HookLlamaResolver();

        var outline = new StoryOutline
        {
            Title = "The Fox and the Winter Lantern",
            Moral = "small lights make the dark friendly",
            Settings = ["a snowy pine hollow", "the frozen brook"],
            Characters = [new CharacterCard { Name = "Pip", Kind = "fox", Trait = "curious" }],
            Scenes = [new SceneOutline { Id = 1, Summary = "Pip finds a lantern in the snow." }],
            Seed = 42,
        };

        using var engine = new LlamaStoryEngine(new TestModelProvider()) { Seed = 7 };
        var spec = await engine.GeneratePosterSpecAsync(outline, CancellationToken.None);

        // The grammar guarantees structural validity; assert the contract shape…
        Assert.Equal(4, spec.Layers.Count);
        Assert.Equal(new[] { 0, 1, 2, 3 }, spec.Layers.Select(l => l.Depth));
        Assert.True(Enum.IsDefined(spec.Palette));
        Assert.All(spec.Layers.SelectMany(l => l.Elements), e =>
        {
            Assert.True(Enum.IsDefined(e.Shape), $"undefined shape {e.Shape}");
            Assert.InRange(e.X, 0, 1);
            Assert.InRange(e.Y, 0, 1);
            Assert.InRange(e.Tint, 0, 1);
            Assert.InRange(e.Glow, 0, 1);
        });
        Assert.True(spec.Layers.Sum(l => l.Elements.Count) >= 2, "the formation is not empty");

        // …and that the composer accepts the real model's composition end-to-end.
        var composed = new Core.Poster.PosterComposer().Resolve(spec);
        Assert.Equal(4, composed.Layers.Count);
        Assert.NotNull(composed.Layers[0].BackgroundTopHex);
    }
}
