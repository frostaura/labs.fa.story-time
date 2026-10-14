using StoryTime.Core.ViewModels;

namespace StoryTime.App.Views;

/// <summary>The mandatory storyteller-voice step. Route: <see cref="Routes.StorytellerVoice"/>
/// with <c>?purpose=onboarding|repair|settings</c>.</summary>
public partial class StorytellerVoicePage : ContentPage, IQueryAttributable
{
    private readonly StorytellerVoiceViewModel _vm;

    public StorytellerVoicePage(StorytellerVoiceViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        _vm.Dispatch = action => MainThread.BeginInvokeOnMainThread(action);
        BindingContext = vm;
        BuildSteps();
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("purpose", out var purpose)) _vm.ApplyPurpose(purpose?.ToString());
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _vm.Activate();
    }

    protected override void OnDisappearing()
    {
        _vm.Deactivate();
        base.OnDisappearing();
    }

    private void BuildSteps()
    {
        var number = 1;
        foreach (var step in _vm.StepList)
        {
            var row = new Grid { ColumnDefinitions = [new ColumnDefinition(44), new ColumnDefinition(GridLength.Star)], Padding = new Thickness(0, 12) };
            row.Add(new Label
            {
                Text = number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                FontFamily = "OutfitLight",
                FontSize = 26,
                TextColor = (Color)Application.Current!.Resources["Accent"],
                VerticalOptions = LayoutOptions.Start,
            }, 0);
            row.Add(new Label { Text = step, Style = (Style)Application.Current.Resources["Body"], VerticalOptions = LayoutOptions.Center }, 1);
            StepList.Children.Add(row);
            StepList.Children.Add(new BoxView());
            number++;
        }
    }
}
