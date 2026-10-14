using StoryTime.Core.Abstractions;
using StoryTime.Core.Services;
using Xunit;

namespace StoryTime.Core.Tests;

/// <summary>Scripted StoreKit 2 shim: each call parks its completion so a test decides when
/// and how the App Store answers.</summary>
internal sealed class FakeAppStoreBridge : IAppStoreBridge
{
    public bool CanMakePayments { get; set; } = true;
    public int StartCalls { get; private set; }
    public Action<string, bool>? OnEntitlement { get; private set; }
    public Action<string>? ProductCompletion { get; private set; }
    public Action<string, string?>? PurchaseCompletion { get; private set; }
    public Action<string, string?>? RestoreCompletion { get; private set; }
    public int PurchaseCalls { get; private set; }

    public void Start(string productId, Action<string, bool> onEntitlement)
    {
        StartCalls++;
        OnEntitlement = onEntitlement;
    }

    public void LoadProduct(string productId, Action<string> completion) => ProductCompletion = completion;

    public void Purchase(string productId, Action<string, string?> completion)
    {
        PurchaseCalls++;
        PurchaseCompletion = completion;
    }

    public void Restore(string productId, Action<string, string?> completion) => RestoreCompletion = completion;
}

public class StoreKitPremiumStoreTests
{
    private readonly InMemorySettingsStore _settings = new();
    private readonly FakeAppStoreBridge _bridge = new();
    private readonly StoreKitPremiumStore _store;
    private int _changes;

    public StoreKitPremiumStoreTests()
    {
        _store = new StoreKitPremiumStore(_bridge, new PremiumLedger(_settings));
        _store.EntitlementChanged += (_, _) => _changes++;
    }

    [Theory]
    [InlineData("""{"state":"available","displayPrice":"$4.99","displayName":"Premium"}""", PremiumProductState.Available, "$4.99")]
    [InlineData("""{"state":"notFound"}""", PremiumProductState.NotFound, null)]
    [InlineData("""{"state":"storeUnavailable","message":"offline"}""", PremiumProductState.StoreUnavailable, null)]
    [InlineData("""{"state":"purchasesDisabled"}""", PremiumProductState.PurchasesDisabled, null)]
    [InlineData("""{"state":"available"}""", PremiumProductState.StoreUnavailable, null)]   // no price: never "Buy for null"
    [InlineData("not json", PremiumProductState.StoreUnavailable, null)]
    public async Task Product_lookup_maps_every_store_answer(string json, PremiumProductState state, string? price)
    {
        var load = _store.LoadProductAsync(CancellationToken.None);
        _bridge.ProductCompletion!(json);
        var info = await load;
        Assert.Equal(state, info.State);
        Assert.Equal(price, info.LocalizedPrice);
    }

    [Fact]
    public async Task Purchases_disabled_short_circuits_without_asking_the_store()
    {
        _bridge.CanMakePayments = false;
        Assert.Equal(PremiumProductState.PurchasesDisabled, (await _store.LoadProductAsync(CancellationToken.None)).State);
        Assert.Equal(PremiumPurchaseResult.PurchasesDisabled, await _store.PurchaseAsync(CancellationToken.None));
        Assert.Null(_bridge.ProductCompletion);
        Assert.Equal(0, _bridge.PurchaseCalls);
    }

