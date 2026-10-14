using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StoryTime.Core.Abstractions;
using StoryTime.Core.Services;

namespace StoryTime.Core.ViewModels;

/// <summary>Every state the Premium page can honestly be in.</summary>
public enum PremiumPageState
{
    Loading,
    Available,
    Purchasing,
    Restoring,
    Purchased,
    Pending,
    Failed,
    NotFound,
    StoreUnavailable,
    PurchasesDisabled,
}

/// <summary>The Premium page: one on-device, one-off purchase through the App Store
/// (<see cref="IPremiumStore"/>). No subscription, no account, no server. The price comes
/// from the store, localized; every failure has plain, true copy. Debug builds keep a quiet
/// local testing unlock (<see cref="ISettingsStore.DebugPremiumOverride"/>).</summary>
public sealed partial class PremiumViewModel : ObservableObject
{
    public const string StoreUnavailableText = "The App Store isn't available right now. Please try again later.";
    public const string NotFoundText =
        "Premium can't be bought from the App Store right now. Everything free keeps working. Please try again later.";
    public const string PurchasesDisabledText =
        "In-app purchases are turned off on this device. A grown-up can allow them in Settings → Screen Time.";
    public const string FailedText = "The purchase didn't go through, and nothing was charged. Please try again.";
    public const string NothingToRestoreText = "No earlier Premium purchase was found for this Apple ID.";
    public const string RestoredText = "Your purchase is restored.";

    private readonly ISettingsStore _settings;
    private readonly INavigator _navigator;
    private readonly IHaptics _haptics;
    private readonly IPremiumStore _store;
    private readonly PremiumLedger _ledger;

    private string? _price;
    private bool _subscribed;

    public PremiumViewModel(ISettingsStore settings, INavigator navigator, IHaptics haptics,
        IPremiumStore store, PremiumLedger ledger)
    {
        _settings = settings;
        _navigator = navigator;
        _haptics = haptics;
        _store = store;
        _ledger = ledger;
        _isUnlockedForTesting = settings.DebugPremiumOverride;
    }

