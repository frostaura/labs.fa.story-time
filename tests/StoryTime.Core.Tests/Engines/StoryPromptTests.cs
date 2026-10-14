using StoryTime.Contracts;
using StoryTime.Core.Engines.Prompts;
using Xunit;

namespace StoryTime.Core.Tests.Engines;

public class StoryPromptTests
{
    [Theory]
    [InlineData(AgeBand.Age3To4)]
    [InlineData(AgeBand.Age5To6)]
    [InlineData(AgeBand.Age7To8)]
    public void EveryBand_CarriesTheHardExclusions(AgeBand band)
    {
        var prompt = StoryPrompts.SystemPrompt(band);
        Assert.Contains("no violence", prompt);
        Assert.Contains("no weapons", prompt);
        Assert.Contains("no death", prompt);
        Assert.Contains("no scary imagery", prompt);
        Assert.Contains("Never end on a cliffhanger", prompt);
    }

    [Fact]
    public void Band3To4_HasVocabularyCeilingAndNoPeril()
    {
        var prompt = StoryPrompts.SystemPrompt(AgeBand.Age3To4);
        Assert.Contains("3 to 4", prompt);
        Assert.Contains("five to ten words", prompt);
        Assert.Contains("no peril of any kind", prompt);
    }

    [Fact]
    public void Band5To6_AllowsTinyAdventuresOnly()
    {
        var prompt = StoryPrompts.SystemPrompt(AgeBand.Age5To6);
        Assert.Contains("5 to 6", prompt);
        Assert.Contains("nothing is ever dangerous", prompt);
    }

    [Fact]
    public void Band7To8_AllowsGentleSuspenseThatResolves()
    {
        var prompt = StoryPrompts.SystemPrompt(AgeBand.Age7To8);
        Assert.Contains("7 to 8", prompt);
        Assert.Contains("Gentle suspense is allowed", prompt);
        Assert.Contains("resolve kindly", prompt);
    }

    // ---- Content filter level (additive 2026-07-30) ----

    [Theory]
    [InlineData(AgeBand.Age3To4)]
    [InlineData(AgeBand.Age5To6)]
    [InlineData(AgeBand.Age7To8)]
    public void EveryBand_CarriesTheHardExclusions_AtBraveishToo(AgeBand band)
    {
        // The level must never weaken a safety layer: the absolute rules are identical.
        var prompt = StoryPrompts.SystemPrompt(band, ContentFilterLevel.Braveish);
        Assert.Contains("no violence", prompt);
        Assert.Contains("no weapons", prompt);
        Assert.Contains("no death", prompt);
        Assert.Contains("no scary imagery", prompt);
        Assert.Contains("Never end on a cliffhanger", prompt);
    }

    [Theory]
    [InlineData(AgeBand.Age5To6)]
    [InlineData(AgeBand.Age7To8)]
    public void Braveish_WidensThemePaletteForOlderBands(AgeBand band)
    {
        var braveish = StoryPrompts.SystemPrompt(band, ContentFilterLevel.Braveish);
        Assert.Contains("a small brave journey", braveish);
        Assert.Contains("a puzzle to solve", braveish);
        Assert.Contains("a storm watched safely from indoors", braveish);
        Assert.Contains("a lost thing found", braveish);
        // Widening is purely additive — everything in the Gentle prompt survives.
        var gentle = StoryPrompts.SystemPrompt(band);
        Assert.DoesNotContain("Brave-ish", gentle);
        Assert.DoesNotContain("a small brave journey", gentle);
    }

    [Fact]
    public void Braveish_Age3To4_IsIdenticalToGentle()
    {
        // The youngest band tones Brave-ish down automatically — byte-identical prompt.
        Assert.Equal(
            StoryPrompts.SystemPrompt(AgeBand.Age3To4, ContentFilterLevel.Gentle),
            StoryPrompts.SystemPrompt(AgeBand.Age3To4, ContentFilterLevel.Braveish));
    }

    [Theory]
    [InlineData(AgeBand.Age3To4)]
    [InlineData(AgeBand.Age5To6)]
    [InlineData(AgeBand.Age7To8)]
    public void DefaultLevel_IsGentle(AgeBand band) =>
        Assert.Equal(StoryPrompts.SystemPrompt(band), StoryPrompts.SystemPrompt(band, ContentFilterLevel.Gentle));

    [Fact]
    public void OutlinePrompt_CarriesProfileAndSceneCount()
    {
        var profile = new ChildProfile { Name = "Riley", Age = 5, Interests = ["foxes", "ocean"] };
        var prompt = StoryPrompts.OutlineUser(profile, null, 6);
        Assert.Contains("Riley", prompt);
        Assert.Contains("foxes, ocean", prompt);
        Assert.Contains("exactly 6 scenes", prompt);
    }

    [Fact]
    public void OutlinePrompt_CarriesSeriesBible()
    {
        var profile = new ChildProfile { Name = "Riley", Age = 6 };
        var bible = new StoryBible
        {
            EpisodeCount = 3,
            LastEpisodeRecap = "Pip found a lantern.",
            Characters = [new CharacterCard { Name = "Pip", Kind = "fox", Trait = "curious" }],
            Places = ["the quiet shore"],
        };
        var prompt = StoryPrompts.OutlineUser(profile, bible, 5);
        Assert.Contains("Pip the curious fox", prompt);
        Assert.Contains("Previously: Pip found a lantern.", prompt);
    }

    [Fact]
    public void ScenePrompt_MarksTheFinalSceneForGentleEnding()
    {
        var profile = new ChildProfile { Name = "Riley", Age = 5 };
        var outline = new StoryOutline
        {
            Title = "T",
            Scenes = [new SceneOutline { Id = 1 }, new SceneOutline { Id = 2 }],
        };
        var mid = StoryPrompts.SceneUser(profile, outline, outline.Scenes[0], "");
        var final = StoryPrompts.SceneUser(profile, outline, outline.Scenes[1], "tail text");
        Assert.Contains("settled, never on a cliffhanger", mid);
        Assert.Contains("final scene", final);
        Assert.Contains("drifting toward sleep", final);
        Assert.Contains("...tail text", final);
    }

    [Fact]
    public void PolishWindowUser_MiddleWindow_ForbidsClosingTheStory()
    {
        var profile = new ChildProfile { Name = "Aria", Age = 6 };
        var mid = StoryPrompts.PolishWindowUser(profile, "Window text.", "previous tail.", isFinalWindow: false);
        Assert.Contains("middle of a bedtime story", mid);
        Assert.Contains("Do NOT end the story here", mid);
        Assert.Contains("...previous tail.", mid);
        Assert.DoesNotContain("soft goodnight", mid);
        // The flow + pacing channels survive the window scoping (founder prose pass).
        Assert.Contains("merge them into one longer flowing sentence", mid);
        Assert.Contains("em-dash", mid);
    }

    [Fact]
    public void PolishWindowUser_FinalWindow_ClosesGently()
    {
        var profile = new ChildProfile { Name = "Aria", Age = 6 };
        var final = StoryPrompts.PolishWindowUser(profile, "Window text.", "previous tail.", isFinalWindow: true);
        Assert.Contains("closing passage", final);
        Assert.Contains("soft goodnight", final);
        Assert.DoesNotContain("Do NOT end the story here", final);
    }
}
