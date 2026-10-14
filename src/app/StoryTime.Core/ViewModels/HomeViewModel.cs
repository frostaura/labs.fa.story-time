using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StoryTime.Contracts;
using StoryTime.Core.Abstractions;
using StoryTime.Core.Pipeline;
using StoryTime.Core.Services;

namespace StoryTime.Core.ViewModels;

public sealed partial class HomeViewModel : ObservableObject
{
    private readonly IStoryStore _store;
    private readonly IWeaveHost _weaveHost;
    private readonly CooldownService _cooldown;
    private readonly IEntitlements _entitlements;
    private readonly INavigator _navigator;
    private readonly PlayerLaunchContext _playerContext;
    private readonly KidModeService _kidMode;
    private readonly PosterArtRefresher _artRefresher;
    private readonly IHaptics _haptics;
    private readonly WeaveSheetViewModel _weaveSheet;
    private readonly IStoryCatalog _catalog;
    private readonly CoalescingReloader _reloader;

    private ChildProfile? _profile;

    public HomeViewModel(IStoryStore store, IWeaveHost weaveHost, CooldownService cooldown,
        IEntitlements entitlements, INavigator navigator, PlayerLaunchContext playerContext, KidModeService kidMode,
        PosterArtRefresher artRefresher, IHaptics haptics, WeaveSheetViewModel weaveSheet,
        RecipeShelfViewModel recipeShelf, IStoryCatalog catalog)
    {
        _store = store;
        _weaveHost = weaveHost;
        _cooldown = cooldown;
        _entitlements = entitlements;
        _navigator = navigator;
        _playerContext = playerContext;
        _kidMode = kidMode;
        _artRefresher = artRefresher;
        _haptics = haptics;
        _weaveSheet = weaveSheet;
        _catalog = catalog;
        Recipe = recipeShelf;
        _reloader = new CoalescingReloader(RefreshStoriesAsync);

        // ANY story mutation, from anywhere, re-renders the carousel live — a finished
        // weave, an approval unlock in the player, a favourite, a delete, a discarded
        // cancel (IStoryCatalog). This replaced a WeaveCompleted-only subscription that
        // left every other page stale.
        _catalog.Changed += (_, _) => StoriesRefresh = _reloader.RequestAsync();

        // The weave host still drives the CTA/cooldown copy (not a store mutation).
        _weaveHost.WeaveCompleted += (_, _) => WeaveCompletedRefresh = HandleWeaveCompletedAsync();
        _weaveHost.WeaveCancelled += (_, _) => RefreshCooldown();

        // The recipe shelf mutates the SAME profile instance this VM holds and saves it
        // on every change — the summary line re-renders immediately from shared state.
        Recipe.ProfileChanged += (_, _) =>
        {
            if (_profile is not null) SummaryLine = BuildSummary(_profile);
        };
    }

    /// <summary>The "Tonight's recipe" shelf state (interests / narrator / mode; the
    /// summary line is its entry point).</summary>
    public RecipeShelfViewModel Recipe { get; }

    /// <summary>Last completion-triggered refresh (awaitable by tests).</summary>
    public Task? WeaveCompletedRefresh { get; private set; }

    /// <summary>Last catalog-triggered list reload (awaitable by tests).</summary>
    public Task? StoriesRefresh { get; private set; }

    private async Task HandleWeaveCompletedAsync()
    {
        RefreshCooldown();
        await _reloader.RequestAsync();
    }

    [ObservableProperty]
    private string _greeting = "Good evening.";

    [ObservableProperty]
    private string _headline = "Tonight's story";

    [ObservableProperty]
    private string _summaryLine = string.Empty;

