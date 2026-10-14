using System.Text.Json;
using StoryTime.Contracts;
using Xunit;
using StoryTime.Core.Audio;
using StoryTime.Core.Poster;
using StoryTime.Core.Services;
using StoryTime.Core.ViewModels;

namespace StoryTime.Core.Tests;

public class AmbientComposerTests
{
    private static AmbientContext Ctx(PosterPalette palette = PosterPalette.NightHarbor,
        string mood = "calm", int seed = 7) => new() { Palette = palette, Mood = mood, Seed = seed };

    [Fact]
    public void Render_IsDeterministicPerSeedContext()
    {
        var a = AmbientComposer.Render(Ctx());
        var b = AmbientComposer.Render(Ctx());
        Assert.Equal(a.Left, b.Left);
        Assert.Equal(a.Right, b.Right);
    }

    [Fact]
    public void Render_DifferentSeedsDiffer()
    {
        var a = AmbientComposer.Render(Ctx(seed: 7));
        var b = AmbientComposer.Render(Ctx(seed: 8));
        Assert.NotEqual(a.Left, b.Left);
    }

    [Fact]
    public void Render_DifferentPalettesDiffer()
    {
        var a = AmbientComposer.Render(Ctx(PosterPalette.NightHarbor));
        var b = AmbientComposer.Render(Ctx(PosterPalette.EmberSky));
        Assert.NotEqual(a.Left, b.Left);
    }

    [Fact]
    public void Render_MeetsLevelBounds()
    {
        var loop = AmbientComposer.Render(Ctx());
        var peakDb = 20 * Math.Log10(loop.Peak());
        var rmsDb = 20 * Math.Log10(loop.Rms());
        Assert.True(peakDb <= -5.9, $"peak {peakDb:F1} dBFS must stay at/below −6 dBFS");
        Assert.InRange(rmsDb, -34, -18);   // a bed, not a lead: quiet but present
    }

    [Fact]
    public void Render_HasNoDcOffset()
    {
        var loop = AmbientComposer.Render(Ctx());
        var meanL = loop.Left.Average(v => (double)v);
        var meanR = loop.Right.Average(v => (double)v);
        Assert.True(Math.Abs(meanL) < 1e-3, $"L DC {meanL:E2}");
        Assert.True(Math.Abs(meanR) < 1e-3, $"R DC {meanR:E2}");
    }

    [Theory]
    [InlineData(PosterPalette.NightHarbor)]
    [InlineData(PosterPalette.EmberSky)]
    [InlineData(PosterPalette.OceanDeep)]
    [InlineData(PosterPalette.SnowHush)]
    public void Render_LoopSeamIsContinuous(PosterPalette palette)
    {
        var loop = AmbientComposer.Render(Ctx(palette));
        var n = loop.Left.Length;

        // Click test: the wrap-around first difference must not exceed the loop's own
        // maximum sample-to-sample step (a click would be an outlier spike).
        double maxDelta = 0;
        for (var i = 1; i < n; i++)
            maxDelta = Math.Max(maxDelta, Math.Abs(loop.Left[i] - loop.Left[i - 1]));
        var seamDelta = Math.Abs(loop.Left[0] - loop.Left[n - 1]);
        Assert.True(seamDelta <= maxDelta * 1.5,
            $"seam delta {seamDelta:E2} vs max in-loop delta {maxDelta:E2}");

        // Energy continuity: the last and first 100 samples belong to the same sound.
        double RmsOf(ReadOnlySpan<float> s)
        {
            double sum = 0;
            foreach (var v in s) sum += (double)v * v;
            return Math.Sqrt(sum / s.Length);
        }
        var head = RmsOf(loop.Left.AsSpan(0, 4800));
        var tail = RmsOf(loop.Left.AsSpan(n - 4800, 4800));
        var ratioDb = Math.Abs(20 * Math.Log10(Math.Max(head, 1e-9) / Math.Max(tail, 1e-9)));
        Assert.True(ratioDb < 6, $"seam energy jump {ratioDb:F1} dB");
    }

    [Fact]
    public void Render_IsStereo_NotDualMono()
    {
        var loop = AmbientComposer.Render(Ctx());
        var identical = true;
        for (var i = 0; i < loop.Left.Length; i += 997)
            if (Math.Abs(loop.Left[i] - loop.Right[i]) > 1e-6) { identical = false; break; }
        Assert.False(identical, "left and right channels must differ (stereo width)");
    }

