using System;
using Foundation;
using ObjCRuntime;

namespace StoryTimeNative
{
    /// <summary>Result completion: (resultJson?, SEFMErrorCode, errorPayloadJson?) — main queue.</summary>
    delegate void SEFMResultHandler([NullAllowed] string resultJson, nint code, [NullAllowed] string errorJson);

    /// <summary>Appended scene text (main queue, coalesced).</summary>
    delegate void SEFMDeltaHandler(string delta);

    /// <summary>Token count completion: (count or -1, SEFMErrorCode).</summary>
    delegate void SEFMCountHandler(nint count, nint code);

    /// <summary>Swift <c>SEFMEngine</c> (src/native/StoryTimeNative/Sources/FoundationModels).</summary>
    [BaseType(typeof(NSObject), Name = "SEFMEngine")]
    [DisableDefaultCtor]
    interface SEFMEngine
    {
        [Static]
        [Export("shared")]
        SEFMEngine Shared { get; }

        [Export("availabilityJSON")]
        string AvailabilityJson();

        [Export("prewarmWithStage:")]
        void Prewarm(string stage);

        [Export("countTokens:completion:")]
        void CountTokens(string text, SEFMCountHandler completion);

        [Export("generateOutline:completion:")]
        NSUuid GenerateOutline(string requestJson, SEFMResultHandler completion);

        [Export("streamScene:onDelta:completion:")]
        NSUuid StreamScene(string requestJson, SEFMDeltaHandler onDelta, SEFMResultHandler completion);

        [Export("digestScene:completion:")]
        NSUuid DigestScene(string requestJson, SEFMResultHandler completion);

        [Export("polishScene:completion:")]
        NSUuid PolishScene(string requestJson, SEFMResultHandler completion);

        [Export("generatePoster:completion:")]
        NSUuid GeneratePoster(string requestJson, SEFMResultHandler completion);

        [Export("cancel:")]
        void Cancel(NSUuid requestId);
    }
}
