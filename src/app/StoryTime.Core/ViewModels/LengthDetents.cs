using StoryTime.Contracts;

namespace StoryTime.Core.ViewModels;

/// <summary>The 5/10/15 length-track detent math, extracted from the App control so the
/// snap-to-nearest behaviour is unit-testable. The control tracks the finger continuously
/// and commits <see cref="Nearest"/> on release.</summary>
public static class LengthDetents
{
    public static readonly StoryLength[] All = [StoryLength.Five, StoryLength.Ten, StoryLength.Fifteen];

    /// <summary>Detent index for a selected length; unknown values settle on 5 (index 0).</summary>
    public static int IndexOf(StoryLength length)
    {
        var index = Array.IndexOf(All, length);
        return index < 0 ? 0 : index;
    }

    /// <summary>Nearest detent index for a 0..1 track fraction. Midpoints round away from
    /// zero so 0.25 → 10 and 0.75 → 15 symmetrically (banker's rounding would make the two
    /// halves of the track behave differently).</summary>
    public static int NearestIndex(double fraction) =>
        (int)Math.Round(Math.Clamp(fraction, 0, 1) * (All.Length - 1), MidpointRounding.AwayFromZero);

    public static StoryLength Nearest(double fraction) => All[NearestIndex(fraction)];
}
