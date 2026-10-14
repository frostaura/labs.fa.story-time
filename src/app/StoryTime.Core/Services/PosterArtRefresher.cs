using System.Text.Json;
using StoryTime.Contracts;
using StoryTime.Core.Abstractions;
using StoryTime.Core.Poster;

namespace StoryTime.Core.Services;

/// <summary>Re-renders cached poster thumbnails when the poster art version changes.
/// Thumbnail filenames carry the composer's ArtVersion (…-thumb-v{N}.png); any story whose
/// thumbnail predates the current version (or whose file is missing) re-renders from its
/// stored PosterSpecJson — the spec itself is re-repaired by the current composer on the
/// way through Resolve. Old files are deleted after a successful re-render. Player posters
/// need no sweep: they resolve the stored spec at render time.</summary>
public sealed class PosterArtRefresher
{
    private readonly IStoryStore _store;
    private readonly IPosterRasterizer _rasterizer;
    private readonly StoryTimeOptions _options;
    private bool _ran;

    public PosterArtRefresher(IStoryStore store, IPosterRasterizer rasterizer, StoryTimeOptions options)
    {
        _store = store;
        _rasterizer = rasterizer;
        _options = options;
    }

    public static string CurrentSuffix => $"-thumb-v{PosterComposer.ArtVersion}.png";

    /// <summary>True when the story's cached thumbnail is up to date on disk.</summary>
    public static bool IsCurrent(StoryRecord story) =>
        !string.IsNullOrEmpty(story.ThumbnailPath)
        && story.ThumbnailPath.EndsWith(CurrentSuffix, StringComparison.Ordinal)
        && File.Exists(story.ThumbnailPath);

    /// <summary>Sweeps every profile's stories once per app run. Cheap when current
    /// (filename check per story); safe to call repeatedly.</summary>
    public async Task EnsureCurrentAsync(int profileId, CancellationToken ct = default)
    {
        if (_ran) return;
        _ran = true;

        foreach (var story in await _store.GetStoriesAsync(profileId))
        {
            ct.ThrowIfCancellationRequested();
            if (IsCurrent(story) || string.IsNullOrEmpty(story.PosterSpecJson)) continue;

            PosterSpec? spec;
            try { spec = JsonSerializer.Deserialize<PosterSpec>(story.PosterSpecJson); }
            catch (JsonException) { continue; }
            if (spec is null) continue;

            var newPath = Path.Combine(_options.MediaDirectory, $"story-{story.Id}{CurrentSuffix}");
            string written;
            try { written = await _rasterizer.FlattenToPngAsync(spec, newPath, 512, ct); }
            catch (IOException) { continue; }
            if (string.IsNullOrEmpty(written)) continue; // headless rasterizer — leave as-is

            var oldPath = story.ThumbnailPath;
            story.ThumbnailPath = written;
            await _store.SaveStoryAsync(story);
            if (!string.IsNullOrEmpty(oldPath) && oldPath != written && File.Exists(oldPath))
            {
                try { File.Delete(oldPath); } catch (IOException) { /* stale file is harmless */ }
            }
        }
    }
}
