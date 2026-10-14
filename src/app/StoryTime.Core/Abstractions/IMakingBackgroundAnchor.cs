namespace StoryTime.Core.Abstractions;

/// <summary>Platform anchor that keeps a story run alive when the app leaves the foreground
/// (additive, background-generation pass 2026-07-31). The App-layer story host calls it on
/// story start/end. On iOS it arms the <c>MakingBackgroundCoordinator</c>: a
/// <c>BeginBackgroundTask</c> grace window on backgrounding (~30 s, measured in logs), then
/// a clean checkpoint, plus an opportunistic <c>BGProcessingTask</c> resume. Progress display
/// is NOT routed through this seam — subscribers read <see cref="IStoryMakingHost.ProgressChanged"/>.</summary>
public interface IMakingBackgroundAnchor
{
    /// <summary>A story run (fresh or resumed) just started; always called from the foreground.</summary>
    void MakingStarted(string childName);

    /// <summary>The story run ended — completed, failed, or cancelled. Idempotent.</summary>
    void MakingEnded();
}
