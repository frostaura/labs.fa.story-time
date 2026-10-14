using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StoryTime.Contracts;
using StoryTime.Core.Abstractions;

namespace StoryTime.Core.Engines.Sherpa;

/// <summary>
/// Neural persona TTS: sherpa-onnx + Kokoro (kokoro-multi-lang-v1_0; falls back to any
/// bundle laid out the same way, incl. the int8 low-storage tier and legacy v0_19).
/// Narration is assembled prosody-aware: per-sentence synthesis with content-aware,
/// per-gap silences (ProsodyDirector — punctuation class × narrative-context modifiers,
/// deterministic), gentle deterministic speed variation, cozy mastering per chunk
/// (AudioShaper.MasterChunk: duration-compensated pitch-down + shelf EQ), edge-silence
/// trimming + fades per chunk (AudioShaper), and whole-story RMS-targeted loudness
/// normalization under a −6 dBFS bedtime ceiling (legacy −1 dBFS peak when the cozy
/// chain is off).
/// SentenceTimestamps are accumulated over the real timeline including the varied gaps,
/// so player seek positions stay exact.
///
/// Speaker id mapping — kokoro-multi-lang-v1_0 (53 voices; docs table verified 2026-07-30):
///   Ember  (warm &amp; unhurried) = af_aoede   = sid 1   (PROVISIONAL pending founder audition,
///     voice-character pass 2026-07-31: founder heard af_heart as "too loud, too high-pitched,
///     ending sentences on a high note"; af_aoede measured warmest of 9 candidates — spectral
///     centroid 1094 Hz vs af_heart 1692, median F0 174.5 vs 196.7 Hz, sentence endings
///     −6.1 st below sentence mean vs −3.6, voiced fraction 90%. Audition set:
///     docs/design/qa-audio/audition-*.wav; revert = sid 3 af_heart.)
///   Willow (soft &amp; bright)    = af_bella   = sid 2   (grade A-)
///   Sage   (deep &amp; gentle)    = am_michael = sid 16
/// Legacy kokoro-en-v0_19 (11 voices) ids differ: Ember=af_bella(1), Willow=af_sarah(3),
/// Sage=am_michael(6) — resolved at load time from the bundle's voice count.
///
/// The native linkage is swappable via <see cref="NativeFactory"/>: desktop/Android use the
/// managed NuGet wrapper; the iOS head installs a static-linkage P/Invoke backend from a
/// ModuleInitializer (Platforms/iOS/SherpaNativeIos.cs).
/// </summary>
public sealed class SherpaTtsEngine : ITtsEngine, IDisposable
{
    /// <summary>Platform hook: replace the native backend (set before first synthesis;
    /// iOS installs its static-linkage binding here). Null → managed NuGet wrapper.</summary>
    public static Func<SherpaTtsNativeOptions, ISherpaTtsNative>? NativeFactory;

    /// <summary>Bump when voices/prosody change so cached narrator-picker samples refresh.</summary>
    public const int VoiceSampleVersion = 4; // v4: cozy mastering chain + Ember speaker audition

    /// <summary>Audition hook (harness `tts --sid`): raw Kokoro speaker id used for every
    /// voice when set. Null → the persona map. Never set by the app heads.</summary>
    public int? SpeakerIdOverride { get; set; }

    private readonly IModelProvider _models;
    private readonly ILogger<SherpaTtsEngine> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ISherpaTtsNative? _tts;
    private bool _legacyVoiceMap; // kokoro-en-v0_19 (11 speakers) id table

    public SherpaTtsEngine(IModelProvider models, ILogger<SherpaTtsEngine>? log = null)
    {
        _models = models;
        _log = log ?? NullLogger<SherpaTtsEngine>.Instance;
    }

    public string Name => "sherpa-kokoro";
    public bool HasPersonaVoices => true;

    /// <summary>Speaker ids for kokoro-multi-lang-v1_0 (default) and legacy v0_19 bundles.</summary>
    public static int SpeakerId(NarratorVoice voice, bool legacyV019 = false) => legacyV019
        ? voice switch
        {
            NarratorVoice.Ember => 1,  // af_bella
            NarratorVoice.Willow => 3, // af_sarah
            NarratorVoice.Sage => 6,   // am_michael
            _ => 1,
        }
        : voice switch
        {
            NarratorVoice.Ember => 1,  // af_aoede — provisional (founder audition pending); af_heart=3 to revert
            NarratorVoice.Willow => 2, // af_bella
            NarratorVoice.Sage => 16,  // am_michael
            _ => 1,
        };

