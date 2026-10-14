using StoryTime.Contracts;
using StoryTime.Core.Abstractions;
using StoryTime.Core.Pipeline;
using StoryTime.Core.Poster;
using StoryTime.Core.Services;
using StoryTime.Core.ViewModels;
using Xunit;

namespace StoryTime.Core.Tests;

/// <summary>The Tick coalescing window (≥60 ms) that keeps scrubbing from buzzing —
/// PlatformHaptics delegates to this Core helper precisely so it is testable here.</summary>
public class HapticTickLimiterTests
{
    [Fact]
    public void FirstTick_AlwaysPasses()
    {
        var limiter = new HapticTickLimiter(nowMs: () => 0);
        Assert.True(limiter.TryPass());
    }

    [Fact]
    public void TicksInsideTheWindow_AreCoalesced()
    {
        long now = 1000;
        var limiter = new HapticTickLimiter(nowMs: () => now);

        Assert.True(limiter.TryPass());
        now += 20;
        Assert.False(limiter.TryPass());
        now += 39;   // 59 ms after the passed tick — still inside
        Assert.False(limiter.TryPass());
        now += 1;    // exactly 60 ms — window reopens
        Assert.True(limiter.TryPass());
    }

    [Fact]
    public void BlockedTicks_DoNotResetTheWindow()
    {
        long now = 0;
        var limiter = new HapticTickLimiter(nowMs: () => now);

        Assert.True(limiter.TryPass());
        // A blocked tick every 30 ms must not keep pushing the window forward.
        now = 30;
        Assert.False(limiter.TryPass());
        now = 60;
        Assert.True(limiter.TryPass());
    }

    [Fact]
    public void CustomInterval_IsHonoured()
    {
        long now = 0;
        var limiter = new HapticTickLimiter(minIntervalMs: 120, nowMs: () => now);
        Assert.True(limiter.TryPass());
        now = 119;
        Assert.False(limiter.TryPass());
        now = 120;
        Assert.True(limiter.TryPass());
    }
}

/// <summary>The anticipatory-haptics coalescing contract (docs/design/mockup-spec.md): a
/// press-down haptic fires at gesture recognition and must swallow the SAME gesture's
/// later command-level haptic — without ever swallowing the NEXT gesture's press.</summary>
public class HapticPressCoalescingTests
{
    private static (HapticTickLimiter Limiter, Func<long> Read, Action<long> Advance) Clocked(long windowMs)
    {
        long now = 0;
        var limiter = new HapticTickLimiter(windowMs, () => now);
        return (limiter, () => now, ms => now += ms);
    }

    [Fact]
    public void Press_ArmsTheLongRefractory_SoTheCommandHapticIsSwallowed()
    {
        var (limiter, _, advance) = Clocked(HapticTickLimiter.LightWindowMs);

        Assert.True(limiter.TryPassPress());          // finger down — this is the thump

        advance(HapticTickLimiter.LightWindowMs);     // past the short window…
        Assert.False(limiter.TryPass());              // …but the command still coalesces

        advance(HapticTickLimiter.PressRefractoryMs - HapticTickLimiter.LightWindowMs - 1);
        Assert.False(limiter.TryPass());              // 349 ms after the press — still inside
    }

    [Fact]
    public void CommandHaptic_PassesOnceTheRefractoryElapses()
    {
        var (limiter, _, advance) = Clocked(HapticTickLimiter.LightWindowMs);

        Assert.True(limiter.TryPassPress());
        advance(HapticTickLimiter.PressRefractoryMs);
        Assert.True(limiter.TryPass());   // an unrelated later command is not suppressed forever
    }

    [Fact]
    public void ANewPress_IsNeverSilencedByThePreviousPressRefractory()
    {
        var (limiter, _, advance) = Clocked(HapticTickLimiter.LightWindowMs);

        Assert.True(limiter.TryPassPress());
        advance(HapticTickLimiter.LightWindowMs);   // fast repeat tap, well inside 350 ms
        Assert.True(limiter.TryPassPress());        // a new finger is a new gesture
    }

    [Fact]
    public void TwoPressSourcesOnOneTap_StayASingleThump()
    {
        // e.g. a row's TouchBehavior and the pill inside it both seeing the same pointer-down.
        var (limiter, _, advance) = Clocked(HapticTickLimiter.LightWindowMs);

        Assert.True(limiter.TryPassPress());
        advance(HapticTickLimiter.LightWindowMs - 1);
        Assert.False(limiter.TryPassPress());
    }

