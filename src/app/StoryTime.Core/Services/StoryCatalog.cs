using StoryTime.Core.Abstractions;

namespace StoryTime.Core.Services;

/// <summary>Singleton implementation of <see cref="IStoryCatalog"/>. Core cannot reference
/// MAUI, so the UI-thread marshalling is injected as a delegate by the App layer
/// (MauiProgram sets <see cref="Dispatcher"/> to MainThread.BeginInvokeOnMainThread).
/// Left null (tests) — raises happen inline on the calling thread.</summary>
public sealed class StoryCatalog : IStoryCatalog
{
    public event EventHandler<StoryChangedEventArgs>? Changed;

    /// <summary>Set once at startup by the App layer. Must post to the UI thread:
    /// subscribers mutate ObservableCollections bound to live CollectionViews, and the
    /// pipeline writes stories from a background task.</summary>
    public Action<Action>? Dispatcher { get; set; }

    public void NotifyChanged(StoryChangeKind kind, int storyId, int profileId)
    {
        var handler = Changed;
        if (handler is null) return;
        var args = new StoryChangedEventArgs(kind, storyId, profileId);
        var dispatcher = Dispatcher;
        if (dispatcher is null) handler(this, args);
        else dispatcher(() => handler(this, args));
    }
}

/// <summary>Collapses a burst of reload requests into at most one in-flight reload plus
/// one queued follow-up. A single weave writes its story row six times (title, prose,
/// poster, teaser, completion…), and every list is subscribed — without this, each list
/// would stack six overlapping sqlite reads and could apply them out of order.</summary>
public sealed class CoalescingReloader(Func<Task> reload)
{
    private readonly Func<Task> _reload = reload;
    private readonly object _gate = new();
    private bool _running;
    private bool _pending;

    /// <summary>The in-flight (or last completed) reload. Tests await this; production
    /// code fires and forgets.</summary>
    public Task Current { get; private set; } = Task.CompletedTask;

    public Task RequestAsync()
    {
        lock (_gate)
        {
            if (_running)
            {
                _pending = true;
                return Current;
            }
            _running = true;
            _pending = false;
            Current = RunAsync();
            return Current;
        }
    }

    private async Task RunAsync()
    {
        while (true)
        {
            try
            {
                await _reload();
            }
            catch (Exception)
            {
                // A list refresh must never take the app down; the next change re-reads.
            }
            lock (_gate)
            {
                if (!_pending)
                {
                    _running = false;
                    return;
                }
                _pending = false;
            }
        }
    }
}
