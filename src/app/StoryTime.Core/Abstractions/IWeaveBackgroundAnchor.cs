namespace StoryTime.Core.Abstractions;

/// <summary>Platform anchor that keeps a weave alive when the app leaves the foreground
/// (additive, background-generation pass 2026-07-31). The App-layer weave host calls it on
/// weave start/end; each platform does what it honestly can:
/// <list type="bullet">
/// <item><b>Android</b> — starts/stops the <c>WeaveForegroundService</c> (foreground service
/// + partial wake lock), so the pipeline Task survives activity destruction and screen-off
/// for the whole ~10-minute weave.</item>
/// <item><b>iOS</b> — arms the <c>WeaveBackgroundCoordinator</c>: a
/// <c>BeginBackgroundTask</c> grace window on backgrounding (~30 s, measured in logs),
/// then a clean checkpoint, plus an opportunistic <c>BGProcessingTask</c> resume.</item>
/// </list>
/// Progress display is NOT routed through this seam — the Android service subscribes to
/// <see cref="IWeaveHost.ProgressChanged"/> directly for its notification updates.</summary>
public interface IWeaveBackgroundAnchor
{
    /// <summary>A weave (fresh or resumed) just started; always called from the foreground.</summary>
    void WeaveStarted(string childName);

    /// <summary>The weave ended — completed, failed, or cancelled. Idempotent.</summary>
    void WeaveEnded();
}
