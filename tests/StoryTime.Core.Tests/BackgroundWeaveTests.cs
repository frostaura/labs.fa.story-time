using StoryTime.Contracts;
using StoryTime.Core;
using StoryTime.Core.Audio;
using StoryTime.Core.Services;
using Xunit;

namespace StoryTime.Core.Tests;

/// <summary>Core-side pins for the background-weave pass (2026-07-31): the Android loom
/// notification's copy + update throttle, and the loom hum's volume/palette/caching rules.
/// The platform pieces (foreground service, wake lock, iOS grace window) are device-verified
/// in <c>docs/design/qa-report.md</c> — these tests hold the parts Core actually owns.</summary>
public class WeaveNotificationCopyTests
{
    [Fact]
    public void Title_names_the_child()
    {
        Assert.Equal("Weaving Mila's story…", WeaveNotificationCopy.Title("Mila"));
    }

    [Theory]
    [InlineData(WeaveStage.Outline, "Outline")]
    [InlineData(WeaveStage.Scenes, "Scenes · stitch · polish")]
    [InlineData(WeaveStage.Poster, "Poster — 4 layers")]
    [InlineData(WeaveStage.Narration, "Narration — teaser first")]
    [InlineData(WeaveStage.Done, "Done")]
    public void Stage_names_match_the_weave_sheet_rows(WeaveStage stage, string expected)
    {
        Assert.Equal(expected, WeaveNotificationCopy.StageName(stage));
    }

    [Fact]
    public void StageLine_is_stage_then_percent()
    {
        var line = WeaveNotificationCopy.StageLine(new WeaveProgress { Stage = WeaveStage.Poster, Percent = 47 });
        Assert.Equal("Poster — 4 layers · 47%", line);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(140, 100)]
    public void StageLine_clamps_nonsense_percentages(int reported, int shown)
    {
        var line = WeaveNotificationCopy.StageLine(new WeaveProgress { Stage = WeaveStage.Outline, Percent = reported });
        Assert.EndsWith($"· {shown}%", line);
    }

    [Fact]
    public void Channel_copy_is_the_loom()
    {
        Assert.Equal("The loom", WeaveNotificationCopy.ChannelName);
        Assert.False(string.IsNullOrWhiteSpace(WeaveNotificationCopy.ChannelDescription));
    }
}

public class NotificationUpdateThrottleTests
{
    private static readonly DateTime T0 = new(2026, 7, 31, 20, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void First_report_always_posts()
    {
        Assert.True(new NotificationUpdateThrottle().ShouldPost(T0));
    }

    [Fact]
    public void Rapid_reports_are_suppressed_within_the_window()
    {
        var throttle = new NotificationUpdateThrottle();
        Assert.True(throttle.ShouldPost(T0));
        Assert.False(throttle.ShouldPost(T0.AddSeconds(1)));
        Assert.False(throttle.ShouldPost(T0.AddSeconds(9.5)));
    }

    [Fact]
    public void One_update_per_ten_seconds()
    {
        var throttle = new NotificationUpdateThrottle();
        Assert.True(throttle.ShouldPost(T0));
        Assert.True(throttle.ShouldPost(T0.Add(NotificationUpdateThrottle.MinInterval)));
        Assert.False(throttle.ShouldPost(T0.Add(NotificationUpdateThrottle.MinInterval).AddSeconds(1)));
    }

    [Fact]
    public void A_ten_minute_weave_posts_at_most_sixty_one_times()
    {
        var throttle = new NotificationUpdateThrottle();
        var posts = 0;
        // The pipeline reports far more often than this; one per second is already generous.
        for (var second = 0; second <= 600; second++)
            if (throttle.ShouldPost(T0.AddSeconds(second))) posts++;
        Assert.True(posts <= 61, $"posted {posts} times in 10 minutes");
    }

    [Fact]
    public void Reset_lets_the_next_report_post_immediately()
    {
        var throttle = new NotificationUpdateThrottle();
        Assert.True(throttle.ShouldPost(T0));
        throttle.Reset();
        Assert.True(throttle.ShouldPost(T0.AddSeconds(1)));
    }
}

public class LoomHumTests
{
    private static ChildProfile Profile(params string[] interests) =>
        new() { Name = "Mila", Age = 5, Interests = [.. interests] };

    [Fact]
    public void Zero_music_slider_is_an_opt_out()
    {
        Assert.Equal(0, LoomHum.VolumeFor(0));
        Assert.Equal(0, LoomHum.VolumeFor(0.005));
    }

    [Fact]
    public void Hum_is_never_inaudible_when_it_plays()
    {
        // The whole feature rests on the hum being a REAL audible thing rather than a silent
        // keep-alive; a low-but-nonzero slider must still clear the audible floor.
        for (var slider = 0.02; slider <= 1.0; slider += 0.02)
        {
            var volume = LoomHum.VolumeFor(slider);
            Assert.True(volume >= LoomHum.MinAudibleVolume, $"slider {slider:F2} → {volume:F3}");
        }
    }

