using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using StoryTime.Core.Engines.Sherpa;

namespace StoryTime.App.Platforms.iOS;

/// <summary>
/// iOS static-linkage backend for SherpaTtsEngine. The managed org.k2fsa.sherpa.onnx
/// wrapper P/Invokes the DYNAMIC "sherpa-onnx-c-api" library, which does not exist on
/// iOS — here sherpa-onnx is linked STATICALLY (Native.props: sherpa-onnx.xcframework +
/// onnxruntime.xcframework, v1.13.4 prebuilt release artifacts), so this file carries a
/// thin DllImport("__Internal") binding of the offline-TTS C API (6 functions) plus the
/// exact v1.13.4 SherpaOnnxOfflineTtsConfig struct layout (vendored header:
/// sherpa-onnx.xcframework/*/Headers/c-api.h — all seven model-family sub-structs must
/// be present because the config is passed BY VALUE layout; do not remove "unused" ones).
///
/// A ModuleInitializer installs <see cref="Create"/> as SherpaTtsEngine.NativeFactory,
/// mirroring the LlamaNativeResolver auto-registration pattern — no MauiProgram wiring.
/// </summary>
public static class SherpaNativeIos
{
    [ModuleInitializer]
    internal static void AutoRegister() => SherpaTtsEngine.NativeFactory = Create;

    public static ISherpaTtsNative Create(SherpaTtsNativeOptions options) => new IosSherpaTtsNative(options);

    // ---------------- struct layout (c-api.h @ v1.13.4 — verbatim field order) ----------------

    [StructLayout(LayoutKind.Sequential)]
    internal struct VitsConfig
    {
        public IntPtr Model, Lexicon, Tokens, DataDir;
        public float NoiseScale, NoiseScaleW, LengthScale;
        public IntPtr DictDir;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MatchaConfig
    {
        public IntPtr AcousticModel, Vocoder, Lexicon, Tokens, DataDir;
        public float NoiseScale, LengthScale;
        public IntPtr DictDir;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KokoroConfig
    {
        public IntPtr Model, Voices, Tokens, DataDir;
        public float LengthScale;
        public IntPtr DictDir, Lexicon, Lang;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KittenConfig
    {
        public IntPtr Model, Voices, Tokens, DataDir;
        public float LengthScale;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ZipvoiceConfig
    {
        public IntPtr Tokens, Encoder, Decoder, Vocoder, DataDir, Lexicon;
        public float FeatScale, TShift, TargetRms, GuidanceScale;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PocketConfig
    {
        public IntPtr LmFlow, LmMain, Encoder, Decoder, TextConditioner, VocabJson, TokenScoresJson;
        public int VoiceEmbeddingCacheCapacity;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SupertonicConfig
    {
        public IntPtr DurationPredictor, TextEncoder, VectorEstimator, Vocoder, TtsJson, UnicodeIndexer, VoiceStyle;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ModelConfig
    {
        public VitsConfig Vits;
        public int NumThreads;
        public int Debug;
        public IntPtr Provider;
        public MatchaConfig Matcha;
        public KokoroConfig Kokoro;
        public KittenConfig Kitten;
        public ZipvoiceConfig Zipvoice;
        public PocketConfig Pocket;
        public SupertonicConfig Supertonic;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TtsConfig
    {
        public ModelConfig Model;
        public IntPtr RuleFsts;
        public int MaxNumSentences;
        public IntPtr RuleFars;
        public float SilenceScale;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GeneratedAudio
    {
        public IntPtr Samples; // const float*
        public int N;
        public int SampleRate;
    }

    // ---------------- the six offline-TTS C entry points ----------------

    [DllImport("__Internal", EntryPoint = "SherpaOnnxCreateOfflineTts")]
    internal static extern IntPtr CreateOfflineTts(ref TtsConfig config);

    [DllImport("__Internal", EntryPoint = "SherpaOnnxDestroyOfflineTts")]
    internal static extern void DestroyOfflineTts(IntPtr tts);

    [DllImport("__Internal", EntryPoint = "SherpaOnnxOfflineTtsSampleRate")]
    internal static extern int OfflineTtsSampleRate(IntPtr tts);

    [DllImport("__Internal", EntryPoint = "SherpaOnnxOfflineTtsNumSpeakers")]
    internal static extern int OfflineTtsNumSpeakers(IntPtr tts);

    [DllImport("__Internal", EntryPoint = "SherpaOnnxOfflineTtsGenerate")]
    internal static extern IntPtr OfflineTtsGenerate(IntPtr tts, IntPtr utf8Text, int sid, float speed);

    [DllImport("__Internal", EntryPoint = "SherpaOnnxDestroyOfflineTtsGeneratedAudio")]
    internal static extern void DestroyGeneratedAudio(IntPtr audio);
}

/// <summary>ISherpaTtsNative over the statically-linked sherpa-onnx C API.</summary>
internal sealed class IosSherpaTtsNative : ISherpaTtsNative
{
    private IntPtr _tts;

    public IosSherpaTtsNative(SherpaTtsNativeOptions o)
    {
        var strings = new List<IntPtr>();
        IntPtr U(string s)
        {
            var p = Marshal.StringToCoTaskMemUTF8(s);
            strings.Add(p);
            return p;
        }
        try
        {
            var config = default(SherpaNativeIos.TtsConfig); // all-zero = C memset pattern
            config.Model.Kokoro.Model = U(o.ModelPath);
            config.Model.Kokoro.Voices = U(o.VoicesPath);
            config.Model.Kokoro.Tokens = U(o.TokensPath);
            config.Model.Kokoro.DataDir = U(o.DataDir);
            config.Model.Kokoro.LengthScale = 1.0f;
            config.Model.Kokoro.DictDir = U(string.Empty);
            config.Model.Kokoro.Lexicon = U(o.LexiconPath);
            config.Model.Kokoro.Lang = U(o.Lang);
            config.Model.NumThreads = o.NumThreads;
            config.Model.Debug = 0;
            config.Model.Provider = U("cpu");
            config.MaxNumSentences = 1;

            _tts = SherpaNativeIos.CreateOfflineTts(ref config);
            if (_tts == IntPtr.Zero)
                throw new InvalidOperationException($"SherpaOnnxCreateOfflineTts failed for {o.ModelPath}");
        }
        finally
        {
            foreach (var p in strings) Marshal.FreeCoTaskMem(p);
        }
    }

    public int SampleRate => SherpaNativeIos.OfflineTtsSampleRate(_tts);
    public int NumSpeakers => SherpaNativeIos.OfflineTtsNumSpeakers(_tts);

    public float[] Generate(string text, float speed, int speakerId)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(text);
        try
        {
            var handle = SherpaNativeIos.OfflineTtsGenerate(_tts, utf8, speakerId, speed);
            if (handle == IntPtr.Zero) return [];
            try
            {
                var audio = Marshal.PtrToStructure<SherpaNativeIos.GeneratedAudio>(handle);
                var samples = new float[audio.N];
                if (audio.N > 0) Marshal.Copy(audio.Samples, samples, 0, audio.N);
                return samples;
            }
            finally
            {
                SherpaNativeIos.DestroyGeneratedAudio(handle);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    public void Dispose()
    {
        if (_tts != IntPtr.Zero)
        {
            SherpaNativeIos.DestroyOfflineTts(_tts);
            _tts = IntPtr.Zero;
        }
    }
}
