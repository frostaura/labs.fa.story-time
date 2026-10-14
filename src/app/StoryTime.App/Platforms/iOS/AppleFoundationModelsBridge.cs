using Foundation;
using StoryTime.Core.Engines.AppleFoundation;
using StoryTimeNative;

namespace StoryTime.App.Platforms.iOS;

/// <summary>iOS <see cref="IFoundationModelsBridge"/> over the StoryTimeNative binding
/// (Swift SEFMEngine). Native completions arrive on the main queue; continuations are
/// released asynchronously so engine work never runs on the UI thread. Token cancellation
/// cancels the native request; a cancelled request surfaces as OperationCanceledException.</summary>
public sealed class AppleFoundationModelsBridge : IFoundationModelsBridge
{
    private static SEFMEngine Engine => SEFMEngine.Shared;

    public FoundationModelsAvailability GetAvailability() =>
        FoundationModelsAvailability.Parse(Engine.AvailabilityJson());

    public void Prewarm(string stage) => Engine.Prewarm(stage);

    public Task<int?> CountTokensAsync(string text, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Engine.CountTokens(text, (count, code) => tcs.TrySetResult(code == 0 && count >= 0 ? (int)count : null));
        return tcs.Task;
    }

    public Task<string> GenerateOutlineAsync(string requestJson, CancellationToken ct) =>
        Call(handler => Engine.GenerateOutline(requestJson, handler), ct);

    public Task<string> StreamSceneAsync(string requestJson, Action<string> onDelta, CancellationToken ct) =>
        Call(handler => Engine.StreamScene(requestJson, delta => onDelta(delta), handler), ct);

    public Task<string> DigestSceneAsync(string requestJson, CancellationToken ct) =>
        Call(handler => Engine.DigestScene(requestJson, handler), ct);

    public Task<string> PolishSceneAsync(string requestJson, CancellationToken ct) =>
        Call(handler => Engine.PolishScene(requestJson, handler), ct);

    public Task<string> GeneratePosterAsync(string requestJson, CancellationToken ct) =>
        Call(handler => Engine.GeneratePoster(requestJson, handler), ct);

    private static async Task<string> Call(Func<SEFMResultHandler, NSUuid> start, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestId = start((result, code, error) =>
        {
            if (code == (nint)(int)FmErrorCode.Ok && result is not null)
                tcs.TrySetResult(result);
            else if (code == (nint)(int)FmErrorCode.Cancelled && ct.IsCancellationRequested)
                tcs.TrySetCanceled(ct);
            else
                tcs.TrySetException(FoundationModelsException.FromNative((int)code, error));
        });
        await using var registration = ct.Register(() => Engine.Cancel(requestId));
        return await tcs.Task;
    }
}
