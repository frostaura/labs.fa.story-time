using StoryTime.Contracts;

namespace StoryTime.Core.Audio;

/// <summary>"The loom hums" (founder decision 2026-07-31): a gentle, genuinely audible
/// ambient soundscape that plays while a story weaves. It is a real feature — the weave has
/// a voice — and on iOS the active playback session is also what legitimately keeps the app
/// running in the background so the weave completes with the screen off. This class is the
/// deterministic seed side: profile → palette family → composer context → cache name.
/// Rendering/caching is <see cref="LoomHumTrackProvider"/>; playback policy (fade-in delay,
/// ducking under narration, opt-out) lives in the App's LoomHumService.</summary>
public static class LoomHum
{
    /// <summary>Genuinely-audible floor. The composer normalizes beds to RMS ≈ −24 dBFS, so
    /// the naive "slider × 0.25" (0.3 × 0.25 = 0.075 ≈ −22 dB of extra attenuation) was
    /// inaudible in practice — and an inaudible keep-alive is exactly the App Store abuse
    /// pattern this feature must never be. Slider × 0.5, clamped to [0.12, 0.5].</summary>
    public const double MinAudibleVolume = 0.12;
    public const double MaxVolume = 0.5;
    public const double SliderShare = 0.5;

    /// <summary>Fade-in delay after weave start — the sheet settles first, then the loom
    /// starts humming.</summary>
    public static readonly TimeSpan FadeInDelay = TimeSpan.FromSeconds(2);

    /// <summary>Hum target volume for the parent's Music setting. A slider at (effectively)
    /// zero is an opt-out — respect it and return 0 (on iOS the weave then falls back to the
    /// checkpoint/resume path when backgrounded). Otherwise low but audible.</summary>
    public static double VolumeFor(double musicVolume) =>
        musicVolume <= 0.01 ? 0 : Math.Clamp(musicVolume * SliderShare, MinAudibleVolume, MaxVolume);

    /// <summary>Palette-family guess from the profile's first interest (the onboarding set;
    /// unknown or empty interests settle on NightHarbor). Deterministic so repeat weaves for
    /// the same profile reuse one cached loop per palette family.</summary>
    public static PosterPalette PaletteFor(ChildProfile profile) =>
        (profile.Interests.FirstOrDefault() ?? string.Empty) switch
        {
            "Foxes" => PosterPalette.EmberSky,
            "Ocean" => PosterPalette.OceanDeep,
            "Magic" => PosterPalette.PlumDusk,
            "Space" => PosterPalette.StarfieldViolet,
            "Forest" => PosterPalette.PineForest,
            "Clouds" => PosterPalette.CloudDrift,
            "Stars" => PosterPalette.StarfieldViolet,
            "Music" => PosterPalette.LanternGlow,
            "Trains" => PosterPalette.MistValley,
            "Gardens" => PosterPalette.MoonMeadow,
            _ => PosterPalette.NightHarbor,
        };

    /// <summary>Composer context for a profile's hum. Seed is palette-stable (not per-story):
    /// one generic loom loop per palette family, cached forever.</summary>
    public static AmbientContext ContextFor(ChildProfile profile)
    {
        var palette = PaletteFor(profile);
        return new AmbientContext { Palette = palette, Mood = "calm", Seed = 7919 * (1 + (int)palette) };
    }

    public static string FileNameFor(PosterPalette palette) => $"loom-{palette}.wav";
}

/// <summary>Compose-and-cache for loom-hum loops, mirroring <see cref="AmbientTrackProvider"/>
/// but keyed per palette family (`loom-{palette}.wav` in the media directory, AppData) instead
/// of per story — repeat weaves start instantly; first render is sub-second-to-few-seconds and
/// happens off-thread before the ~2 s fade-in delay elapses.</summary>
public sealed class LoomHumTrackProvider
{
    private readonly StoryTimeOptions _options;
    private readonly SemaphoreSlim _renderLock = new(1, 1);

    public LoomHumTrackProvider(StoryTimeOptions options) => _options = options;

    public string PathFor(PosterPalette palette) =>
        Path.Combine(_options.MediaDirectory, LoomHum.FileNameFor(palette));

    /// <summary>Return the cached hum for this profile's palette family, composing it first
    /// if needed. Deterministic per palette family.</summary>
    public async Task<string> GetOrRenderAsync(ChildProfile profile, CancellationToken ct)
    {
        var ctx = LoomHum.ContextFor(profile);
        var path = PathFor(ctx.Palette);
        if (File.Exists(path)) return path;

        await _renderLock.WaitAsync(ct);
        try
        {
            if (File.Exists(path)) return path;
            await Task.Run(() =>
            {
                var loop = AmbientComposer.Render(ctx);
                // Temp + move so a cancelled render never leaves a truncated wav behind.
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
