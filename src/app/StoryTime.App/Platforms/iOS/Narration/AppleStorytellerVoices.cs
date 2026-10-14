using AVFoundation;
using Foundation;
using StoryTime.Core.Abstractions;
using StoryTime.Core.Engines.Narration;
using UIKit;

namespace StoryTime.App.Platforms.iOS.Narration;

/// <summary>
/// The installed system voices, ranked by Core's <see cref="VoiceRanker"/> and checked
/// against the storyteller requirement (Serena Premium). Re-checks on
/// <c>AVSpeechSynthesisVoice.AvailableVoicesDidChangeNotification</c> and whenever the app
/// returns to the foreground (the parent comes back from Settings), persists the chosen
/// identifier, and re-validates it at launch. Never hard-codes identifiers.
/// </summary>
public sealed class AppleStorytellerVoices : IStorytellerVoices, IDisposable
{
    private const string PreferenceKey = "narration.storyteller.voice-id";
    private readonly Lock _gate = new();
    private readonly NSObject _voicesObserver;
    private readonly NSObject _foregroundObserver;
    private StorytellerVoice? _current;

    public AppleStorytellerVoices()
    {
        Refresh();
        _voicesObserver = AVSpeechSynthesisVoice.Notifications.ObserveAvailableVoicesDidChange((_, _) => Refresh());
        _foregroundObserver = UIApplication.Notifications.ObserveWillEnterForeground((_, _) => Refresh());
    }

    public StorytellerVoice? Current
    {
        get { lock (_gate) return _current; }
    }

    public event EventHandler? Changed;

    public void Refresh()
    {
        StorytellerVoice? next;
        try
        {
            var installed = AVSpeechSynthesisVoice.GetSpeechVoices().Select(Map).ToList();
            var region = NSLocale.CurrentLocale.CountryCode;
            var policy = new VoiceLanguagePolicy("en", string.IsNullOrEmpty(region) ? null : region);
            var persisted = Preferences.Default.Get<string?>(PreferenceKey, null);
            next = VoiceRanker.Revalidate(persisted, installed, policy);
            if (next is { MeetsRequirement: true }) Preferences.Default.Set(PreferenceKey, next.Choice.VoiceId);
            Console.WriteLine($"StoryNarration voices: {installed.Count} installed, storyteller={next?.Choice.VoiceId ?? "none"} " +
                              $"ready={next?.MeetsRequirement ?? false} best={next?.BestTier}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"StoryNarration voices: enumeration failed: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        bool changed;
        lock (_gate)
        {
            changed = _current?.Choice.VoiceId != next?.Choice.VoiceId
                      || _current?.MeetsRequirement != next?.MeetsRequirement
                      || _current?.BestTier != next?.BestTier;
            _current = next;
        }
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    private static InstalledVoice Map(AVSpeechSynthesisVoice v) => new(
        v.Identifier,
        v.Name,
        v.Language,
        v.Quality switch
        {
            AVSpeechSynthesisVoiceQuality.Premium => VoiceQualityTier.Premium,
            AVSpeechSynthesisVoiceQuality.Enhanced => VoiceQualityTier.Enhanced,
            _ => VoiceQualityTier.Default,
        },
        v.Gender switch
        {
            AVSpeechSynthesisVoiceGender.Male => VoiceGender.Male,
            AVSpeechSynthesisVoiceGender.Female => VoiceGender.Female,
            _ => VoiceGender.Unspecified,
        },
        v.VoiceTraits.HasFlag(AVSpeechSynthesisVoiceTraits.IsNoveltyVoice),
        v.VoiceTraits.HasFlag(AVSpeechSynthesisVoiceTraits.IsPersonalVoice));

    public void Dispose()
    {
        _voicesObserver.Dispose();
        _foregroundObserver.Dispose();
    }
}
