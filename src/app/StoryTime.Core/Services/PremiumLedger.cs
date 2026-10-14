using StoryTime.Core.Abstractions;

namespace StoryTime.Core.Services;

/// <summary>The single writer of the local Premium entitlement. The platform store reports
/// what the App Store said; this decides what is recorded, so the rules are platform-free
/// and tested. The App Store stays the source of truth: a successful restore that returns
/// no Premium transaction clears the local record (different Apple ID, or a refund), while a
/// failed restore changes nothing.</summary>
public sealed class PremiumLedger
{
    private readonly ISettingsStore _settings;

    public PremiumLedger(ISettingsStore settings) => _settings = settings;

    public bool IsRecorded => _settings.PremiumPurchased;

    /// <summary>A Purchased or Restored transaction for <paramref name="productId"/>.
    /// Returns true when the recorded entitlement changed.</summary>
    public bool RecordGranted(string productId)
    {
        if (!IsPremiumProduct(productId) || _settings.PremiumPurchased) return false;
        _settings.PremiumPurchased = true;
        return true;
    }

    /// <summary>A restore finished successfully. Returns true when the recorded entitlement changed.</summary>
    public bool RecordRestoreFinished(bool sawPremium)
    {
        if (sawPremium) return RecordGranted(PremiumProduct.Id);
        if (!_settings.PremiumPurchased) return false;
        _settings.PremiumPurchased = false;
        return true;
    }

    /// <summary>The App Store revoked entitlements (refund, Family Sharing removed).
    /// Returns true when the recorded entitlement changed.</summary>
    public bool RecordRevoked(IEnumerable<string> productIds)
    {
        if (!productIds.Any(IsPremiumProduct) || !_settings.PremiumPurchased) return false;
        _settings.PremiumPurchased = false;
        return true;
    }

    public static bool IsPremiumProduct(string? productId) =>
        string.Equals(productId, PremiumProduct.Id, StringComparison.Ordinal);
}
