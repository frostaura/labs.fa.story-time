using StoryTime.Contracts;

namespace StoryTime.Core.Abstractions;

/// <summary>Hosts a StoryPipeline run on a background task that survives page navigation.
/// App implements (WeaveHostService) and marshals events to the UI thread.
/// (Additive Phase B abstraction.)</summary>
public interface IWeaveHost
{
    bool IsWeaving { get; }
    WeaveProgress? CurrentProgress { get; }
    /// <summary>The story produced by the most recent completed weave, if any.</summary>
    StoryRecord? LastCompleted { get; }
    /// <summary>Gentle failure message when the last weave failed; null otherwise.</summary>
    string? LastFailureMessage { get; }

    event EventHandler<WeaveProgress>? ProgressChanged;
    event EventHandler<StoryRecord>? WeaveCompleted;
    event EventHandler<string>? WeaveFailed;

    /// <summary>Starts a weave for the profile. No-op if one is already running.</summary>
    Task StartAsync(ChildProfile profile);
    /// <summary>Resumes a checkpointed weave if one is pending (called at app start).</summary>
    Task TryResumeAsync();
    Task CancelAsync();

    /// <summary>Founder cancel: stops the weave AND discards the half-woven story, its files
    /// and the checkpoint — the loom is immediately free for a new story. Raises
    /// <see cref="WeaveCancelled"/> when done. (CancelAsync keeps the checkpoint — that path
    /// is the iOS background-suspend, not a user cancel.)</summary>
    Task CancelAndDiscardAsync();

    event EventHandler? WeaveCancelled;
}
