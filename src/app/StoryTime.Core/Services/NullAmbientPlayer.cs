using StoryTime.Core.Abstractions;

namespace StoryTime.Core.Services;

/// <summary>Core fallback for the harness and non-mobile heads — the bed simply does not
/// sound. App heads register the platform AmbientPlayer first (TryAdd ordering).</summary>
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
