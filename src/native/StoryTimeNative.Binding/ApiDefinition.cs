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

    /// <summary>Product lookup result JSON <c>{state, displayPrice?, displayName?}</c> — main queue.</summary>
    delegate void SESKProductHandler(string resultJson);

    /// <summary>Purchase / restore outcome (a <c>PremiumPurchaseResult</c> name) plus optional detail — main queue.</summary>
    delegate void SESKOutcomeHandler(string outcome, [NullAllowed] string detail);

    /// <summary>Verified entitlement change for a product: owned, or revoked/refunded — main queue.</summary>
    delegate void SESKEntitlementHandler(string productId, bool owned);

    /// <summary>Swift <c>SESKStore</c> (src/native/StoryTimeNative/Sources/Store): StoreKit 2.</summary>
    [BaseType(typeof(NSObject), Name = "SESKStore")]
    [DisableDefaultCtor]
    interface SESKStore
    {
        [Static]
        [Export("shared")]
        SESKStore Shared { get; }

        [Export("canMakePayments")]
        bool CanMakePayments { get; }

        [Export("startWithProductId:onEntitlement:")]
        void Start(string productId, SESKEntitlementHandler onEntitlement);

        [Export("loadProduct:completion:")]
        void LoadProduct(string productId, SESKProductHandler completion);

        [Export("purchase:completion:")]
        void Purchase(string productId, SESKOutcomeHandler completion);

        [Export("restore:completion:")]
        void Restore(string productId, SESKOutcomeHandler completion);
    }
}