    [ObservableProperty]
    private string _avatarInitial = "•";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WeaveCommand))]
    private bool _canWeave = true;

    [ObservableProperty]
    private string _weaveButtonText = "Weave tonight's story";

    [ObservableProperty]
    private StoryLength _selectedLength = StoryLength.Ten;

    [ObservableProperty]
    private bool _showPremiumHint;

    public ObservableCollection<StoryListItem> Stories { get; } = [];

    public bool HasStories => Stories.Count > 0;

    /// <summary>Call on page appearing and on a timer while the cooldown runs.</summary>
    public async Task ActivateAsync()
    {
        _profile = await _store.GetActiveProfileAsync();
        if (_profile is null) return;

        Greeting = GreetingFor(DateTime.Now.Hour);
        Headline = $"Tonight's story for {_profile.Name}";
        AvatarInitial = _profile.Name.Length > 0 ? _profile.Name[..1].ToUpperInvariant() : "•";
        SelectedLength = _profile.Length;
        SummaryLine = BuildSummary(_profile);

        // Re-render any thumbnails cached under an older poster art version.
        await _artRefresher.EnsureCurrentAsync(_profile.Id);

        // Through the reloader, never straight into RefreshStoriesAsync: a catalog-driven
        // reload can be in flight (a weave is writing while Home appears), and two passes
        // mutating the same ObservableCollection corrupt it — the trailing trim throws
        // IndexOutOfRange. The reloader is the single writer.
        await _reloader.RequestAsync();
        RefreshCooldown();
    }

    /// <summary>The carousel's own reload. Call it through <c>_reloader</c>, never directly:
    /// it is the only writer of <see cref="Stories"/> and must stay single-threaded.
    /// Cheap, and safe to run on any story change.
    /// Visibility rule (documented in docs/design/mockup-spec.md): Home shows only what a
    /// parent can actually press play on — Ready and AwaitingApproval, newest first. A
    /// weave in progress belongs to the weave sheet, and a failed weave belongs in the
    /// Library where it can be understood, not on the bedtime screen.</summary>
    private async Task RefreshStoriesAsync()
    {
        _profile ??= await _store.GetActiveProfileAsync();
        if (_profile is null) return;

        var stories = await _store.GetStoriesAsync(_profile.Id);
        var visible = stories
            .Where(s => s.State is StoryState.Ready or StoryState.AwaitingApproval)
            .OrderByDescending(s => s.CreatedUtc)
            .Take(8)
            .ToList();

        var previousHeadId = Stories.Count > 0 ? Stories[0].Id : 0;

        // Rebuild in place: replacing the whole collection resets the carousel offset on
        // every single weave write, so the list is only touched where it actually differs.
        StoryListItem.SyncInto(Stories, visible);
        OnPropertyChanged(nameof(HasStories));

        // A newly-woven story is inserted at index 0, and Android's RecyclerView anchors on
        // the item it was already showing — so the carousel kept the OLD first card on
        // screen and tonight's story sat off to the left, unseen (observed on the emulator
        // 2026-07-31). The view listens for this and snaps back to the head.
        if (Stories.Count > 0 && Stories[0].Id != previousHeadId)
            HeadStoryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised when the newest visible story changes identity — i.e. the carousel
    /// gained a new first card. The view scrolls back to the head; nothing else listens.</summary>
    public event EventHandler? HeadStoryChanged;

    /// <summary>Cheap tick — updates the weave CTA against the live cooldown.</summary>
    public void RefreshCooldown()
    {
        if (_weaveHost.IsWeaving)
        {
            CanWeave = false;
            WeaveButtonText = "Weaving tonight's story…";
            return;
        }
        var next = _cooldown.NextWeaveAtLocal;
        if (next is null)
        {
            CanWeave = true;
            WeaveButtonText = "Weave tonight's story";
        }
        else
        {
            CanWeave = false;
            WeaveButtonText = $"The loom rests — next weave at {next:HH:mm}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanWeave))]
    private async Task WeaveAsync()
    {
        if (_profile is null || _cooldown.IsActive || _weaveHost.IsWeaving) return;
        _haptics.Medium();   // weave start — the one action of weight on this screen
        if (_profile.Length != SelectedLength)
        {
            _profile.Length = SelectedLength;
            await _store.SaveProfileAsync(_profile);
        }
        // The pipeline gets a SNAPSHOT: recipe-shelf edits made while this weave runs
        // must only affect the NEXT weave, never the one already on the loom.
        await _weaveHost.StartAsync(Snapshot(_profile));
        // No page navigation any more: the weave sheet slides up expanded over Home
        // (founder decision 2026-07-30 — generation runs behind the whole app).
        await _weaveSheet.ShowForWeaveAsync();
        RefreshCooldown();
    }

    [RelayCommand]
    private async Task SelectLengthAsync(StoryLength length)
    {
        if (!_entitlements.CanUseLength(length))
        {
            _haptics.Warning();          // premium-gate bump
            ShowPremiumHint = true;
            return;
        }
        if (length != SelectedLength) _haptics.Selection();
        ShowPremiumHint = false;
        SelectedLength = length;
        // Write-through: the pick persists the moment it lands, not at weave time.
        // (Root cause of the founder's "my selection isn't remembered": the old handler
        // only wrote a local property, and ActivateAsync re-read the stored profile on
        // every appearing — overwriting the choice on the next visit or cold start.)
        if (_profile is not null && _profile.Length != length)
        {
            _profile.Length = length;
            await _store.SaveProfileAsync(_profile);
            SummaryLine = BuildSummary(_profile);
        }
    }

    /// <summary>The summary line under the headline — a whispered entry point into the
    /// recipe shelf. Blocked while the weave sheet is expanded (its scrim owns the screen;
    /// no entry point is visible then anyway).</summary>
    [RelayCommand]
    private async Task AdjustRecipeAsync()
    {
        if (_weaveSheet.State == WeaveSheetState.Expanded) return;
        _profile ??= await _store.GetActiveProfileAsync();
        if (_profile is null) return;
        _haptics.Light();
        await Recipe.OpenAsync(_profile);
    }

    private static ChildProfile Snapshot(ChildProfile profile) => new()
    {
        Id = profile.Id,
        Name = profile.Name,
        Age = profile.Age,
        Interests = [.. profile.Interests],
        Voice = profile.Voice,
        Length = profile.Length,
        Mode = profile.Mode,
        ContentFilter = profile.ContentFilter,
    };

    /// <summary>The gate upsell (premium hint) routes to the Premium page on tap.</summary>
    [RelayCommand]
    private Task OpenPremiumAsync()
    {
        _haptics.Light();
        return _navigator.GoToAsync(Routes.Premium);
    }

    [RelayCommand]
    private async Task OpenStoryAsync(StoryListItem item)
    {
        if (item is null) return;
        _playerContext.StoryId = item.Id;
        _playerContext.TeaserOnly = item.Record.State == StoryState.AwaitingApproval;
        await _navigator.GoToAsync(Routes.Player);
    }

    [RelayCommand]
    private async Task ToggleFavoriteAsync(StoryListItem item)
    {
        if (item is null) return;
        item.IsFavorite = !item.IsFavorite;
        item.Record.IsFavorite = item.IsFavorite;
        await _store.SaveStoryAsync(item.Record);   // ♥ propagates via the catalog
        await _reloader.RequestAsync();
    }

    [RelayCommand]
    private async Task DeleteStoryAsync(StoryListItem item)
    {
        if (item is null) return;
        // The catalog notification removes it here, in the Library and on the Kid Shelf;
        // awaiting the reload just means THIS page is settled when the command returns.
        await _store.DeleteStoryAsync(item.Id);
        await _reloader.RequestAsync();
    }

    [RelayCommand]
    private async Task OpenShelfAsync()
    {
        _haptics.Light();
        _kidMode.Enter();
        await _navigator.GoToAsync(Routes.Shelf);
    }

    [RelayCommand]
    private Task OpenSettingsAsync()
    {
        _haptics.Light();
        return _navigator.GoToAsync(Routes.Settings);
    }

    [RelayCommand]
    private Task OpenLibraryAsync() => _navigator.GoToAsync(Routes.Library);

    private static string GreetingFor(int hour) => hour switch
    {
        >= 5 and < 12 => "Good morning.",
        >= 12 and < 17 => "Good afternoon.",
        _ => "Good evening.",
    };

    private static string BuildSummary(ChildProfile profile)
    {
        var interests = profile.Interests.Count > 0
            ? string.Join(" · ", profile.Interests.Take(3))
            : "Gentle tales";
        return $"{interests} · {profile.Voice} · {(int)profile.Length} min";
    }
}
