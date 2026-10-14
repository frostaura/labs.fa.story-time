using StoryTime.Contracts;
using StoryTime.Core.Pipeline;
using StoryTime.Core.Poster;
using StoryTime.Core.Services;
using StoryTime.Core.ViewModels;
using StoryTime.Core.Abstractions;
using Xunit;

namespace StoryTime.Core.Tests;

public class HomeViewModelTests : IAsyncLifetime
{
    private TestEnv _env = null!;
    private FakeClock _clock = null!;
    private InMemorySettingsStore _settings = null!;
    private CooldownService _cooldown = null!;
    private FakeWeaveHost _host = null!;
    private FakeNavigator _navigator = null!;
    private WeaveSheetViewModel _sheet = null!;
    private RecipeShelfViewModel _recipe = null!;
    private FakeHaptics _haptics = null!;
    private HomeViewModel _vm = null!;

    public async Task InitializeAsync()
    {
        _env = await TestEnv.CreateAsync();
        _clock = new FakeClock();
        _settings = new InMemorySettingsStore();
        var entitlements = new StoreEntitlements(_settings, _clock);
        _cooldown = new CooldownService(_settings, _clock, entitlements);
        _host = new FakeWeaveHost();
        _navigator = new FakeNavigator();
        _haptics = new FakeHaptics();
        _sheet = new WeaveSheetViewModel(_host, _env.Store, _cooldown, _navigator, new PlayerLaunchContext(),
            _settings, new NullHaptics(), entitlements, new KidModeService());
        _recipe = new RecipeShelfViewModel(_env.Store, new FakeTtsEngine(), _haptics, _host, new NullVoiceSamplePlayer());
        _vm = new HomeViewModel(_env.Store, _host, _cooldown, entitlements, _navigator,
            new PlayerLaunchContext(), new KidModeService(),
            new PosterArtRefresher(_env.Store, new NullPosterRasterizer(), _env.Options), new NullHaptics(),
            _sheet, _recipe, _env.Catalog);

        var profile = TestEnv.Profile();
        await _env.Store.SaveProfileAsync(profile);
    }

    public async Task DisposeAsync() => await _env.DisposeAsync();

    [Fact]
    public async Task Activate_BuildsHeadlineAndSummary()
    {
        await _vm.ActivateAsync();
        Assert.Equal("Tonight's story for Aria", _vm.Headline);
        Assert.Contains("Foxes", _vm.SummaryLine);
        Assert.Equal("A", _vm.AvatarInitial);
    }

