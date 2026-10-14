using StoryTime.Contracts;
using StoryTime.Core.Abstractions;
using StoryTime.Core.Pipeline;
using StoryTime.Core.Poster;
using StoryTime.Core.Services;
using StoryTime.Core.ViewModels;
using Xunit;

namespace StoryTime.Core.Tests;

/// <summary>Founder bug 2026-07-31: "ensure that newly-generated stories show up on all the
/// pages they are supposed to". Every page now hangs off one Core-level notification
/// (IStoryCatalog), raised by the store on every mutation, so these tests hold all three
/// list view-models live AT ONCE and never re-activate them — exactly the situation that
/// used to go stale.
///
/// The visibility contract under test (documented in docs/design/mockup-spec.md):
///   Home     — Ready + AwaitingApproval only, newest first, ≤8.
///   Library  — everything, including Weaving and Failed ("Every story").
///   Shelf    — Ready ONLY; an unapproved story must not reach a child's hands.</summary>
public class StoryPropagationTests : IAsyncLifetime
{
    private TestEnv _env = null!;
    private ChildProfile _profile = null!;
    private HomeViewModel _home = null!;
    private LibraryViewModel _library = null!;
    private ShelfViewModel _shelf = null!;
    private FakeWeaveHost _host = null!;

    public async Task InitializeAsync()
    {
        _env = await TestEnv.CreateAsync();
        var clock = new FakeClock();
        var settings = new InMemorySettingsStore();
        var entitlements = new StubEntitlements(settings, clock);
        var cooldown = new CooldownService(settings, clock, entitlements);
        _host = new FakeWeaveHost();
        var navigator = new FakeNavigator();
        var kidMode = new KidModeService();
        var sheet = new WeaveSheetViewModel(_host, _env.Store, cooldown, navigator, new PlayerLaunchContext(),
            settings, new NullHaptics(), entitlements, kidMode);

        _home = new HomeViewModel(_env.Store, _host, cooldown, entitlements, navigator,
            new PlayerLaunchContext(), kidMode,
            new PosterArtRefresher(_env.Store, new NullPosterRasterizer(), _env.Options), new NullHaptics(),
            sheet, new RecipeShelfViewModel(_env.Store, new NullHaptics(), _host), _env.Catalog);
        _library = new LibraryViewModel(_env.Store, navigator, new PlayerLaunchContext(), sheet,
            new NullHaptics(), _env.Catalog);
        _shelf = new ShelfViewModel(_env.Store, navigator, new PlayerLaunchContext(), kidMode,
            new NullHaptics(), _env.Catalog);

        _profile = TestEnv.Profile();
        await _env.Store.SaveProfileAsync(_profile);
        _profile = (await _env.Store.GetActiveProfileAsync())!;

        await _home.ActivateAsync();
        await _library.ActivateAsync();
        await _shelf.ActivateAsync();
    }

    public async Task DisposeAsync() => await _env.DisposeAsync();

    /// <summary>Drain whatever the last mutation kicked off on every page.</summary>
    private async Task SettleAsync()
    {
        for (var i = 0; i < 4; i++)
        {
            if (_home.StoriesRefresh is { } h) await h;
            if (_library.StoriesRefresh is { } l) await l;
            if (_shelf.TilesRefresh is { } s) await s;
        }
    }

    private StoryRecord Story(StoryState state, string title = "The Lantern Fox") => new()
    {
        ProfileId = _profile.Id,
        Title = title,
        State = state,
        DurationSeconds = 300,
        CreatedUtc = DateTime.UtcNow,
    };

    [Fact]
    public async Task ReadyStory_AppearsEverywhere_WithoutReactivatingAnyPage()
    {
        Assert.Empty(_home.Stories);
        Assert.Empty(_library.Stories);
        Assert.Empty(_shelf.Tiles);

        await _env.Store.SaveStoryAsync(Story(StoryState.Ready));
        await SettleAsync();

        Assert.Single(_home.Stories);
        Assert.Single(_library.Stories);
        Assert.Single(_shelf.Tiles);
    }