    [Fact]
    public void Hum_never_shouts_over_the_room()
    {
        Assert.Equal(LoomHum.MaxVolume, LoomHum.VolumeFor(1.0), 6);
        Assert.True(LoomHum.MaxVolume < 1.0);
    }

    [Fact]
    public void Default_slider_lands_in_the_gentle_band()
    {
        // InMemorySettingsStore / PreferencesSettingsStore both default MusicVolume to 0.3.
        var volume = LoomHum.VolumeFor(0.3);
        Assert.InRange(volume, LoomHum.MinAudibleVolume, 0.25);
    }

    [Fact]
    public void Volume_is_monotone_in_the_slider()
    {
        var previous = 0.0;
        for (var slider = 0.02; slider <= 1.0; slider += 0.02)
        {
            var volume = LoomHum.VolumeFor(slider);
            Assert.True(volume >= previous);
            previous = volume;
        }
    }

    [Theory]
    [InlineData("Foxes", PosterPalette.EmberSky)]
    [InlineData("Ocean", PosterPalette.OceanDeep)]
    [InlineData("Space", PosterPalette.StarfieldViolet)]
    [InlineData("Stars", PosterPalette.StarfieldViolet)]
    public void Palette_follows_the_first_interest(string interest, PosterPalette expected)
    {
        Assert.Equal(expected, LoomHum.PaletteFor(Profile(interest)));
    }

    [Fact]
    public void Unknown_or_missing_interests_settle_on_NightHarbor()
    {
        Assert.Equal(PosterPalette.NightHarbor, LoomHum.PaletteFor(Profile()));
        Assert.Equal(PosterPalette.NightHarbor, LoomHum.PaletteFor(Profile("Dinosaurs")));
    }

    [Fact]
    public void Context_is_deterministic_per_palette_family_not_per_story()
    {
        var a = LoomHum.ContextFor(Profile("Ocean"));
        var b = LoomHum.ContextFor(Profile("Ocean"));
        Assert.Equal(a.Seed, b.Seed);
        Assert.Equal(a.Palette, b.Palette);
        Assert.NotEqual(a.Seed, LoomHum.ContextFor(Profile("Space")).Seed);
    }

    [Fact]
    public void Cache_file_name_is_keyed_by_palette()
    {
        Assert.Equal("loom-OceanDeep.wav", LoomHum.FileNameFor(PosterPalette.OceanDeep));
        Assert.NotEqual(LoomHum.FileNameFor(PosterPalette.OceanDeep),
            LoomHum.FileNameFor(PosterPalette.EmberSky));
    }

    [Fact]
    public void Fade_in_delay_lets_the_sheet_settle_first()
    {
        Assert.InRange(LoomHum.FadeInDelay.TotalSeconds, 1, 4);
    }

    [Fact]
    public async Task Repeat_weaves_reuse_one_cached_loop_per_palette_family()
    {
        var dir = Path.Combine(Path.GetTempPath(), "storytime-loom-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var provider = new LoomHumTrackProvider(new StoryTimeOptions { MediaDirectory = dir });
            var first = await provider.GetOrRenderAsync(Profile("Ocean"), CancellationToken.None);
            Assert.True(File.Exists(first));
            var stamp = File.GetLastWriteTimeUtc(first);

            // A second weave for the same profile — and any profile in the same palette
            // family — must hit the cache, not recompose.
            var second = await provider.GetOrRenderAsync(Profile("Ocean"), CancellationToken.None);
            Assert.Equal(first, second);
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(second));

            // A different family gets its own file.
            var other = await provider.GetOrRenderAsync(Profile("Space"), CancellationToken.None);
            Assert.NotEqual(first, other);
            Assert.True(File.Exists(other));

            // No truncated temp files left behind.
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Rendered_hum_is_a_playable_non_silent_wav()
    {
        var dir = Path.Combine(Path.GetTempPath(), "storytime-loom-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var provider = new LoomHumTrackProvider(new StoryTimeOptions { MediaDirectory = dir });
            var path = await provider.GetOrRenderAsync(Profile("Forest"), CancellationToken.None);

            var bytes = await File.ReadAllBytesAsync(path);
            Assert.True(bytes.Length > 44, "wav has no payload");
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
            Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(bytes, 8, 4));
            // Genuinely audible: at least some samples well away from silence.
            var peak = 0;
            for (var i = 44; i + 1 < bytes.Length; i += 2)
                peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(bytes, i)));
            Assert.True(peak > 1000, $"loom hum peak is only {peak}");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
