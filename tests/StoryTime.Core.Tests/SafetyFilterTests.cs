using StoryTime.Core.Pipeline;
using Xunit;

namespace StoryTime.Core.Tests;

public class SafetyFilterTests
{
    private readonly SafetyFilter _filter = new();

    [Theory]
    [InlineData("The monster crept closer.", "monster")]
    [InlineData("He picked up the sword.", "sword")]
    [InlineData("It was a terrifying night.", "terrifying")]
    [InlineData("The ghost wailed.", "ghost")]
    [InlineData("They had a fight.", "fight")]
    [InlineData("The flower died.", "died")]
    public void Flags_ScaryLexicon(string text, string expected)
    {
        var flags = _filter.Check(text);
        Assert.Contains(expected, flags);
    }

    [Fact]
    public void CleanBedtimeText_Passes()
    {
        const string text = "The sleepy fox curled up under the warm moon. " +
                            "Everything was calm and cozy, and the stars hummed a lullaby. Goodnight.";
        Assert.True(_filter.IsClean(text));
    }

    [Theory]
    [InlineData("The evening was warm and kind.")]       // "war" inside "warm"
    [InlineData("The story had begun at dusk.")]         // "gun" inside "begun"
    [InlineData("They wandered past the biggest hill.")] // "die" inside "wandered"? no — control clean row
    [InlineData("A skeleton key? No — a wooden acorn.")] // flagged word present → handled by other test
    public void WordBoundaries_AvoidSubstringFalsePositives(string text)
    {
        // Only the fourth row contains a real flagged term.
        if (text.Contains("skeleton"))
        {
            Assert.False(_filter.IsClean(text));
        }
        else
        {
            Assert.True(_filter.IsClean(text));
        }
    }

    [Fact]
    public void WhitelistedPhrases_Pass()
    {
        Assert.True(_filter.IsClean("Pip was dying to know what the surprise was."));
        Assert.True(_filter.IsClean("A ghost light glowed kindly at the window."));
    }

    [Fact]
    public void WhitelistedPhrase_DoesNotExcuseOtherUses()
    {
        // Same term outside the whitelisted phrase still flags.
        var flags = _filter.Check("Pip was dying to know. The fire was dying.");
        Assert.Contains("dying", flags);
    }

    [Fact]
    public void Matching_IsCaseInsensitive()
    {
        Assert.False(_filter.IsClean("The MONSTER was near."));
    }

    [Fact]
    public void MultipleTerms_AllReportedOnce()
    {
        var flags = _filter.Check("The monster and the ghost saw another monster.");
        Assert.Equal(2, flags.Count);
        Assert.Contains("monster", flags);
        Assert.Contains("ghost", flags);
    }

    [Fact]
    public void EmptyText_IsClean()
    {
        Assert.True(_filter.IsClean(""));
        Assert.True(_filter.IsClean("   "));
    }

    [Fact]
    public void Filter_IsUntouchedByContentFilterLevel()
    {
        // Structural invariant: the content filter level (Settings → Content filters) never
        // reaches this layer — SafetyFilter has no level input, so a scary passage flags
        // identically whatever level the app is set to. This test documents that contract.
        const string scary = "The monster screamed as the ghost attacked in the terrifying dark.";
        var settings = new StoryTime.Core.Services.InMemorySettingsStore();

        settings.ContentFilter = StoryTime.Contracts.ContentFilterLevel.Gentle;
        var flagsAtGentle = _filter.Check(scary);

        settings.ContentFilter = StoryTime.Contracts.ContentFilterLevel.Braveish;
        var flagsAtBraveish = _filter.Check(scary);

        Assert.Equal(flagsAtGentle, flagsAtBraveish);
        Assert.Contains("monster", flagsAtBraveish);
        Assert.Contains("screamed", flagsAtBraveish);
        Assert.Contains("ghost", flagsAtBraveish);
        Assert.Contains("attacked", flagsAtBraveish);
        Assert.Contains("terrifying", flagsAtBraveish);
    }
}