    [Fact]
    public void MoodMap_IsTotal_AndDistinctWhereItMatters()
    {
        foreach (var palette in Enum.GetValues<PosterPalette>())
            Assert.NotNull(AmbientMoodMap.ForPalette(palette));

        // The design anchors: NightHarbor is dorian calm, EmberSky warm maj7, OceanDeep waves.
        Assert.Equal("night-calm", AmbientMoodMap.ForPalette(PosterPalette.NightHarbor).Name);
        Assert.Equal("warm-ember", AmbientMoodMap.ForPalette(PosterPalette.EmberSky).Name);
        Assert.Equal("ocean-waves", AmbientMoodMap.ForPalette(PosterPalette.OceanDeep).Name);
        Assert.True(AmbientMoodMap.ForPalette(PosterPalette.OceanDeep).NoiseWaveDepth > 0.5,
            "ocean character carries the wave-shaped noise bed");

        // Six distinct characters across the twelve palettes.
        var names = Enum.GetValues<PosterPalette>().Select(p => AmbientMoodMap.ForPalette(p).Name).Distinct().ToList();
        Assert.Equal(6, names.Count);
    }

    [Fact]
    public void MoodTweaks_AdjustWithoutReplacing()
    {
        var calm = AmbientMoodMap.ApplyMood(AmbientMoodMap.NightCalm, "calm");
        var sleepy = AmbientMoodMap.ApplyMood(AmbientMoodMap.NightCalm, "sleepy");
        Assert.Equal(AmbientMoodMap.NightCalm, calm);
        Assert.True(sleepy.TempoBpm < calm.TempoBpm);
        Assert.Equal(calm.RootMidi, sleepy.RootMidi);   // same key, different breath
    }

    [Fact]
    public void Context_FromStory_ParsesPosterSpec_AndFallsBack()
    {
        var spec = new PosterSpec { Palette = PosterPalette.EmberSky, Mood = PosterMood.Cozy, Seed = 42 };
        var story = new StoryRecord { Id = 3, PosterSpecJson = JsonSerializer.Serialize(spec) };
        var ctx = AmbientContext.FromStory(story);
        Assert.Equal(PosterPalette.EmberSky, ctx.Palette);
        Assert.Equal("cozy", ctx.Mood);
        Assert.Equal(42, ctx.Seed);

        // A story stored under the retired v2 formation contract still yields its palette,
        // mood and seed (Mood was a free string there; Palette a number).
        const string legacy = "{\"Palette\":10,\"Mood\":\"cozy\",\"Layers\":[{\"Depth\":0,\"Elements\":[{\"Shape\":0}]}],\"Seed\":42}";
        var old = AmbientContext.FromStory(new StoryRecord { Id = 4, PosterSpecJson = legacy });
        Assert.Equal(PosterPalette.EmberSky, old.Palette);
        Assert.Equal("cozy", old.Mood);
        Assert.Equal(42, old.Seed);

        var bare = AmbientContext.FromStory(new StoryRecord { Id = 9, PosterSpecJson = "not json" });
        Assert.Equal(PosterPalette.NightHarbor, bare.Palette);
        Assert.Equal(9, bare.Seed);
    }

    [Fact]
    public void WriteWav_RoundTripsHeaderAndSize()
    {
        var loop = AmbientComposer.Render(Ctx(PosterPalette.PlumDusk, seed: 3));
        var path = Path.Combine(Path.GetTempPath(), "tw-tests", $"ambient-{Guid.NewGuid():N}.wav");
        try
        {
            AmbientComposer.WriteWav(path, loop);
            using var fs = File.OpenRead(path);
            using var r = new BinaryReader(fs);
            Assert.Equal("RIFF"u8.ToArray(), r.ReadBytes(4));
            fs.Seek(22, SeekOrigin.Begin);
            Assert.Equal(2, r.ReadUInt16());                       // stereo
            Assert.Equal((uint)loop.SampleRate, r.ReadUInt32());
            fs.Seek(40, SeekOrigin.Begin);
            Assert.Equal((uint)(loop.Left.Length * 4), r.ReadUInt32());
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class AmbientTrackProviderTests : IAsyncLifetime
{
    private TestEnv _env = null!;

    public async Task InitializeAsync() => _env = await TestEnv.CreateAsync();
    public async Task DisposeAsync() => await _env.DisposeAsync();

    private static StoryRecord Story(int id) => new() { Id = id, PosterSpecJson = string.Empty };

    [Fact]
    public async Task GetOrRender_CachesBesideNarration_AndStoreDeleteRemovesIt()
    {
        var provider = new AmbientTrackProvider(_env.Options);
        var story = new StoryRecord
        {
            Title = "Seam Test",
            State = StoryState.Ready,
            CreatedUtc = DateTime.UtcNow,
        };
        await _env.Store.SaveStoryAsync(story);

        var path = await provider.GetOrRenderAsync(story, CancellationToken.None);
        Assert.True(File.Exists(path));
        Assert.Equal(AmbientTrackProvider.PathFor(_env.Options, story.Id), path);
        Assert.StartsWith(_env.Options.MediaDirectory, path);

        // Second call is a cache hit — same file, untouched.
        var written = File.GetLastWriteTimeUtc(path);
        var again = await provider.GetOrRenderAsync(story, CancellationToken.None);
        Assert.Equal(path, again);
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));

        // Deleting the story deletes its bed.
        await _env.Store.DeleteStoryAsync(story.Id);
        Assert.False(File.Exists(path));
    }
}

public class PlayerAmbientMixTests : IAsyncLifetime
{
    private TestEnv _env = null!;
    private FakePlayerProvider _players = null!;
    private FakeAmbientPlayer _ambient = null!;
    private FakeEntitlements _entitlements = null!;
    private InMemorySettingsStore _settings = null!;
    private PlayerLaunchContext _context = null!;
    private StoryRecord _story = null!;

