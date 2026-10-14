using Android.Content;
using StoryTime.Core.Abstractions;

namespace StoryTime.App.Platforms.Android;

/// <summary>Android implementation of <see cref="IWeaveBackgroundAnchor"/>: starts and stops
/// <see cref="WeaveForegroundService"/> around a weave. The service does the real work
/// (foreground promotion, partial wake lock, "The loom" notification); this class exists only
/// so Core can express "keep this alive" without knowing what a Service is.
///
/// <para>Both calls are idempotent and safe from any thread. <c>WeaveEnded</c> is invoked from
/// the pipeline's <c>finally</c>, so every exit — completed, gently failed, cancelled,
/// nothing-to-resume — tears the service (and therefore the wake lock) down.</para></summary>
public sealed class AndroidWeaveBackgroundAnchor : IWeaveBackgroundAnchor
{
    private readonly object _gate = new();
    private bool _running;

    public void WeaveStarted(string childName)
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
        }
        try
        {
            var context = global::Android.App.Application.Context;
            var intent = new Intent(context, typeof(WeaveForegroundService));
            intent.PutExtra(WeaveForegroundService.ExtraChildName, childName);
            // StartForegroundService (not StartService): O+ requires the promotion contract,
            // and a weave always starts from a foregrounded app, so the background-start
            // restriction never applies.
            context.StartForegroundService(intent);
        }
        catch (Exception)
        {
            // A device that refuses the service still weaves — foregrounded only, exactly as
            // before this pass. Never let the anchor take the weave down.
            lock (_gate) _running = false;
        }
    }

    public void WeaveEnded()
    {
        lock (_gate)
        {
            if (!_running) return;
            _running = false;
        }
        try
        {
            var context = global::Android.App.Application.Context;
            context.StopService(new Intent(context, typeof(WeaveForegroundService)));
        }
        catch (Exception) { /* already gone */ }
    }
}