    /// <summary>The quiet testing row renders only in Debug builds.</summary>
    public static bool IsDebugBuild =>
#if DEBUG
        true;
#else
        false;
#endif

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PriceTitle))]
    [NotifyPropertyChangedFor(nameof(PriceBody))]
    [NotifyPropertyChangedFor(nameof(PrimaryText))]
    [NotifyPropertyChangedFor(nameof(ShowPrimary))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(ShowRestore))]
    [NotifyCanExecuteChangedFor(nameof(PrimaryCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    private PremiumPageState _state = PremiumPageState.Loading;

    /// <summary>A one-off result line under the card (restore outcomes). Empty when quiet.</summary>
    [ObservableProperty]
    private string _notice = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TestingRowText))]
    private bool _isUnlockedForTesting;

    public string TestingRowText => IsUnlockedForTesting
        ? "Premium · unlocked for testing — tap to relock"
        : "unlock for testing";

    public bool IsBusy => State is PremiumPageState.Loading or PremiumPageState.Purchasing or PremiumPageState.Restoring;

    public string PriceTitle => State switch
    {
        PremiumPageState.Purchased => "Premium is yours",
        PremiumPageState.Pending => "Waiting for approval",
        PremiumPageState.Available or PremiumPageState.Purchasing or PremiumPageState.Failed
            when _price is not null => $"Premium · {_price}",
        _ => "Premium",
    };

    public string PriceBody => State switch
    {
        PremiumPageState.Loading => "Checking the App Store…",
        PremiumPageState.Available => "One payment, yours to keep. No subscription, no account.",
        PremiumPageState.Purchasing => "Waiting for the App Store…",
        PremiumPageState.Restoring => "Looking for your earlier purchase…",
        PremiumPageState.Purchased => "Thank you. Longer stories, music and shorter waits are on.",
        PremiumPageState.Pending => "A family organiser needs to approve this purchase. Premium turns on as soon as they do.",
        PremiumPageState.Failed => FailedText,
        PremiumPageState.NotFound => NotFoundText,
        PremiumPageState.StoreUnavailable => StoreUnavailableText,
        PremiumPageState.PurchasesDisabled => PurchasesDisabledText,
        _ => string.Empty,
    };

    /// <summary>Buy when there is something to buy; Try again when the store let us down.</summary>
    public string PrimaryText => State switch
    {
        PremiumPageState.NotFound or PremiumPageState.StoreUnavailable or PremiumPageState.PurchasesDisabled => "Try again",
        _ => _price is null ? "Buy Premium" : $"Buy Premium · {_price}",
    };

    public bool ShowPrimary => State is not (PremiumPageState.Loading or PremiumPageState.Restoring
        or PremiumPageState.Purchased or PremiumPageState.Pending);

    public bool ShowRestore => State is not (PremiumPageState.Purchased or PremiumPageState.Pending);

    public async Task ActivateAsync()
    {
        _haptics.Light();   // paygate page opening
        IsUnlockedForTesting = _settings.DebugPremiumOverride;
        if (!_subscribed)
        {
            _store.EntitlementChanged += OnEntitlementChanged;
            _subscribed = true;
        }
        Notice = string.Empty;
        if (_ledger.IsRecorded)
        {
            State = PremiumPageState.Purchased;
            return;
        }
        await LoadProductAsync();
    }

    /// <summary>Page left: stop listening (the store is a singleton, this VM is not).</summary>
    public void Deactivate()
    {
        if (!_subscribed) return;
        _store.EntitlementChanged -= OnEntitlementChanged;
        _subscribed = false;
    }

    private void OnEntitlementChanged(object? sender, EventArgs e)
    {
        if (_ledger.IsRecorded)
        {
            if (State != PremiumPageState.Purchased) _haptics.Celebrate();
            State = PremiumPageState.Purchased;
        }
        else if (State == PremiumPageState.Purchased)
        {
            _ = LoadProductAsync();
        }
    }

    private async Task LoadProductAsync()
    {
        State = PremiumPageState.Loading;
        PremiumProductInfo info;
        try
        {
            info = await _store.LoadProductAsync(CancellationToken.None);
        }
        catch (Exception)
        {
            info = PremiumProductInfo.Unavailable;
        }
        if (_ledger.IsRecorded)
        {
            State = PremiumPageState.Purchased;
            return;
        }
        _price = info.State == PremiumProductState.Available ? info.LocalizedPrice : null;
        State = info.State switch
        {
            PremiumProductState.Available => PremiumPageState.Available,
            PremiumProductState.NotFound => PremiumPageState.NotFound,
            PremiumProductState.PurchasesDisabled => PremiumPageState.PurchasesDisabled,
            _ => PremiumPageState.StoreUnavailable,
        };
    }

    private bool CanRunPrimary() => ShowPrimary && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRunPrimary))]
    private async Task PrimaryAsync()
    {
        Notice = string.Empty;
        if (State is PremiumPageState.NotFound or PremiumPageState.StoreUnavailable or PremiumPageState.PurchasesDisabled)
        {
            await LoadProductAsync();
            return;
        }

        State = PremiumPageState.Purchasing;
        PremiumPurchaseResult result;
        try
        {
            result = await _store.PurchaseAsync(CancellationToken.None);
        }
        catch (Exception)
        {
            result = PremiumPurchaseResult.Failed;
        }
        Apply(result);
    }

    private bool CanRestore() => ShowRestore && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task RestoreAsync()
    {
        Notice = string.Empty;
        var before = State;
        State = PremiumPageState.Restoring;
        PremiumPurchaseResult result;
        try
        {
            result = await _store.RestoreAsync(CancellationToken.None);
        }
        catch (Exception)
        {
            result = PremiumPurchaseResult.StoreUnavailable;
        }

        switch (result)
        {
            case PremiumPurchaseResult.Restored:
            case PremiumPurchaseResult.Purchased:
                Apply(PremiumPurchaseResult.Restored);
                break;
            case PremiumPurchaseResult.NothingToRestore:
                _haptics.Selection();
                Notice = NothingToRestoreText;
                State = _price is not null ? PremiumPageState.Available : RestingStateAfter(before);
                break;
            case PremiumPurchaseResult.Cancelled:
                State = RestingStateAfter(before);
                break;
            default:
                _haptics.Warning();
                Notice = StoreUnavailableText;
                State = RestingStateAfter(before);
                break;
        }
    }

    /// <summary>Where the page returns after a restore that changed nothing: never back to
    /// a busy state, never to Failed (a restore does not fail a purchase).</summary>
    private PremiumPageState RestingStateAfter(PremiumPageState before) => before switch
    {
        PremiumPageState.Loading or PremiumPageState.Purchasing or PremiumPageState.Restoring or PremiumPageState.Failed
            => _price is not null ? PremiumPageState.Available : PremiumPageState.StoreUnavailable,
        _ => before,
    };

    private void Apply(PremiumPurchaseResult result)
    {
        switch (result)
        {
            case PremiumPurchaseResult.Purchased:
            case PremiumPurchaseResult.Restored:
                _haptics.Celebrate();   // premium unlocked — two-beat
                if (result == PremiumPurchaseResult.Restored) Notice = RestoredText;
                State = PremiumPageState.Purchased;
                break;
            case PremiumPurchaseResult.Cancelled:
                State = _price is not null ? PremiumPageState.Available : PremiumPageState.StoreUnavailable;
                break;
            case PremiumPurchaseResult.Pending:
                State = PremiumPageState.Pending;
                break;
            case PremiumPurchaseResult.NotFound:
                _haptics.Warning();
                _price = null;
                State = PremiumPageState.NotFound;
                break;
            case PremiumPurchaseResult.PurchasesDisabled:
                _haptics.Warning();
                State = PremiumPageState.PurchasesDisabled;
                break;
            case PremiumPurchaseResult.StoreUnavailable:
                _haptics.Warning();
                State = PremiumPageState.StoreUnavailable;
                break;
            default:
                _haptics.Warning();
                State = PremiumPageState.Failed;
                break;
        }
    }

    [RelayCommand]
    private void ToggleTestingUnlock()
    {
        if (!IsDebugBuild) return;
        _settings.DebugPremiumOverride = !_settings.DebugPremiumOverride;
        IsUnlockedForTesting = _settings.DebugPremiumOverride;
        if (IsUnlockedForTesting) _haptics.Celebrate();   // premium unlocked — two-beat
        else _haptics.Selection();
    }

    [RelayCommand]
    private Task CloseAsync() => _navigator.GoBackAsync();
}
