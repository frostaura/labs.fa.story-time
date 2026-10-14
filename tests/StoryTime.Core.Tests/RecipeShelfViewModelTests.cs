using StoryTime.Contracts;
using StoryTime.Core.Services;
using StoryTime.Core.ViewModels;
using Xunit;

namespace StoryTime.Core.Tests;

/// <summary>"Tonight's recipe" shelf: every change live-persists through
/// IStoryStore.SaveProfileAsync (no Save button), with a Selection haptic per change.</summary>
public class RecipeShelfViewModelTests : IAsyncLifetime
{
    private TestEnv _env = null!;
    private FakeWeaveHost _host = null!;
    private FakeTtsEngine _tts = null!;
    private FakeHaptics _haptics = null!;
    private RecipeShelfViewModel _vm = null!;
    private ChildProfile _profile = null!;

    public async Task InitializeAsync()
    {
        _env = await TestEnv.CreateAsync();
        _host = new FakeWeaveHost();
        _tts = new FakeTtsEngine();
        _haptics = new FakeHaptics();
        _vm = new RecipeShelfViewModel(_env.Store, _tts, _haptics, _host, new NullVoiceSamplePlayer());

        _profile = TestEnv.Profile();
        await _env.Store.SaveProfileAsync(_profile);
    }

    public async Task DisposeAsync() => await _env.DisposeAsync();

    [Fact]
    public async Task Open_LoadsSelectionsFromProfile_AndOpens()
    {
        await _vm.OpenAsync(_profile);
        Assert.True(_vm.IsOpen);
        string[] expected = ["Foxes", "Ocean"];
        Assert.Equal(expected, _vm.Interests.Where(i => i.IsSelected).Select(i => i.Name).ToArray());
        Assert.Equal(NarratorVoice.Ember, _vm.Voices.Single(v => v.IsSelected).Voice);
        Assert.True(_vm.IsSeries);
        Assert.Equal(RecipeShelfViewModel.SeriesHint, _vm.ModeHint);
    }

    [Fact]
    public async Task ToggleInterest_WritesThroughInstantly_WithSelectionHaptic()
    {
        await _vm.OpenAsync(_profile);
        await _vm.ToggleInterestCommand.ExecuteAsync(_vm.Interests.First(i => i.Name == "Magic"));
        Assert.Contains("Magic", (await _env.Store.GetActiveProfileAsync())!.Interests);
        Assert.Equal(1, _haptics.Count("Selection"));

        // And off again — the deselect persists too.
        await _vm.ToggleInterestCommand.ExecuteAsync(_vm.Interests.First(i => i.Name == "Magic"));
        Assert.DoesNotContain("Magic", (await _env.Store.GetActiveProfileAsync())!.Interests);
        Assert.Equal(2, _haptics.Count("Selection"));
    }

    [Fact]
    public async Task SelectVoice_WritesThroughInstantly()
    {
        await _vm.OpenAsync(_profile);
        await _vm.SelectVoiceCommand.ExecuteAsync(_vm.Voices.First(v => v.Voice == NarratorVoice.Sage));
        Assert.Equal(NarratorVoice.Sage, (await _env.Store.GetActiveProfileAsync())!.Voice);
        Assert.Equal(1, _haptics.Count("Selection"));
        Assert.True(_vm.Voices.Single(v => v.IsSelected).Voice == NarratorVoice.Sage);
    }

    [Fact]
    public async Task ReselectingCurrentVoice_DoesNotTickOrRewrite()
    {
        await _vm.OpenAsync(_profile);
        var saves = 0;
        _vm.ProfileChanged += (_, _) => saves++;
        await _vm.SelectVoiceCommand.ExecuteAsync(_vm.Voices.First(v => v.Voice == NarratorVoice.Ember));
        Assert.Equal(0, _haptics.Count("Selection"));
        Assert.Equal(0, saves);
    }

    [Fact]
    public async Task PlaySample_SelectsAndRendersTheVoice()
    {
        await _vm.OpenAsync(_profile);
        await _vm.PlaySampleCommand.ExecuteAsync(_vm.Voices.First(v => v.Voice == NarratorVoice.Willow));
        Assert.Equal(NarratorVoice.Willow, (await _env.Store.GetActiveProfileAsync())!.Voice);
        Assert.Equal(NarratorVoice.Willow, _vm.Voices.Single(v => v.IsSelected).Voice);
    }

    [Fact]
    public async Task SetMode_WritesThroughInstantly_AndHintFollows()
    {
        await _vm.OpenAsync(_profile);
        await _vm.SetSeriesCommand.ExecuteAsync(false);
        Assert.Equal(StoryMode.OneShot, (await _env.Store.GetActiveProfileAsync())!.Mode);
        Assert.Equal(RecipeShelfViewModel.OneShotHint, _vm.ModeHint);
        Assert.Equal(1, _haptics.Count("Selection"));

        // Re-tapping the active mode is silent.
        await _vm.SetSeriesCommand.ExecuteAsync(false);
        Assert.Equal(1, _haptics.Count("Selection"));
    }

    [Fact]
    public async Task Open_WhileWeaving_ShowsNextStoryWhisper()
    {
        _host.IsWeaving = true;
        await _vm.OpenAsync(_profile);
        Assert.True(_vm.IsWeaving);
    }

    [Fact]
    public async Task Close_HidesShelf_WithLightHaptic()
    {
        await _vm.OpenAsync(_profile);
        _vm.CloseCommand.Execute(null);
        Assert.False(_vm.IsOpen);
        Assert.Equal(1, _haptics.Count("Light"));
    }

    [Fact]
    public async Task EveryMutation_RaisesProfileChanged()
    {
        await _vm.OpenAsync(_profile);
        var raised = 0;
        _vm.ProfileChanged += (_, _) => raised++;
        await _vm.ToggleInterestCommand.ExecuteAsync(_vm.Interests.First(i => i.Name == "Stars"));
        await _vm.SelectVoiceCommand.ExecuteAsync(_vm.Voices.First(v => v.Voice == NarratorVoice.Sage));
        await _vm.SetSeriesCommand.ExecuteAsync(false);
        Assert.Equal(3, raised);
    }
}
