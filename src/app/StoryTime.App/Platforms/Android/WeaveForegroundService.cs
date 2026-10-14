using Android.App;
using Android.Content;
using Android.OS;
using Android.Runtime;
using AndroidX.Core.App;
using StoryTime.Contracts;
using StoryTime.Core.Abstractions;
using StoryTime.Core.Services;
using AndroidResource = Android.Resource;

namespace StoryTime.App.Platforms.Android;

/// <summary>Keeps a weave running while the app is backgrounded or the screen is off
/// (background-generation pass, 2026-07-31).
///
/// <para><b>Why a foreground service at all.</b> The pipeline is a plain <c>Task</c> owned by
/// <c>WeaveHostService</c>. Without a foreground service Android is free to freeze the process
/// (cached-app freezer) or kill it under memory pressure the moment the last activity is
/// destroyed — and a ~10-minute on-device LLM+TTS weave with the screen off is exactly the
/// window in which that happens. A started foreground service pins the process at the
/// foreground-service oom_adj bucket for the whole weave.</para>
///
/// <para><b>Service type: <c>specialUse</c>, deliberately.</b> The four candidates at
/// targetSdk 35/36:</para>
/// <list type="bullet">
/// <item><c>shortService</c> — hard ~3-minute cap, and blowing the cap ANRs the app even if
/// other services are healthy. A weave is 3–15 minutes. Disqualified outright.</item>
/// <item><c>dataSync</c> — its documented use cases are transfer-shaped (upload/download,
/// backup-restore, import/export, "local file processing"). On-device inference is neither a
/// transfer nor file processing; declaring it would be a policy stretch that a Play reviewer
/// checks against a demo video. It is also the type Google is actively steering apps off:
/// apps targeting Android 15+ get a cumulative 6 h / 24 h budget with an
/// <c>onTimeout</c> → ANR contract, and a whole migration guide pointing elsewhere.</item>
/// <item><c>mediaProcessing</c> — "time-consuming operations on media assets, like converting
/// media to different formats". It fits the narration stage only; outline/scenes/polish/poster
/// are text and vector work. Also carries the same 6 h / 24 h timeout.</item>
/// <item><c>specialUse</c> — "covers any valid foreground service use cases that aren't covered
/// by the other foreground service types", no system timeout, and requires an explicit
/// free-form justification (<c>android.app.PROPERTY_SPECIAL_USE_FGS_SUBTYPE</c>) that Play
/// reviews at submission. That is precisely this case: user-initiated, on-device generative
/// compute, no network, bounded by one story.</item>
/// </list>
/// <para>So: <c>specialUse</c> with an honest subtype string in the manifest. The cost is a
/// manual Play review with a demo video (Stage-7 compliance gate owns that); the alternative
/// was mis-declaring the work to dodge the review, which is worse and also timeout-capped.</para>
///
/// <para><b>Wake lock.</b> A foreground service keeps the process alive but does NOT keep the
/// CPU awake once the screen goes off — the device suspends and the weave stalls until the
/// next wake. A PARTIAL wake lock (CPU only; screen and keyboard stay off) is therefore held
/// for the weave's duration and released in every exit path, including <c>OnDestroy</c> and
/// the safety timeout. Battery cost is real and bounded: the weave is already pegging 4 cores
/// for its 3–15 minutes, so the wake lock adds no work — it only prevents the suspend that
/// would stretch those minutes into hours. It is acquired with a hard
/// <see cref="WakeLockTimeout"/> ceiling so a lost <c>WeaveEnded</c> can never drain a phone
/// overnight.</para>
///
/// <para><b>Notification.</b> LOW importance, silent channel ("The loom"), ongoing, no
/// vibration and no sound — the loom hums, it never buzzes. Content is
/// <c>WeaveNotificationCopy</c> (Core, unit-tested, same stage names as the weave sheet's
/// rows); updates are throttled to ≤ 1 / 10 s because each post is a binder round-trip and the
/// shade is not a progress bar. Tapping it opens MainActivity (SingleTop), where the existing
/// weave-sheet mini-bar surfaces the live weave.</para></summary>
[Register("net.frostaura.storytime.WeaveForegroundService")]
public sealed class WeaveForegroundService : Service
{
    public const string ChannelId = "storytime.loom";
    public const string ExtraChildName = "child-name";
    private const int NotificationId = 1914;   // 1913 is the story-ready ping

