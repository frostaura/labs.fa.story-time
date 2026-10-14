using StoryTime.Contracts;
using StoryTime.Core.Abstractions;
using StoryTime.Core.Pipeline;
using StoryTime.Core.Services;
using StoryTime.Core.ViewModels;
using Xunit;

namespace StoryTime.Core.Tests;

public class PremiumLedgerTests
{
    private readonly InMemorySettingsStore _settings = new();
    private PremiumLedger Ledger => new(_settings);

    [Fact]
    public void Grant_RecordsOnlyThePremiumProduct()
    {
        Assert.False(Ledger.RecordGranted("net.frostaura.storytime.something-else"));
        Assert.False(_settings.PremiumPurchased);
        Assert.True(Ledger.RecordGranted(PremiumProduct.Id));
        Assert.True(_settings.PremiumPurchased);
        Assert.False(Ledger.RecordGranted(PremiumProduct.Id));   // idempotent: no change reported
    }

    [Fact]
    public void RestoreWithPremium_Grants()
    {
        Assert.True(Ledger.RecordRestoreFinished(sawPremium: true));
        Assert.True(_settings.PremiumPurchased);
    }

    [Fact]
    public void SuccessfulRestoreWithoutPremium_ClearsTheLocalRecord()
    {
        _settings.PremiumPurchased = true;
        Assert.True(Ledger.RecordRestoreFinished(sawPremium: false));
        Assert.False(_settings.PremiumPurchased);
    }

    [Fact]
    public void Revoke_ClearsOnlyForPremium()
    {
        _settings.PremiumPurchased = true;
        Assert.False(Ledger.RecordRevoked(["other.product"]));
        Assert.True(_settings.PremiumPurchased);
        Assert.True(Ledger.RecordRevoked([PremiumProduct.Id]));
        Assert.False(_settings.PremiumPurchased);
    }

    [Fact]
    public void ProductId_IsTheAppStoreIdentifier() =>
        Assert.Equal("net.frostaura.storytime.premium", PremiumProduct.Id);
}

public class StoreEntitlementsPurchaseTests
{
    [Fact]
    public void Purchase_UnlocksEveryPremiumGate()
    {
        var settings = new InMemorySettingsStore();
        var clock = new FakeClock();
        var entitlements = new StoreEntitlements(settings, clock);
        Assert.False(entitlements.IsPremium);

        new PremiumLedger(settings).RecordGranted(PremiumProduct.Id);

        Assert.True(entitlements.IsPremium);
        Assert.Equal(15, entitlements.CooldownMinutes);
        Assert.True(entitlements.CanUseLength(StoryLength.Fifteen));
    }

    [Fact]
    public void PurchasedPremium_CooldownIsFifteenMinutes_NotSkipped()
    {
        // Unlike the Debug testing unlock, a real purchase keeps the bedtime wait — just shorter.
        var settings = new InMemorySettingsStore { PremiumPurchased = true };
        var clock = new FakeClock();
        var cooldown = new CooldownService(settings, clock, new StoreEntitlements(settings, clock));
        cooldown.Start();
        Assert.Equal(clock.UtcNow.AddMinutes(15), settings.CooldownEndsUtc);
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(10, cooldown.Remaining!.Value.TotalMinutes, precision: 1);
    }
}

public class PremiumPurchaseFlowTests
{
    private readonly InMemorySettingsStore _settings = new();
    private readonly FakeHaptics _haptics = new();
    private readonly FakePremiumStore _store;
    private readonly PremiumViewModel _vm;

    public PremiumPurchaseFlowTests()
    {
        var ledger = new PremiumLedger(_settings);
        _store = new FakePremiumStore(ledger);
        _vm = new PremiumViewModel(_settings, new FakeNavigator(), _haptics, _store, ledger);
    }

    [Fact]
    public async Task Available_ShowsTheStoresLocalizedPrice()
    {
        _store.Product = new PremiumProductInfo(PremiumProductState.Available, "$4.99");
        await _vm.ActivateAsync();
        Assert.Equal(PremiumPageState.Available, _vm.State);
        Assert.Equal("Premium · $4.99", _vm.PriceTitle);
        Assert.Equal("Buy Premium · $4.99", _vm.PrimaryText);
        Assert.True(_vm.ShowPrimary);
        Assert.True(_vm.ShowRestore);
        Assert.True(_vm.PrimaryCommand.CanExecute(null));
    }

