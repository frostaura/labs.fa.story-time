using StoryTime.Core.Abstractions;

namespace StoryTime.Core.Services;

public sealed class NullVoiceSamplePlayer : IVoiceSamplePlayer
{
    public Task PlayAsync(string wavPath, CancellationToken ct) => Task.CompletedTask;
}
