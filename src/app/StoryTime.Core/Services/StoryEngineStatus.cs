using CommunityToolkit.Mvvm.ComponentModel;
using StoryTime.Core.Engines.AppleFoundation;

namespace StoryTime.Core.Services;

/// <summary>Which story engine is in charge and why, for UI guidance. Apple's on-device model
/// writes brand-new stories when it is available; otherwise StoryTime uses its built-in
/// template collection. Guidance is shown only when the parent can change the outcome —
/// Apple Intelligence switched off, or the model still downloading (recipe rule 6).</summary>
public interface IStoryEngineStatus
{
    FoundationModelsAvailability Availability { get; }
    /// <summary>True when the parent can act (turn Apple Intelligence on, or wait for it).</summary>
    bool ShowGuidance { get; }
    /// <summary>Plain-words guidance for a parent; empty when none applies.</summary>
    string GuidanceText { get; }
    /// <summary>Re-reads availability (call on appearing and on app resume).</summary>
    void Refresh();
}

public sealed class StoryEngineStatus : ObservableObject, IStoryEngineStatus
{
    public const string NotEnabledText =
        "Turn on Apple Intelligence in Settings › Apple Intelligence & Siri to create brand-new stories. " +
        "Until then StoryTime uses its built-in story collection.";

    public const string ModelNotReadyText =
        "Apple Intelligence is still getting ready on this iPhone. " +
        "Until it finishes, StoryTime uses its built-in story collection.";

    private readonly Func<FoundationModelsAvailability> _probe;

    public StoryEngineStatus(Func<FoundationModelsAvailability> probe)
    {
        _probe = probe;
        _availability = probe();
    }

    private FoundationModelsAvailability _availability;

    public FoundationModelsAvailability Availability
    {
        get => _availability;
        private set
        {
            if (!SetProperty(ref _availability, value)) return;
            OnPropertyChanged(nameof(ShowGuidance));
            OnPropertyChanged(nameof(GuidanceText));
        }
    }

    public bool ShowGuidance => GuidanceText.Length > 0;

    public string GuidanceText => Availability.Reason switch
    {
        FmUnavailableReason.AppleIntelligenceNotEnabled => NotEnabledText,
        FmUnavailableReason.ModelNotReady => ModelNotReadyText,
        _ => string.Empty,
    };

    public void Refresh() => Availability = _probe();
}
