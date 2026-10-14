using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StoryTime.Contracts;
using StoryTime.Core.Abstractions;
using StoryTime.Core.Services;

namespace StoryTime.Core.ViewModels;

/// <summary>Kid Shelf — browse/replay only. Entering sets kid mode (back-nav blocked);
/// only the 2-second long-press on the lock pill exits.</summary>
public sealed partial class ShelfViewModel : ObservableObject
{
    private readonly IStoryStore _store;
    private readonly INavigator _navigator;
    private readonly PlayerLaunchContext _playerContext;
    private readonly KidModeService _kidMode;
    private readonly IHaptics _haptics;
    private readonly IStoryCatalog _catalog;
    private readonly CoalescingReloader _reloader;

    public ShelfViewModel(IStoryStore store, INavigator navigator, PlayerLaunchContext playerContext, KidModeService kidMode,
        IHaptics haptics, IStoryCatalog catalog)
    {
        _store = store;
        _navigator = navigator;
        _playerContext = playerContext;
        _kidMode = kidMode;
        _haptics = haptics;
        _catalog = catalog;
        _reloader = new CoalescingReloader(RefreshTilesAsync);

        // Live: a story approved in the player (or a story run finishing with the approval
        // step off) lands on the shelf without leaving and re-entering it.
        _catalog.Changed += (_, _) => TilesRefresh = _reloader.RequestAsync();
    }

    [ObservableProperty]
    private string _headline = "The shelf";

    public ObservableCollection<StoryListItem> Tiles { get; } = [];

    /// <summary>Last catalog-triggered reload (awaitable by tests).</summary>
    public Task? TilesRefresh { get; private set; }

    public async Task ActivateAsync()
    {
        _kidMode.Enter();
        var profile = await _store.GetActiveProfileAsync();
        if (profile is null) return;
        Headline = $"{profile.Name}'s shelf";
        await _reloader.RequestAsync();
    }

    /// <summary>The shelf stays Ready-ONLY, and that is deliberate: an AwaitingApproval
    /// story has not been heard by a grown-up yet, and the Kid Shelf is the one surface a
    /// child drives unsupervised. Showing it here would route around the approval covenant
    /// (CLAUDE.md: parental oversight is structural). It appears the instant the teaser is
    /// approved — the catalog carries that change here live.
    ///
    /// Only ever invoked through <c>_reloader</c> — the single writer of <see cref="Tiles"/>.</summary>
    private async Task RefreshTilesAsync()
    {
        var profile = await _store.GetActiveProfileAsync();
        if (profile is null) return;
        var ready = (await _store.GetStoriesAsync(profile.Id))
            .Where(s => s.State == StoryState.Ready)
            .ToList();
        StoryListItem.SyncInto(Tiles, ready);
    }

    /// <summary>Tap a story to listen again — replay only, full audio (Ready stories only).</summary>
    [RelayCommand]
    private async Task ReplayAsync(StoryListItem item)
    {
        if (item is null) return;
        _playerContext.StoryId = item.Id;
        _playerContext.TeaserOnly = false;
        await _navigator.GoToAsync(Routes.Player);
    }

    /// <summary>Called by the view after a completed 2-second long-press on the lock pill.
    /// (The hold-start Light tick lives in the view, which owns the touch state.)</summary>
    [RelayCommand]
    private async Task ExitShelfAsync()
    {
        _haptics.Success();   // hold completed — the lock opens
        _kidMode.Exit();
        await _navigator.GoToAsync(Routes.Home);
    }
}
