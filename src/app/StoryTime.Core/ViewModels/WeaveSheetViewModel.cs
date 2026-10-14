using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StoryTime.Contracts;
using StoryTime.Core.Abstractions;
using StoryTime.Core.Pipeline;
using StoryTime.Core.Services;

namespace StoryTime.Core.ViewModels;

public enum StageRowState { Waiting, Weaving, Done }

/// <summary>The weave sheet's presentation state. Hidden = no chrome at all; Collapsed =
/// docked mini-bar at the bottom of every host page; Expanded = near-full-height sheet.</summary>
public enum WeaveSheetState { Hidden, Collapsed, Expanded }

public sealed partial class StageRow : ObservableObject
{
    public StageRow(WeaveStage stage, string name, string shortName)
    {
        Stage = stage;
        Name = name;
        ShortName = shortName;
    }

    public WeaveStage Stage { get; }
    public string Name { get; }
    /// <summary>Terse label for the mini-bar detail line ("Narration · 62%").</summary>
    public string ShortName { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateLabel))]
    private StageRowState _state = StageRowState.Waiting;

    public string StateLabel => State switch
    {
        StageRowState.Waiting => "waiting",
        StageRowState.Weaving => "weaving…",
        _ => "done",
    };
}

/// <summary>Singleton state behind the weave sheet + mini-bar (founder decision 2026-07-30:
/// the Generating page became a slide-up sheet so a weave runs behind the whole app).
/// One instance lives for the app lifetime; every page's WeaveSheetHost binds to it, which
/// is exactly why expand/collapse survives navigation. Weave-host events are subscribed once
/// in the constructor (the host marshals them to the UI thread).</summary>
public sealed partial class WeaveSheetViewModel : ObservableObject
{
    private readonly IWeaveHost _weaveHost;
    private readonly IStoryStore _store;
    private readonly CooldownService _cooldown;
    private readonly INavigator _navigator;
    private readonly PlayerLaunchContext _playerContext;
    private readonly ISettingsStore _settings;
    private readonly IHaptics _haptics;
    private readonly IEntitlements _entitlements;
    private readonly KidModeService _kidMode;

    private string _childName = "your little one";
    private int _readyStoryId;
    private WeaveStage? _lastTickedStage;

    public WeaveSheetViewModel(IWeaveHost weaveHost, IStoryStore store, CooldownService cooldown,
        INavigator navigator, PlayerLaunchContext playerContext, ISettingsStore settings, IHaptics haptics,
        IEntitlements entitlements, KidModeService kidMode)
    {
        _weaveHost = weaveHost;
        _store = store;
        _cooldown = cooldown;
        _navigator = navigator;
        _playerContext = playerContext;
        _settings = settings;
        _haptics = haptics;
        _entitlements = entitlements;
        _kidMode = kidMode;

        Stages =
        [
            new StageRow(WeaveStage.Outline, "Outline", "Outline"),
            new StageRow(WeaveStage.Scenes, "Scenes · stitch · polish", "Scenes"),
            new StageRow(WeaveStage.Poster, "Poster — 4 layers", "Poster"),
            new StageRow(WeaveStage.Narration, "Narration — teaser first", "Narration"),
        ];

        // Singleton lifetime — subscribe once, never unsubscribe.
        _weaveHost.ProgressChanged += (_, p) => OnProgress(p);
        _weaveHost.WeaveCompleted += (_, s) => OnCompleted(s);
        _weaveHost.WeaveFailed += (_, m) => OnFailed(m);
        _weaveHost.WeaveCancelled += (_, _) => OnCancelled();
    }

    /// <summary>Two-tap stop: first tap arms a confirm (auto-reverts after 4 s), second
    /// cancels and discards. Hidden in Kid Mode and once the weave has finished.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StopLabel))]
    private bool _isConfirmingStop;

    private int _stopConfirmGeneration;

    public string StopLabel => IsConfirmingStop ? "Stop? It won't be saved — tap again" : "■  stop this story";

    public bool ShowStopRow => IsVisible && !IsDone && !IsFailed && !_kidMode.IsKidMode;

