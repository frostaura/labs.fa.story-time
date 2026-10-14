using StoryTime.Contracts;

namespace StoryTime.Core.Audio;

/// <summary>Lazy compose-and-cache for ambient beds. The loop is rendered on first play
/// (off the UI thread) and cached as <c>story-{id}-ambient.wav</c> beside the narration in
/// the media directory; SqliteStoryStore deletes it with the story.</summary>
public sealed class AmbientTrackProvider
{
    private readonly StoryTimeOptions _options;
    private readonly SemaphoreSlim _renderLock = new(1, 1);

    public AmbientTrackProvider(StoryTimeOptions options) => _options = options;

    public static string PathFor(StoryTimeOptions options, int storyId) =>
        Path.Combine(options.MediaDirectory, $"story-{storyId}-ambient.wav");

    /// <summary>Return the cached bed for this story, composing it first if needed.
    /// Deterministic per story; typically well under a second of render time.</summary>
    public async Task<string> GetOrRenderAsync(StoryRecord story, CancellationToken ct)
    {
        var path = PathFor(_options, story.Id);
        if (File.Exists(path)) return path;

        await _renderLock.WaitAsync(ct);
        try
        {
            if (File.Exists(path)) return path;
            await Task.Run(() =>
            {
                var loop = AmbientComposer.Render(AmbientContext.FromStory(story));
                // Write via temp + move so a cancelled render never leaves a truncated wav.
                var tmp = path + ".tmp";
                AmbientComposer.WriteWav(tmp, loop);
                File.Move(tmp, path, overwrite: true);
            }, ct);
            return path;
        }
        finally
        {
            _renderLock.Release();
        }
    }
}
