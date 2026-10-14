using BackgroundTasks;
using Foundation;
using StoryTime.Core.Abstractions;
using UIKit;

namespace StoryTime.App.Platforms.iOS;

/// <summary>iOS implementation of <see cref="IWeaveBackgroundAnchor"/> — the honest maximum
/// iOS allows for a ~10-minute on-device weave (background-generation pass, 2026-07-31).
///
/// <para>iOS has no equivalent of Android's foreground service. What it has, in descending
/// order of reliability, is what this class stacks:</para>
/// <list type="number">
/// <item><b>An active audio session.</b> While the loom hum is genuinely playing through a
/// <c>Playback</c>-category session and the app declares <c>UIBackgroundModes: audio</c>, the
/// app simply keeps running in the background — the pipeline Task never pauses. This is the
/// primary path, and it is not a trick: the hum is an audible feature the parent can hear and
/// switch off (see LoomHumService / LoomHum.MinAudibleVolume). If the parent silences it, or
/// the system takes the session (call, Siri), the app falls back to 2 and 3.</item>
/// <item><b>A <c>BeginBackgroundTask</c> grace window.</b> On backgrounding mid-weave we take
/// a background task assertion and keep generating until iOS calls the expiration handler.
/// The granted budget is the system's to decide (historically ~30 s, occasionally more); we
/// LOG what was actually granted rather than assuming, then cancel cleanly — which leaves the
/// pipeline's checkpoint intact — and end the assertion before iOS kills the app for
/// overrunning it.</item>
/// <item><b>An opportunistic <c>BGProcessingTaskRequest</c>.</b> Submitted at the same moment,
/// it asks iOS to hand the app CPU again later (typically while charging and idle, often
/// overnight). Its handler resumes the checkpoint through the ordinary
/// <c>IWeaveHost.TryResumeAsync</c> path, so a completion there posts the same "story is
/// ready" notification the foreground path posts. iOS promises nothing about when — or
/// whether — it runs, so <b>no product copy may promise it</b>. It is a bonus, not a
/// feature.</item>
/// </list>
/// <para>Ordering matters: 1 makes 2 and 3 unnecessary most of the time; 2 makes 3 unnecessary
/// for short weaves; 3 is the last, unpromised chance. Anything left after all three is
/// exactly the pre-existing behaviour — the weave resumes when the parent reopens the
/// app.</para></summary>
public sealed class WeaveBackgroundCoordinator : IWeaveBackgroundAnchor
{
    /// <summary>Must match Info.plist's <c>BGTaskSchedulerPermittedIdentifiers</c>.</summary>
    public const string ResumeTaskId = "net.frostaura.storytime.weave-resume";

    private const string Tag = "TWBg";

    private readonly object _gate = new();
    private nint _assertion = UIApplication.BackgroundTaskInvalid;
    private NSObject? _didEnterBackground;
    private NSObject? _willEnterForeground;
    private CancellationTokenSource? _sampler;
    private bool _weaving;

    // ---------------- IWeaveBackgroundAnchor ----------------

    public void WeaveStarted(string childName)
    {
        lock (_gate)
        {
            _weaving = true;
            if (_didEnterBackground is not null) return;
        }

        // Subscribed once, for the app's lifetime — the handlers no-op when not weaving.
        _didEnterBackground = UIApplication.Notifications.ObserveDidEnterBackground((_, _) => OnBackgrounded());
        _willEnterForeground = UIApplication.Notifications.ObserveWillEnterForeground((_, _) => EndAssertion("foregrounded"));
    }

    public void WeaveEnded()
    {
        lock (_gate) _weaving = false;
        EndAssertion("weave ended");
    }

    // ---------------- grace window ----------------

    private void OnBackgrounded()
    {
        lock (_gate)
        {
            if (!_weaving || _assertion != UIApplication.BackgroundTaskInvalid) return;
        }

        var app = UIApplication.SharedApplication;
        var started = DateTime.UtcNow;
        nint assertion = UIApplication.BackgroundTaskInvalid;
        assertion = app.BeginBackgroundTask("storytime.weave", () =>
        {
            // iOS is about to reclaim the app. Cancelling the host stops the pipeline at its
            // next token boundary and LEAVES THE CHECKPOINT — the weave resumes on the next
            // launch (or on the BGProcessingTask below, if iOS grants one).
            var granted = (DateTime.UtcNow - started).TotalSeconds;
            Log($"grace EXPIRED after {granted:F1}s — checkpointing");
            RequestCheckpointCancel();
            EndAssertion("expired");
        });

        lock (_gate) _assertion = assertion;

        if (assertion == UIApplication.BackgroundTaskInvalid)
        {
            Log("BeginBackgroundTask REFUSED — checkpointing immediately");
            RequestCheckpointCancel();
            return;
        }

        Log($"backgrounded mid-weave; assertion={assertion}; iOS says {Remaining(app)} remain");
        StartSampler(started);
        SubmitResumeRequest();
    }