    [RelayCommand]
    private async Task StopWeaveAsync()
    {
        if (!IsConfirmingStop)
        {
            _haptics.Warning();
            IsConfirmingStop = true;
            var generation = ++_stopConfirmGeneration;
            await Task.Delay(4000);
            if (generation == _stopConfirmGeneration) IsConfirmingStop = false;
            return;
        }
        _stopConfirmGeneration++;
        IsConfirmingStop = false;
        _haptics.Light();
        await _weaveHost.CancelAndDiscardAsync();
    }

    private void OnCancelled()
    {
        IsConfirmingStop = false;
        IsDone = false;
        IsFailed = false;
        State = WeaveSheetState.Hidden;
        MiniTitle = string.Empty;
        MiniDetail = string.Empty;
        Dismissed?.Invoke(this, EventArgs.Empty);   // hosts refresh: CTA returns, lists drop the row
    }

    /// <summary>Foreground gate, pushed by the App layer (WeaveSheetHost mirrors the
    /// controller's window Resumed/Stopped state). Weave-driven haptics — the stage-change
    /// loom-click and the story-ready Celebrate — only fire while true: a phone in a
    /// pocket must never buzz because a background weave advanced.</summary>
    public bool IsForegrounded { get; set; } = true;

    public ObservableCollection<StageRow> Stages { get; }

    /// <summary>Stage-boundary fractions for the progress bar's tick marks, read from the
    /// pipeline's own weights (5/45/50/100) so they can never drift from the Report calls.</summary>
    public static IReadOnlyList<double> StageTicks { get; } =
        StoryPipeline.StageBoundaryPercents.Select(p => p / 100.0).ToArray();