    private async Task<ISherpaTtsNative> GetTtsAsync(CancellationToken ct)
    {
        if (_tts is not null) return _tts;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_tts is not null) return _tts;
            var availability = await _models.GetAvailabilityAsync(ct).ConfigureAwait(false);
            if (!availability.TtsReady || availability.TtsDirectory is null)
                throw new InvalidOperationException("SherpaTtsEngine selected but TTS model is not available (IModelProvider.TtsReady == false)");
            var dir = availability.TtsDirectory;

            // Quality tier (fp32 model.onnx) preferred; int8 is the low-storage tier.
            var fp32 = Path.Combine(dir, "model.onnx");
            var int8 = Path.Combine(dir, "model.int8.onnx");
            var modelPath = File.Exists(fp32) ? fp32 : int8;
            var lexicon = Path.Combine(dir, "lexicon-us-en.txt");
            var options = new SherpaTtsNativeOptions
            {
                ModelPath = modelPath,
                VoicesPath = Path.Combine(dir, "voices.bin"),
                TokensPath = Path.Combine(dir, "tokens.txt"),
                DataDir = Path.Combine(dir, "espeak-ng-data"),
                LexiconPath = File.Exists(lexicon) ? lexicon : string.Empty,
                Lang = File.Exists(lexicon) ? "en-us" : string.Empty,
                NumThreads = Math.Min(4, Environment.ProcessorCount),
            };

            _log.LogInformation("Loading Kokoro TTS: {Model} (backend: {Backend})",
                modelPath, NativeFactory is null ? "managed" : "platform");
            var factory = NativeFactory ?? (o => new ManagedSherpaTtsNative(o));
            _tts = await Task.Run(() => factory(options), ct).ConfigureAwait(false);
            _legacyVoiceMap = _tts.NumSpeakers > 0 && _tts.NumSpeakers <= 11;
            _log.LogInformation("Kokoro ready: {Rate} Hz, {Speakers} speakers{Legacy}",
                _tts.SampleRate, _tts.NumSpeakers, _legacyVoiceMap ? " (legacy v0_19 voice map)" : "");
            return _tts;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<NarrationManifest> SynthesizeAsync(IReadOnlyList<string> sentences, NarratorVoice voice, string outputPath,
        IProgress<double>? progress, CancellationToken ct)
        => SynthesizeAsync(new NarrationScript
        {
            SentenceList = sentences.ToList(),
            ParagraphStartSet = [],
        }, voice, outputPath, progress, ct);

