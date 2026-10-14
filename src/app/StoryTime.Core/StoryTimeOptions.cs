namespace StoryTime.Core;

/// <summary>Injectable environment knobs so Core stays platform-free and testable.</summary>
public sealed class StoryTimeOptions
{
    /// <summary>Absolute path to the sqlite database file (storytime.db3 in AppData on device).</summary>
    public string DatabasePath { get; set; } = Path.Combine(Path.GetTempPath(), "storytime.db3");

    /// <summary>Directory where narration audio and poster thumbnails are written.</summary>
    public string MediaDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "storytime-media");

    /// <summary>Force a pipeline strategy (tests, measurement runs). Null → the pipeline
    /// picks: Overlapped unless the device is low-memory (&lt; 6 GB or platform low-RAM flag),
    /// then Sequential. Additive 2026-07-31.</summary>
    public Abstractions.WeaveStrategy? ForcedWeaveStrategy { get; set; }
}
