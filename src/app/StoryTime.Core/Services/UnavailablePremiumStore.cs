using StoryTime.Core.Abstractions;

namespace StoryTime.Core.Services;

/// <summary>Core fallback for hosts with no App Store (tests, headless tools): every call
/// reports the store as unavailable. The iOS head registers StoreKitPremiumStore first.</summary>
public sealed class UnavailablePremiumStore : IPremiumStore
{
    public Task<PremiumProductInfo> LoadProductAsync(CancellationToken ct) =>
        Task.FromResult(PremiumProductInfo.Unavailable);

    public Task<PremiumPurchaseResult> PurchaseAsync(CancellationToken ct) =>
        Task.FromResult(PremiumPurchaseResult.StoreUnavailable);

    public Task<PremiumPurchaseResult> RestoreAsync(CancellationToken ct) =>
        Task.FromResult(PremiumPurchaseResult.StoreUnavailable);

    /// <summary>Never raised: with no store, nothing can change the entitlement.</summary>
    public event EventHandler? EntitlementChanged { add { } remove { } }
}
