using StoryTime.Core.ViewModels;

namespace StoryTime.App.Views;

public partial class OnboardingRulesPage : ContentPage
{
    public OnboardingRulesPage(OnboardingRulesViewModel vm)
    {
        BindingContext = vm;
        InitializeComponent();
    }
}
