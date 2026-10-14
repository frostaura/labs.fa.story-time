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
                Description = $"“{storyTitle}” — woven for {childName}.",
            };
            await LocalNotificationCenter.Current.Show(request);
        }
        catch (Exception)
        {
            // A missed ping must never break the weave.
        }
    }
}

/// <summary>Identity encoder — returns the wav unchanged. Real AAC/m4a encoding
/// (AVAssetWriter / MediaCodec) is Phase C's concern; the seam is honored now.</summary>
public sealed class PassThroughAudioEncoder : IAudioEncoder
{
    public Task<string> EncodeToCompressedAsync(string wavPath, CancellationToken ct) =>
        Task.FromResult(wavPath);
}

/// <summary>No models are present in Phase B — always not-ready, so DI selects the
/// TemplateStoryEngine + platform TTS. Phase C replaces this registration with the real
/// bundle/asset-pack/AppData resolver (recorded in docs/known-gaps.md).</summary>
public sealed class ModelProviderStub : IModelProvider
{
    public Task<ModelAvailability> GetAvailabilityAsync(CancellationToken ct) =>
        Task.FromResult(new ModelAvailability { LlmReady = false, TtsReady = false });
}