    [Fact]
    public async Task Buy_Purchased_RecordsEntitlementAndCelebrates()
    {
        await _vm.ActivateAsync();
        await _vm.PrimaryCommand.ExecuteAsync(null);
        Assert.Equal(PremiumPageState.Purchased, _vm.State);
        Assert.True(_settings.PremiumPurchased);
        Assert.Equal("Premium is yours", _vm.PriceTitle);
        Assert.False(_vm.ShowPrimary);
        Assert.False(_vm.ShowRestore);
        Assert.Equal("Celebrate", _haptics.Calls[^1]);
    }

    [Fact]
    public async Task Buy_Cancelled_ReturnsQuietlyToAvailable()
    {
        _store.NextPurchase = PremiumPurchaseResult.Cancelled;
        await _vm.ActivateAsync();
        await _vm.PrimaryCommand.ExecuteAsync(null);
        Assert.Equal(PremiumPageState.Available, _vm.State);
        Assert.Equal(string.Empty, _vm.Notice);
        Assert.False(_settings.PremiumPurchased);
    }

    [Fact]
    public async Task Buy_Failed_SaysNothingWasCharged_AndOffersBuyAgain()
    {
        _store.NextPurchase = PremiumPurchaseResult.Failed;
        await _vm.ActivateAsync();
        await _vm.PrimaryCommand.ExecuteAsync(null);
        Assert.Equal(PremiumPageState.Failed, _vm.State);
        Assert.Equal(PremiumViewModel.FailedText, _vm.PriceBody);
        Assert.StartsWith("Buy Premium", _vm.PrimaryText);
        Assert.True(_vm.PrimaryCommand.CanExecute(null));
        Assert.Equal("Warning", _haptics.Calls[^1]);
    }

    [Fact]
    public async Task Buy_AskToBuy_WaitsThenUnlocksOutOfBand()
    {
        _store.NextPurchase = PremiumPurchaseResult.Pending;
        await _vm.ActivateAsync();
        await _vm.PrimaryCommand.ExecuteAsync(null);
        Assert.Equal(PremiumPageState.Pending, _vm.State);
        Assert.False(_vm.ShowPrimary);

        _store.RaiseOutOfBand(granted: true);
        Assert.Equal(PremiumPageState.Purchased, _vm.State);
    }

    [Fact]
    public async Task ProductNotFound_HasRealCopy_AndTryAgainReloads()
    {
        _store.Product = new PremiumProductInfo(PremiumProductState.NotFound);
        await _vm.ActivateAsync();
        Assert.Equal(PremiumPageState.NotFound, _vm.State);
        Assert.Equal(PremiumViewModel.NotFoundText, _vm.PriceBody);
        Assert.Equal("Premium", _vm.PriceTitle);
        Assert.Equal("Try again", _vm.PrimaryText);

        _store.Product = new PremiumProductInfo(PremiumProductState.Available, "€5,99");
        await _vm.PrimaryCommand.ExecuteAsync(null);
        Assert.Equal(0, _store.PurchaseCount);
        Assert.Equal(2, _store.LoadCount);
        Assert.Equal(PremiumPageState.Available, _vm.State);
        Assert.Equal("Buy Premium · €5,99", _vm.PrimaryText);
    }

    [Fact]
    public async Task StoreUnavailable_SaysSoPlainly()
    {
        _store.Product = PremiumProductInfo.Unavailable;
        await _vm.ActivateAsync();
        Assert.Equal(PremiumPageState.StoreUnavailable, _vm.State);
        Assert.Equal("The App Store isn't available right now. Please try again later.", _vm.PriceBody);
        Assert.Equal("Try again", _vm.PrimaryText);
    }

    [Fact]
    public async Task PurchasesDisabled_PointsAtScreenTime()
    {
        _store.Product = new PremiumProductInfo(PremiumProductState.PurchasesDisabled);
        await _vm.ActivateAsync();
        Assert.Equal(PremiumPageState.PurchasesDisabled, _vm.State);
        Assert.Contains("Screen Time", _vm.PriceBody);
    }

