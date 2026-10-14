namespace StoryTime.Core.Abstractions;

/// <summary>Platform anchor that keeps a weave alive when the app leaves the foreground
/// (additive, background-generation pass 2026-07-31). The App-layer weave host calls it on
/// weave start/end. On iOS it arms the <c>WeaveBackgroundCoordinator</c>: a
/// <c>BeginBackgroundTask</c> grace window on backgrounding (~30 s, measured in logs), then
/// a clean checkpoint, plus an opportunistic <c>BGProcessingTask</c> resume. Progress display
/// is NOT routed through this seam — subscribers read <see cref="IWeaveHost.ProgressChanged"/>.</summary>
public interface IWeaveBackgroundAnchor
{
    /// <summary>A weave (fresh or resumed) just started; always called from the foreground.</summary>
    void WeaveStarted(string childName);

    /// <summary>The weave ended — completed, failed, or cancelled. Idempotent.</summary>
    void WeaveEnded();
}