    [Fact]
    public async Task AwaitingApproval_ShowsOnHomeAndLibraryWithWaitingMeta_ButNotOnTheKidShelf()
    {
        await _env.Store.SaveStoryAsync(Story(StoryState.AwaitingApproval));
        await SettleAsync();

        Assert.Single(_home.Stories);
        Assert.Single(_library.Stories);
        Assert.Contains("waiting for you", _home.Stories[0].MetaLine);
        // The approval covenant: unheard stories never reach the child-facing shelf.
        Assert.Empty(_shelf.Tiles);
    }

    [Fact]
    public async Task Approval_UnlocksTheStoryOntoTheKidShelfLive()
    {
        var story = Story(StoryState.AwaitingApproval);
        await _env.Store.SaveStoryAsync(story);
        await SettleAsync();
        Assert.Empty(_shelf.Tiles);

        // The real approval path: the player writes Ready through the store.
        var context = new PlayerLaunchContext { StoryId = story.Id, TeaserOnly = true };
        var player = new PlayerViewModel(_env.Store, new FakePlayerProvider(), new PosterComposer(), context,
            new FakeNavigator(), new NullHaptics(), new FakeEntitlements(), new InMemorySettingsStore(),
            new FakeAmbientPlayer(), new Core.Audio.AmbientTrackProvider(_env.Options));
        await player.ActivateAsync();
        await player.ApproveCommand.ExecuteAsync(null);
        await SettleAsync();

        Assert.Single(_shelf.Tiles);
        Assert.Single(_home.Stories);
        Assert.Contains("min", _home.Stories[0].MetaLine);   // no longer "waiting for you"
        Assert.DoesNotContain("waiting for you", _home.Stories[0].MetaLine);
    }

    [Fact]
    public async Task WeavingRow_IsLibraryOnly_AndBecomesVisibleEverywhereWhenItFinishes()
    {
        var story = Story(StoryState.Weaving);
        await _env.Store.SaveStoryAsync(story);
        await SettleAsync();

        Assert.Single(_library.Stories);
        Assert.Contains("weaving…", _library.Stories[0].MetaLine);
        Assert.Empty(_home.Stories);   // the weave belongs to the sheet, not the carousel
        Assert.Empty(_shelf.Tiles);

        story.State = StoryState.Ready;
        await _env.Store.SaveStoryAsync(story);
        await SettleAsync();

        Assert.Single(_library.Stories);
        Assert.Single(_home.Stories);
        Assert.Single(_shelf.Tiles);
        // The Library row was refreshed in place, not rebuilt.
        Assert.DoesNotContain("weaving…", _library.Stories[0].MetaLine);
    }

    [Fact]
    public async Task FailedStory_StaysInTheLibraryAndNowhereElse()
    {
        await _env.Store.SaveStoryAsync(Story(StoryState.Failed));
        await SettleAsync();

        Assert.Single(_library.Stories);
        Assert.Contains("didn't finish", _library.Stories[0].MetaLine);
        Assert.Empty(_home.Stories);
        Assert.Empty(_shelf.Tiles);
    }

    [Fact]
    public async Task Delete_RemovesTheStoryFromEveryPage()
    {
        var story = Story(StoryState.Ready);
        await _env.Store.SaveStoryAsync(story);
        await SettleAsync();
        Assert.Single(_shelf.Tiles);

        await _home.DeleteStoryCommand.ExecuteAsync(_home.Stories[0]);
        await SettleAsync();

        Assert.Empty(_home.Stories);
        Assert.Empty(_library.Stories);
        Assert.Empty(_shelf.Tiles);
    }

    [Fact]
    public async Task CancelledWeave_DiscardsTheHalfWovenRowFromTheLibrary()
    {
        await _env.Store.SaveStoryAsync(Story(StoryState.Weaving));
        await SettleAsync();
        Assert.Single(_library.Stories);

        // What WeaveHostService.CancelAndDiscardAsync does: delete the weaving row.
        foreach (var s in await _env.Store.GetStoriesAsync(_profile.Id))
        {
            if (s.State == StoryState.Weaving) await _env.Store.DeleteStoryAsync(s.Id);
        }
        await SettleAsync();

        Assert.Empty(_library.Stories);
        Assert.Empty(_home.Stories);
    }

