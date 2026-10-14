using StoryTime.Core.Abstractions;

namespace StoryTime.Core.Services;

/// <summary>No-op haptics for tests, the harness, and non-mobile TFMs. The App registers
/// PlatformHaptics before AddStoryTimeCore, which wins via TryAdd ordering.</summary>
public sealed class NullHaptics : IHaptics
{
    public void Light() { }
    public void Selection() { }
    public void Medium() { }
    public void Success() { }
    public void Warning() { }
    public void Tick() { }
    public void Celebrate() { }
}
