using System.Text.Json;
using StoryTime.Contracts;
using StoryTime.Core.Abstractions;
using StoryTime.Core.Engines;

namespace StoryTime.Core.Services;

/// <summary>Builds the single pre-woven welcome story ("The Lantern Bear") deterministically
/// after onboarding completes, so the shelf and home are never empty on first open.
/// State Ready, no audio file — the player uses live TTS mode.</summary>
public sealed class WelcomeStoryFactory
{
    /// <summary>Fixed seed so every install weaves the identical welcome story.</summary>
    public const int WelcomeSeed = 20260730;
    public const string WelcomeTitle = "The Lantern Bear";

    private readonly IStoryStore _store;
    private readonly IPosterComposer _composer;
    private readonly IPosterRasterizer _rasterizer;
    private readonly StoryTimeOptions _options;
    private readonly IClock _clock;

    public WelcomeStoryFactory(IStoryStore store, IPosterComposer composer, IPosterRasterizer rasterizer,
        StoryTimeOptions options, IClock clock)
    {
        _store = store;
        _composer = composer;
        _rasterizer = rasterizer;
        _options = options;
        _clock = clock;
    }

    public async Task<StoryRecord> CreateAsync(ChildProfile profile, CancellationToken ct)
    {
        var engine = new TemplateStoryEngine(() => WelcomeSeed);

        // Welcome story is always the short length regardless of the profile's setting.
        var welcomeProfile = new ChildProfile
        {
            Id = profile.Id,
            Name = profile.Name,
            Age = profile.Age,
            Interests = profile.Interests,
            Voice = profile.Voice,
            Length = StoryLength.Five,
            Mode = StoryMode.OneShot,
        };

        var outline = await engine.GenerateOutlineAsync(welcomeProfile, null, TemplateStoryEngine.SceneCount(StoryLength.Five), ct);
        outline.Title = WelcomeTitle;

        var scenes = new List<string>();
        foreach (var scene in outline.Scenes)
        {
            var tail = scenes.Count > 0 ? scenes[^1] : string.Empty;
            scenes.Add(await engine.GenerateSceneAsync(welcomeProfile, outline, scene, tail, ct));
        }
        var polished = await engine.PolishAsync(welcomeProfile, string.Join("\n\n", scenes), ct);

        var spec = _composer.Validate(await engine.GeneratePosterSpecAsync(outline, ct));

        var story = new StoryRecord
        {
            ProfileId = profile.Id,
            Title = WelcomeTitle,
            Mode = StoryMode.OneShot,
            EpisodeNumber = 0,
            FullText = polished,
            PosterSpecJson = JsonSerializer.Serialize(spec),
            Voice = profile.Voice,
            State = StoryState.Ready,
            CreatedUtc = _clock.UtcNow,
            // No AudioPath — the player runs in live TTS mode for this story.
            DurationSeconds = EstimateSeconds(polished),
        };
        await _store.SaveStoryAsync(story);

        var thumbPath = Path.Combine(_options.MediaDirectory, $"story-{story.Id}-thumb-v{Poster.PosterComposer.ArtVersion}.png");
        story.ThumbnailPath = await _rasterizer.FlattenToPngAsync(spec, thumbPath, 512, ct);
        await _store.SaveStoryAsync(story);
        return story;
    }

    /// <summary>Same estimate the live-TTS narration mode uses: ~2.6 words per second.</summary>
    public static double EstimateSeconds(string text) =>
        Math.Round(text.Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries).Length / 2.6, 1);
}
