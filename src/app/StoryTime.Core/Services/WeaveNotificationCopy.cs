using StoryTime.Contracts;

namespace StoryTime.Core.Services;

/// <summary>Copy for the Android foreground-weave ("The loom") notification. Kept in Core so
/// the strings and stage names are unit-tested and stay in lockstep with the weave sheet's
/// stage-row labels (mockup-spec §Generating).</summary>
public static class WeaveNotificationCopy
{
    /// <summary>Silent low-priority channel — the loom hums, it never buzzes.</summary>
    public const string ChannelName = "The loom";
    public const string ChannelDescription = "Quiet progress while a story weaves";

    public static string Title(string childName) => $"Weaving {childName}'s story…";

    /// <summary>"{stage} · {percent}%" — the notification's second line.</summary>
    public static string StageLine(WeaveProgress progress) =>
        $"{StageName(progress.Stage)} · {Math.Clamp(progress.Percent, 0, 100)}%";

    /// <summary>Stage display names — identical to the weave sheet's four stage rows.</summary>
    public static string StageName(WeaveStage stage) => stage switch
    {
        WeaveStage.Outline => "Outline",
        WeaveStage.Scenes => "Scenes · stitch · polish",
        WeaveStage.Poster => "Poster — 4 layers",
        WeaveStage.Narration => "Narration — teaser first",
        _ => "Done",
    };
}

/// <summary>Rate limit for ongoing-notification updates: the first report always posts,
/// after that at most one per <see cref="MinInterval"/> (spec: ≤ 1 update / 10 s — the
/// notification shade is not a progress bar, and each update costs a binder call).</summary>
public sealed class NotificationUpdateThrottle
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(10);

    private DateTime _lastPostUtc = DateTime.MinValue;

    /// <summary>True when an update may post now; records the post when it may.</summary>
    public bool ShouldPost(DateTime utcNow)
    {
        if (utcNow - _lastPostUtc < MinInterval) return false;
        _lastPostUtc = utcNow;
        return true;
    }

    /// <summary>Forget the last post — the next report posts immediately (used when the
    /// notification is rebuilt from scratch, e.g. service restart).</summary>
    public void Reset() => _lastPostUtc = DateTime.MinValue;
}
