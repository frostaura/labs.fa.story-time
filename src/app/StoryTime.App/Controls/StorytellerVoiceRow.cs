using StoryTime.Core.Abstractions;
using StoryTime.Core.Engines.Narration;
using StoryTime.Core.ViewModels;

namespace StoryTime.App.Controls;

/// <summary>Self-contained parent-settings row: "Storyteller voice · Serena (Premium)" (or a
/// gentle "not installed" line), opening the storyteller-voice step. Resolves its services
/// itself so the settings page needs only a one-line include.</summary>
public sealed class StorytellerVoiceRow : ContentView
{
    public const string Title = "Storyteller voice";
    public const string MissingCaption = "Serena isn't installed. Tap to set her up.";

    private readonly Label _caption;
    private IStorytellerVoices? _voices;

    public StorytellerVoiceRow()
    {
        var resources = Application.Current!.Resources;
        _caption = new Label { Style = (Style)resources["Caption"] };
        var text = new VerticalStackLayout
        {
            Spacing = 2,
            VerticalOptions = LayoutOptions.Center,
            Children =
            {
                new Label { Style = (Style)resources["UiText"], FontSize = 15, Text = Title },
                _caption,
            },
        };
        var chevron = new Label
        {
            Text = "›",
            FontSize = 22,
            TextColor = (Color)resources["Accent"],
            VerticalOptions = LayoutOptions.Center,
        };
        var grid = new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)],
            HeightRequest = 64,
        };
        grid.Add(text, 0);
        grid.Add(chevron, 1);
        grid.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(async () =>
                await Shell.Current.GoToAsync($"{Routes.StorytellerVoice}?purpose=settings")),
        });
        Content = grid;

        Loaded += (_, _) =>
        {
            _voices ??= IPlatformApplication.Current?.Services.GetService<IStorytellerVoices>();
            if (_voices is null) return;
            _voices.Changed -= OnChanged;
            _voices.Changed += OnChanged;
            Update();
        };
        Unloaded += (_, _) =>
        {
            if (_voices is not null) _voices.Changed -= OnChanged;
        };
    }

    private void OnChanged(object? sender, EventArgs e) => MainThread.BeginInvokeOnMainThread(Update);

    private void Update() =>
        _caption.Text = _voices?.HasStorytellerVoice == true ? StorytellerRequirement.Serena.DisplayName : MissingCaption;
}
