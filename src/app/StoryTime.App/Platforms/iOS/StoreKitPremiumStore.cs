// StoreKit 1 is marked obsoleted (not removed) from iOS 18 and still ships and works on
// iOS 26. It is the only StoreKit surface bound for .NET for iOS — StoreKit 2 is Swift-only and
// would need a native bridge — so this file, and only this file, opts out of the platform-
// compatibility analyzer for it. Founder decision: one on-device, one-off purchase, no server.
// Migration target when a Swift bridge exists: StoreKit 2 Product/Transaction APIs.
#pragma warning disable CA1422

using Foundation;
using StoreKit;
using StoryTime.Core.Abstractions;
using StoryTime.Core.Services;

namespace StoryTime.App.Platforms.iOS;

/// <summary>StoryTime Premium through StoreKit: one non-consumable product
/// (<see cref="PremiumProduct.Id"/>), bought or restored with the App Store and recorded on
/// the device by <see cref="PremiumLedger"/>. The transaction observer is attached at launch
/// (<see cref="StoreKitObserverStarter"/>) so purchases that finish while the app is closed —
/// Ask to Buy approvals, interrupted purchases — are delivered and recorded.</summary>
public sealed class StoreKitPremiumStore : IPremiumStore, IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private readonly PremiumLedger _ledger;
    private readonly Observer _observer;
    private bool _observing;

    private SKProduct? _product;
    private TaskCompletionSource<PremiumPurchaseResult>? _purchase;
    private TaskCompletionSource<PremiumPurchaseResult>? _restore;
    private bool _restoreSawPremium;

    public StoreKitPremiumStore(PremiumLedger ledger)
    {
        _ledger = ledger;
        _observer = new Observer(this);
    }

    public event EventHandler? EntitlementChanged;

    /// <summary>Attach the transaction observer. Idempotent; called once at launch.</summary>
    public void StartObserving()
    {
        if (_observing) return;
        SKPaymentQueue.DefaultQueue.AddTransactionObserver(_observer);
        _observing = true;
    }

    public async Task<PremiumProductInfo> LoadProductAsync(CancellationToken ct)
    {
        if (!SKPaymentQueue.CanMakePayments) return new PremiumProductInfo(PremiumProductState.PurchasesDisabled);

        var tcs = new TaskCompletionSource<(SKProduct? Product, bool Failed)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var productDelegate = new ProductsDelegate(tcs);
        using var request = new SKProductsRequest(new NSSet(PremiumProduct.Id))
        {
            Delegate = productDelegate,
        };
        request.Start();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        using (timeout.Token.Register(() => tcs.TrySetResult((null, true))))
        {
            var (product, failed) = await tcs.Task.ConfigureAwait(true);
            if (failed)
            {
                request.Cancel();
                return PremiumProductInfo.Unavailable;
            }
            if (product is null) return new PremiumProductInfo(PremiumProductState.NotFound);

            _product = product;
            return new PremiumProductInfo(PremiumProductState.Available, FormatPrice(product));
        }
    }

    public async Task<PremiumPurchaseResult> PurchaseAsync(CancellationToken ct)
    {
        StartObserving();
        if (_product is null)
        {
            var info = await LoadProductAsync(ct).ConfigureAwait(true);
            switch (info.State)
            {
                case PremiumProductState.NotFound: return PremiumPurchaseResult.NotFound;
                case PremiumProductState.PurchasesDisabled: return PremiumPurchaseResult.PurchasesDisabled;
                case PremiumProductState.StoreUnavailable: return PremiumPurchaseResult.StoreUnavailable;
            }
        }
        if (_product is null) return PremiumPurchaseResult.NotFound;
        if (!SKPaymentQueue.CanMakePayments) return PremiumPurchaseResult.PurchasesDisabled;

        // One purchase at a time; a second tap joins the first.
        if (_purchase is { Task.IsCompleted: false } running) return await running.Task.ConfigureAwait(true);

        var tcs = new TaskCompletionSource<PremiumPurchaseResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _purchase = tcs;
        SKPaymentQueue.DefaultQueue.AddPayment(SKPayment.CreateFrom(_product));
        // No timeout: the App Store sheet waits on the parent (Face ID, password, Ask to Buy).
        using (ct.Register(() => tcs.TrySetResult(PremiumPurchaseResult.Cancelled)))
            return await tcs.Task.ConfigureAwait(true);
    }

    public async Task<PremiumPurchaseResult> RestoreAsync(CancellationToken ct)
    {
        StartObserving();
        if (_restore is { Task.IsCompleted: false } running) return await running.Task.ConfigureAwait(true);

        var tcs = new TaskCompletionSource<PremiumPurchaseResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _restore = tcs;
        _restoreSawPremium = false;
        SKPaymentQueue.DefaultQueue.RestoreCompletedTransactions();
        using (ct.Register(() => tcs.TrySetResult(PremiumPurchaseResult.Cancelled)))
            return await tcs.Task.ConfigureAwait(true);
    }

    private static string FormatPrice(SKProduct product)
    {
        using var formatter = new NSNumberFormatter
        {
            NumberStyle = NSNumberFormatterStyle.Currency,
            Locale = product.PriceLocale,
        };
        return formatter.StringFromNumber(product.Price) ?? product.Price.ToString();
    }

    // ---- Observer callbacks (StoreKit delivers them on the main thread) ----

    private void OnUpdatedTransactions(SKPaymentTransaction[] transactions)
    {
        var changed = false;
        foreach (var transaction in transactions)
        {
            var isPremium = PremiumLedger.IsPremiumProduct(transaction.Payment?.ProductIdentifier);
            switch (transaction.TransactionState)
            {
                case SKPaymentTransactionState.Purchasing:
                    break;   // the App Store sheet is up; nothing to record yet
                case SKPaymentTransactionState.Deferred:
                    if (isPremium) _purchase?.TrySetResult(PremiumPurchaseResult.Pending);
                    break;   // Ask to Buy: the approved transaction arrives later as Purchased
                case SKPaymentTransactionState.Purchased:
                    if (isPremium)
                    {
                        changed |= _ledger.RecordGranted(PremiumProduct.Id);
                        if (_purchase?.TrySetResult(PremiumPurchaseResult.Purchased) == true) changed = false;
                    }
                    SKPaymentQueue.DefaultQueue.FinishTransaction(transaction);
                    break;
                case SKPaymentTransactionState.Restored:
                    if (isPremium)
                    {
                        _restoreSawPremium = true;
                        var granted = _ledger.RecordGranted(PremiumProduct.Id);
                        // A restore in flight reports its own result; only an unprompted
                        // restored transaction is news for listeners.
                        if (_restore is not { Task.IsCompleted: false }) changed |= granted;
                    }
                    SKPaymentQueue.DefaultQueue.FinishTransaction(transaction);
                    break;
                case SKPaymentTransactionState.Failed:
                    if (isPremium) _purchase?.TrySetResult(IsCancellation(transaction.Error)
                        ? PremiumPurchaseResult.Cancelled
                        : PremiumPurchaseResult.Failed);
                    SKPaymentQueue.DefaultQueue.FinishTransaction(transaction);
                    break;
            }
        }
        // A grant nobody was awaiting (delivered at launch, or an Ask to Buy approval) —
        // tell whoever is listening.
        if (changed) EntitlementChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnRestoreFinished()
    {
        var saw = _restoreSawPremium;
        var changed = _ledger.RecordRestoreFinished(saw);
        var awaited = _restore?.TrySetResult(saw ? PremiumPurchaseResult.Restored : PremiumPurchaseResult.NothingToRestore) == true;
        if (changed && !awaited) EntitlementChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnRestoreFailed(NSError error) =>
        _restore?.TrySetResult(IsCancellation(error) ? PremiumPurchaseResult.Cancelled : PremiumPurchaseResult.StoreUnavailable);

    private void OnRevoked(string[] productIdentifiers)
    {
        if (_ledger.RecordRevoked(productIdentifiers)) EntitlementChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsCancellation(NSError? error) =>
        error is not null
        && error.Domain == SKError.PaymentCancelled.GetDomain()?.ToString()
        && error.Code == (nint)(long)SKError.PaymentCancelled;

    public void Dispose()
    {
        if (_observing) SKPaymentQueue.DefaultQueue.RemoveTransactionObserver(_observer);
        _observing = false;
        _observer.Dispose();
        _product?.Dispose();
    }

    /// <summary>StoreKit's observer is an NSObject; it forwards to the store.</summary>
    private sealed class Observer(StoreKitPremiumStore owner) : SKPaymentTransactionObserver
    {
        public override void UpdatedTransactions(SKPaymentQueue queue, SKPaymentTransaction[] transactions) =>
            owner.OnUpdatedTransactions(transactions);

        public override void RestoreCompletedTransactionsFinished(SKPaymentQueue queue) =>
            owner.OnRestoreFinished();

        public override void RestoreCompletedTransactionsFailedWithError(SKPaymentQueue queue, NSError error) =>
            owner.OnRestoreFailed(error);

        public override void DidRevokeEntitlements(SKPaymentQueue queue, string[] productIdentifiers) =>
            owner.OnRevoked(productIdentifiers);
    }

    /// <summary>Completes the product lookup: our product, "not sold here", or failure.</summary>
    private sealed class ProductsDelegate(TaskCompletionSource<(SKProduct? Product, bool Failed)> tcs)
        : SKProductsRequestDelegate
    {
        public override void ReceivedResponse(SKProductsRequest request, SKProductsResponse response) =>
            tcs.TrySetResult((response.Products?.FirstOrDefault(p => PremiumLedger.IsPremiumProduct(p.ProductIdentifier)), false));

        public override void RequestFailed(SKRequest request, NSError error) =>
            tcs.TrySetResult((null, true));
    }
}

/// <summary>Attaches the StoreKit transaction observer while the app launches, as Apple
/// requires, without touching AppDelegate.</summary>
public sealed class StoreKitObserverStarter : Microsoft.Maui.Hosting.IMauiInitializeService
{
    public void Initialize(IServiceProvider services) =>
        services.GetRequiredService<StoreKitPremiumStore>().StartObserving();
}
