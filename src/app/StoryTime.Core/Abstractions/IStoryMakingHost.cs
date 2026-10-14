using StoryTime.Contracts;

namespace StoryTime.Core.Abstractions;

/// <summary>Hosts a StoryPipeline run on a background task that survives page navigation.
/// App implements (StoryMakingHost) and marshals events to the UI thread.
/// (Additive Phase B abstraction.)</summary>
public interface IStoryMakingHost
{
    bool IsMaking { get; }
    MakingProgress? CurrentProgress { get; }
    /// <summary>The story produced by the most recent completed story run, if any.</summary>
    StoryRecord? LastCompleted { get; }
    /// <summary>Gentle failure message when the last story run failed; null otherwise.</summary>
    string? LastFailureMessage { get; }

    event EventHandler<MakingProgress>? ProgressChanged;
    event EventHandler<StoryRecord>? MakingCompleted;
    event EventHandler<string>? MakingFailed;

    /// <summary>Starts a story run for the profile. No-op if one is already running.</summary>
    Task StartAsync(ChildProfile profile);
    /// <summary>Resumes a checkpointed story run if one is pending (called at app start).</summary>
    Task TryResumeAsync();
    Task CancelAsync();

    /// <summary>Founder cancel: stops the story run AND discards the unfinished story, its files
    /// and the checkpoint — the host is immediately free for a new story. Raises
    /// <see cref="MakingCancelled"/> when done. (CancelAsync keeps the checkpoint — that path
    /// is the iOS background-suspend, not a user cancel.)</summary>
    Task CancelAndDiscardAsync();

    event EventHandler? MakingCancelled;
}