    [Fact]
    public void APassedCommandCall_ReArmsOnlyTheShortWindow_NotTheRefractory()
    {
        var (limiter, _, advance) = Clocked(HapticTickLimiter.SelectionWindowMs);

        Assert.True(limiter.TryPass());
        advance(HapticTickLimiter.SelectionWindowMs);
        Assert.True(limiter.TryPass());   // no press happened, so no long refractory is armed
    }

    [Fact]
    public void BlockedPressCalls_DoNotPushTheWindowForward()
    {
        var (limiter, _, advance) = Clocked(HapticTickLimiter.SelectionWindowMs);

        Assert.True(limiter.TryPassPress());
        advance(HapticTickLimiter.SelectionWindowMs - 20);
        Assert.False(limiter.TryPassPress());
        advance(20);   // exactly one short window after the PASSED press
        Assert.True(limiter.TryPassPress());
    }

    [Fact]
    public void EachKindCarriesItsOwnWindow()
    {
        // Light and Medium happen to share a value today; assert against the constants so
        // the test still means something if one of them is retuned.
        long[] windows =
        [
            HapticTickLimiter.LightWindowMs,
            HapticTickLimiter.MediumWindowMs,
            HapticTickLimiter.SelectionWindowMs,
        ];

        foreach (var windowMs in windows)
        {
            var (limiter, _, advance) = Clocked(windowMs);

            Assert.True(limiter.TryPassPress());
            advance(windowMs - 1);
            Assert.False(limiter.TryPassPress());
            advance(1);
            Assert.True(limiter.TryPassPress());
        }
    }

    [Fact]
    public void PressWindows_AreShorterThanTheRefractoryTheyArm()
    {
        // The whole mechanism depends on this ordering: the gate that lets the NEXT press
        // through must be strictly shorter than the refractory that eats the LAST command.
        Assert.True(HapticTickLimiter.LightWindowMs < HapticTickLimiter.PressRefractoryMs);
        Assert.True(HapticTickLimiter.MediumWindowMs < HapticTickLimiter.PressRefractoryMs);
        Assert.True(HapticTickLimiter.SelectionWindowMs < HapticTickLimiter.PressRefractoryMs);
    }

    [Fact]
    public void PressRefractory_IsCustomisable()
    {
        var (limiter, _, advance) = Clocked(HapticTickLimiter.LightWindowMs);

        Assert.True(limiter.TryPassPress(refractoryMs: 1000));
        advance(999);
        Assert.False(limiter.TryPass());
        advance(1);
        Assert.True(limiter.TryPass());
    }
}

/// <summary>The weave sheet's stage-transition "loom-click" and story-ready Celebrate,
/// including the foreground gate: a background weave must never buzz the pocket.</summary>
public class WeaveSheetHapticsTests : IAsyncLifetime
{
    private TestEnv _env = null!;
    private FakeWeaveHost _host = null!;
    private FakeHaptics _haptics = null!;
    private WeaveSheetViewModel _vm = null!;

    public async Task InitializeAsync()
    {
        _env = await TestEnv.CreateAsync();
        _host = new FakeWeaveHost();
        _haptics = new FakeHaptics();
        var settings = new InMemorySettingsStore();
        var clock = new FakeClock();
        var entitlements = new StubEntitlements(settings, clock);
        var cooldown = new CooldownService(settings, clock, entitlements);
        _vm = new WeaveSheetViewModel(_host, _env.Store, cooldown, new FakeNavigator(), new PlayerLaunchContext(),
            settings, _haptics, entitlements, new KidModeService());
        await _env.Store.SaveProfileAsync(TestEnv.Profile());
    }

    public async Task DisposeAsync() => await _env.DisposeAsync();

    private void Raise(WeaveStage stage, int percent) =>
        _host.RaiseProgress(new WeaveProgress { Stage = stage, Percent = percent, StageFraction = 0.5 });

    [Fact]
    public async Task StageTransitions_TickOncePerStage_NeverOnFirstOrWithinStage()
    {
        await _vm.ShowForWeaveAsync();

        Raise(WeaveStage.Outline, 2);      // first stage seen — weave start already answered with Medium
        Assert.Equal(0, _haptics.Count("Tick"));

        Raise(WeaveStage.Outline, 4);      // progress within a stage is not a transition
        Assert.Equal(0, _haptics.Count("Tick"));

        Raise(WeaveStage.Scenes, 20);
        Assert.Equal(1, _haptics.Count("Tick"));

        Raise(WeaveStage.Scenes, 40);
        Assert.Equal(1, _haptics.Count("Tick"));

        Raise(WeaveStage.Poster, 47);
        Assert.Equal(2, _haptics.Count("Tick"));

        Raise(WeaveStage.Narration, 60);
        Assert.Equal(3, _haptics.Count("Tick"));
    }

