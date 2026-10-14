using SherpaOnnx;

namespace StoryTime.Core.Engines.Sherpa;

/// <summary>
/// Everything SherpaTtsEngine needs to know to open a Kokoro bundle. Model file is
/// resolved by the engine: model.onnx (fp32, quality tier — default) is preferred over
/// model.int8.onnx (low-storage tier) when both exist in the directory.
/// </summary>
public sealed class SherpaTtsNativeOptions
{
    public required string ModelPath { get; init; }
    public required string VoicesPath { get; init; }
    public required string TokensPath { get; init; }
    public required string DataDir { get; init; }
    /// <summary>Optional lexicon (kokoro-multi-lang bundles: lexicon-us-en.txt). Empty for v0_19.</summary>
    public string LexiconPath { get; init; } = string.Empty;
    /// <summary>Language hint for multi-lang bundles ("en-us"); empty for v0_19.</summary>
    public string Lang { get; init; } = string.Empty;
    public int NumThreads { get; init; } = 2;
}

/// <summary>
/// Thin seam over the sherpa-onnx offline-TTS C API so platforms can swap the linkage:
/// desktop/Android use the managed NuGet wrapper (dynamic libs); iOS injects a
/// static-linkage P/Invoke binding via <see cref="SherpaTtsEngine.NativeFactory"/>.
/// </summary>
public interface ISherpaTtsNative : IDisposable
{
    int SampleRate { get; }
    int NumSpeakers { get; }
    /// <summary>Synthesize one chunk; returns mono float samples in [-1,1].</summary>
    float[] Generate(string text, float speed, int speakerId);
}

/// <summary>Default backend: the org.k2fsa.sherpa.onnx managed wrapper (loads the
/// dynamic sherpa-onnx-c-api library — osx-arm64 desktop, android-arm64).</summary>
internal sealed class ManagedSherpaTtsNative : ISherpaTtsNative
{
    private readonly OfflineTts _tts;

    public ManagedSherpaTtsNative(SherpaTtsNativeOptions o)
    {
        var config = new OfflineTtsConfig();
        config.Model.Kokoro.Model = o.ModelPath;
        config.Model.Kokoro.Voices = o.VoicesPath;
        config.Model.Kokoro.Tokens = o.TokensPath;
        config.Model.Kokoro.DataDir = o.DataDir;
        config.Model.Kokoro.Lexicon = o.LexiconPath;
        config.Model.Kokoro.Lang = o.Lang;
        config.Model.NumThreads = o.NumThreads;
        config.Model.Debug = 0;
        config.Model.Provider = "cpu";
        _tts = new OfflineTts(config);
    }

    public int SampleRate => _tts.SampleRate;
    public int NumSpeakers => _tts.NumSpeakers;

    public float[] Generate(string text, float speed, int speakerId)
    {
        var audio = _tts.Generate(text, speed, speakerId);
        var samples = audio.Samples;
        audio.Dispose(); // has Dispose() but does not implement IDisposable
        return samples;
    }

    public void Dispose() => _tts.Dispose();
}
