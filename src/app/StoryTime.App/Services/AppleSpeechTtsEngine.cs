using System.Security.Cryptography;
using System.Text;
using StoryTime.Contracts;
using StoryTime.Core.Abstractions;
using StoryTime.Core.Engines.Narration;
using StoryTime.App.Platforms.iOS.Narration;

namespace StoryTime.App.Services;

/// <summary>
/// "SoothingNarrator": on-device narration with Apple speech. Each utterance is written to PCM
/// by <see cref="AvSpeechUtteranceRenderer"/>, then Core's SoothingNarrator chain trims,
/// resamples, splices the measured pause map, masters, normalises to −18 LUFS and lays the pad
/// under it, producing a seekable 48 kHz WAV with per-sentence timestamps.
///
/// <para>There is ONE storyteller (founder decision 2026-10-14: Serena Premium). Every
/// <see cref="NarratorVoice"/> value narrates with it; the persona value is kept only so stored
/// profiles and stories stay readable. When no eligible English voice exists at all, narration
/// falls back to <see cref="PlatformTtsEngine"/> (live speech, no file) so a story still plays.</para>
/// </summary>
public sealed class AppleSpeechTtsEngine(IStorytellerVoices voices, AvSpeechUtteranceRenderer renderer, PlatformTtsEngine fallback)
    : ITtsEngine
{
    /// <summary>Bumped whenever the recipe or mastering changes, so cached samples re-render.</summary>
    public const int RecipeVersion = 3;

    public const string SampleText =
        "Once upon a time, in a quiet little harbour, the lamps glowed soft and gold. " +
        "\"Are you ready for a story?\" whispered the sea. And the little boat said, \"Yes, please.\"";

    public string Name => "AppleSpeech";

    /// <summary>The persona concept is retired: one storyteller narrates every story.</summary>
    public bool HasPersonaVoices => false;

    public Task<NarrationManifest> SynthesizeAsync(IReadOnlyList<string> sentences, NarratorVoice voice, string outputPath,
        IProgress<double>? progress, CancellationToken ct)
        => SynthesizeAsync(new NarrationScript { SentenceList = sentences.ToList(), ParagraphStartSet = [] }, voice, outputPath, progress, ct);

    public async Task<NarrationManifest> SynthesizeAsync(NarrationScript script, NarratorVoice voice, string outputPath,
        IProgress<double>? progress, CancellationToken ct)
    {
        var storyteller = voices.Current;
        if (storyteller is null)
        {
            Log("no eligible voice installed; falling back to live platform speech");
            return await ((ITtsEngine)fallback).SynthesizeAsync(script, voice, outputPath, progress, ct).ConfigureAwait(false);
        }
        var plan = SoothingPlanner.Plan(script);
        var result = await Narrator().RenderAsync(plan, storyteller.Choice, outputPath, progress, ct).ConfigureAwait(false);
        Log($"rendered {Path.GetFileName(outputPath)}: {result.Manifest.DurationSeconds:0.0}s, {plan.Segments.Count} utterances, " +
            $"voice={result.VoiceId} (fallbacks {result.VoiceFallbacks}), {result.IntegratedLufs:0.0} LUFS, limit {result.MaxLimitingDb:0.0} dB");
        return result.Manifest;
    }

    /// <summary>Short spoken sample through the FULL chain (pad included, with the sample
    /// profile's short lead), cached per voice and recipe version.</summary>
    public async Task<string> RenderVoiceSampleAsync(NarratorVoice voice, CancellationToken ct)
    {
        var storyteller = voices.Current;
        if (storyteller is null) return await fallback.RenderVoiceSampleAsync(voice, ct).ConfigureAwait(false);

        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{storyteller.Choice.VoiceId}|{RecipeVersion}|{SampleText}")))[..16];
        var dir = Path.Combine(FileSystem.CacheDirectory, "voice-samples");
        var path = Path.Combine(dir, $"sample-{key}.wav");
        if (File.Exists(path)) return path;

        Directory.CreateDirectory(dir);
        var split = SentenceSplitter.SplitScript(SampleText);
        var script = new NarrationScript { SentenceList = split.SentenceList, ParagraphStartSet = split.ParagraphStartSet, IsExcerpt = true };
        var plan = SoothingPlanner.Plan(script, SoothingOptions.Sample);
        var tmp = path + ".tmp.wav";
        await Narrator().RenderAsync(plan, storyteller.Choice, tmp, null, ct).ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    private SoothingNarrator Narrator() => new(renderer)
    {
        Trace = (phase, elapsed) => Log($"phase {phase}: {elapsed.TotalMilliseconds:0} ms"),
    };

    private static void Log(string message) => Console.WriteLine($"StoryNarration {message}");
}