    [Fact]
    public async Task WeaveComplete_CelebratesOnce_NotPlainSuccess()
    {
        await _vm.ShowForWeaveAsync();
        Raise(WeaveStage.Narration, 90);

        var story = new StoryRecord { Id = 7, Title = "The Lantern Fox", State = StoryState.Ready };
        _host.RaiseCompleted(story);
        _host.RaiseCompleted(story);   // duplicate completion must not double-celebrate

        Assert.Equal(1, _haptics.Count("Celebrate"));
        Assert.Equal(0, _haptics.Count("Success"));
    }

    [Fact]
    public async Task Backgrounded_StageTicksAndCelebrate_StaySilent()
    {
        await _vm.ShowForWeaveAsync();
        _vm.IsForegrounded = false;

        Raise(WeaveStage.Outline, 2);
        Raise(WeaveStage.Scenes, 20);
        Raise(WeaveStage.Poster, 47);
        _host.RaiseCompleted(new StoryRecord { Id = 8, Title = "Quiet", State = StoryState.Ready });

        Assert.Equal(0, _haptics.Count("Tick"));
        Assert.Equal(0, _haptics.Count("Celebrate"));

        // Back in the foreground the loom-click resumes from where it left off.
        _vm.IsForegrounded = true;
    }

    [Fact]
    public async Task ResumedWeave_FirstObservedStage_DoesNotTick()
    {
        // App relaunch mid-pipeline: the sheet auto-surfaces on the first progress event.
        Raise(WeaveStage.Narration, 62);
        Assert.Equal(0, _haptics.Count("Tick"));
        Assert.Equal(WeaveSheetState.Collapsed, _vm.State);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ExpandAndCollapse_BothAnswerWithLight()
    {
        await _vm.ShowForWeaveAsync();
        Raise(WeaveStage.Outline, 2);
        _haptics.Calls.Clear();

        _vm.CollapseCommand.Execute(null);   // mid-weave: collapse commit
        Assert.Equal(1, _haptics.Count("Light"));

        _vm.ExpandCommand.Execute(null);     // expand commit
        Assert.Equal(2, _haptics.Count("Light"));
    }

    [Fact]
    public async Task MiniBarDismiss_LightsOnlyWhenFinished()
    {
        await _vm.ShowForWeaveAsync();
        Raise(WeaveStage.Outline, 2);
        _vm.CollapseCommand.Execute(null);
        _haptics.Calls.Clear();

        _vm.DismissFromMiniBarCommand.Execute(null);   // inert mid-weave
        Assert.Empty(_haptics.Calls);

        _host.RaiseCompleted(new StoryRecord { Id = 9, Title = "Done", State = StoryState.Ready });
        _haptics.Calls.Clear();
        _vm.DismissFromMiniBarCommand.Execute(null);   // dismiss commit
        Assert.Equal(1, _haptics.Count("Light"));
        Assert.Equal(WeaveSheetState.Hidden, _vm.State);
    }
}

/// <summary>The rest of the VM-owned haptic map: player, premium, onboarding.</summary>
public class HapticMapTests : IAsyncLifetime
{
    private TestEnv _env = null!;

    public async Task InitializeAsync() => _env = await TestEnv.CreateAsync();

    public async Task DisposeAsync() => await _env.DisposeAsync();

    private async Task<(PlayerViewModel Vm, FakeHaptics Haptics)> CreatePlayerAsync()
    {
        var haptics = new FakeHaptics();
        var players = new FakePlayerProvider();
        var context = new PlayerLaunchContext();
        var vm = new PlayerViewModel(_env.Store, players, new PosterComposer(), context, new FakeNavigator(),
            haptics, new FakeEntitlements(), new InMemorySettingsStore(), new FakeAmbientPlayer(),
            new Core.Audio.AmbientTrackProvider(_env.Options));

        var profile = TestEnv.Profile();
        await _env.Store.SaveProfileAsync(profile);
        var story = new StoryRecord
        {
            ProfileId = profile.Id,
            Title = "The Lantern Fox",
            Mode = StoryMode.Series,
            State = StoryState.AwaitingApproval,
            DurationSeconds = 300,
            CreatedUtc = DateTime.UtcNow,
        };
        await _env.Store.SaveStoryAsync(story);
        context.StoryId = story.Id;
        context.TeaserOnly = true;
        await vm.ActivateAsync();
        return (vm, haptics);
    }

    [Fact]
    public async Task PlayerOpen_AnswersWithLight()
    {
        var (_, haptics) = await CreatePlayerAsync();
        Assert.Equal(1, haptics.Count("Light"));
    }

