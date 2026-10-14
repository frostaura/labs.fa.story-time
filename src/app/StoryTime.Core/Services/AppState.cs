using StoryTime.Contracts;

namespace StoryTime.Core.Services;

/// <summary>Carries the in-progress profile across the three onboarding pages.</summary>
public sealed class OnboardingState
{
    public ChildProfile Profile { get; } = new()
    {
        Age = 5,
        Interests = ["Foxes", "Ocean", "Magic"],
        Voice = NarratorVoice.Ember,
        Length = StoryLength.Ten,
        Mode = StoryMode.Series,
    };
}

/// <summary>Parameters for the next player open (Shell query strings stay out of Core).</summary>
public sealed class PlayerLaunchContext
{
    public int StoryId { get; set; }
    public bool TeaserOnly { get; set; }
}

/// <summary>Kid Shelf lock: while active, back-navigation is blocked; only the 2-second
/// long-press on the lock pill exits.</summary>
public sealed class KidModeService
{
    public bool IsKidMode { get; private set; }
    public void Enter() => IsKidMode = true;
    public void Exit() => IsKidMode = false;
}
