using StoryTime.Core.Abstractions;

namespace StoryTime.Core.Services;

/// <summary>Core fallback for tests — the bed simply does not sound. The App head
/// registers the platform AmbientPlayer first (TryAdd ordering).</summary>
public sealed class NullAmbientPlayer : IAmbientPlayer
{
    public bool IsPlaying { get; private set; }

    public Task StartAsync(string wavPath, double volume, CancellationToken ct)
    {
        IsPlaying = true;
        return Task.CompletedTask;
    }

    public void SetVolume(double volume) { }

    public Task StopAsync()
    {
        IsPlaying = false;
        return Task.CompletedTask;
    }
}