    [Fact]
    public async Task AlreadyPurchased_OpensPurchased_WithoutAskingTheStore()
    {
        _settings.PremiumPurchased = true;
        await _vm.ActivateAsync();
        Assert.Equal(PremiumPageState.Purchased, _vm.State);
        Assert.Equal(0, _store.LoadCount);
    }

    [Fact]
    public async Task Restore_Found_Unlocks()
    {
        _store.NextRestore = PremiumPurchaseResult.Restored;
        await _vm.ActivateAsync();
        await _vm.RestoreCommand.ExecuteAsync(null);
        Assert.Equal(PremiumPageState.Purchased, _vm.State);
        Assert.Equal(PremiumViewModel.RestoredText, _vm.Notice);
        Assert.True(_settings.PremiumPurchased);
    }

    [Fact]
    public async Task Restore_NothingFound_SaysSo_AndKeepsBuyAvailable()
    {
        await _vm.ActivateAsync();
        await _vm.RestoreCommand.ExecuteAsync(null);
        Assert.Equal(PremiumPageState.Available, _vm.State);
        Assert.Equal(PremiumViewModel.NothingToRestoreText, _vm.Notice);
        Assert.False(_settings.PremiumPurchased);
    }

    [Fact]
    public async Task Restore_StoreDown_KeepsState_AndShowsUnavailableNotice()
    {
        _store.NextRestore = PremiumPurchaseResult.StoreUnavailable;
        await _vm.ActivateAsync();
        await _vm.RestoreCommand.ExecuteAsync(null);
        Assert.Equal(PremiumPageState.Available, _vm.State);
        Assert.Equal(PremiumViewModel.StoreUnavailableText, _vm.Notice);
    }

    [Fact]
    public async Task Refund_OutOfBand_ReturnsToBuyable()
    {
        _settings.PremiumPurchased = true;
        await _vm.ActivateAsync();
        _store.RaiseOutOfBand(granted: false);
        Assert.Equal(PremiumPageState.Available, _vm.State);
        Assert.False(_settings.PremiumPurchased);
    }

    [Fact]
    public async Task Deactivate_Unsubscribes_AndReactivateDoesNotDoubleSubscribe()
    {
        await _vm.ActivateAsync();
        await _vm.ActivateAsync();
        Assert.Equal(1, _store.SubscriberCount);
        _vm.Deactivate();
        Assert.Equal(0, _store.SubscriberCount);
    }

    [Fact]
    public async Task NoCopy_PromisesSomethingLater()
    {
        foreach (var state in new[] { PremiumProductState.Available, PremiumProductState.NotFound,
                     PremiumProductState.StoreUnavailable, PremiumProductState.PurchasesDisabled })
        {
            _store.Product = new PremiumProductInfo(state, state == PremiumProductState.Available ? "$4.99" : null);
            await _vm.ActivateAsync();
            foreach (var text in new[] { _vm.PriceTitle, _vm.PriceBody, _vm.PrimaryText })
            {
                Assert.DoesNotContain("coming soon", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("full release", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("subscribe", text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}

public class PremiumRegistrationTests
{
    [Fact]
    public void Core_RegistersStoreEntitlementsLedgerAndAnUnavailableStoreFallback()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddStoryTimeCore();
        Type? Impl<T>() => services.Single(d => d.ServiceType == typeof(T)).ImplementationType;
        Assert.Equal(typeof(StoreEntitlements), Impl<IEntitlements>());
        Assert.Equal(typeof(UnavailablePremiumStore), Impl<IPremiumStore>());
        Assert.Contains(services, d => d.ServiceType == typeof(PremiumLedger));
    }

    [Fact]
    public async Task UnavailableStore_ReportsUnavailableEverywhere()
    {
        var store = new UnavailablePremiumStore();
        Assert.Equal(PremiumProductState.StoreUnavailable, (await store.LoadProductAsync(CancellationToken.None)).State);
        Assert.Equal(PremiumPurchaseResult.StoreUnavailable, await store.PurchaseAsync(CancellationToken.None));
        Assert.Equal(PremiumPurchaseResult.StoreUnavailable, await store.RestoreAsync(CancellationToken.None));
    }
}
