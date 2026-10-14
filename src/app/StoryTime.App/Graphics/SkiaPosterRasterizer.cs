using StoryTime.Contracts;
using StoryTime.Core.Abstractions;
using StoryTime.Poster;

namespace StoryTime.App.Graphics;

/// <summary>IPosterRasterizer over the procedural engine — the flattened still poster
/// (ambient baked at t = 0) saved beside the story as its thumbnail.</summary>
public sealed class SkiaPosterRasterizer : IPosterRasterizer
{
    public Task<string> FlattenToPngAsync(PosterSpec spec, string outputPath, int sizePx, CancellationToken ct) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var image = PosterEngine.RenderFlattened(spec, sizePx, sizePx);
            var bytes = PosterEngine.EncodePng(image, fast: false);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllBytes(outputPath, bytes);
            return outputPath;
        }, ct);
}
