using StoryTime.Contracts;
using StoryTime.Core.Abstractions;
using StoryTime.Core.Pipeline;

namespace StoryTime.App.Services;

/// <summary>Hosts the StoryPipeline on a background task that survives page navigation.
/// Checkpoint-resume covers app restarts (TryResumeAsync at start). Background survival is
/// the platform anchor's job (IWeaveBackgroundAnchor): the iOS coordinator keeps the audible
/// loom hum's playback session alive, buys a short grace window when it cannot, checkpoints,
/// and resumes. Events are marshaled to the UI thread.</summary>
public sealed class WeaveHostService : IWeaveHost, IDisposable
{
    private readonly StoryPipeline _pipeline;
    private readonly IStoryStore _store;
    private readonly INotificationService _notifications;
    private readonly IWeaveBackgroundAnchor? _anchor;
    private readonly LoomHumService? _hum;

    private CancellationTokenSource? _cts;
    private Task? _run;
    private bool _permissionAsked;

    public WeaveHostService(StoryPipeline pipeline, IStoryStore store,
        INotificationService notifications, IWeaveBackgroundAnchor? anchor = null,
        LoomHumService? hum = null)
    {
        _pipeline = pipeline;
        _store = store;
        _notifications = notifications;
        _anchor = anchor;
        _hum = hum;
    }

    public bool IsWeaving { get; private set; }
    public WeaveProgress? CurrentProgress { get; private set; }
    public StoryRecord? LastCompleted { get; private set; }
    public string? LastFailureMessage { get; private set; }

    public event EventHandler<WeaveProgress>? ProgressChanged;
    public event EventHandler<StoryRecord>? WeaveCompleted;
    public event EventHandler<string>? WeaveFailed;

    public async Task StartAsync(ChildProfile profile)
    {
        if (IsWeaving) return;

        // Polite, contextual permission ask: the first weave is the exact moment the
        // "we'll send a gentle ping when it's ready" promise becomes real, so this is when
        // UNUserNotificationCenter authorization is requested — once per app run; the OS
        // de-duplicates after a decision. A denial never blocks the weave (the story-ready
        // ping is simply suppressed).
        if (!_permissionAsked)
        {
            _permissionAsked = true;
            await _notifications.RequestPermissionAsync();
        }

        Begin();
        _anchor?.WeaveStarted(profile.Name);
        // The loom hums for the duration (founder decision 2026-07-31) — and on iOS the
        // active playback session is what keeps this Task running in the background.
        _hum?.OnWeaveStarted(profile);
        var token = _cts!.Token;
        _run = Task.Run(() => ExecuteAsync(async () => (StoryRecord?)await _pipeline.WeaveAsync(profile, Progress(), token), token));
    }

    public async Task TryResumeAsync()
    {
        if (IsWeaving) return;
        var checkpoint = await _store.GetCheckpointAsync();
        if (checkpoint is null) return;
        var profile = await _store.GetActiveProfileAsync();
        if (profile is null) return;

        Begin();
        _anchor?.WeaveStarted(profile.Name);
        _hum?.OnWeaveStarted(profile);
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
        IsWeaving = false;
    }

    public event EventHandler? WeaveCancelled;

    public async Task CancelAndDiscardAsync()
    {
        await CancelAsync();

        // Discard the half-woven story (record + audio/art files) and the checkpoint.
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
                if (s.State == StoryState.Weaving)
                    await _store.DeleteStoryAsync(s.Id);
        }

        CurrentProgress = null;
        LastCompleted = null;
        LastFailureMessage = null;
        MainThread.BeginInvokeOnMainThread(() => WeaveCancelled?.Invoke(this, EventArgs.Empty));
    }

    private void Begin()
    {
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        IsWeaving = true;
        LastCompleted = null;
        LastFailureMessage = null;
        CurrentProgress = null;
    }

    private IProgress<WeaveProgress> Progress() => new SyncProgress(p =>
    {
        CurrentProgress = p;
        MainThread.BeginInvokeOnMainThread(() => ProgressChanged?.Invoke(this, p));
    });

    private async Task ExecuteAsync(Func<Task<StoryRecord?>> weave, CancellationToken token)
    {
        try
        {
            var story = await weave();
            IsWeaving = false;
            CurrentProgress = null;
            if (story is null) return; // nothing to resume

            if (story.State == StoryState.Failed)
            {
                LastFailureMessage = StoryPipeline.GentleFailureMessage;
                MainThread.BeginInvokeOnMainThread(() => WeaveFailed?.Invoke(this, LastFailureMessage));
            }
            else
            {
                LastCompleted = story;
                MainThread.BeginInvokeOnMainThread(() => WeaveCompleted?.Invoke(this, story));
            }
        }
        catch (OperationCanceledException)
        {
            IsWeaving = false; // checkpoint stays; resume on next start
        }
        catch (Exception)
        {
            IsWeaving = false;
            LastFailureMessage = StoryPipeline.GentleFailureMessage;
            MainThread.BeginInvokeOnMainThread(() => WeaveFailed?.Invoke(this, LastFailureMessage));
        }
        finally
        {
            // Every exit — completion, gentle failure, cancel, resume-with-nothing —
            // stops the hum instantly and releases the platform anchor (any background
            // grace ends). Both idempotent by contract.
            // Hum first: the ready ping should land into quiet, not over a fading loop.
            _hum?.OnWeaveEnded();
            _anchor?.WeaveEnded();
        }
    }

    public void Dispose() => _cts?.Dispose();

    private sealed class SyncProgress : IProgress<WeaveProgress>
    {
        private readonly Action<WeaveProgress> _handler;
        public SyncProgress(Action<WeaveProgress> handler) => _handler = handler;
        public void Report(WeaveProgress value) => _handler(value);
    }
}
