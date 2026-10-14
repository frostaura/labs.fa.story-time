#if DEBUG
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using StoryTime.Contracts;
using StoryTime.Core.Engines.Sherpa;

namespace StoryTime.App.Platforms.iOS;

/// <summary>
/// Headless proof that neural TTS works on iOS without any UI/DI involvement, following
/// the established sim QA pattern (env var via SIMCTL_CHILD_*). Launch with
/// STORYTIME_QA_TTS=1: after startup this synthesizes a two-sentence line through the
/// full SherpaTtsEngine (static sherpa-onnx + Kokoro) and writes qa-tts.wav +
/// qa-tts.json into Library/ (the app data container), then leaves the app running.
/// Debug-only; inert unless the env var is set.
/// </summary>
public static class SherpaQaSelfTest
{
    [ModuleInitializer]
    internal static void AutoRegister()
    {
        if (Environment.GetEnvironmentVariable("STORYTIME_QA_TTS") != "1") return;
        Task.Run(RunAsync);
    }

    private static async Task RunAsync()
    {
        var lib = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var wavPath = Path.Combine(lib, "qa-tts.wav");
        var reportPath = Path.Combine(lib, "qa-tts.json");
        try
        {
            var provider = new IosModelProvider();
            using var engine = new SherpaTtsEngine(provider);
            var script = SentenceSplitter.SplitScript(
                "Hello little dreamer, this is Ember speaking from your iPhone. \n\nEvery word of this sentence was woven on the device itself.");
            var sw = Stopwatch.StartNew();
            var manifest = await engine.SynthesizeAsync(script, NarratorVoice.Ember, wavPath, null, CancellationToken.None);
            sw.Stop();
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new
            {
                ok = true,
                engine = engine.Name,
                durationSeconds = manifest.DurationSeconds,
                wallSeconds = sw.Elapsed.TotalSeconds,
                rtf = sw.Elapsed.TotalSeconds / Math.Max(manifest.DurationSeconds, 0.01),
                sentences = manifest.Timestamps.Count,
                wav = manifest.AudioPath,
            }));
            Console.WriteLine($"[qa-tts] OK: {manifest.DurationSeconds:F1}s audio in {sw.Elapsed.TotalSeconds:F1}s → {wavPath}");
        }
        catch (Exception ex)
        {
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new { ok = false, error = ex.ToString() }));
            Console.WriteLine($"[qa-tts] FAILED: {ex}");
        }
    }
}
#endif
