using StoryTime.Contracts;
using StoryTime.Core.Abstractions;
using StoryTime.Core.Pipeline;

namespace StoryTime.App.Services;

/// <summary>Hosts the StoryPipeline on a background task that survives page navigation.
/// Checkpoint-resume covers app restarts (TryResumeAsync at start). Background survival is
/// the platform anchor's job (IMakingBackgroundAnchor): the iOS coordinator keeps the audible
/// soft hum's playback session alive, buys a short grace window when it cannot, checkpoints,
/// and resumes. Events are marshaled to the UI thread.</summary>
public sealed class StoryMakingHost : IStoryMakingHost, IDisposable
{
    private readonly StoryPipeline _pipeline;
    private readonly IStoryStore _store;
    private readonly INotificationService _notifications;
    private readonly IMakingBackgroundAnchor? _anchor;
    private readonly SoftHumService? _hum;

    private CancellationTokenSource? _cts;
    private Task? _run;
    private bool _permissionAsked;

    public StoryMakingHost(StoryPipeline pipeline, IStoryStore store,
        INotificationService notifications, IMakingBackgroundAnchor? anchor = null,
        SoftHumService? hum = null)
    {
        _pipeline = pipeline;
        _store = store;
        _notifications = notifications;
        _anchor = anchor;
        _hum = hum;
    }

    public bool IsMaking { get; private set; }
    public MakingProgress? CurrentProgress { get; private set; }
    public StoryRecord? LastCompleted { get; private set; }
    public string? LastFailureMessage { get; private set; }

    public event EventHandler<MakingProgress>? ProgressChanged;
    public event EventHandler<StoryRecord>? MakingCompleted;
    public event EventHandler<string>? MakingFailed;

    public async Task StartAsync(ChildProfile profile)
    {
        if (IsMaking) return;

        // Polite, contextual permission ask: the first story run is the exact moment the
        // "we'll send a gentle ping when it's ready" promise becomes real, so this is when
        // UNUserNotificationCenter authorization is requested — once per app run; the OS
        // de-duplicates after a decision. A denial never blocks the story run (the story-ready
        // ping is simply suppressed).
        if (!_permissionAsked)
        {
            _permissionAsked = true;
            await _notifications.RequestPermissionAsync();
        }

        Begin();
        _anchor?.MakingStarted(profile.Name);
        // The soft hum for the duration (founder decision 2026-07-31) — and on iOS the
        // active playback session is what keeps this Task running in the background.
        _hum?.OnMakingStarted(profile);
        var token = _cts!.Token;
        _run = Task.Run(() => ExecuteAsync(async () => (StoryRecord?)await _pipeline.MakeStoryAsync(profile, Progress(), token), token));
    }

    public async Task TryResumeAsync()
    {
        if (IsMaking) return;
        var checkpoint = await _store.GetCheckpointAsync();
        if (checkpoint is null) return;
        var profile = await _store.GetActiveProfileAsync();
        if (profile is null) return;

        Begin();
        _anchor?.MakingStarted(profile.Name);
        _hum?.OnMakingStarted(profile);
        var token = _cts!.Token;
        _run = Task.Run(() => ExecuteAsync(async () =>
            await _pipeline.ResumeAsync(profile, Progress(), token), token));
    }

    public async Task CancelAsync()
    {
        _cts?.Cancel();
        if (_run is not null)
        {
            try { await _run; } catch (OperationCanceledException) { }
        }
        IsMaking = false;
    }

    public event EventHandler? MakingCancelled;

    public async Task CancelAndDiscardAsync()
    {
        await CancelAsync();

        // Discard the unfinished story (record + audio/art files) and the checkpoint.
        var checkpoint = await _store.GetCheckpointAsync();
        if (checkpoint is not null)
        {
            await _store.DeleteStoryAsync(checkpoint.StoryId);
            await _store.SaveCheckpointAsync(null);
        }
        // Belt-and-braces: a cancel can land before the first checkpoint write.
        var profile = await _store.GetActiveProfileAsync();
        if (profile is not null)
        {
            foreach (var s in await _store.GetStoriesAsync(profile.Id))
                if (s.State == StoryState.Making)
                    await _store.DeleteStoryAsync(s.Id);
        }

        CurrentProgress = null;
        LastCompleted = null;
        LastFailureMessage = null;
        MainThread.BeginInvokeOnMainThread(() => MakingCancelled?.Invoke(this, EventArgs.Empty));
    }

    private void Begin()
    {
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        IsMaking = true;
        LastCompleted = null;
        LastFailureMessage = null;
        CurrentProgress = null;
    }

    private IProgress<MakingProgress> Progress() => new SyncProgress(p =>
    {
        CurrentProgress = p;
        MainThread.BeginInvokeOnMainThread(() => ProgressChanged?.Invoke(this, p));
    });

    private async Task ExecuteAsync(Func<Task<StoryRecord?>> make, CancellationToken token)
    {
        try
        {
            var story = await make();
            IsMaking = false;
            CurrentProgress = null;
            if (story is null) return; // nothing to resume

            if (story.State == StoryState.Failed)
            {
                LastFailureMessage = StoryPipeline.GentleFailureMessage;
                MainThread.BeginInvokeOnMainThread(() => MakingFailed?.Invoke(this, LastFailureMessage));
            }
            else
            {
                LastCompleted = story;
                MainThread.BeginInvokeOnMainThread(() => MakingCompleted?.Invoke(this, story));
            }
        }
        catch (OperationCanceledException)
        {
            IsMaking = false; // checkpoint stays; resume on next start
        }
        catch (Exception)
        {
            IsMaking = false;
            LastFailureMessage = StoryPipeline.GentleFailureMessage;
            MainThread.BeginInvokeOnMainThread(() => MakingFailed?.Invoke(this, LastFailureMessage));
        }
        finally
        {
            // Every exit — completion, gentle failure, cancel, resume-with-nothing —
            // stops the hum instantly and releases the platform anchor (any background
            // grace ends). Both idempotent by contract.
            // Hum first: the ready ping should land into quiet, not over a fading loop.
            _hum?.OnMakingEnded();
            _anchor?.MakingEnded();
        }
    }

    public void Dispose() => _cts?.Dispose();

    private sealed class SyncProgress : IProgress<MakingProgress>
    {
        private readonly Action<MakingProgress> _handler;
        public SyncProgress(Action<MakingProgress> handler) => _handler = handler;
        public void Report(MakingProgress value) => _handler(value);
    }
}
