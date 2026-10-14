namespace StoryTime.Core.Abstractions;

/// <summary>The one thing StoryTime sells: a single non-consumable, one-off purchase.
/// No subscription, no account, no server — the App Store is the only party involved.</summary>
public static class PremiumProduct
{
    /// <summary>Must match the product created in App Store Connect and the local
    /// StoreKit configuration file (Platforms/iOS/StoryTime.storekit).</summary>
    public const string Id = "net.frostaura.storytime.premium";
}

/// <summary>What the store said when asked for the Premium product.</summary>
public enum PremiumProductState
{
    /// <summary>The product exists and can be bought; <see cref="PremiumProductInfo.LocalizedPrice"/> is set.</summary>
    Available,
    /// <summary>The store answered, but does not sell this product here (not yet created in
    /// App Store Connect, not cleared for sale, or not offered in this storefront).</summary>
    NotFound,
    /// <summary>The store could not be reached (offline, App Store outage, request failed).</summary>
    StoreUnavailable,
    /// <summary>In-app purchases are switched off on this device (Screen Time restrictions).</summary>
    PurchasesDisabled,
}

public sealed record PremiumProductInfo(PremiumProductState State, string? LocalizedPrice = null)
{
    public static PremiumProductInfo Unavailable { get; } = new(PremiumProductState.StoreUnavailable);
}

/// <summary>Outcome of a Buy or Restore request.</summary>
public enum PremiumPurchaseResult
{
    Purchased,
    Restored,
    /// <summary>The parent closed the App Store sheet. Not an error.</summary>
    Cancelled,
    /// <summary>Ask to Buy: waiting for a family organiser; the entitlement lands later
    /// through <see cref="IPremiumStore.EntitlementChanged"/>.</summary>
    Pending,
    Failed,
    /// <summary>Restore completed and this Apple ID has no Premium purchase.</summary>
    NothingToRestore,
    StoreUnavailable,
    NotFound,
    PurchasesDisabled,
}

/// <summary>The on-device store seam. iOS: StoreKit (StoreKitPremiumStore). Tests and
/// headless hosts: <see cref="Services.UnavailablePremiumStore"/> or a fake.</summary>
public interface IPremiumStore
{
    Task<PremiumProductInfo> LoadProductAsync(CancellationToken ct);
    Task<PremiumPurchaseResult> PurchaseAsync(CancellationToken ct);
    Task<PremiumPurchaseResult> RestoreAsync(CancellationToken ct);

    /// <summary>Raised when the entitlement changes outside a Buy/Restore call — an Ask to
    /// Buy approval arriving later, a transaction finishing at launch, or a refund.</summary>
    event EventHandler? EntitlementChanged;
}
