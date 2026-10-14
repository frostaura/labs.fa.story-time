using System.Windows.Input;
using CommunityToolkit.Maui.Core;
using StoryTime.App.Services;
using StoryTime.Core.Abstractions;
using StoryTime.Core.ViewModels;

namespace StoryTime.App.Views;

public partial class HomePage : ContentPage
{
    private readonly HomeViewModel _vm;
    private readonly IHaptics _haptics;
    private IDispatcherTimer? _cooldownTimer;
    private bool _breathing;

    private DateTime _lastLongPressUtc = DateTime.MinValue;
    private int _carouselIndex = -1;

    public HomePage(HomeViewModel vm, IHaptics haptics)
    {
        _vm = vm;
        _haptics = haptics;
        BindingContext = vm;
        LongPressCommand = new Command<StoryListItem>(item =>
        {
            _lastLongPressUtc = DateTime.UtcNow;
            // Anticipatory rule: Medium the instant the long-press threshold is recognized,
            // BEFORE the menu is built/shown (the TWHaptics trace must precede TWAction).
            _haptics.Medium();
            _ = ShowContextMenuAsync(item);
        });
        RowTapCommand = new Command<StoryListItem>(item =>
        {
            // A completed long-press also reports a touch-completed — suppress that tap.
            if ((DateTime.UtcNow - _lastLongPressUtc).TotalMilliseconds < 900) return;
            _vm.OpenStoryCommand.Execute(item);
        });
        InitializeComponent();
        LengthTrack.LengthRequested += (_, length) => _vm.SelectLengthCommand.Execute(length);
        // Any carousel refresh (appearing, weave-complete) resets the offset — de-prime
        // the snap index so the settling scroll can never fire a phantom tick.
        _vm.Stories.CollectionChanged += (_, _) => _carouselIndex = -1;
        // A new story is inserted at index 0, but the native carousel anchors on the item
        // it was already showing, so tonight's story would sit off-screen to the left until
        // the parent swiped back (observed 2026-07-31). Snap to the head.
        _vm.HeadStoryChanged += (_, _) => Dispatcher.Dispatch(() =>
        {
            if (_vm.Stories.Count == 0) return;
            _carouselIndex = -1;
            Carousel.ScrollTo(0, position: ScrollToPosition.Start, animate: false);
        });
    }

    /// <summary>Long-press context menu — share · favorite · delete (design whisper copy).</summary>
    public ICommand LongPressCommand { get; }

    /// <summary>Action breadcrumb for the weave CTA so QA can prove the press-down Medium
    /// precedes the weave starting (the button's own press haptic comes from the style's
    /// PressHaptics.OnPressed="Medium" at pointer-down; Clicked is release-time).</summary>
    private void OnWeaveClicked(object? sender, EventArgs e) => UxTrace.Action("Weave start");

    /// <summary>Press-down Light for every touch site on this page (cards, avatar door,
    /// summary row, premium hint) — fires at pointer-down, before commands/menus/nav.</summary>
    private void OnPressDownLight(object? sender, TouchStatusChangedEventArgs e)
    {
        if (e.Status == TouchStatus.Started) PressHaptics.Light();
    }

    /// <summary>Row tap → open the story (suppressed right after a long-press).</summary>
    public ICommand RowTapCommand { get; }

    private bool _qaShelfOpened;

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.ActivateAsync();
        if (!_vm.HasStories)
        {
            // First reveal after onboarding can race the welcome-story write; one bounded retry.
            await Task.Delay(600);
            await _vm.ActivateAsync();
        }

        // QA hook (iOS sim is screenshot-only — TCC): cold-start with the recipe shelf
        // open. Handled here rather than in App.OnShellReadyAsync because the shelf is
        // Home-local state, not a route.
        if (!_qaShelfOpened && Environment.GetEnvironmentVariable("STORYTIME_QA_ROUTE") == "recipe-shelf")
        {
            _qaShelfOpened = true;
            await _vm.AdjustRecipeCommand.ExecuteAsync(null);
        }

        _cooldownTimer ??= Dispatcher.CreateTimer();
        _cooldownTimer.Interval = TimeSpan.FromSeconds(10);
        _cooldownTimer.Tick -= OnCooldownTick;
        _cooldownTimer.Tick += OnCooldownTick;
        _cooldownTimer.Start();