    public async Task InitializeAsync()
    {
        _env = await TestEnv.CreateAsync();
        _players = new FakePlayerProvider();
        _ambient = new FakeAmbientPlayer();
        _entitlements = new FakeEntitlements { IsPremium = true };
        _settings = new InMemorySettingsStore { MusicVolume = 0.4 };
        _context = new PlayerLaunchContext();

        var profile = TestEnv.Profile();
        await _env.Store.SaveProfileAsync(profile);
        _story = new StoryRecord
        {
            ProfileId = profile.Id,
            Title = "The Quiet Tide",
            State = StoryState.Ready,
            DurationSeconds = 300,
            CreatedUtc = DateTime.UtcNow,
        };
        await _env.Store.SaveStoryAsync(_story);
        _context.StoryId = _story.Id;
    }

    public async Task DisposeAsync() => await _env.DisposeAsync();

    private PlayerViewModel Vm() => new(_env.Store, _players, new PosterComposer(), _context,
        new FakeNavigator(), new NullHaptics(), _entitlements, _settings, _ambient,
        new AmbientTrackProvider(_env.Options));

    [Fact]
    public async Task Premium_PlayStartsBed_DuckedUnderVoice()
    {
        var vm = Vm();
        await vm.ActivateAsync();
        Assert.True(vm.IsMusicLive);
        Assert.Equal(0.4, vm.MusicVolume, 3);

        await vm.PlayPauseCommand.ExecuteAsync(null);
        Assert.True(_ambient.IsPlaying);
        Assert.Equal(0.4 * PlayerViewModel.AmbientDuckWhileNarrating, _ambient.LastVolume, 3);
        Assert.Equal(AmbientTrackProvider.PathFor(_env.Options, _story.Id), _ambient.StartedPath);
    }

    [Fact]
    public async Task Pause_LiftsBedGently_StopFadesItOut()
    {
        var vm = Vm();
        await vm.ActivateAsync();
        await vm.PlayPauseCommand.ExecuteAsync(null);   // play
        await vm.PlayPauseCommand.ExecuteAsync(null);   // pause
        Assert.True(_ambient.IsPlaying);
        Assert.Equal(0.4 * PlayerViewModel.AmbientPausedFactor, _ambient.LastVolume, 3);

        await vm.StopCommand.ExecuteAsync(null);
        Assert.False(_ambient.IsPlaying);
    }

    [Fact]
    public async Task SliderDrag_RetargetsVolumeLive_AndPersists()
    {
        var vm = Vm();
        await vm.ActivateAsync();
        await vm.PlayPauseCommand.ExecuteAsync(null);

        vm.MusicVolume = 0.8;
        await Task.Delay(150);   // OnMusicVolumeChanged syncs fire-and-forget
        Assert.Equal(0.8 * PlayerViewModel.AmbientDuckWhileNarrating, _ambient.LastVolume, 3);
        Assert.Equal(0.8, _settings.MusicVolume, 3);
    }

    [Fact]
    public async Task NonPremium_NeverStartsBed_AndRowIsLocked()
    {
        _entitlements.IsPremium = false;
        var vm = Vm();
        await vm.ActivateAsync();
        Assert.False(vm.IsMusicLive);
        Assert.True(vm.IsMusicLocked);

        await vm.PlayPauseCommand.ExecuteAsync(null);
        Assert.False(_ambient.IsPlaying);
        Assert.Equal(0, _ambient.StartCount);
    }

    [Fact]
    public async Task Deactivate_StopsBed()
    {
        var vm = Vm();
        await vm.ActivateAsync();
        await vm.PlayPauseCommand.ExecuteAsync(null);
        Assert.True(_ambient.IsPlaying);

        await vm.DeactivateAsync();
        Assert.False(_ambient.IsPlaying);
    }

    [Fact]
    public async Task VoiceRow_TracksPlayerVolume_DisabledWithoutSupport()
    {
        var vm = Vm();
        await vm.ActivateAsync();
        Assert.True(vm.IsVoiceRowEnabled);
        vm.VoiceVolume = 0.5;
        Assert.Equal(0.5, _players.Player.Volume, 3);

        await vm.DeactivateAsync();
        _players.Player.SupportsVolume = false;
        var vm2 = Vm();
        await vm2.ActivateAsync();
        Assert.False(vm2.IsVoiceRowEnabled);
    }
}
