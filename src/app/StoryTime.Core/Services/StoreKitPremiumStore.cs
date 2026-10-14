using System.Text.Json;
using StoryTime.Core.Abstractions;

namespace StoryTime.Core.Services;

/// <summary>The StoreKit 2 surface as the native shim exposes it (Swift <c>SESKStore</c> in
/// src/native/StoryTimeNative/Sources/Store). Callbacks arrive on the main thread. Outcome
/// strings are <see cref="PremiumPurchaseResult"/> names.</summary>
public interface IAppStoreBridge
{
    /// <summary>False when in-app purchases are switched off (Screen Time restrictions).</summary>
    bool CanMakePayments { get; }

    /// <summary>Starts the <c>Transaction.updates</c> listener and the launch reconcile.
    /// <paramref name="onEntitlement"/> receives (productId, owned) for every verified change.</summary>
    void Start(string productId, Action<string, bool> onEntitlement);

    /// <summary><c>Product.products(for:)</c>: JSON <c>{state, displayPrice?, displayName?}</c>.</summary>
    void LoadProduct(string productId, Action<string> completion);

    /// <summary><c>Product.purchase()</c>: (outcome, detail?).</summary>
    void Purchase(string productId, Action<string, string?> completion);

    /// <summary><c>AppStore.sync()</c> then <c>Transaction.currentEntitlements</c>: (outcome, detail?).</summary>
    void Restore(string productId, Action<string, string?> completion);
}

/// <summary>StoryTime Premium through StoreKit 2: one non-consumable product
/// (<see cref="PremiumProduct.Id"/>), bought or restored with the App Store and recorded on
/// the device by <see cref="PremiumLedger"/>. <see cref="Start"/> runs at launch so Ask to
/// Buy approvals, purchases made on another device, refunds and revocations are delivered
/// and recorded even when nobody is waiting on a Buy or Restore call.</summary>
public sealed class StoreKitPremiumStore : IPremiumStore
{
    private static readonly TimeSpan ProductTimeout = TimeSpan.FromSeconds(20);

    private readonly IAppStoreBridge _bridge;
    private readonly PremiumLedger _ledger;
    private bool _started;
    private TaskCompletionSource<PremiumPurchaseResult>? _purchase;
    private TaskCompletionSource<PremiumPurchaseResult>? _restore;

    public StoreKitPremiumStore(IAppStoreBridge bridge, PremiumLedger ledger)
    {
        _bridge = bridge;
        _ledger = ledger;
    }

    public event EventHandler? EntitlementChanged;

    /// <summary>Starts the transaction listener. Idempotent; called once at launch.</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        _bridge.Start(PremiumProduct.Id, OnEntitlement);
    }

    public async Task<PremiumProductInfo> LoadProductAsync(CancellationToken ct)
    {
        if (!_bridge.CanMakePayments) return new PremiumProductInfo(PremiumProductState.PurchasesDisabled);

        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _bridge.LoadProduct(PremiumProduct.Id, json => tcs.TrySetResult(json));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProductTimeout);
        using (timeout.Token.Register(() => tcs.TrySetResult(null)))
            return ParseProduct(await tcs.Task.ConfigureAwait(true));
    }

    public async Task<PremiumPurchaseResult> PurchaseAsync(CancellationToken ct)
    {
        Start();
        if (!_bridge.CanMakePayments) return PremiumPurchaseResult.PurchasesDisabled;
        // One purchase at a time; a second tap joins the first.
        if (_purchase is { Task.IsCompleted: false } running) return await running.Task.ConfigureAwait(true);

        var tcs = new TaskCompletionSource<PremiumPurchaseResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _purchase = tcs;
        _bridge.Purchase(PremiumProduct.Id, (outcome, _) =>
        {
            var result = ParseOutcome(outcome);
            var changed = result == PremiumPurchaseResult.Purchased && _ledger.RecordGranted(PremiumProduct.Id);
            // Nobody waiting any more (the caller gave up): the grant is news for listeners.
            if (!tcs.TrySetResult(result) && changed) EntitlementChanged?.Invoke(this, EventArgs.Empty);
        });
        // No timeout: the App Store sheet waits on the parent (Face ID, password, Ask to Buy).
        using (ct.Register(() => tcs.TrySetResult(PremiumPurchaseResult.Cancelled)))
            return await tcs.Task.ConfigureAwait(true);
    }

    public async Task<PremiumPurchaseResult> RestoreAsync(CancellationToken ct)
    {
        Start();
        if (_restore is { Task.IsCompleted: false } running) return await running.Task.ConfigureAwait(true);

        var tcs = new TaskCompletionSource<PremiumPurchaseResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _restore = tcs;
        _bridge.Restore(PremiumProduct.Id, (outcome, _) =>
        {
            var result = ParseOutcome(outcome, PremiumPurchaseResult.StoreUnavailable);
            var changed = result switch
            {
                PremiumPurchaseResult.Restored => _ledger.RecordRestoreFinished(sawPremium: true),
                PremiumPurchaseResult.NothingToRestore => _ledger.RecordRestoreFinished(sawPremium: false),
                _ => false,
            };
            if (!tcs.TrySetResult(result) && changed) EntitlementChanged?.Invoke(this, EventArgs.Empty);
        });
        using (ct.Register(() => tcs.TrySetResult(PremiumPurchaseResult.Cancelled)))
            return await tcs.Task.ConfigureAwait(true);
    }

    /// <summary>Verified change from the listener or the launch reconcile: owned grants,
    /// not owned is a refund or revocation.</summary>
    private void OnEntitlement(string productId, bool owned)
    {
        var changed = owned ? _ledger.RecordGranted(productId) : _ledger.RecordRevoked([productId]);
        if (changed) EntitlementChanged?.Invoke(this, EventArgs.Empty);
    }

    internal static PremiumProductInfo ParseProduct(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return PremiumProductInfo.Unavailable;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var state = root.TryGetProperty("state", out var s) ? s.GetString() : null;
            var price = root.TryGetProperty("displayPrice", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            return state switch
            {
                "available" when !string.IsNullOrWhiteSpace(price) => new PremiumProductInfo(PremiumProductState.Available, price),
                "notFound" => new PremiumProductInfo(PremiumProductState.NotFound),
                "purchasesDisabled" => new PremiumProductInfo(PremiumProductState.PurchasesDisabled),
                _ => PremiumProductInfo.Unavailable,
            };
        }
        catch (JsonException)
        {
            return PremiumProductInfo.Unavailable;
        }
    }

    /// <summary>Outcome name → result; anything unknown is <paramref name="fallback"/>
    /// (a purchase that cannot be classified failed; it never counts as bought).</summary>
    internal static PremiumPurchaseResult ParseOutcome(string? outcome,
        PremiumPurchaseResult fallback = PremiumPurchaseResult.Failed) =>
        !string.IsNullOrWhiteSpace(outcome) && !char.IsDigit(outcome[0])
        && Enum.TryParse<PremiumPurchaseResult>(outcome, ignoreCase: true, out var result) && Enum.IsDefined(result)
            ? result
            : fallback;
}