    /// <summary>Raised when a finished (done or failed) sheet is dismissed; hosts/pages use
    /// it to refresh what the weave produced.</summary>
    public event EventHandler? Dismissed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible))]
    [NotifyPropertyChangedFor(nameof(IsExpanded))]
    [NotifyPropertyChangedFor(nameof(IsCollapsed))]
    [NotifyPropertyChangedFor(nameof(ShowTeaserCta))]
    [NotifyPropertyChangedFor(nameof(ShowStopRow))]
    private WeaveSheetState _state = WeaveSheetState.Hidden;

    public bool IsVisible => State != WeaveSheetState.Hidden;
    public bool IsExpanded => State == WeaveSheetState.Expanded;
    public bool IsCollapsed => State == WeaveSheetState.Collapsed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressFraction))]
    [NotifyPropertyChangedFor(nameof(EyebrowText))]
    private int _percent;

    public double ProgressFraction => Percent / 100.0;

    /// <summary>The old giant percentage, demoted to an eyebrow next to the progress bar.</summary>
    public string EyebrowText => IsFailed ? "STORYTIME" : IsDone ? "STORY READY" : $"WEAVING · {Percent}%";

    [ObservableProperty]
    private string _headline = "Weaving the story…";

    [ObservableProperty]
    private string _subText = string.Empty;

    [ObservableProperty]
    private string _cooldownLine = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTeaserCta))]
    [NotifyPropertyChangedFor(nameof(EyebrowText))]
    [NotifyPropertyChangedFor(nameof(CloseLabel))]
    [NotifyPropertyChangedFor(nameof(ShowStopRow))]
    private bool _isDone;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EyebrowText))]
    [NotifyPropertyChangedFor(nameof(CloseLabel))]
    [NotifyPropertyChangedFor(nameof(ShowStopRow))]
    private bool _isFailed;

    /// <summary>Ghost-button copy: reassures mid-weave (per the mockup-spec
    /// reinterpretation), plain close once the weave has finished.</summary>
    public string CloseLabel => IsDone || IsFailed ? "↓ close" : "Close this — we'll keep weaving";

    [ObservableProperty]
    private string _failureMessage = string.Empty;

    [ObservableProperty]
    private string _miniTitle = string.Empty;

    [ObservableProperty]
    private string _miniDetail = string.Empty;

    /// <summary>The done CTA stays hidden in Kid Mode: the teaser flow carries the parental
    /// approval step, and the Kid Shelf is browse-only by covenant. Recomputed on every
    /// state change because kid mode toggles between navigations.</summary>
    public bool ShowTeaserCta => IsDone && !_kidMode.IsKidMode;

    public const string MiniTitleReady = "Story ready — tap for the teaser ▶︎";

    /// <summary>iOS keeps generating in the background only while the loom hum is audible
    /// (verified 2026-07-31: 39 min backgrounded, unlimited assertion while the audio session
    /// lives). Conditional by design — the hum is a parent-switchable feature, and if it is
    /// off the weave suspends and resumes on return, so this must never promise a ping.</summary>
    public static string BackgroundHint => "The loom hums while it works — leave it playing and it keeps weaving.";

    /// <summary>Home calls this right after starting a weave: reset to a fresh weaving
    /// state and open the sheet expanded. No page navigation happens any more.</summary>
    public async Task ShowForWeaveAsync()
    {
        await EnsureChildNameAsync();
        ResetToWeaving();
        State = WeaveSheetState.Expanded;
    }

    /// <summary>Opens the sheet expanded in the weave's ACTUAL current state (no reset) —
    /// used when tapping a still-weaving story in the Library.</summary>
    public Task ExpandCurrentAsync() => ExpandForQaAsync();

    /// <summary>QA-route hook ("weaving" cold-start route): opens the sheet expanded over
    /// Home in whatever state the weave host is actually in (idle shows 0% / waiting rows).</summary>
    public async Task ExpandForQaAsync()
    {
        await EnsureChildNameAsync();
        if (!IsDone && !IsFailed)
        {
            Headline = $"Weaving {_childName}'s story…";
            SubText = BackgroundHint;
            MiniTitle = $"Weaving {_childName}'s story…";
            if (string.IsNullOrEmpty(MiniDetail)) MiniDetail = $"Outline · {DisplayFloorPercent}%";
            if (Percent < DisplayFloorPercent) Percent = DisplayFloorPercent;
        }
        if (_weaveHost.CurrentProgress is { } current) Apply(current);
        else if (_weaveHost.LastCompleted is { } done) MarkDone(done);
        RefreshCooldownLine();
        State = WeaveSheetState.Expanded;
    }

    public void RefreshCooldownLine()
    {
        // Honest minutes: 30, or 15 when premium (dev unlock included).
        var minutes = _entitlements.CooldownMinutes;
        var next = _cooldown.NextWeaveAtLocal;
        CooldownLine = next is not null
            ? $"After this story the loom rests {minutes} minutes — next weave at {next:HH:mm}."
            : $"After this story the loom rests {minutes} minutes.";
    }

    [RelayCommand]
    private void Expand()
    {
        if (State == WeaveSheetState.Expanded) return;
        _haptics.Light();
        RefreshCooldownLine();
        State = WeaveSheetState.Expanded;
    }

    /// <summary>Drag-down / handle-tap / scrim-tap on the expanded sheet. Mid-weave it
    /// collapses to the mini-bar; once finished (done or failed) it dismisses entirely.</summary>
    [RelayCommand]
    private void Collapse()
    {
        if (State != WeaveSheetState.Expanded) return;
        _haptics.Light();   // collapse commit — the mirror of the expand Light
        if (IsDone || IsFailed) Dismiss();
        else State = WeaveSheetState.Collapsed;
    }

    /// <summary>Swipe-down on the mini-bar: inert while weaving (the bar is already
    /// minimal), dismisses once the story is done or the weave failed.</summary>
    [RelayCommand]
    private void DismissFromMiniBar()
    {
        if (State != WeaveSheetState.Collapsed) return;
        if (IsDone || IsFailed)
        {
            _haptics.Light();   // dismiss commit
            Dismiss();
        }
    }

    private void Dismiss()
    {
        State = WeaveSheetState.Hidden;
        Dismissed?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task PlayTeaserAsync()
    {
        if (_readyStoryId == 0) return;
        _haptics.Light();
        _playerContext.StoryId = _readyStoryId;
        _playerContext.TeaserOnly = _settings.ApprovalStep;
        Dismiss();   // the sheet's job is done; the story lives on the shelf/carousel now
        await _navigator.GoToAsync(Routes.Player);
    }

    private void OnProgress(WeaveProgress progress)
    {
        // A weave resumed at app-start (or noticed late) auto-surfaces the mini-bar.
        if (State == WeaveSheetState.Hidden)
        {
            ResetToWeaving();
            _ = RefreshNameLateAsync();   // resume path: name loads after the bar appears
            State = WeaveSheetState.Collapsed;
        }
        if (IsDone || IsFailed) ResetToWeaving();   // a fresh weave after a finished one
        Apply(progress);
    }

    private void OnCompleted(StoryRecord story)
    {
        if (State == WeaveSheetState.Hidden) State = WeaveSheetState.Collapsed;
        MarkDone(story);
    }

    private void OnFailed(string message)
    {
        if (State == WeaveSheetState.Hidden) State = WeaveSheetState.Collapsed;
        MarkFailed(message);
    }

    /// <summary>Displayed progress never starts below this — a small head-start so entering
    /// the sheet feels like a win already underway (founder call 2026-07-30). Display-only:
    /// the pipeline's real percent catches up and passes it within the Outline stage.</summary>
    public const int DisplayFloorPercent = 5;

    private void ResetToWeaving()
    {
        IsConfirmingStop = false;
        _stopConfirmGeneration++;
        Percent = DisplayFloorPercent;
        IsDone = false;
        IsFailed = false;
        FailureMessage = string.Empty;
        _readyStoryId = 0;
        _lastTickedStage = null;
        foreach (var row in Stages) row.State = StageRowState.Waiting;
        Headline = $"Weaving {_childName}'s story…";
        SubText = BackgroundHint;
        MiniTitle = $"Weaving {_childName}'s story…";
        MiniDetail = $"Outline · {DisplayFloorPercent}%";
        RefreshCooldownLine();
    }

    private void Apply(WeaveProgress progress)
    {
        // The quiet "loom-click": one Tick per stage transition (Outline→Scenes→Poster→
        // Narration), foreground only, even while collapsed. The FIRST stage seen never
        // ticks — weave start already answered with Medium, and a resume mid-pipeline is
        // not a user gesture. Done celebrates in MarkDone instead.
        if (progress.Stage != WeaveStage.Done)
        {
            if (_lastTickedStage is { } previous && progress.Stage > previous && IsForegrounded)
                _haptics.Tick();
            _lastTickedStage = progress.Stage;
        }
        Percent = Math.Max(DisplayFloorPercent, progress.Percent);
        foreach (var row in Stages)
        {
            row.State = row.Stage < progress.Stage ? StageRowState.Done
                : row.Stage == progress.Stage ? StageRowState.Weaving
                : StageRowState.Waiting;
        }
        if (progress.Stage == WeaveStage.Done)
        {
            foreach (var row in Stages) row.State = StageRowState.Done;
        }
        var current = Stages.FirstOrDefault(r => r.State == StageRowState.Weaving);
        MiniDetail = $"{current?.ShortName ?? "Weaving"} · {Percent}%";
        RefreshCooldownLine();
    }

    private void MarkDone(StoryRecord story)
    {
        if (!IsDone && IsForegrounded) _haptics.Celebrate();   // weave complete / story ready (once, gentle two-beat, foreground only)
        Percent = 100;
        foreach (var row in Stages) row.State = StageRowState.Done;
        IsDone = true;
        _readyStoryId = story.Id;
        Headline = $"{_childName}'s story is ready.";
        SubText = _settings.ApprovalStep
            ? "Listen to the teaser first — then it unlocks for the shelf."
            : "All set for tonight.";
        MiniTitle = MiniTitleReady;
        MiniDetail = string.Empty;
        RefreshCooldownLine();
    }

    private void MarkFailed(string message)
    {
        IsFailed = true;
        FailureMessage = message;
        Headline = "The loom needs a moment.";
        SubText = string.Empty;
        MiniTitle = "The loom needs a moment.";
        MiniDetail = string.Empty;
    }

    private async Task EnsureChildNameAsync()
    {
        var profile = await _store.GetActiveProfileAsync();
        if (profile is not null) _childName = profile.Name;
    }

    /// <summary>Resume-at-startup surfaces the bar before the profile is loaded; patch the
    /// name into the copy once it arrives (events are already on the UI thread).</summary>
    private async Task RefreshNameLateAsync()
    {
        await EnsureChildNameAsync();
        if (IsDone)
        {
            Headline = $"{_childName}'s story is ready.";
        }
        else if (!IsFailed)
        {
            Headline = $"Weaving {_childName}'s story…";
            MiniTitle = Headline;
        }
    }
}
