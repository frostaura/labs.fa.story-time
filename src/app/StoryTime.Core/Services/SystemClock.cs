using StoryTime.Core.Abstractions;

namespace StoryTime.Core.Services;

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;

    /// <summary>Monotonic since boot — immune to wall-clock fiddling (resets on reboot).</summary>
    public long MonotonicMs => Environment.TickCount64;
}