    [Fact]
    public async Task WeaveButton_DisabledDuringCooldown_WithNextWeaveLabel()
    {
        _cooldown.Start();
        await _vm.ActivateAsync();
        Assert.False(_vm.CanWeave);
        Assert.StartsWith("Next story at ", _vm.WeaveButtonText);
        Assert.False(_vm.WeaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task WeaveButton_EnabledWhenIdle()
    {
        await _vm.ActivateAsync();
        Assert.True(_vm.CanWeave);
        Assert.Equal("Make tonight's story", _vm.WeaveButtonText);
    }

    [Fact]
    public async Task WeaveButton_ShowsWeavingWhileHostRuns()
    {
        _host.IsWeaving = true;
        await _vm.ActivateAsync();
        Assert.False(_vm.CanWeave);
        Assert.Equal("Making tonight's story…", _vm.WeaveButtonText);
    }

    [Fact]
    public async Task Weave_StartsHostAndOpensSheetExpanded_NoNavigation()
    {
        await _vm.ActivateAsync();
        await _vm.WeaveCommand.ExecuteAsync(null);
        Assert.NotNull(_host.StartedWith);
        // The Weaving page is gone: the sheet opens expanded over Home instead.
        Assert.DoesNotContain(Routes.Weaving, _navigator.Routes);
        Assert.Equal(WeaveSheetState.Expanded, _sheet.State);
        Assert.Equal("Making Aria's story…", _sheet.Headline);
    }

    [Fact]
    public async Task WeaveCompletion_RefreshesCarouselWhileSittingOnHome()
    {
        await _vm.ActivateAsync();
        Assert.Empty(_vm.Stories);

        var profile = (await _env.Store.GetActiveProfileAsync())!;
        var story = new StoryRecord { ProfileId = profile.Id, Title = "T", State = StoryState.Ready, CreatedUtc = DateTime.UtcNow };
        await _env.Store.SaveStoryAsync(story);

        _host.RaiseCompleted(story);
        await _vm.WeaveCompletedRefresh!;
        Assert.Single(_vm.Stories);
    }

    [Fact]
    public async Task SelectLength_GatesFifteenBehindPremium()
    {
        await _vm.ActivateAsync();
        await _vm.SelectLengthCommand.ExecuteAsync(StoryLength.Fifteen);
        Assert.True(_vm.ShowPremiumHint);
        Assert.NotEqual(StoryLength.Fifteen, _vm.SelectedLength);

        await _vm.SelectLengthCommand.ExecuteAsync(StoryLength.Five);
        Assert.False(_vm.ShowPremiumHint);
        Assert.Equal(StoryLength.Five, _vm.SelectedLength);
    }

    [Fact]
    public async Task SelectLength_WritesThroughToProfileImmediately()
    {
        // Root cause of "my selection isn't remembered": the old handler kept the pick in
        // a local property (persisted only at weave time) while ActivateAsync re-read the
        // stored profile on every appearing, overwriting it. Now the pick saves on commit.
        await _vm.ActivateAsync();
        Assert.Equal(StoryLength.Five, _vm.SelectedLength);   // stored profile default
        await _vm.SelectLengthCommand.ExecuteAsync(StoryLength.Ten);
        Assert.Equal(StoryLength.Ten, (await _env.Store.GetActiveProfileAsync())!.Length);
        Assert.Contains("10 min", _vm.SummaryLine);

        // Cold restart: a brand-new VM activating from the store sees the pick.
        var fresh = new HomeViewModel(_env.Store, _host, _cooldown, new FakeEntitlements(), _navigator,
            new PlayerLaunchContext(), new KidModeService(),
            new PosterArtRefresher(_env.Store, new NullPosterRasterizer(), _env.Options), new NullHaptics(),
            _sheet, new RecipeShelfViewModel(_env.Store, new FakeTtsEngine(), new FakeHaptics(), _host, new NullVoiceSamplePlayer()), _env.Catalog);
        await fresh.ActivateAsync();
        Assert.Equal(StoryLength.Ten, fresh.SelectedLength);
    }

    [Fact]
    public async Task SelectLength_RejectedFifteen_DoesNotPersist()
    {
        await _vm.ActivateAsync();
        await _vm.SelectLengthCommand.ExecuteAsync(StoryLength.Fifteen);
        Assert.Equal(StoryLength.Five, (await _env.Store.GetActiveProfileAsync())!.Length);
    }

    [Fact]
    public async Task AdjustRecipe_OpensShelfLoadedFromProfile()
    {
        await _vm.ActivateAsync();
        await _vm.AdjustRecipeCommand.ExecuteAsync(null);
        Assert.True(_recipe.IsOpen);
        Assert.False(_recipe.IsWeaving);
        Assert.True(_recipe.Interests.First(i => i.Name == "Foxes").IsSelected);
        Assert.True(_recipe.Voices.First(v => v.Voice == NarratorVoice.Ember).IsSelected);
        Assert.True(_recipe.IsSeries);
    }

    [Fact]
    public async Task AdjustRecipe_BlockedWhileWeaveSheetExpanded()
    {
        await _vm.ActivateAsync();
        await _sheet.ShowForWeaveAsync();   // sheet expanded — its scrim owns the screen
        await _vm.AdjustRecipeCommand.ExecuteAsync(null);
        Assert.False(_recipe.IsOpen);
    }

    [Fact]
    public async Task RecipeEdit_RerendersSummaryLineImmediately()
    {
        await _vm.ActivateAsync();
        await _vm.AdjustRecipeCommand.ExecuteAsync(null);
        await _recipe.SelectVoiceCommand.ExecuteAsync(_recipe.Voices.First(v => v.Voice == NarratorVoice.Willow));
        Assert.Contains("Willow", _vm.SummaryLine);

        await _recipe.ToggleInterestCommand.ExecuteAsync(_recipe.Interests.First(i => i.Name == "Magic"));
        Assert.Contains("Magic", _vm.SummaryLine);
    }

    [Fact]
    public async Task RecipeEditDuringWeave_AffectsNextWeaveOnly()
    {
        await _vm.ActivateAsync();
        await _vm.WeaveCommand.ExecuteAsync(null);   // host snapshots the recipe at start
        Assert.Equal(NarratorVoice.Ember, _host.StartedWith!.Voice);

        _sheet.State = WeaveSheetState.Collapsed;    // mini-bar docked; shelf may open
        await _vm.AdjustRecipeCommand.ExecuteAsync(null);
        Assert.True(_recipe.IsOpen);
        Assert.True(_recipe.IsWeaving);              // eyebrow whispers "for the next story"

        await _recipe.SelectVoiceCommand.ExecuteAsync(_recipe.Voices.First(v => v.Voice == NarratorVoice.Sage));
        // The running weave keeps its snapshot; the store carries the next weave's recipe.
        Assert.Equal(NarratorVoice.Ember, _host.StartedWith!.Voice);
        Assert.Equal(NarratorVoice.Sage, (await _env.Store.GetActiveProfileAsync())!.Voice);
    }

    [Fact]
    public async Task FavoriteToggle_Persists()
    {
        var profile = (await _env.Store.GetActiveProfileAsync())!;
        var story = new StoryRecord { ProfileId = profile.Id, Title = "T", State = StoryState.Ready, CreatedUtc = DateTime.UtcNow };
        await _env.Store.SaveStoryAsync(story);
        await _vm.ActivateAsync();

        var item = Assert.Single(_vm.Stories);
        await _vm.ToggleFavoriteCommand.ExecuteAsync(item);
        Assert.True(item.IsFavorite);

        var reloaded = await _env.Store.GetStoryAsync(story.Id);
        Assert.True(reloaded!.IsFavorite);
    }

    [Fact]
    public async Task Delete_RemovesStoryAndFiles()
    {
        var profile = (await _env.Store.GetActiveProfileAsync())!;
        Directory.CreateDirectory(_env.Options.MediaDirectory);
        var audio = Path.Combine(_env.Options.MediaDirectory, "a.wav");
        await File.WriteAllTextAsync(audio, "x");
        var story = new StoryRecord { ProfileId = profile.Id, Title = "T", State = StoryState.Ready, AudioPath = audio, CreatedUtc = DateTime.UtcNow };
        await _env.Store.SaveStoryAsync(story);
        await _vm.ActivateAsync();

        await _vm.DeleteStoryCommand.ExecuteAsync(Assert.Single(_vm.Stories));
        Assert.Empty(_vm.Stories);
        Assert.Null(await _env.Store.GetStoryAsync(story.Id));
        Assert.False(File.Exists(audio));
    }
}

public class WeaveSheetViewModelTests : IAsyncLifetime
{
    private TestEnv _env = null!;
    private FakeWeaveHost _host = null!;
    private FakeNavigator _navigator = null!;
    private InMemorySettingsStore _settings = null!;
    private KidModeService _kidMode = null!;
    private WeaveSheetViewModel _vm = null!;

    public async Task InitializeAsync()
    {
        _env = await TestEnv.CreateAsync();
        _host = new FakeWeaveHost();
        _navigator = new FakeNavigator();
        _settings = new InMemorySettingsStore();
        _kidMode = new KidModeService();
        var clock = new FakeClock();
        var cooldown = new CooldownService(_settings, clock, new StoreEntitlements(_settings, clock));
        _vm = new WeaveSheetViewModel(_host, _env.Store, cooldown, _navigator, new PlayerLaunchContext(), _settings,
            new NullHaptics(), new StoreEntitlements(_settings, clock), _kidMode);
        await _env.Store.SaveProfileAsync(TestEnv.Profile());
    }

    public async Task DisposeAsync() => await _env.DisposeAsync();

    [Fact]
    public async Task ShowForWeave_OpensExpandedWithFreshState()
    {
        await _vm.ShowForWeaveAsync();
        Assert.Equal(WeaveSheetState.Expanded, _vm.State);
        Assert.True(_vm.IsVisible);
        Assert.Equal(WeaveSheetViewModel.DisplayFloorPercent, _vm.Percent);
        Assert.All(_vm.Stages, s => Assert.Equal(StageRowState.Waiting, s.State));
        Assert.Equal("Making Aria's story…", _vm.Headline);
        Assert.Equal("Making Aria's story…", _vm.MiniTitle);
        Assert.Equal($"MAKING · {WeaveSheetViewModel.DisplayFloorPercent}%", _vm.EyebrowText);
        Assert.Equal("Close — your story keeps going", _vm.CloseLabel);
    }

    [Fact]
    public async Task StageRows_TrackProgress_AndMiniDetailFollows()
    {
        await _vm.ShowForWeaveAsync();
        _host.RaiseProgress(new WeaveProgress { Stage = WeaveStage.Scenes, Percent = 20, StageFraction = 0.4 });
        Assert.Equal(StageRowState.Done, _vm.Stages[0].State);
        Assert.Equal(StageRowState.Weaving, _vm.Stages[1].State);
        Assert.Equal(StageRowState.Waiting, _vm.Stages[2].State);
        Assert.Equal(20, _vm.Percent);
        Assert.Equal(0.2, _vm.ProgressFraction, precision: 5);
        Assert.Equal("making…", _vm.Stages[1].StateLabel);
        Assert.Equal("Writing · 20%", _vm.MiniDetail);
        Assert.Equal("MAKING · 20%", _vm.EyebrowText);
    }

    [Fact]
    public async Task CollapsePersistsAcrossNavigation_SingletonState()
    {
        await _vm.ShowForWeaveAsync();
        _vm.CollapseCommand.Execute(null);
        Assert.Equal(WeaveSheetState.Collapsed, _vm.State);
        Assert.True(_vm.IsCollapsed);

        // Progress keeps flowing while collapsed (nothing re-expands uninvited),
        // which is exactly what every page's host observes after navigation.
        _host.RaiseProgress(new WeaveProgress { Stage = WeaveStage.Narration, Percent = 62, StageFraction = 0.3 });
        Assert.Equal(WeaveSheetState.Collapsed, _vm.State);
        Assert.Equal("Voice · 62%", _vm.MiniDetail);

        _vm.ExpandCommand.Execute(null);
        Assert.Equal(WeaveSheetState.Expanded, _vm.State);
    }

    [Fact]
    public async Task MiniBarSwipeDown_InertWhileWeaving()
    {
        await _vm.ShowForWeaveAsync();
        _vm.CollapseCommand.Execute(null);
        _vm.DismissFromMiniBarCommand.Execute(null);
        Assert.Equal(WeaveSheetState.Collapsed, _vm.State);   // still weaving — bar stays
    }

    [Fact]
    public async Task Completion_WhileCollapsed_TurnsMiniBarIntoReadyState()
    {
        await _vm.ShowForWeaveAsync();
        _vm.CollapseCommand.Execute(null);
        _host.RaiseCompleted(new StoryRecord { Id = 12, Title = "T", State = StoryState.AwaitingApproval });

        Assert.Equal(WeaveSheetState.Collapsed, _vm.State);
        Assert.True(_vm.IsDone);
        Assert.Equal(WeaveSheetViewModel.MiniTitleReady, _vm.MiniTitle);
        Assert.Equal("STORY READY", _vm.EyebrowText);
        Assert.Equal("Aria's story is ready.", _vm.Headline);
        Assert.All(_vm.Stages, s => Assert.Equal(StageRowState.Done, s.State));

        // Tap opens the done sheet; drag-down then dismisses entirely.
        _vm.ExpandCommand.Execute(null);
        Assert.Equal(WeaveSheetState.Expanded, _vm.State);
        var dismissed = false;
        _vm.Dismissed += (_, _) => dismissed = true;
        _vm.CollapseCommand.Execute(null);
        Assert.Equal(WeaveSheetState.Hidden, _vm.State);
        Assert.True(dismissed);
    }

    [Fact]
    public async Task Completion_OffersTeaser_AndPlayingDismissesSheet()
    {
        await _vm.ShowForWeaveAsync();
        _host.RaiseCompleted(new StoryRecord { Id = 12, Title = "T", State = StoryState.AwaitingApproval });
        Assert.True(_vm.ShowTeaserCta);

        await _vm.PlayTeaserCommand.ExecuteAsync(null);
        Assert.Contains(Routes.Player, _navigator.Routes);
        Assert.Equal(WeaveSheetState.Hidden, _vm.State);
    }

    [Fact]
    public async Task KidMode_HidesTeaserCta_SheetStaysStagesAndCooldownOnly()
    {
        await _vm.ShowForWeaveAsync();
        _host.RaiseCompleted(new StoryRecord { Id = 12, Title = "T", State = StoryState.AwaitingApproval });
        Assert.True(_vm.ShowTeaserCta);

        // On the Kid Shelf the approval-bearing teaser flow must not be reachable.
        _kidMode.Enter();
        _vm.CollapseCommand.Execute(null);   // state change recomputes the CTA
        _vm.ExpandCommand.Execute(null);
        Assert.False(_vm.ShowTeaserCta);

        _kidMode.Exit();
        _vm.CollapseCommand.Execute(null);
        _vm.ExpandCommand.Execute(null);
        Assert.True(_vm.ShowTeaserCta);
    }

    [Fact]
    public void ProgressWhileHidden_AutoSurfacesMiniBar()
    {
        // Resume-at-startup path: no page ever opened the sheet.
        _host.RaiseProgress(new WeaveProgress { Stage = WeaveStage.Outline, Percent = 2, StageFraction = 0.4 });
        Assert.Equal(WeaveSheetState.Collapsed, _vm.State);
        Assert.Equal(WeaveSheetViewModel.DisplayFloorPercent, _vm.Percent); // 2% real, floored for display
    }

    [Fact]
    public async Task Failure_ShowsGentleMessage_AndDismisses()
    {
        await _vm.ShowForWeaveAsync();
        _host.RaiseFailed(StoryPipeline.GentleFailureMessage);
        Assert.True(_vm.IsFailed);
        Assert.Equal(StoryPipeline.GentleFailureMessage, _vm.FailureMessage);
        Assert.Equal("↓ close", _vm.CloseLabel);

        _vm.CollapseCommand.Execute(null);   // finished sheet dismisses rather than collapses
        Assert.Equal(WeaveSheetState.Hidden, _vm.State);
    }

    [Fact]
    public async Task MakingCopy_UsesSoftHumWording()
    {
        await _vm.ShowForWeaveAsync();
        Assert.Equal(WeaveSheetViewModel.BackgroundHint, _vm.SubText);
    }

    [Fact]
    public async Task StopWeave_FirstTapArms_SecondTapDiscardsAndHides()
    {
        await _vm.ShowForWeaveAsync();
        Assert.True(_vm.ShowStopRow);

        var first = _vm.StopWeaveCommand.ExecuteAsync(null);   // arms; auto-revert timer runs
        Assert.True(_vm.IsConfirmingStop);
        Assert.Contains("tap again", _vm.StopLabel);

        await _vm.StopWeaveCommand.ExecuteAsync(null);         // confirms
        Assert.True(_host.DiscardCalled);
        Assert.Equal(WeaveSheetState.Hidden, _vm.State);
        Assert.False(_vm.IsConfirmingStop);
    }

    [Fact]
    public async Task StopRow_HiddenWhenDoneAndInKidMode()
    {
        await _vm.ShowForWeaveAsync();
        _kidMode.Enter();
        Assert.False(_vm.ShowStopRow);
        _kidMode.Exit();
        _host.RaiseCompleted(new StoryRecord { Id = 9, Title = "T" });
        Assert.False(_vm.ShowStopRow);
    }

    [Fact]
    public async Task QaExpand_OpensExpandedOverIdleHost()
    {
        await _vm.ExpandForQaAsync();
        Assert.Equal(WeaveSheetState.Expanded, _vm.State);
        Assert.Equal(WeaveSheetViewModel.DisplayFloorPercent, _vm.Percent);
        Assert.Equal("Making Aria's story…", _vm.Headline);
    }

    [Fact]
    public void StageTicks_MatchPipelineBoundaries()
    {
        Assert.Equal([0.05, 0.45, 0.50, 1.0], WeaveSheetViewModel.StageTicks);
    }
}

public class PlayerViewModelTests : IAsyncLifetime
{
    private TestEnv _env = null!;
    private FakePlayerProvider _players = null!;
    private PlayerLaunchContext _context = null!;
    private PlayerViewModel _vm = null!;
    private StoryRecord _story = null!;

    public async Task InitializeAsync()
    {
        _env = await TestEnv.CreateAsync();
        _players = new FakePlayerProvider();
        _context = new PlayerLaunchContext();
        _vm = new PlayerViewModel(_env.Store, _players, new PosterComposer(), _context, new FakeNavigator(),
            new NullHaptics(), new FakeEntitlements(), new InMemorySettingsStore(), new FakeAmbientPlayer(),
            new Core.Audio.AmbientTrackProvider(_env.Options));

        var profile = TestEnv.Profile();
        await _env.Store.SaveProfileAsync(profile);
        _story = new StoryRecord
        {
            ProfileId = profile.Id,
            Title = "The Lantern Fox",
            Mode = StoryMode.Series,
            EpisodeNumber = 2,
            RecapLine = "Previously: a gentle beginning.",
            State = StoryState.AwaitingApproval,
            DurationSeconds = 300,
            CreatedUtc = DateTime.UtcNow,
        };
        await _env.Store.SaveStoryAsync(_story);
        _context.StoryId = _story.Id;
        _context.TeaserOnly = true;
    }

    public async Task DisposeAsync() => await _env.DisposeAsync();

    [Fact]
    public async Task Activate_LoadsStoryAndTeaserMode()
    {
        await _vm.ActivateAsync();
        Assert.Equal("The Lantern Fox", _vm.Title);
        Assert.Equal("EPISODE 2 · 4 LAYERS · TILTS WITH YOUR PHONE", _vm.Eyebrow);
        Assert.True(_vm.IsTeaserOnly);
        Assert.True(_vm.IsAwaitingApproval);
        Assert.True(_players.Player.LoadedTeaserOnly);
        Assert.Equal("Previously: a gentle beginning.", _vm.RecapLine);
    }

    [Fact]
    public async Task Approve_TransitionsToReadyAndPersists()
    {
        await _vm.ActivateAsync();
        await _vm.ApproveCommand.ExecuteAsync(null);

        Assert.False(_vm.IsAwaitingApproval);
        Assert.False(_vm.IsTeaserOnly);
        Assert.False(_players.Player.LoadedTeaserOnly); // reloaded full
        var reloaded = await _env.Store.GetStoryAsync(_story.Id);
        Assert.Equal(StoryState.Ready, reloaded!.State);
    }

    [Fact]
    public async Task PlayPause_TogglesPlayer()
    {
        await _vm.ActivateAsync();
        await _vm.PlayPauseCommand.ExecuteAsync(null);
        Assert.True(_vm.IsPlaying);
        Assert.True(_players.Player.IsPlaying);
        await _vm.PlayPauseCommand.ExecuteAsync(null);
        Assert.False(_vm.IsPlaying);
    }

    [Fact]
    public async Task Seek_MapsFractionToSeconds()
    {
        await _vm.ActivateAsync();
        _vm.DurationSeconds = 200;
        await _vm.SeekToFractionCommand.ExecuteAsync(0.5);
        Assert.Equal(100, _players.Player.PositionSeconds, precision: 1);
    }

    [Fact]
    public async Task OneShot_UsesOneShotEyebrow()
    {
        _story.Mode = StoryMode.OneShot;
        _story.EpisodeNumber = 0;
        await _env.Store.SaveStoryAsync(_story);
        await _vm.ActivateAsync();
        Assert.StartsWith("ONE-SHOT", _vm.Eyebrow);
    }
}

public class SettingsViewModelTests : IAsyncLifetime
{
    private TestEnv _env = null!;
    private FakeBiometricGate _gate = null!;
    private InMemorySettingsStore _settings = null!;
    private FakeNavigator _navigator = null!;
    private SettingsViewModel _vm = null!;

    public async Task InitializeAsync()
    {
        _env = await TestEnv.CreateAsync();
        _gate = new FakeBiometricGate();
        _settings = new InMemorySettingsStore();
        _navigator = new FakeNavigator();
        var clock = new FakeClock();
        _vm = new SettingsViewModel(_gate, _settings, new StoreEntitlements(_settings, clock), _env.Store, _navigator,
            new NullHaptics());
        await _env.Store.SaveProfileAsync(TestEnv.Profile());
    }

    public async Task DisposeAsync() => await _env.DisposeAsync();

    [Fact]
    public async Task Gate_PassOpensSettingsWithBadge()
    {
        var opened = await _vm.ActivateAsync(CancellationToken.None);
        Assert.True(opened);
        Assert.True(_vm.IsUnlocked);
        Assert.Equal("✓ FACE ID", _vm.BadgeText);
        Assert.Equal("Trial · 7 nights left", _vm.TrialLine);
    }

    [Fact]
    public async Task Gate_FailKeepsSettingsLocked()
    {
        _gate.Result = false;
        var opened = await _vm.ActivateAsync(CancellationToken.None);
        Assert.False(opened);
        Assert.False(_vm.IsUnlocked);
        Assert.True(_vm.GateFailed);
    }

    [Fact]
    public async Task Toggles_PersistToSettingsStore()
    {
        await _vm.ActivateAsync(CancellationToken.None);
        _vm.ToggleApprovalCommand.Execute(null);
        Assert.False(_settings.ApprovalStep);
        _vm.ToggleNotificationsCommand.Execute(null);
        Assert.False(_settings.StoryReadyNotifications);
    }

    /// <summary>Quality analytics was removed entirely (founder decision 2026-07-31).
    /// This asserts the removal is structural — the settings seam carries no analytics
    /// member at all, so nothing can quietly start writing events again.</summary>
    [Fact]
    public void Analytics_HasNoSettingsSeamAtAll()
    {
        var settingsMembers = typeof(ISettingsStore).GetMembers().Select(m => m.Name);
        Assert.DoesNotContain(settingsMembers, n => n.Contains("Analytic", StringComparison.OrdinalIgnoreCase));

        var vmMembers = typeof(SettingsViewModel).GetMembers().Select(m => m.Name);
        Assert.DoesNotContain(vmMembers, n => n.Contains("Analytic", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Upgrade_RoutesToPremiumPage()
    {
        await _vm.ActivateAsync(CancellationToken.None);
        await _vm.ShowUpgradeCommand.ExecuteAsync(null);
        Assert.Contains(Routes.Premium, _navigator.Routes);
    }

    [Fact]
    public async Task TrialCard_ReflectsDebugPremiumUnlock()
    {
        _settings.DebugPremiumOverride = true;
        await _vm.ActivateAsync(CancellationToken.None);
        Assert.Equal("Premium · unlocked for testing", _vm.TrialLine);
    }

    [Fact]
    public async Task ProfileEditor_SavesChanges()
    {
        await _vm.ActivateAsync(CancellationToken.None);
        _vm.ProfileName = "Nova";
        _vm.SelectProfileAgeCommand.Execute(7);
        await _vm.SaveProfileCommand.ExecuteAsync(null);

        var profile = await _env.Store.GetActiveProfileAsync();
        Assert.Equal("Nova", profile!.Name);
        Assert.Equal(7, profile.Age);
    }

    [Fact]
    public void FormatBytes_IsHumane()
    {
        Assert.Equal("512 B", SettingsViewModel.FormatBytes(512));
        Assert.Equal("1.5 KB", SettingsViewModel.FormatBytes(1536));
        Assert.Equal("42.5 MB", SettingsViewModel.FormatBytes((long)(42.5 * 1024 * 1024)));
    }

    [Fact]
    public async Task ContentFilterRow_ShowsCurrentLevelName()
    {
        await _vm.ActivateAsync(CancellationToken.None);
        Assert.Equal("Gentle", _vm.ContentFilterName);

        _settings.ContentFilter = ContentFilterLevel.Braveish;
        await _vm.ActivateAsync(CancellationToken.None);   // re-entry refreshes the row
        Assert.Equal("Brave-ish", _vm.ContentFilterName);
    }

    [Fact]
    public async Task ContentFilterRow_RoutesToFiltersPage()
    {
        await _vm.ActivateAsync(CancellationToken.None);
        await _vm.OpenContentFiltersCommand.ExecuteAsync(null);
        Assert.Contains(Routes.ContentFilters, _navigator.Routes);
    }
}

public class ContentFiltersViewModelTests
{
    private readonly InMemorySettingsStore _settings = new();
    private readonly FakeNavigator _navigator = new();
    private readonly FakeHaptics _haptics = new();

    private ContentFiltersViewModel Vm() => new(_settings, _navigator, _haptics);

    [Fact]
    public void SettingsStore_DefaultsToGentle()
    {
        Assert.Equal(ContentFilterLevel.Gentle, _settings.ContentFilter);
        var vm = Vm();
        Assert.True(vm.IsGentle);
        Assert.False(vm.IsBraveish);
    }

    [Fact]
    public void SelectBraveish_PersistsRoundTripWithSelectionHaptic()
    {
        var vm = Vm();
        vm.SelectBraveishCommand.Execute(null);

        Assert.Equal(ContentFilterLevel.Braveish, _settings.ContentFilter);
        Assert.True(vm.IsBraveish);
        Assert.Equal(1, _haptics.Count("Selection"));

        // A fresh VM (fresh page) reads the persisted level back.
        var again = Vm();
        Assert.True(again.IsBraveish);

        again.SelectGentleCommand.Execute(null);
        Assert.Equal(ContentFilterLevel.Gentle, _settings.ContentFilter);
    }

    [Fact]
    public void ReselectingSameLevel_DoesNotTickAgain()
    {
        var vm = Vm();
        vm.SelectGentleCommand.Execute(null);              // already gentle — no change
        Assert.Empty(_haptics.Calls);
    }

    [Fact]
    public void Activate_ReReadsTheStore()
    {
        var vm = Vm();
        _settings.ContentFilter = ContentFilterLevel.Braveish;
        vm.Activate();
        Assert.True(vm.IsBraveish);
    }

    [Fact]
    public async Task Close_PopsBack()
    {
        await Vm().CloseCommand.ExecuteAsync(null);
        Assert.Equal(1, _navigator.BackCount);
    }

    [Fact]
    public void DisplayName_MatchesParentFacingCopy()
    {
        Assert.Equal("Gentle", ContentFiltersViewModel.DisplayName(ContentFilterLevel.Gentle));
        Assert.Equal("Brave-ish", ContentFiltersViewModel.DisplayName(ContentFilterLevel.Braveish));
    }
}

public class OnboardingViewModelTests : IAsyncLifetime
{
    private TestEnv _env = null!;
    private OnboardingState _state = null!;
    private FakeNavigator _navigator = null!;
    private InMemorySettingsStore _settings = null!;

    public async Task InitializeAsync()
    {
        _env = await TestEnv.CreateAsync();
        _state = new OnboardingState();
        _navigator = new FakeNavigator();
        _settings = new InMemorySettingsStore();
    }

    public async Task DisposeAsync() => await _env.DisposeAsync();

    [Fact]
    public async Task ProfileStep_WritesStateAndAdvances()
    {
        var vm = new OnboardingProfileViewModel(_state, _navigator, new NullHaptics());
        vm.Nickname = "Milo";
        vm.SelectAgeCommand.Execute(4);
        vm.ToggleInterestCommand.Execute(vm.Interests.First(i => i.Name == "Space"));
        await vm.ContinueCommand.ExecuteAsync(null);

        Assert.Equal("Milo", _state.Profile.Name);
        Assert.Equal(4, _state.Profile.Age);
        Assert.Contains("Space", _state.Profile.Interests);
        Assert.Contains(Routes.OnboardingVoice, _navigator.Routes);
    }

    [Fact]
    public void ProfileStep_HasSensibleDefaults()
    {
        var vm = new OnboardingProfileViewModel(_state, _navigator, new NullHaptics());
        Assert.Equal(5, vm.SelectedAge);
        Assert.True(vm.Interests.First(i => i.Name == "Foxes").IsSelected);
        Assert.True(vm.Interests.First(i => i.Name == "Ocean").IsSelected);
        Assert.True(vm.Interests.First(i => i.Name == "Magic").IsSelected);
    }

    [Fact]
    public async Task VoiceStep_GatesFifteenAndKeepsHintCopy()
    {
        var clock = new FakeClock();
        var vm = new OnboardingVoiceViewModel(_state, _navigator, new FakeTtsEngine(), new StoreEntitlements(_settings, clock),
            new NullHaptics(), new NullVoiceSamplePlayer());
        vm.SelectLengthCommand.Execute(StoryLength.Fifteen);
        Assert.True(vm.ShowPremiumHint);
        Assert.NotEqual(StoryLength.Fifteen, vm.SelectedLength);

        Assert.Equal(OnboardingVoiceViewModel.SeriesHint, vm.ModeHint);
        vm.SetSeriesCommand.Execute(false);
        Assert.Equal(OnboardingVoiceViewModel.OneShotHint, vm.ModeHint);

        vm.SelectVoiceCommand.Execute(vm.Voices[2]);
        await vm.ContinueCommand.ExecuteAsync(null);
        Assert.Equal(NarratorVoice.Sage, _state.Profile.Voice);
        Assert.Equal(StoryMode.OneShot, _state.Profile.Mode);
    }

    [Fact]
    public async Task RulesStep_CreatesProfileWelcomeStoryAndCompletesOnboarding()
    {
        var clock = new FakeClock();
        var factory = new WelcomeStoryFactory(_env.Store, new PosterComposer(), new NullPosterRasterizer(), _env.Options, clock);
        var vm = new OnboardingRulesViewModel(_state, _navigator, _env.Store, _settings, clock, factory, new NullHaptics());
        _state.Profile.Name = "Aria";

        await vm.StartCommand.ExecuteAsync(null);

        Assert.True(_settings.OnboardingComplete);
        Assert.NotNull(_settings.FirstRunUtc);
        var profile = await _env.Store.GetActiveProfileAsync();
        Assert.NotNull(profile);

        var stories = await _env.Store.GetStoriesAsync(profile.Id);
        var welcome = Assert.Single(stories);
        Assert.Equal(WelcomeStoryFactory.WelcomeTitle, welcome.Title);
        Assert.Equal(StoryState.Ready, welcome.State);
        Assert.Equal(string.Empty, welcome.AudioPath); // live TTS mode
        Assert.True(welcome.DurationSeconds > 0);
        Assert.Contains(Routes.Home, _navigator.Routes);
    }

    [Fact]
    public async Task WelcomeStory_IsDeterministicAcrossInstalls()
    {
        var clock = new FakeClock();
        var factory = new WelcomeStoryFactory(_env.Store, new PosterComposer(), new NullPosterRasterizer(), _env.Options, clock);
        var profile = TestEnv.Profile();
        await _env.Store.SaveProfileAsync(profile);

        var a = await factory.CreateAsync(profile, CancellationToken.None);
        var b = await factory.CreateAsync(profile, CancellationToken.None);
        Assert.Equal(a.FullText, b.FullText);
        Assert.Equal(a.PosterSpecJson, b.PosterSpecJson);
    }
}

public class PremiumViewModelTests
{
    [Fact]
    public void TestingUnlock_FlipsOverrideAndEntitlements()
    {
        var settings = new InMemorySettingsStore();
        var navigator = new FakeNavigator();
        var entitlements = new StoreEntitlements(settings, new FakeClock());
        var vm = FakePremiumStore.CreateViewModel(settings, navigator, new NullHaptics());

        Assert.False(vm.IsUnlockedForTesting);
        vm.ToggleTestingUnlockCommand.Execute(null);

        // The command is Debug-only; in Debug test runs it flips, in Release it must not.
        if (PremiumViewModel.IsDebugBuild)
        {
            Assert.True(vm.IsUnlockedForTesting);
            Assert.True(settings.DebugPremiumOverride);
            Assert.True(entitlements.IsPremium);
            Assert.Equal(15, entitlements.CooldownMinutes);

            vm.ToggleTestingUnlockCommand.Execute(null);
            Assert.False(settings.DebugPremiumOverride);
            Assert.False(entitlements.IsPremium);
        }
        else
        {
            Assert.False(settings.DebugPremiumOverride);
            Assert.False(entitlements.IsPremium);
        }
    }

    [Fact]
    public async Task Activate_ReadsOverrideFromStore()
    {
        var settings = new InMemorySettingsStore { DebugPremiumOverride = true };
        var vm = FakePremiumStore.CreateViewModel(settings, new FakeNavigator(), new NullHaptics());
        await vm.ActivateAsync();
        Assert.True(vm.IsUnlockedForTesting);
        Assert.StartsWith("Premium · unlocked for testing", vm.TestingRowText);
    }

    [Fact]
    public async Task Close_NavigatesBack()
    {
        var navigator = new FakeNavigator();
        var vm = FakePremiumStore.CreateViewModel(new InMemorySettingsStore(), navigator, new NullHaptics());
        await vm.CloseCommand.ExecuteAsync(null);
        Assert.Equal(1, navigator.BackCount);
    }
}

/// <summary>Pins the length-track snap math the App control commits on release
/// (drag-to-nearest-detent; midpoints round away from zero so both halves of the
/// track behave symmetrically).</summary>
public class LengthDetentsTests
{
    [Theory]
    [InlineData(0.00, StoryLength.Five)]
    [InlineData(0.10, StoryLength.Five)]
    [InlineData(0.24, StoryLength.Five)]
    [InlineData(0.25, StoryLength.Ten)]     // midpoint rounds up (away from zero)
    [InlineData(0.50, StoryLength.Ten)]
    [InlineData(0.74, StoryLength.Ten)]
    [InlineData(0.75, StoryLength.Fifteen)] // symmetric with 0.25
    [InlineData(0.90, StoryLength.Fifteen)]
    [InlineData(1.00, StoryLength.Fifteen)]
    public void Nearest_SnapsToNearestDetent(double fraction, StoryLength expected) =>
        Assert.Equal(expected, LengthDetents.Nearest(fraction));

    [Theory]
    [InlineData(-0.5, StoryLength.Five)]
    [InlineData(1.5, StoryLength.Fifteen)]
    public void Nearest_ClampsOutOfRangeFractions(double fraction, StoryLength expected) =>
        Assert.Equal(expected, LengthDetents.Nearest(fraction));

    [Theory]
    [InlineData(StoryLength.Five, 0)]
    [InlineData(StoryLength.Ten, 1)]
    [InlineData(StoryLength.Fifteen, 2)]
    public void IndexOf_MapsLengthsToDetents(StoryLength length, int expected) =>
        Assert.Equal(expected, LengthDetents.IndexOf(length));

    [Fact]
    public void IndexOf_UnknownValueSettlesOnFirstDetent() =>
        Assert.Equal(0, LengthDetents.IndexOf((StoryLength)42));
}