    [Fact]
    public async Task SeekCommit_IsSelection()
    {
        var (vm, haptics) = await CreatePlayerAsync();
        haptics.Calls.Clear();
        await vm.SeekToFractionCommand.ExecuteAsync(0.4);
        Assert.Equal(1, haptics.Count("Selection"));
    }

    [Fact]
    public async Task StopAndReplay_AnswerWithLight()
    {
        var (vm, haptics) = await CreatePlayerAsync();
        haptics.Calls.Clear();
        await vm.StopCommand.ExecuteAsync(null);
        await vm.ReplayCommand.ExecuteAsync(null);
        Assert.Equal(2, haptics.Count("Light"));
    }

    [Fact]
    public async Task ApprovalUnlock_Celebrates()
    {
        var (vm, haptics) = await CreatePlayerAsync();
        haptics.Calls.Clear();
        await vm.ApproveCommand.ExecuteAsync(null);
        Assert.Equal(1, haptics.Count("Celebrate"));
        Assert.Equal(0, haptics.Count("Success"));
    }

    [Fact]
    public async Task ApprovalUnlock_CelebrateIsTheTerminalCall()
    {
        // Anticipatory ordering, VM half: the press-down Light lands first (App layer) and
        // the outcome Celebrate must be LAST — it is never coalesced, so nothing may follow
        // it and re-order the felt sequence.
        var (vm, haptics) = await CreatePlayerAsync();
        haptics.Calls.Clear();
        await vm.ApproveCommand.ExecuteAsync(null);
        Assert.Equal("Celebrate", haptics.Calls[^1]);
    }

    [Fact]
    public void PremiumDebugUnlock_CelebrateIsTheTerminalCall()
    {
        var haptics = new FakeHaptics();
        var vm = new PremiumViewModel(new InMemorySettingsStore(), new FakeNavigator(), haptics);
        vm.Activate();
        haptics.Calls.Clear();

        vm.ToggleTestingUnlockCommand.Execute(null);
        Assert.Equal("Celebrate", haptics.Calls[^1]);
    }

    [Fact]
    public void PremiumDebugUnlock_Celebrates_RelockIsSelection()
    {
        var haptics = new FakeHaptics();
        var settings = new InMemorySettingsStore();
        var vm = new PremiumViewModel(settings, new FakeNavigator(), haptics);

        vm.ToggleTestingUnlockCommand.Execute(null);   // unlock
        Assert.Equal(1, haptics.Count("Celebrate"));

        vm.ToggleTestingUnlockCommand.Execute(null);   // relock
        Assert.Equal(1, haptics.Count("Selection"));
        Assert.Equal(1, haptics.Count("Celebrate"));
    }

    [Fact]
    public void PremiumOpen_AnswersWithLight()
    {
        var haptics = new FakeHaptics();
        var vm = new PremiumViewModel(new InMemorySettingsStore(), new FakeNavigator(), haptics);
        vm.Activate();
        Assert.Equal(1, haptics.Count("Light"));
    }

    [Fact]
    public async Task OnboardingFinalStart_IsMedium_CrossingTheThreshold()
    {
        var haptics = new FakeHaptics();
        var settings = new InMemorySettingsStore();
        var clock = new FakeClock();
        var state = new OnboardingState();
        state.Profile.Name = "Aria";
        var factory = new WelcomeStoryFactory(_env.Store, new PosterComposer(), new NullPosterRasterizer(),
            _env.Options, clock);
        var vm = new OnboardingRulesViewModel(state, new FakeNavigator(), _env.Store, settings, clock, factory, haptics);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(1, haptics.Count("Medium"));
        Assert.Equal(0, haptics.Count("Light"));
    }

    [Fact]
    public async Task SettingsGate_LightOnPromptAndPass_WarningOnFail()
    {
        var gate = new FakeBiometricGate();
        var settings = new InMemorySettingsStore();
        var clock = new FakeClock();
        var haptics = new FakeHaptics();
        var vm = new SettingsViewModel(gate, settings, new StubEntitlements(settings, clock), _env.Store,
            new FakeNavigator(), haptics);

        await _env.Store.SaveProfileAsync(TestEnv.Profile());

        gate.Result = true;
        Assert.True(await vm.ActivateAsync(CancellationToken.None));
        Assert.Equal(2, haptics.Count("Light"));   // prompt appearing + gate pass
        Assert.Equal(0, haptics.Count("Warning"));

        haptics.Calls.Clear();
        gate.Result = false;
        Assert.False(await vm.ActivateAsync(CancellationToken.None));
        Assert.Equal(1, haptics.Count("Light"));   // prompt appearing only
        Assert.Equal(1, haptics.Count("Warning")); // gate fail
    }
}
