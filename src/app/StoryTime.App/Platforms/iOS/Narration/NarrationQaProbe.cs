using StoryTime.Contracts;
using StoryTime.Core.Abstractions;
using StoryTime.Core.Engines.Narration;
using StoryTime.Core.Engines.Narration.Dsp;

namespace StoryTime.App.Platforms.iOS.Narration;

/// <summary>
/// QA-only cold-start probe (STORYTIME_QA_NARRATION=1; SIMCTL_CHILD_ prefix on the iOS
/// simulator): renders the voice sample and a short two-paragraph script through the real
/// AppleSpeechTtsEngine — AVSpeechSynthesizer.WriteUtterance, buffer reading, the full
/// SoothingNarrator chain — and logs the measured result as "StoryNarration QA …". The
/// simulator has no Serena, so this proves the render path on the best installed voice.
/// Never runs without the environment variable.
/// </summary>
public static class NarrationQaProbe
{
    public const string Script =
        "The little boat bobbed at the edge of the quiet harbour. The lamps glowed like sleepy fireflies.\n\n" +
        "\"Are you ready?\" asked Pip. Mila nodded… and smiled.\n\n" +
        "And so the harbour fell asleep. Goodnight, Mila. Goodnight.";

    public static async Task RunIfRequestedAsync(IServiceProvider services)
    {
        if (Environment.GetEnvironmentVariable("STORYTIME_QA_NARRATION") != "1") return;
        try
        {
            var tts = services.GetRequiredService<ITtsEngine>();
            var voices = services.GetRequiredService<IStorytellerVoices>();
            Log($"voice={voices.Current?.Choice.VoiceId ?? "none"} meetsRequirement={voices.HasStorytellerVoice}");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var sample = await tts.RenderVoiceSampleAsync(NarratorVoice.Ember, CancellationToken.None);
            Log($"sample {sample} in {sw.ElapsedMilliseconds} ms; {Measure(sample)}");

            sw.Restart();
            var path = Path.Combine(FileSystem.CacheDirectory, "qa-narration.wav");
            var manifest = await tts.SynthesizeAsync(SentenceSplitter.SplitScript(Script), NarratorVoice.Ember, path, null, CancellationToken.None);
            Log($"script {path} {manifest.DurationSeconds:0.00}s in {sw.ElapsedMilliseconds} ms; " +
                $"{manifest.Timestamps.Count} timestamps; {Measure(path)}");
            foreach (var t in manifest.Timestamps) Log($"  sentence {t.Index}: {t.StartSeconds:0.000}–{t.EndSeconds:0.000}");
        }
        catch (Exception ex)
        {
            Log($"FAILED {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string Measure(string wav)
    {
        if (string.IsNullOrEmpty(wav) || !File.Exists(wav)) return "no file";
        var bytes = File.ReadAllBytes(wav);
        var rate = BitConverter.ToInt32(bytes, 24);
        var x = new float[(bytes.Length - 44) / 2];
        for (var i = 0; i < x.Length; i++) x[i] = BitConverter.ToInt16(bytes, 44 + 2 * i) / 32768f;
        return $"{rate} Hz, {LoudnessMeter.Measure(x, rate):0.00} LUFS, {TruePeakMeter.MeasureDbtp(x):0.00} dBTP";
    }

    private static void Log(string message) => Console.WriteLine($"StoryNarration QA {message}");
}
