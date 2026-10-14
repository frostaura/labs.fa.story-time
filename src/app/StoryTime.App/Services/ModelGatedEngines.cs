using Microsoft.Extensions.Logging;
using StoryTime.Contracts;
using StoryTime.Core.Abstractions;
using StoryTime.Core.Engines;

namespace StoryTime.App.Services;

/// <summary>
/// Lazily selects the real llama.cpp engine when models resolve, else the deterministic
/// template engine. Resolution happens on first use — never at startup — so a Debug
/// LAN model bootstrap (GBs) runs under the Weaving screen, not app launch
/// (docs/native-integration.md, orchestrator note).
/// </summary>
public sealed class ModelGatedStoryEngine(IModelProvider models, ILoggerFactory loggers) : IStoryEngine
{
    private IStoryEngine? _inner;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string Name => _inner?.Name ?? "auto";

    private async Task<IStoryEngine> ResolveAsync(CancellationToken ct)
    {
        if (_inner is not null) return _inner;
        await _gate.WaitAsync(ct);
        try
        {
            if (_inner is not null) return _inner;
            var availability = await models.GetAvailabilityAsync(ct);
            _inner = availability.LlmReady
                ? new LlamaStoryEngine(models, loggers.CreateLogger<LlamaStoryEngine>())
                : new TemplateStoryEngine(() => Random.Shared.Next());
            loggers.CreateLogger<ModelGatedStoryEngine>()
                .LogInformation("Story engine resolved: {Engine}", _inner.Name);
            return _inner;
        }
        finally { _gate.Release(); }
    }

    public async Task<StoryOutline> GenerateOutlineAsync(ChildProfile profile, StoryBible? bible, int sceneCount, CancellationToken ct)
        => await (await ResolveAsync(ct)).GenerateOutlineAsync(profile, bible, sceneCount, ct);

    public async Task<string> GenerateSceneAsync(ChildProfile profile, StoryOutline outline, SceneOutline scene, string previousTail, CancellationToken ct)
        => await (await ResolveAsync(ct)).GenerateSceneAsync(profile, outline, scene, previousTail, ct);

    public async Task<string> PolishAsync(ChildProfile profile, string stitchedText, CancellationToken ct)
        => await (await ResolveAsync(ct)).PolishAsync(profile, stitchedText, ct);

    // Forward explicitly — the interface default would route to THIS wrapper's PolishAsync
    // and silently bypass the inner engine's window-aware polish (a middle window would get
    // the whole-story closing-goodnight prompt).
    public async Task<string> PolishWindowAsync(ChildProfile profile, string windowText, string previousTail, bool isFinalWindow, CancellationToken ct)
        => await (await ResolveAsync(ct)).PolishWindowAsync(profile, windowText, previousTail, isFinalWindow, ct);

    public async Task<PosterSpec> GeneratePosterSpecAsync(StoryOutline outline, CancellationToken ct)
        => await (await ResolveAsync(ct)).GeneratePosterSpecAsync(outline, ct);

    public async Task<string> RepairPassageAsync(ChildProfile profile, string passage, string reason, CancellationToken ct)
        => await (await ResolveAsync(ct)).RepairPassageAsync(profile, passage, reason, ct);
}

#if ANDROID || IOS
/// <summary>Kokoro neural TTS when the model bundle resolves; platform TTS otherwise.
/// Android uses the managed sherpa wrapper (dynamic android-arm64 runtime); iOS uses the
/// static-linkage backend installed by Platforms/iOS/SherpaNativeIos.cs
/// (SherpaTtsEngine.NativeFactory, auto-registered via ModuleInitializer).</summary>
public sealed class ModelGatedTtsEngine(IModelProvider models, PlatformTtsEngine fallback, ILoggerFactory loggers) : ITtsEngine
{
    private ITtsEngine? _inner;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string Name => _inner?.Name ?? "auto";
    public bool HasPersonaVoices => _inner?.HasPersonaVoices ?? false;

    private async Task<ITtsEngine> ResolveAsync(CancellationToken ct)
    {
        if (_inner is not null) return _inner;
        await _gate.WaitAsync(ct);
        try
        {
            if (_inner is not null) return _inner;
            var availability = await models.GetAvailabilityAsync(ct);
            _inner = availability.TtsReady
                ? new Core.Engines.Sherpa.SherpaTtsEngine(models, loggers.CreateLogger<Core.Engines.Sherpa.SherpaTtsEngine>())
                : fallback;
            loggers.CreateLogger<ModelGatedTtsEngine>()
                .LogInformation("TTS engine resolved: {Engine}", _inner.Name);
            return _inner;
        }
        finally { _gate.Release(); }
    }

    public async Task<NarrationManifest> SynthesizeAsync(IReadOnlyList<string> sentences, NarratorVoice voice, string outputPath,
        IProgress<double>? progress, CancellationToken ct)
        => await (await ResolveAsync(ct)).SynthesizeAsync(sentences, voice, outputPath, progress, ct);

    public async Task<NarrationManifest> SynthesizeAsync(NarrationScript script, NarratorVoice voice, string outputPath,
        IProgress<double>? progress, CancellationToken ct)
        => await (await ResolveAsync(ct)).SynthesizeAsync(script, voice, outputPath, progress, ct);

    public async Task<string> RenderVoiceSampleAsync(NarratorVoice voice, CancellationToken ct)
        => await (await ResolveAsync(ct)).RenderVoiceSampleAsync(voice, ct);
}
#endif
