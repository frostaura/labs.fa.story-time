using Plugin.LocalNotification;
using StoryTime.Core.Abstractions;

namespace StoryTime.App.Services;

/// <summary>Plugin.LocalNotification wrapper — "a gentle ping when it's ready".</summary>
public sealed class LocalNotificationService : StoryTime.Core.Abstractions.INotificationService
{
    private const int StoryReadyId = 1913;

    public async Task<bool> RequestPermissionAsync()
    {
        try
        {
            return await LocalNotificationCenter.Current.RequestNotificationPermission();
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task ShowStoryReadyAsync(string childName, string storyTitle)
    {
        try
        {
            var request = new NotificationRequest
            {
                NotificationId = StoryReadyId,
                Title = "Tonight's story is ready",
                Description = $"“{storyTitle}” is ready for {childName}.",
            };
            await LocalNotificationCenter.Current.Show(request);
        }
        catch (Exception)
        {
            // A missed ping must never break the weave.
        }
    }
}

/// <summary>Identity encoder — returns the wav unchanged. AAC/m4a encoding (AVAssetWriter)
/// plugs in behind IAudioEncoder when rendered narration lands.</summary>
public sealed class PassThroughAudioEncoder : IAudioEncoder
{
    public Task<string> EncodeToCompressedAsync(string wavPath, CancellationToken ct) =>
        Task.FromResult(wavPath);
}