        _breathing = true;
        _ = BreatheAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _cooldownTimer?.Stop();
        _breathing = false;
    }

    private void OnCooldownTick(object? sender, EventArgs e) => _vm.RefreshCooldown();

    /// <summary>Carousel snap geometry: card WidthRequest (220) + LinearItemsLayout
    /// ItemSpacing (16), in device-independent units — HorizontalOffset reports DIPs
    /// (verified on device: a ~200 px shift logs as ~77). Keep in sync with the
    /// XAML.</summary>
    private const double CarouselCellWidth = 220 + 16;

    /// <summary>Tick when the snapped card changes while the user swipes the carousel.
    /// CollectionView has no snap event, so the nearest snap index is derived from
    /// HorizontalOffset / cell width on every Scrolled. Guards keep it honest: the first
    /// observation only primes the index (page appearing / items refresh must not tick),
    /// and only single-step changes fire — a drag/settle walks the index one card at a
    /// time across many Scrolled events, while a programmatic reset (items re-populated)
    /// jumps it, which stays silent. With fewer than ~3 cards the scrollable range is
    /// under half a cell, no snap change is derivable, and the carousel stays silent —
    /// which matches the finger: no card actually changes.</summary>
    private void OnCarouselScrolled(object? sender, ItemsViewScrolledEventArgs e)
    {
        var index = Math.Max(0, (int)Math.Round(e.HorizontalOffset / CarouselCellWidth));
        if (_carouselIndex >= 0 && Math.Abs(index - _carouselIndex) == 1) _haptics.Tick();
        _carouselIndex = index;
    }

    /// <summary>Subtle breathing scale + glow on the hero CTA — gentle, never bouncy.</summary>
    private async Task BreatheAsync()
    {
        var accent = (Color)Application.Current!.Resources["Accent"];
        while (_breathing)
        {
            var startedMs = Environment.TickCount64;
            if (WeaveButton.IsEnabled)
            {
                WeaveButton.Shadow = new Shadow { Brush = new SolidColorBrush(accent), Radius = 24, Opacity = 0.30f, Offset = new Point(0, 0) };
                await WeaveButton.ScaleToAsync(1.015, 1900, Easing.SinInOut);
                if (!_breathing) break;
                WeaveButton.Shadow = new Shadow { Brush = new SolidColorBrush(accent), Radius = 16, Opacity = 0.16f, Offset = new Point(0, 0) };
                await WeaveButton.ScaleToAsync(1.0, 1900, Easing.SinInOut);
            }
            else
            {
                WeaveButton.Scale = 1.0;
                WeaveButton.Shadow = new Shadow { Brush = Brush.Transparent, Radius = 0, Opacity = 0 };
            }
            // ScaleTo completes SYNCHRONOUSLY when the platform view is detached (e.g. Home
            // resuming right after onboarding pops) or when system animations are off. Without
            // this floor the loop then spins the UI thread with zero yields — a deterministic
            // first-run ANR (main thread wedged in ValueAnimator add/cancel; observed 2026-07-30).
            if (Environment.TickCount64 - startedMs < 600) await Task.Delay(600);
        }
    }

    private async Task ShowContextMenuAsync(StoryListItem item)
    {
        var favoriteLabel = item.IsFavorite ? "Unfavorite" : "Favorite";
        UxTrace.Action("ContextMenu show");
        var action = await DisplayActionSheetAsync(item.Title, "Cancel", "Delete", "Share", favoriteLabel);
        switch (action)
        {
            case "Share":
                await Share.Default.RequestAsync(new ShareTextRequest
                {
                    Title = item.Title,
                    Text = $"“{item.Title}” — a bedtime story woven at home with StoryTime.",
                });
                break;
            case "Favorite":
            case "Unfavorite":
                await _vm.ToggleFavoriteCommand.ExecuteAsync(item);
                break;
            case "Delete":
                var confirmed = await DisplayAlertAsync("Delete this story?",
                    $"“{item.Title}” and its narration will be removed from this phone.", "Delete", "Keep");
                if (confirmed) await _vm.DeleteStoryCommand.ExecuteAsync(item);
                break;
        }
    }
}