    [Fact]
    public async Task FavoriteToggle_PropagatesToTheOtherPages()
    {
        await _env.Store.SaveStoryAsync(Story(StoryState.Ready));
        await SettleAsync();

        await _home.ToggleFavoriteCommand.ExecuteAsync(_home.Stories[0]);
        await SettleAsync();

        Assert.True(_home.Stories[0].IsFavorite);
        Assert.True(_library.Stories[0].IsFavorite);
        Assert.True(_shelf.Tiles[0].IsFavorite);
    }

    [Fact]
    public async Task Carousel_IsNewestFirst_AndCapsAtEight()
    {
        for (var i = 0; i < 10; i++)
        {
            await _env.Store.SaveStoryAsync(new StoryRecord
            {
                ProfileId = _profile.Id,
                Title = $"Story {i}",
                State = StoryState.Ready,
                CreatedUtc = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i),
            });
        }
        await SettleAsync();

        Assert.Equal(8, _home.Stories.Count);
        Assert.Equal("Story 9", _home.Stories[0].Title);
        Assert.Equal(10, _library.Stories.Count);   // "Every story" means every story
    }

    /// <summary>A new story is inserted at index 0, and the native carousel anchors on
    /// the card it was already showing — so without this signal tonight's story sits
    /// off-screen to the left (observed 2026-07-31). It must fire exactly
    /// when the head changes identity, and stay quiet on a plain in-place refresh.</summary>
    [Fact]
    public async Task NewNewestStory_SignalsTheCarouselToSnapBackToTheHead()
    {
        var signals = 0;
        _home.HeadStoryChanged += (_, _) => signals++;

        var older = Story(StoryState.Ready, "Older");
        older.CreatedUtc = new DateTime(2026, 7, 30, 18, 0, 0, DateTimeKind.Utc);
        await _env.Store.SaveStoryAsync(older);
        await SettleAsync();
        Assert.Equal(1, signals);            // empty → first card

        // A change to the existing head is not a new head.
        older.IsFavorite = true;
        await _env.Store.SaveStoryAsync(older);
        await SettleAsync();
        Assert.Equal(1, signals);

        var newer = Story(StoryState.Ready, "Newer");
        newer.CreatedUtc = new DateTime(2026, 7, 31, 9, 0, 0, DateTimeKind.Utc);
        await _env.Store.SaveStoryAsync(newer);
        await SettleAsync();
        Assert.Equal(2, signals);
        Assert.Equal("Newer", _home.Stories[0].Title);
    }

    /// <summary>Rows survive a reload so the bound list keeps its identity (and the Home
    /// carousel keeps its scroll offset) — a weave writes its story six times.</summary>
    [Fact]
    public async Task Reload_RefreshesRowsInPlaceInsteadOfRebuildingThem()
    {
        var story = Story(StoryState.AwaitingApproval, "Draft title");
        await _env.Store.SaveStoryAsync(story);
        await SettleAsync();
        var row = _home.Stories[0];

        story.Title = "The Lantern Fox";
        story.State = StoryState.Ready;
        await _env.Store.SaveStoryAsync(story);
        await SettleAsync();

        Assert.Same(row, _home.Stories[0]);
        Assert.Equal("The Lantern Fox", _home.Stories[0].Title);
    }
}

public class CoalescingReloaderTests
{
    [Fact]
    public async Task Burst_CollapsesIntoOneInFlightReloadPlusOneFollowUp()
    {
        var runs = 0;
        var gate = new TaskCompletionSource();
        var reloader = new CoalescingReloader(async () =>
        {
            runs++;
            await gate.Task;
        });

        var first = reloader.RequestAsync();
        // Five more requests while the first is still in flight.
        for (var i = 0; i < 5; i++) _ = reloader.RequestAsync();
        Assert.Equal(1, runs);

        gate.SetResult();
        await first;

        // Exactly one follow-up pass, not five.
        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task ReloadThatThrows_DoesNotEscapeOrWedgeTheReloader()
    {
        var runs = 0;
        var reloader = new CoalescingReloader(() =>
        {
            runs++;
            throw new InvalidOperationException("store went away");
        });

        await reloader.RequestAsync();
        await reloader.RequestAsync();
        Assert.Equal(2, runs);
    }
}