    public async Task<NarrationManifest> SynthesizeAsync(NarrationScript script, NarratorVoice voice, string outputPath,
        IProgress<double>? progress, CancellationToken ct)
    {
        var tts = await GetTtsAsync(ct).ConfigureAwait(false);
        var sid = SpeakerIdOverride ?? SpeakerId(voice, _legacyVoiceMap);
        var sentences = script.SentenceList;

        // The whole gap plan is computed up front (pure function of the script) so the
        // harness report and any replay recompute byte-identical timelines.
        var gapPlan = ProsodyDirector.GapSecondsByIndex(script);

        return await Task.Run(() =>
        {
            var sampleRate = tts.SampleRate; // 24000 for kokoro
            var chunks = new List<float[]?>(sentences.Count * 2); // null → silence gap
            var gapSamples = new List<int>(sentences.Count * 2);  // parallel: length when null
            var timestamps = new List<SentenceTimestamp>(sentences.Count);
            long cursorSamples = 0;

            void AddGap(double seconds)
            {
                var n = (int)(seconds * sampleRate);
                if (n <= 0) return;
                chunks.Add(null);
                gapSamples.Add(n);
                cursorSamples += n;
            }

            // Overlapped-pipeline edges: an optional leading beat (inter-window paragraph
            // pause carried at the head of windows ≥ 1) — included in the first sentence's
            // start time so concatenated timestamps stay exact.
            if (script.LeadingSilenceSeconds > 0) AddGap(script.LeadingSilenceSeconds);

            var lastSpokenIndex = -1;
            for (var i = 0; i < sentences.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var sentence = sentences[i].Trim();
                if (sentence.Length == 0) continue;

                if (lastSpokenIndex >= 0 && gapPlan.TryGetValue(i, out var gapSeconds))
                    AddGap(gapSeconds);

                // RenderSpeed pre-compensates the cozy pitch-down resample so net pacing
                // stays at the ProsodyDirector speed (±2%).
                var speed = AudioShaper.RenderSpeed(ProsodyDirector.SpeedFor(i));
                var start = cursorSamples / (double)sampleRate;
                var clauses = ProsodyDirector.ChunkSentence(sentence);
                for (var c = 0; c < clauses.Count; c++)
                {
                    if (c > 0) AddGap(ProsodyDirector.CommaPauseSeconds);
                    var shaped = AudioShaper.ShapeChunk(
                        AudioShaper.MasterChunk(tts.Generate(clauses[c], speed, sid), sampleRate), sampleRate);
                    if (shaped.Length == 0) continue;
                    chunks.Add(shaped);
                    gapSamples.Add(0);
                    cursorSamples += shaped.Length;
                }
                var end = cursorSamples / (double)sampleRate;
                timestamps.Add(new SentenceTimestamp { Index = i, StartSeconds = start, EndSeconds = end });
                lastSpokenIndex = i;
                progress?.Report((i + 1) / (double)sentences.Count);
            }
            if (script.AppendFinalTail) AddGap(ProsodyDirector.FinalTailSeconds);

            // Whole-story loudness: RMS-targeted with the −6 dBFS bedtime ceiling (cozy
            // chain on) or legacy −1 dBFS peak normalization (off). One PCM pass either way.
            var gain = AudioShaper.StoryGain(chunks);
            var pcmChunks = new List<short[]>(chunks.Count);
            var gapIdx = 0;
            foreach (var chunk in chunks)
            {
                var gap = gapSamples[gapIdx++];
                pcmChunks.Add(chunk is null ? new short[gap] : ToPcm16(chunk, gain));
            }

            WavAudio.Write(outputPath, pcmChunks, sampleRate);
            var duration = cursorSamples / (double)sampleRate;
            _log.LogInformation("TTS wrote {Path}: {Sentences} sentences, {Duration:F1}s (gain {Gain:F2})",
                outputPath, timestamps.Count, duration, gain);

            return new NarrationManifest
            {
                AudioPath = outputPath,
                DurationSeconds = duration,
                Timestamps = timestamps,
            };
        }, ct).ConfigureAwait(false);
    }

    private static short[] ToPcm16(float[] samples, float gain)
    {
        var pcm = new short[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            var v = Math.Clamp(samples[i] * gain, -1f, 1f);
            pcm[i] = (short)Math.Round(v * short.MaxValue);
        }
        return pcm;
    }

    public async Task<string> RenderVoiceSampleAsync(NarratorVoice voice, CancellationToken ct)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StoryTime", "voice-samples");
        var path = Path.Combine(dir, $"{voice.ToString().ToLowerInvariant()}-v{VoiceSampleVersion}.wav");
        if (File.Exists(path))
        {
            Console.WriteLine($"TWVoice sherpa: cache hit {path}");
            return path;
        }
        Console.WriteLine($"TWVoice sherpa: rendering {voice} → {path}");

        var line = voice switch
        {
            NarratorVoice.Ember => "Hello little dreamer, I'm Ember. Snuggle in, and I'll wrap tonight's story around you like a warm blanket.",
            NarratorVoice.Willow => "Hi there, I'm Willow. Let's drift through a gentle story together, soft as leaves in the evening breeze.",
            _ => "Good evening, I'm Sage. Settle down deep in your pillow, and I'll tell you a calm and cozy tale.",
        };
        var manifest = await SynthesizeAsync(SentenceSplitter.SplitScript(line), voice, path, null, ct).ConfigureAwait(false);
        Console.WriteLine($"TWVoice sherpa: rendered {voice} → '{manifest.AudioPath}' " +
                          $"exists={(!string.IsNullOrEmpty(manifest.AudioPath) && File.Exists(manifest.AudioPath))}");
        return manifest.AudioPath;
    }

    public void Dispose()
    {
        _tts?.Dispose();
        _tts = null;
        _gate.Dispose();
    }
}
