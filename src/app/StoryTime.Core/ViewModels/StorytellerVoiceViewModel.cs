using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StoryTime.Contracts;
using StoryTime.Core.Abstractions;
using StoryTime.Core.Engines.Narration;

namespace StoryTime.Core.ViewModels;

/// <summary>Why the storyteller-voice step is on screen.</summary>
public enum StorytellerVoicePurpose
{
    /// <summary>First run: blocks onboarding until a premium/enhanced voice is installed.</summary>
    Onboarding,
    /// <summary>Launch found the storyteller voice gone (deleted in Settings): blocks the app.</summary>
    Repair,
    /// <summary>Opened from parent settings: informational, can go back.</summary>
    Settings,
}

/// <summary>
/// The mandatory "storyteller voice" step (founder rule 2026-10-14: parents cannot skip the
/// premium voice download). The compact voices every iPhone ships with were judged
/// "extremely robotic", and of the premium voices only Serena (Premium, English UK) passed,
/// so she is the one storyteller and nothing else counts as done. Apps cannot
/// download voices or deep-link into Accessibility settings, so this step explains the path,
/// words the download honestly (a few hundred MB, Wi-Fi advised on mobile data), watches for
/// the voice (voice-list changes and return-to-foreground re-checks happen in
/// <see cref="IStorytellerVoices"/>), plays a sample once it is found, and moves on by itself.
/// </summary>
public sealed partial class StorytellerVoiceViewModel : ObservableObject
{
    public const string Eyebrow = "STORYTELLER VOICE";
    public const string TitleText = "Give the stories a real storyteller.";
    public const string LeadOnboarding =
        "Every StoryTime story is read by Serena, a warm, natural voice Apple makes for reading aloud. " +
        "It's free, but Apple only installs it when you ask, so it takes one quick trip to Settings.";
    public const string LeadRepair =
        "Serena, the storyteller voice, is no longer on this iPhone. " +
        "Download her again and every story will sound like itself.";
    public const string LeadSettings =
        "Every story is read by Serena (Premium). If she's ever removed from this iPhone, you can download her again here.";

    public static readonly IReadOnlyList<string> Steps =
    [
        "Open the Settings app.",
        "Tap Accessibility, then Spoken Content. On some iPhones it's called Read & Speak.",
        "Tap Voices, then English, and find the English (UK) voices.",
        "Tap Serena, then Serena (Premium) to download her.",
        "Come back here. We'll notice as soon as she's ready.",
    ];

    public const string NoticeCellular =
        "You're on mobile data. The voice is a few hundred MB, so Wi-Fi is the kinder choice for your data plan.";
    public const string NoticeWiFi = "You're on Wi-Fi, which is perfect for a download of a few hundred MB.";
    public const string NoticeOffline = "You're offline right now. The voice comes from Apple, so connect to Wi-Fi first.";
    public const string NoticeUnknown = "The voice is a few hundred MB, so Wi-Fi is best.";

    public const string WaitingText = "Waiting for Serena to finish downloading…";
    public const string NotYetText = "Not there yet. Big downloads can take a few minutes, and we'll keep checking.";
    public const string CheckAgainText = "I've downloaded it";

    /// <summary>Pause after the sample before moving on by itself.</summary>
    public static readonly TimeSpan AfterSamplePause = TimeSpan.FromSeconds(0.8);
    /// <summary>Upper bound on the wait for a sample before moving on anyway.</summary>
    public static readonly TimeSpan MaxAutoAdvanceWait = TimeSpan.FromSeconds(12);

    private readonly IStorytellerVoices _voices;
    private readonly INetworkStatus _network;
    private readonly INavigator _navigator;
    private readonly ITtsEngine _tts;
    private readonly IVoiceSamplePlayer _player;
    private readonly IHaptics _haptics;
    private bool _active;
    private bool _advanced;
    private CancellationTokenSource? _autoCts;

    public StorytellerVoiceViewModel(IStorytellerVoices voices, INetworkStatus network, INavigator navigator,
        ITtsEngine tts, IVoiceSamplePlayer player, IHaptics haptics)
    {
        _voices = voices;
        _network = network;
        _navigator = navigator;
        _tts = tts;
        _player = player;
        _haptics = haptics;
        Update();
    }

    /// <summary>Marshals voice/network change callbacks onto the UI thread (set by the page;
    /// direct invocation in tests).</summary>
    public Action<Action> Dispatch { get; set; } = a => a();

