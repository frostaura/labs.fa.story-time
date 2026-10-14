namespace StoryTime.Core.Pipeline;

/// <summary>
/// Optional per-story measurement sink (measurement runs and tests; never required by
/// the app). The pipeline stamps stage wall-clock spans as they run; consumers read after the
/// story completes. Under the overlapped strategy the stage spans OVERLAP by design, so
/// their sum exceeds <see cref="TotalSeconds"/> — the difference is the overlap saving.
/// </summary>
public sealed class MakingTelemetry
{
    public string Strategy { get; set; } = "sequential";
    public double OutlineSeconds { get; set; }
    /// <summary>Scene generation + stitch + polish (producer span under overlap).</summary>
    public double ScenesSeconds { get; set; }
    public double PosterSeconds { get; set; }
    /// <summary>Teaser + full narration synthesis (consumer span under overlap).</summary>
    public double NarrationSeconds { get; set; }
    public double TotalSeconds { get; set; }
    /// <summary>Rendered story audio duration — RTF = NarrationSeconds / this.</summary>
    public double NarrationAudioSeconds { get; set; }
}