    /// <summary>Logs the real, measured grace budget every 5 s. iOS's advertised
    /// <c>BackgroundTimeRemaining</c> is not a promise — the expiry log line above is the
    /// number that actually counts, and this trail shows how it decayed.</summary>
    private void StartSampler(DateTime started)
    {
        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            _sampler?.Cancel();
            _sampler = cts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
                    var tcs = new TaskCompletionSource<string>();
                    MainThread.BeginInvokeOnMainThread(() =>
                        tcs.TrySetResult(Remaining(UIApplication.SharedApplication)));
                    var remaining = await tcs.Task;
                    Log($"grace +{(DateTime.UtcNow - started).TotalSeconds:F0}s elapsed; {remaining} remain");
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { }
        }, cts.Token);
    }

    private static string Remaining(UIApplication app)
    {
        var seconds = app.BackgroundTimeRemaining;
        // Foreground (and audio-backgrounded) apps report DBL_MAX — i.e. "no limit while the
        // audio session keeps me alive". That IS the loom-hum path succeeding.
        return double.IsInfinity(seconds) || seconds > 1e6
            ? "unlimited (audio session alive)"
            : $"{seconds:F1}s";
    }

    private void EndAssertion(string reason)
    {
        nint assertion;
        CancellationTokenSource? sampler;
        lock (_gate)
        {
            assertion = _assertion;
            _assertion = UIApplication.BackgroundTaskInvalid;
            sampler = _sampler;
            _sampler = null;
        }
        sampler?.Cancel();
        if (assertion == UIApplication.BackgroundTaskInvalid) return;

        Log($"ending background assertion ({reason})");
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try { UIApplication.SharedApplication.EndBackgroundTask(assertion); }
            catch (Exception) { }
        });
    }

    private static void RequestCheckpointCancel()
    {
        try
        {
            var host = IPlatformApplication.Current?.Services?.GetService<IWeaveHost>();
            // CancelAsync (NOT CancelAndDiscardAsync): the half-woven story and its checkpoint
            // must survive — this is a suspend, not a user cancel.
            _ = host?.CancelAsync();
        }
        catch (Exception) { }
    }

    // ---------------- BGProcessingTask ----------------

    /// <summary>Registered from AppDelegate BEFORE the app finishes launching (iOS requires
    /// every permitted identifier to be registered by then, or it throws).</summary>
    public static void RegisterResumeTask()
    {
        try
        {
            BGTaskScheduler.Shared.Register(ResumeTaskId, null, task => HandleResume(task));
        }
        catch (Exception ex)
        {
            Log($"BGTask register failed: {ex.GetType().Name}");
        }
    }

    private static void HandleResume(BGTask task)
    {
        Log("BGProcessingTask fired — resuming checkpointed weave");
        var done = false;

        task.ExpirationHandler = () =>
        {
            if (done) return;
            done = true;
            Log("BGProcessingTask expired — checkpointing and rescheduling");
            RequestCheckpointCancel();
            SubmitResumeRequestStatic();
            task.SetTaskCompleted(false);
        };

        _ = Task.Run(async () =>
        {
            var completed = false;
            try
            {
                var host = IPlatformApplication.Current?.Services?.GetService<IWeaveHost>();
                if (host is not null)
                {
                    await host.TryResumeAsync();
                    // TryResumeAsync returns as soon as the run task is spawned; wait for the
                    // pipeline to actually settle so SetTaskCompleted is honest.
                    while (host.IsWeaving) await Task.Delay(1000);
                    completed = host.LastCompleted is not null;
                    // A completion here posts the ordinary "story is ready" notification from
                    // inside StoryPipeline — no separate path, no duplicate ping.
                    Log(completed ? "BGProcessingTask completed the weave" : "BGProcessingTask ran; weave still pending");
                }
            }
            catch (Exception ex)
            {
                Log($"BGProcessingTask failed: {ex.GetType().Name}");
            }
            finally
            {
                if (!done)
                {
                    done = true;
                    if (!completed) SubmitResumeRequestStatic();
                    task.SetTaskCompleted(completed);
                }
            }
        });
    }

    private void SubmitResumeRequest() => SubmitResumeRequestStatic();

    private static void SubmitResumeRequestStatic()
    {
        try
        {
            var request = new BGProcessingTaskRequest(ResumeTaskId)
            {
                // Offline-only invariant: the weave needs no network, ever.
                RequiresNetworkConnectivity = false,
                // Do NOT require the charger — a phone on a nightstand off power is the
                // common case, and requiring external power would mean "usually never".
                RequiresExternalPower = false,
                EarliestBeginDate = null,
            };
            BGTaskScheduler.Shared.Submit(request, out var error);
            Log(error is null ? "BGProcessingTask submitted" : $"BGProcessingTask submit refused: {error.LocalizedDescription}");
        }
        catch (Exception ex)
        {
            // The simulator and some device states refuse scheduling outright — expected,
            // and exactly why nothing in the product copy promises this path.
            Log($"BGProcessingTask submit threw: {ex.GetType().Name}");
        }
    }

    private static void Log(string message) => Console.WriteLine($"{Tag} {message}");
}
