using StoryTime.Core.Services;
using StoryTimeNative;

namespace StoryTime.App.Platforms.iOS;

/// <summary>iOS <see cref="IAppStoreBridge"/> over the StoryTimeNative binding (Swift
/// <c>SESKStore</c>, StoreKit 2). Native callbacks arrive on the main queue.</summary>
public sealed class AppStoreBridge : IAppStoreBridge
{
    private static SESKStore Store => SESKStore.Shared;

    public bool CanMakePayments => Store.CanMakePayments;

    public void Start(string productId, Action<string, bool> onEntitlement) =>
        Store.Start(productId, (id, owned) => onEntitlement(id, owned));

    public void LoadProduct(string productId, Action<string> completion) =>
        Store.LoadProduct(productId, json => completion(json));

    public void Purchase(string productId, Action<string, string?> completion) =>
        Store.Purchase(productId, (outcome, detail) => completion(outcome, detail));

    public void Restore(string productId, Action<string, string?> completion) =>
        Store.Restore(productId, (outcome, detail) => completion(outcome, detail));
}

/// <summary>Starts the StoreKit 2 <c>Transaction.updates</c> listener while the app
/// launches, as Apple requires, without touching AppDelegate.</summary>
public sealed class StoreKitListenerStarter : Microsoft.Maui.Hosting.IMauiInitializeService
{
    public void Initialize(IServiceProvider services) =>
        services.GetRequiredService<StoreKitPremiumStore>().Start();
}
