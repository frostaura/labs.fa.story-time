namespace StoryTime.Core.Abstractions;

/// <summary>Navigation seam so ViewModels stay MAUI-free. App implements over Shell.GoToAsync.
/// (Additive Phase B abstraction — reported in known-gaps/report.)</summary>
public interface INavigator
{
    Task GoToAsync(string route);
    Task GoBackAsync();
}
