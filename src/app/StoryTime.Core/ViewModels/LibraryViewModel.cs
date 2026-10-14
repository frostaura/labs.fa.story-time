using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StoryTime.Contracts;
using StoryTime.Core.Abstractions;
using StoryTime.Core.Services;

namespace StoryTime.Core.ViewModels;

/// <summary>Full story library (flat push route). "Every story" is literal — this is the only
/// surface that shows making and failed rows; Home caps at the 8 newest playable ones.</summary>
public sealed partial class LibraryViewModel : ObservableObject
{
    private readonly IStoryStore _store;
    private readonly INavigator _navigator;
    private readonly PlayerLaunchContext _playerContext;
    private readonly MakingSheetViewModel _makingSheet;
    private readonly IHaptics _haptics;
    private readonly IStoryCatalog _catalog;
    private readonly CoalescingReloader _reloader;

    public LibraryViewModel(IStoryStore store, INavigator navigator, PlayerLaunchContext playerContext,
        MakingSheetViewModel makingSheet, IHaptics haptics, IStoryCatalog catalog)
    {
        _store = store;
        _navigator = navigator;
        _playerContext = playerContext;
        _makingSheet = makingSheet;
        _haptics = haptics;
        _catalog = catalog;
        _reloader = new CoalescingReloader(RefreshStoriesAsync);

        // Live: a story run that starts, advances or finishes while the Library is on screen
        // updates the list in place. Before this the page only reloaded in Appearing.
        _catalog.Changed += (_, _) => StoriesRefresh = _reloader.RequestAsync();
    }

    public ObservableCollection<StoryListItem> Stories { get; } = [];

    /// <summary>Last catalog-triggered reload (awaitable by tests).</summary>
    public Task? StoriesRefresh { get; private set; }

    [ObservableProperty]
    private string _headline = "Every story";

    public Task ActivateAsync() => _reloader.RequestAsync();

    /// <summary>"Every story" means EVERY story — this is the one list that shows a story run
    /// in progress and a story run that failed, because it is the only place a parent can make
    /// sense of either (tapping a making row opens the sheet; a failed row explains
    /// itself in its meta line). Newest first, as the store returns them.
    ///
    /// Only ever invoked through <c>_reloader</c> — it is the single writer of
    /// <see cref="Stories"/>, and two overlapping passes corrupt the collection.</summary>
    private async Task RefreshStoriesAsync()
    {
        var profile = await _store.GetActiveProfileAsync();
        if (profile is null) return;
        StoryListItem.SyncInto(Stories, await _store.GetStoriesAsync(profile.Id));
    }

    [RelayCommand]
    private async Task OpenAsync(StoryListItem item)
    {
        if (item is null) return;
        if (item.Record.State == StoryState.Making)
        {
            // A still-making story opens the making sheet — never a dead tap.
            _haptics.Light();
            await _makingSheet.ExpandCurrentAsync();
            return;
        }
        if (item.Record.State == StoryState.Failed)
        {
            _haptics.Warning();
            return;
        }
        _playerContext.StoryId = item.Id;
        _playerContext.TeaserOnly = item.Record.State == StoryState.AwaitingApproval;
        await _navigator.GoToAsync(Routes.Player);
    }

    [RelayCommand]
    private async Task ToggleFavoriteAsync(StoryListItem item)
    {
        item.IsFavorite = !item.IsFavorite;
        item.Record.IsFavorite = item.IsFavorite;
        await _store.SaveStoryAsync(item.Record);
        await _reloader.RequestAsync();
    }

    [RelayCommand]
    private async Task DeleteAsync(StoryListItem item)
    {
        // The catalog notification does the removing — here and on every other page.
        await _store.DeleteStoryAsync(item.Id);
        await _reloader.RequestAsync();
    }

    [RelayCommand]
    private Task BackAsync() => _navigator.GoBackAsync();
}