    /// <summary>Hard ceiling on the CPU wake lock. Comfortably longer than the worst measured
    /// weave (~17 min overlapped on a 4-vCPU emulator) and far shorter than "forever".</summary>
    private static readonly TimeSpan WakeLockTimeout = TimeSpan.FromMinutes(45);

    private PowerManager.WakeLock? _wakeLock;
    private IWeaveHost? _host;
    private Handler? _poll;
    private Java.Lang.IRunnable? _pollTick;
    private string? _lastLine;
    private readonly NotificationUpdateThrottle _throttle = new();
    private string _childName = "your";
    private bool _started;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        _childName = intent?.GetStringExtra(ExtraChildName) is { Length: > 0 } name ? name : _childName;

        if (!_started)
        {
            _started = true;
            EnsureChannel();
            StartInForeground();
            AcquireWakeLock();
            Subscribe();
        }
        else
        {
            // Re-issued start (e.g. a resume right after a completion): refresh the copy and
            // forget the last line so the next poll always repaints, even if the new weave
            // happens to open on the same stage/percent as the old one ended.
            _throttle.Reset();
            _lastLine = null;
            PollOnce();
        }

        // NOT sticky: if Android kills the process the weave is gone with it, and the
        // checkpoint/resume path (App startup → IWeaveHost.TryResumeAsync) is the correct
        // recovery — a service restarted without its pipeline Task would show a lying
        // notification over nothing.
        return StartCommandResult.NotSticky;
    }

    public override void OnDestroy()
    {
        Unsubscribe();
        ReleaseWakeLock();
        _started = false;
        base.OnDestroy();
    }

    /// <summary>Android 14+ calls this if the system ever time-limits the type. specialUse has
    /// no documented budget today, but a service that ignores onTimeout ANRs — so honor it:
    /// stop cleanly and let checkpoint/resume pick the weave up.</summary>
    public override void OnTimeout(int startId, global::Android.Content.PM.ForegroundService fgsType)
    {
        StopSelfSafely();
        base.OnTimeout(startId, fgsType);
    }

    // ---------------- foreground + notification ----------------

    /// <summary>First line shown before the pipeline's first progress report lands.</summary>
    private static WeaveProgress Opening => new() { Stage = WeaveStage.Outline, Percent = 0 };

    private void StartInForeground()
    {
        var notification = BuildNotification(WeaveNotificationCopy.StageLine(Opening));

        if (Build.VERSION.SdkInt >= BuildVersionCodes.UpsideDownCake)
        {
            StartForeground(NotificationId, notification,
                global::Android.Content.PM.ForegroundService.TypeSpecialUse);
        }
        else
        {
            StartForeground(NotificationId, notification);
        }
    }

    private void EnsureChannel()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
        var manager = (NotificationManager?)GetSystemService(NotificationService);
        if (manager is null || manager.GetNotificationChannel(ChannelId) is not null) return;

        var channel = new NotificationChannel(ChannelId, WeaveNotificationCopy.ChannelName,
            NotificationImportance.Low)
        {
            Description = WeaveNotificationCopy.ChannelDescription,
            LockscreenVisibility = NotificationVisibility.Public,
        };
        channel.EnableVibration(false);
        channel.EnableLights(false);
        channel.SetSound(null, null);
        manager.CreateNotificationChannel(channel);
    }

    private Notification BuildNotification(string stageLine)
    {
        var open = new Intent(this, typeof(MainActivity));
        open.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        var pending = PendingIntent.GetActivity(this, 0, open,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        return new NotificationCompat.Builder(this, ChannelId)
            .SetContentTitle(WeaveNotificationCopy.Title(_childName))
            .SetContentText(stageLine)
            .SetSmallIcon(AndroidResource.Drawable.StatNotifySync)
            .SetPriority(NotificationCompat.PriorityLow)
            .SetSilent(true)
            .SetOngoing(true)
            .SetShowWhen(false)
            .SetOnlyAlertOnce(true)
            .SetContentIntent(pending)
            .SetCategory(NotificationCompat.CategoryProgress)
            .Build()!;
    }

    private void Post(Notification notification)
    {
        try
        {
            NotificationManagerCompat.From(this).Notify(NotificationId, notification);
        }
        catch (Exception)
        {
            // POST_NOTIFICATIONS denied, or the channel was blocked by the parent: the
            // service (and therefore the weave) carries on silently. A missing progress
            // line must never take the loom down.
        }
    }

    // ---------------- progress plumbing ----------------

    /// <summary>Progress is POLLED from <see cref="IWeaveHost.CurrentProgress"/> on the
    /// service's own timer, not taken from <c>ProgressChanged</c>.
    ///
    /// <para>Changed after this pass's device QA showed the shade reading "Outline · 5%" while
    /// the SQLite checkpoint already said Scenes/scene 5. The cause was never isolated between
    /// two candidates — `WeaveHostService` marshals `ProgressChanged` through
    /// <c>MainThread.BeginInvokeOnMainThread</c> (a UI concern the notification has no business
    /// depending on while the activity is destroyed), or the stage genuinely reports rarely on
    /// a contended 4-vCPU emulator — so the fragile dependency was removed rather than
    /// diagnosed. Polling also self-heals the other failure mode: a service that resolves DI a
    /// moment before the MauiApp exists gets <c>null</c> from the event path forever, but
    /// re-resolves here on the next tick.</para>
    ///
    /// <para>The tick interval IS the throttle (≤ 1 post / 10 s), and an unchanged line is not
    /// re-posted at all.</para></summary>
    private void Subscribe()
    {
        var intervalMs = (long)NotificationUpdateThrottle.MinInterval.TotalMilliseconds;
        _poll = new Handler(Looper.MainLooper!);
        _pollTick = new Java.Lang.Runnable(() =>
        {
            PollOnce();
            if (_pollTick is not null) _poll?.PostDelayed(_pollTick, intervalMs);
        });
        PollOnce();   // paint immediately so the first shade pull is never blank
        _poll.PostDelayed(_pollTick, intervalMs);
    }

    private void PollOnce()
    {
        try
        {
            _host ??= IPlatformApplication.Current?.Services?.GetService<IWeaveHost>();
            var progress = _host?.CurrentProgress;
            var line = progress is null
                ? WeaveNotificationCopy.StageLine(Opening)
                : WeaveNotificationCopy.StageLine(progress);
            if (line == _lastLine) return;
            if (!_throttle.ShouldPost(DateTime.UtcNow)) return;
            _lastLine = line;
            Post(BuildNotification(line));
        }
        catch (Exception)
        {
            // A missed progress line must never take the loom down.
        }
    }

    private void Unsubscribe()
    {
        if (_pollTick is not null) _poll?.RemoveCallbacks(_pollTick);
        _poll = null;
        _pollTick = null;
        _host = null;
    }

    // ---------------- wake lock ----------------

    private void AcquireWakeLock()
    {
        try
        {
            var power = (PowerManager?)GetSystemService(PowerService);
            _wakeLock = power?.NewWakeLock(WakeLockFlags.Partial, "storytime:weave");
            _wakeLock?.SetReferenceCounted(false);
            _wakeLock?.Acquire((long)WakeLockTimeout.TotalMilliseconds);
        }
        catch (Exception)
        {
            // No wake lock is a degraded weave (it stalls with the screen off), never a
            // crash — the foreground service still keeps the process alive.
            _wakeLock = null;
        }
    }

    private void ReleaseWakeLock()
    {
        var wakeLock = _wakeLock;
        _wakeLock = null;
        if (wakeLock is null) return;
        try
        {
            if (wakeLock.IsHeld) wakeLock.Release();
        }
        catch (Exception) { /* already released or timed out */ }
        finally
        {
            wakeLock.Dispose();
        }
    }

    private void StopSelfSafely()
    {
        try
        {
            StopForeground(StopForegroundFlags.Remove);
        }
        catch (Exception) { }
        StopSelf();
    }
}