    /// <summary>Delay primitive (tests replace it to run instantly).</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Lead), nameof(ShowBack), nameof(ContinueText))]
    private StorytellerVoicePurpose _purpose = StorytellerVoicePurpose.Onboarding;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWaiting))]
    [NotifyCanExecuteChangedFor(nameof(ContinueCommand))]
    private bool _isReady;

    [ObservableProperty]
    private string _voiceName = string.Empty;

    [ObservableProperty]
    private string _status = WaitingText;

    [ObservableProperty]
    private string _networkNotice = NoticeUnknown;

    [ObservableProperty]
    private bool _isPlayingSample;

    public bool IsWaiting => !IsReady;

    public IReadOnlyList<string> StepList => Steps;

    public string Lead => Purpose switch
    {
        StorytellerVoicePurpose.Repair => LeadRepair,
        StorytellerVoicePurpose.Settings => LeadSettings,
        _ => LeadOnboarding,
    };

    /// <summary>Repair blocks the app outright; the other two can step back.</summary>
    public bool ShowBack => Purpose != StorytellerVoicePurpose.Repair;

    public string ContinueText => Purpose switch
    {
        StorytellerVoicePurpose.Repair => "Back to stories →",
        StorytellerVoicePurpose.Settings => "Done",
        _ => "Continue →",
    };

    /// <summary>Apply a Shell query value ("onboarding" / "repair" / "settings").</summary>
    public void ApplyPurpose(string? value)
    {
        if (Enum.TryParse<StorytellerVoicePurpose>(value, ignoreCase: true, out var purpose)) Purpose = purpose;
    }

    /// <summary>Page appeared: start watching and re-check now.</summary>
    public void Activate()
    {
        if (_active) return;
        _active = true;
        _advanced = false;
        _voices.Changed += OnChanged;
        _network.Changed += OnChanged;
        _voices.Refresh();
        Update();
    }

    /// <summary>Page left: stop watching, cancel any pending auto-advance.</summary>
    public void Deactivate()
    {
        if (!_active) return;
        _active = false;
        _voices.Changed -= OnChanged;
        _network.Changed -= OnChanged;
        _autoCts?.Cancel();
    }

    private void OnChanged(object? sender, EventArgs e) => Dispatch(Update);

    private void Update()
    {
        NetworkNotice = _network.Current switch
        {
            NetworkKind.Cellular => NoticeCellular,
            NetworkKind.WiFi => NoticeWiFi,
            NetworkKind.Offline => NoticeOffline,
            _ => NoticeUnknown,
        };
        var wasReady = IsReady;
        IsReady = _voices.HasStorytellerVoice;
        VoiceName = IsReady ? StorytellerRequirement.Serena.Name : string.Empty;
        if (IsReady)
        {
            Status = string.IsNullOrEmpty(VoiceName)
                ? "Found her. This is how your stories will sound."
                : $"Found {VoiceName}. This is how your stories will sound.";
            if (!wasReady && _active && Purpose != StorytellerVoicePurpose.Settings) _ = RevealAndAdvanceAsync();
        }
        else if (Status != NotYetText)
        {
            Status = WaitingText;
        }
    }

    /// <summary>The voice just arrived: celebrate, play it, then carry on by itself.</summary>
    private async Task RevealAndAdvanceAsync()
    {
        _autoCts?.Cancel();
        var cts = _autoCts = new CancellationTokenSource();
        _haptics.Success();
        var wait = MaxAutoAdvanceWait;
        try
        {
            var duration = await PlaySampleCoreAsync(cts.Token).ConfigureAwait(true);
            if (duration > 0) wait = TimeSpan.FromSeconds(Math.Min(duration, MaxAutoAdvanceWait.TotalSeconds)) + AfterSamplePause;
            else wait = AfterSamplePause;
            await Delay(wait, cts.Token).ConfigureAwait(true);
            if (!cts.IsCancellationRequested) await AdvanceAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException) { /* left the page or tapped Continue */ }
    }

    [RelayCommand]
    private void CheckAgain()
    {
        _haptics.Light();
        _voices.Refresh();
        Update();
        if (!IsReady) Status = NotYetText;
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task PlaySampleAsync()
    {
        _haptics.Light();
        try { await PlaySampleCoreAsync(CancellationToken.None).ConfigureAwait(true); }
        catch (Exception) { /* a preview must never surface an error at bedtime */ }
    }

    /// <summary>Render (full SoothingNarrator chain) and play the Ember sample; returns its
    /// length in seconds (0 when nothing playable was produced).</summary>
    private async Task<double> PlaySampleCoreAsync(CancellationToken ct)
    {
        IsPlayingSample = true;
        try
        {
            // One storyteller: every NarratorVoice value renders with it.
            var path = await _tts.RenderVoiceSampleAsync(NarratorVoice.Ember, ct).ConfigureAwait(true);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return 0;
            await _player.PlayAsync(path, ct).ConfigureAwait(true);
            return WavAudio.ReadDurationSeconds(path);
        }
        finally
        {
            IsPlayingSample = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsReady))]
    private async Task ContinueAsync()
    {
        _autoCts?.Cancel();
        _haptics.Light();
        await AdvanceAsync().ConfigureAwait(true);
    }

    private async Task AdvanceAsync()
    {
        if (_advanced || !IsReady) return;
        _advanced = true;
        switch (Purpose)
        {
            case StorytellerVoicePurpose.Onboarding:
                await _navigator.GoToAsync(Routes.OnboardingRules).ConfigureAwait(true);
                break;
            case StorytellerVoicePurpose.Repair:
                await _navigator.GoToAsync(Routes.Home).ConfigureAwait(true);
                break;
            default:
                await _navigator.GoBackAsync().ConfigureAwait(true);
                break;
        }
    }

    [RelayCommand]
    private Task BackAsync()
    {
        _autoCts?.Cancel();
        return Purpose == StorytellerVoicePurpose.Repair ? Task.CompletedTask : _navigator.GoBackAsync();
    }
}
