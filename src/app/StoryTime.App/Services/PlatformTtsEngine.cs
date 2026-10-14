using StoryTime.Contracts;
using StoryTime.Core.Abstractions;

namespace StoryTime.App.Services;

/// <summary>Honest degraded TTS mode until the AVSpeechSynthesizer narration engine lands
/// (swap point: MauiProgram.RegisterTtsEngine). MAUI TextToSpeech can only speak live — it cannot render audio to a file — so
/// "synthesis" here produces NO audio file: it returns a manifest of sentences with
/// estimated durations (words / 2.6 per second), and the Player runs in live-TTS mode
/// (LiveTtsNarrationPlayer drives SpeakAsync sentence-by-sentence). Voices are the closest
/// platform voices, not the Ember/Willow/Sage personas (HasPersonaVoices = false).</summary>
public sealed class PlatformTtsEngine : ITtsEngine
{
    public const double WordsPerSecond = 2.6;

    private Locale? _preferredLocale;
    private bool _localeResolved;

    public string Name => "PlatformTts";
    public bool HasPersonaVoices => false;

    public async Task<NarrationManifest> SynthesizeAsync(IReadOnlyList<string> sentences, NarratorVoice voice,
        string outputPath, IProgress<double>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // No file is produced — estimate a timestamp map instead (live playback mode).
        var timestamps = new List<SentenceTimestamp>(sentences.Count);
        double t = 0;
        for (var i = 0; i < sentences.Count; i++)
        {
            var words = sentences[i].Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            var duration = Math.Max(0.8, words / WordsPerSecond);
            timestamps.Add(new SentenceTimestamp { Index = i, StartSeconds = t, EndSeconds = t + duration });
            t += duration;
            progress?.Report((i + 1) / (double)sentences.Count);
        }
        await Task.Yield();
        return new NarrationManifest
        {
            AudioPath = string.Empty,       // empty → Player selects live-TTS mode
            TeaserPath = string.Empty,
            DurationSeconds = t,
            Timestamps = timestamps,
        };
    }

    public async Task<string> RenderVoiceSampleAsync(NarratorVoice voice, CancellationToken ct)
    {
        var sample = voice switch
        {
            NarratorVoice.Ember => "Hello, little dreamer. I'm Ember, and I'll keep the fire warm.",
            NarratorVoice.Willow => "Hi there! I'm Willow, and I know where the stars sleep.",
            _ => "Good evening. I am Sage. Let's find a quiet story together.",
        };
        await TextToSpeech.SpeakAsync(sample, await OptionsForAsync(voice), ct);
        return string.Empty;
    }

    /// <summary>Closest platform voice per persona: en locale preferred; pitch shapes the
    /// persona (Ember 1.0, Willow 1.15, Sage 0.85).</summary>
    public async Task<SpeechOptions> OptionsForAsync(NarratorVoice voice)
    {
        if (!_localeResolved)
        {
            try
            {
                var locales = await TextToSpeech.GetLocalesAsync();
                _preferredLocale =
                    locales.FirstOrDefault(l => l.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase)
                                                 && l.Country?.Equals("GB", StringComparison.OrdinalIgnoreCase) == true)
                    ?? locales.FirstOrDefault(l => l.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception)
            {
                _preferredLocale = null; // platform default
            }
            _localeResolved = true;
        }

        return new SpeechOptions
        {
            Locale = _preferredLocale,
            Pitch = voice switch
            {
                NarratorVoice.Willow => 1.15f,
                NarratorVoice.Sage => 0.85f,
                _ => 1.0f,
            },
            Volume = 1.0f,
        };
    }
}
