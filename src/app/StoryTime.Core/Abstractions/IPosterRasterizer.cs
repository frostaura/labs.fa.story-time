using StoryTime.Contracts;

namespace StoryTime.Core.Abstractions;

/// <summary>Flattens a poster spec to a PNG thumbnail on disk. Implemented in the App layer
/// (SkiaPosterRenderer); Core ships a no-op so the pipeline runs headless in tests/harness.
/// (Additive Phase B abstraction.)</summary>
public interface IPosterRasterizer
{
    /// <summary>Renders a flattened square PNG; returns the written path, or empty string
    /// when rasterization is unavailable (headless).</summary>
    Task<string> FlattenToPngAsync(PosterSpec spec, string outputPath, int sizePx, CancellationToken ct);
}

/// <summary>Headless default — no image is produced (tests, harness).</summary>
public sealed class NullPosterRasterizer : IPosterRasterizer
{
    public Task<string> FlattenToPngAsync(PosterSpec spec, string outputPath, int sizePx, CancellationToken ct) =>
        Task.FromResult(string.Empty);
}
