using StoryTime.Core.Abstractions;
using StoryTime.Core.Engines.Narration;
using StoryTime.Core.Services;
using StoryTime.Core.ViewModels;
using Xunit;

namespace StoryTime.Core.Tests;

public class StorytellerVoiceViewModelTests
{
    private sealed class FakeNetwork : INetworkStatus
    {
        public NetworkKind Current { get; set; } = NetworkKind.Unknown;
        public event EventHandler? Changed;
        public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
    }

    private static StorytellerVoice Voice(bool serena) => new(
        new VoiceChoice { VoiceId = serena ? "com.apple.voice.premium.en-GB.Serena" : "samantha", DisplayName = serena ? "Serena (Premium)" : "Samantha" },
        serena,
        serena ? VoiceQualityTier.Premium : VoiceQualityTier.Default);

    private readonly StaticStorytellerVoices _voices = new(Voice(serena: false));
    private readonly FakeNetwork _network = new();
    private readonly FakeNavigator _navigator = new();

    private StorytellerVoiceViewModel Vm(StorytellerVoicePurpose purpose = StorytellerVoicePurpose.Onboarding)
    {
        var vm = new StorytellerVoiceViewModel(_voices, _network, _navigator, new FakeTtsEngine(), new NullVoiceSamplePlayer(), new FakeHaptics())
        {
            Delay = (_, _) => Task.CompletedTask,
        };
        vm.ApplyPurpose(purpose.ToString().ToLowerInvariant());
        return vm;
    }

    [Fact]
    public void BlocksUntilSerenaPremiumIsInstalled()
    {
        var vm = Vm();
        vm.Activate();
        Assert.False(vm.IsReady);
        Assert.True(vm.IsWaiting);
        Assert.False(vm.ContinueCommand.CanExecute(null));
        Assert.Equal(StorytellerVoiceViewModel.WaitingText, vm.Status);
        Assert.Empty(_navigator.Routes);
    }

    [Fact]
    public void DetectingSerena_PlaysAndMovesOnByItself()
    {
        var vm = Vm();
        vm.Activate();
        _voices.Current = Voice(serena: true);
        _voices.Refresh();                                   // AvailableVoicesDidChange / foreground re-check
        Assert.True(vm.IsReady);
        Assert.Contains("Serena", vm.Status);
        Assert.Equal([Routes.OnboardingRules], _navigator.Routes);
    }

    [Fact]
    public void CheckAgain_WhenStillMissing_SaysSoKindly()
    {
        var vm = Vm();
        vm.Activate();
        vm.CheckAgainCommand.Execute(null);
        Assert.Equal(StorytellerVoiceViewModel.NotYetText, vm.Status);
    }

    [Theory]
    [InlineData(NetworkKind.Cellular, StorytellerVoiceViewModel.NoticeCellular)]
    [InlineData(NetworkKind.WiFi, StorytellerVoiceViewModel.NoticeWiFi)]
    [InlineData(NetworkKind.Offline, StorytellerVoiceViewModel.NoticeOffline)]
    [InlineData(NetworkKind.Unknown, StorytellerVoiceViewModel.NoticeUnknown)]
    public void NetworkNotice_FollowsThePath(NetworkKind kind, string expected)
    {
        var vm = Vm();
        vm.Activate();
        _network.Current = kind;
        _network.Raise();
        Assert.Equal(expected, vm.NetworkNotice);
    }

    [Fact]
    public async Task Repair_CannotGoBack_AndReturnsHomeOnceFixed()
    {
        var vm = Vm(StorytellerVoicePurpose.Repair);
        Assert.False(vm.ShowBack);
        await vm.BackCommand.ExecuteAsync(null);
        Assert.Equal(0, _navigator.BackCount);
        vm.Activate();
        _voices.Current = Voice(serena: true);
        _voices.Refresh();
        Assert.Equal([Routes.Home], _navigator.Routes);
    }

    [Fact]
    public void Settings_ShowsState_WithoutNavigatingAway()
    {
        _voices.Current = Voice(serena: true);
        var vm = Vm(StorytellerVoicePurpose.Settings);
        vm.Activate();
        Assert.True(vm.IsReady);
        Assert.Empty(_navigator.Routes);
        Assert.True(vm.ShowBack);
    }

    [Fact]
    public void LeavingThePage_StopsWatching()
    {
        var vm = Vm();
        vm.Activate();
        vm.Deactivate();
        _voices.Current = Voice(serena: true);
        _voices.Refresh();
        Assert.Empty(_navigator.Routes);
    }

    [Fact]
    public void Steps_NameSerenaPremium_AndTheRealSettingsPath()
    {
        var all = string.Join(" ", StorytellerVoiceViewModel.Steps);
        Assert.Contains("Accessibility", all);
        Assert.Contains("Spoken Content", all);
        Assert.Contains("Read & Speak", all);
        Assert.Contains("Serena (Premium)", all);
        Assert.DoesNotContain("App-Prefs", all);
    }
}