    [Theory]
    [InlineData("purchased", PremiumPurchaseResult.Purchased, true)]
    [InlineData("pending", PremiumPurchaseResult.Pending, false)]
    [InlineData("cancelled", PremiumPurchaseResult.Cancelled, false)]
    [InlineData("failed", PremiumPurchaseResult.Failed, false)]
    [InlineData("notFound", PremiumPurchaseResult.NotFound, false)]
    [InlineData("storeUnavailable", PremiumPurchaseResult.StoreUnavailable, false)]
    [InlineData("purchasesDisabled", PremiumPurchaseResult.PurchasesDisabled, false)]
    [InlineData("somethingNew", PremiumPurchaseResult.Failed, false)]
    [InlineData("0", PremiumPurchaseResult.Failed, false)]
    public async Task Purchase_outcomes_map_and_only_a_verified_purchase_grants(string outcome, PremiumPurchaseResult expected, bool granted)
    {
        var purchase = _store.PurchaseAsync(CancellationToken.None);
        _bridge.PurchaseCompletion!(outcome, null);
        Assert.Equal(expected, await purchase);
        Assert.Equal(granted, _settings.PremiumPurchased);
        Assert.Equal(0, _changes);   // the caller got the answer directly
    }

    [Fact]
    public async Task Purchase_starts_the_listener_and_a_second_tap_joins_the_first()
    {
        var first = _store.PurchaseAsync(CancellationToken.None);
        var second = _store.PurchaseAsync(CancellationToken.None);
        Assert.Equal(1, _bridge.PurchaseCalls);
        Assert.Equal(1, _bridge.StartCalls);
        _bridge.PurchaseCompletion!("purchased", null);
        Assert.Equal(PremiumPurchaseResult.Purchased, await first);
        Assert.Equal(PremiumPurchaseResult.Purchased, await second);
    }

    [Fact]
    public async Task A_purchase_finishing_after_the_caller_gave_up_still_grants_and_notifies()
    {
        using var cts = new CancellationTokenSource();
        var purchase = _store.PurchaseAsync(cts.Token);
        await cts.CancelAsync();
        Assert.Equal(PremiumPurchaseResult.Cancelled, await purchase);

        _bridge.PurchaseCompletion!("purchased", null);
        Assert.True(_settings.PremiumPurchased);
        Assert.Equal(1, _changes);
    }

    [Fact]
    public async Task Ask_to_buy_approval_arrives_through_the_listener()
    {
        var purchase = _store.PurchaseAsync(CancellationToken.None);
        _bridge.PurchaseCompletion!("pending", null);
        Assert.Equal(PremiumPurchaseResult.Pending, await purchase);
        Assert.False(_settings.PremiumPurchased);

        _bridge.OnEntitlement!(PremiumProduct.Id, true);
        Assert.True(_settings.PremiumPurchased);
        Assert.Equal(1, _changes);
        _bridge.OnEntitlement!(PremiumProduct.Id, true);   // repeated delivery: no second notice
        Assert.Equal(1, _changes);
    }

    [Fact]
    public void Refund_or_revocation_removes_premium_and_notifies()
    {
        _settings.PremiumPurchased = true;
        _store.Start();
        _store.Start();
        Assert.Equal(1, _bridge.StartCalls);

        _bridge.OnEntitlement!("net.frostaura.storytime.other", false);
        Assert.True(_settings.PremiumPurchased);
        _bridge.OnEntitlement!(PremiumProduct.Id, false);
        Assert.False(_settings.PremiumPurchased);
        Assert.Equal(1, _changes);
    }

    [Theory]
    [InlineData("restored", false, PremiumPurchaseResult.Restored, true)]
    [InlineData("nothingToRestore", true, PremiumPurchaseResult.NothingToRestore, false)]
    [InlineData("cancelled", true, PremiumPurchaseResult.Cancelled, true)]
    [InlineData("storeUnavailable", true, PremiumPurchaseResult.StoreUnavailable, true)]
    [InlineData("garbage", true, PremiumPurchaseResult.StoreUnavailable, true)]
    public async Task Restore_records_only_a_completed_sync(string outcome, bool before, PremiumPurchaseResult expected, bool after)
    {
        _settings.PremiumPurchased = before;
        var restore = _store.RestoreAsync(CancellationToken.None);
        _bridge.RestoreCompletion!(outcome, null);
        Assert.Equal(expected, await restore);
        Assert.Equal(after, _settings.PremiumPurchased);
    }
}
