namespace StoryTime.Core.Abstractions;

/// <summary>What happened to a story in the catalog.</summary>
public enum StoryChangeKind
{
    /// <summary>Created or updated — a weave writing its row, an approval unlocking it,
    /// a favourite toggling.</summary>
    Saved,
    /// <summary>Gone — deleted by the parent, or discarded by a cancelled weave.</summary>
    Removed,
}

public sealed class StoryChangedEventArgs(StoryChangeKind kind, int storyId, int profileId) : EventArgs
{
    public StoryChangeKind Kind { get; } = kind;
    public int StoryId { get; } = storyId;
    public int ProfileId { get; } = profileId;
}

/// <summary>The ONE notification seam for story mutations (founder bug 2026-07-31:
/// "newly-generated stories must show up on all the pages they are supposed to").
///
/// Before this existed, only Home listened — to <c>IWeaveHost.WeaveCompleted</c> — and
/// every other list refreshed solely in its page's Appearing. So a story that finished,
/// was approved, favourited or deleted while the Library or Kid Shelf was on screen left
/// that list stale until the user navigated away and back. Rather than bolt another
/// event onto each producer, the STORE raises this on every mutation, so any future
/// writer is covered by construction: there is no way to change a story without the
/// catalog saying so.
///
/// Every list view-model subscribes and reloads (coalesced — see
/// <see cref="Services.CoalescingReloader"/>). Raises are marshalled to the UI thread by
/// the App layer through <see cref="Services.StoryCatalog.Dispatcher"/>; tests and the
/// harness get direct, synchronous invocation.</summary>
public interface IStoryCatalog
{
    event EventHandler<StoryChangedEventArgs>? Changed;

    void NotifyChanged(StoryChangeKind kind, int storyId, int profileId);
}
