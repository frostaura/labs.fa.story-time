using StoryTime.Core.Engines.Narration;
using Xunit;

namespace StoryTime.Core.Tests.Engines;

public class VoiceSelectionTests
{
    private static InstalledVoice V(string id, string name, string lang, VoiceQualityTier q,
        VoiceGender g = VoiceGender.Unspecified, bool novelty = false, bool personal = false)
        => new(id, name, lang, q, g, novelty, personal);

    // What the founder's iPhone reported out of the box (all compact), plus noise.
    private static readonly InstalledVoice[] OutOfTheBox =
    [
        V("com.apple.voice.compact.en-US.Samantha", "Samantha", "en-US", VoiceQualityTier.Default, VoiceGender.Female),
        V("com.apple.voice.compact.en-IE.Moira", "Moira", "en-IE", VoiceQualityTier.Default, VoiceGender.Female),
        V("com.apple.voice.compact.en-GB.Daniel", "Daniel", "en-GB", VoiceQualityTier.Default, VoiceGender.Male),
        V("com.apple.ttsbundle.siri_Nicky_en-US_compact", "Nicky", "en-US", VoiceQualityTier.Default, VoiceGender.Female),
        V("com.apple.ttsbundle.siri_Arthur_en-GB_compact", "Arthur", "en-GB", VoiceQualityTier.Default, VoiceGender.Male),
        V("com.apple.siri.natural.Nora", "Nora", "en-US", VoiceQualityTier.Premium, VoiceGender.Female),
        V("com.apple.eloquence.en-US.Flo", "Flo", "en-US", VoiceQualityTier.Default),
        V("com.apple.speech.synthesis.voice.Bubbles", "Bubbles", "en-US", VoiceQualityTier.Default, novelty: true),
        V("personal.voice.1", "My Voice", "en-US", VoiceQualityTier.Premium, personal: true),
        V("com.apple.voice.compact.fr-FR.Thomas", "Thomas", "fr-FR", VoiceQualityTier.Default),
        V("com.apple.voice.compact.en-US.Samantha2", "Samantha", "en", VoiceQualityTier.Default),
    ];

    private static readonly InstalledVoice SerenaPremium =
        V("com.apple.voice.premium.en-GB.Serena", "Serena (Premium)", "en-GB", VoiceQualityTier.Premium, VoiceGender.Female);
    private static readonly InstalledVoice AvaPremium =
        V("com.apple.voice.premium.en-US.Ava", "Ava (Premium)", "en-US", VoiceQualityTier.Premium, VoiceGender.Female);
    private static readonly InstalledVoice SerenaEnhanced =
        V("com.apple.voice.enhanced.en-GB.Serena", "Serena (Enhanced)", "en-GB", VoiceQualityTier.Enhanced, VoiceGender.Female);

    [Fact]
    public void Excludes_Novelty_Personal_Eloquence_SiriNatural_AndOtherLanguages()
    {
        var ranked = VoiceRanker.Rank(OutOfTheBox);
        var ids = ranked.Select(v => v.Identifier).ToList();
        Assert.DoesNotContain("com.apple.siri.natural.Nora", ids);
        Assert.DoesNotContain("com.apple.eloquence.en-US.Flo", ids);
        Assert.DoesNotContain("com.apple.speech.synthesis.voice.Bubbles", ids);
        Assert.DoesNotContain("personal.voice.1", ids);
        Assert.DoesNotContain("com.apple.voice.compact.fr-FR.Thomas", ids);
        Assert.Contains("com.apple.voice.compact.en-US.Samantha2", ids);    // bare "en" is English
        Assert.Contains("com.apple.ttsbundle.siri_Nicky_en-US_compact", ids); // Siri-DERIVED compact is fine
    }

    [Fact]
    public void SiriDerivedCompact_RanksAbove_SamanthaAndMoira()
    {
        var ranked = VoiceRanker.Rank(OutOfTheBox);
        Assert.True(ranked[0].IsSiriDerived);
        var names = ranked.Select(v => v.BaseName).ToList();
        Assert.True(names.IndexOf("Arthur") < names.IndexOf("Samantha"));
        Assert.True(names.IndexOf("Nicky") < names.IndexOf("Moira"));
    }

    [Fact]
    public void WithoutSerena_TheStepIsNotDone_ButNarrationStillHasAVoice()
    {
        var choice = VoiceRanker.Choose(OutOfTheBox.Append(AvaPremium).Append(SerenaEnhanced));
        Assert.NotNull(choice);
        Assert.False(choice.MeetsRequirement);                        // Ava / enhanced Serena don't count
        Assert.Equal(AvaPremium.Identifier, choice.Choice.VoiceId);    // but narration uses the best there is
        Assert.Equal(VoiceQualityTier.Premium, choice.BestTier);
    }

    [Fact]
    public void SerenaPremium_IsTheStoryteller_WhereverItRanksByIdentifier()
    {
        var choice = VoiceRanker.Choose(OutOfTheBox.Append(AvaPremium).Append(SerenaEnhanced).Append(SerenaPremium));
        Assert.NotNull(choice);
        Assert.True(choice.MeetsRequirement);
        Assert.Equal(SerenaPremium.Identifier, choice.Choice.VoiceId);
        Assert.Equal(3, choice.Choice.Fallbacks.Count);
        Assert.DoesNotContain(SerenaPremium.Identifier, choice.Choice.Fallbacks);
        Assert.Equal("Serena", SerenaPremium.BaseName);
    }

    [Fact]
    public void Serena_IsMatchedByName_NotIdentifier()
    {
        var renamed = SerenaPremium with { Identifier = "com.apple.voice.premium.en-GB.Serena.v2" };
        Assert.True(VoiceRanker.Choose([renamed])!.MeetsRequirement);
    }

    [Fact]
    public void NothingEligible_IsNull()
        => Assert.Null(VoiceRanker.Choose(OutOfTheBox.Where(v => !VoiceRanker.IsEligible(v, VoiceLanguagePolicy.English))));

    [Fact]
    public void Revalidate_KeepsThePersistedSerena_AndReplacesADeletedOne()
    {
        var installed = OutOfTheBox.Append(SerenaPremium).ToList();
        Assert.Equal(SerenaPremium.Identifier, VoiceRanker.Revalidate(SerenaPremium.Identifier, installed)!.Choice.VoiceId);

        // Serena deleted in Settings: falls back, and the requirement is no longer met.
        var afterDelete = VoiceRanker.Revalidate(SerenaPremium.Identifier, OutOfTheBox)!;
        Assert.False(afterDelete.MeetsRequirement);
        Assert.NotEqual(SerenaPremium.Identifier, afterDelete.Choice.VoiceId);

        // A persisted non-Serena voice is never kept over Serena once she is installed.
        Assert.Equal(SerenaPremium.Identifier, VoiceRanker.Revalidate("com.apple.voice.compact.en-US.Samantha", installed)!.Choice.VoiceId);
    }

    [Fact]
    public void LanguagePolicy_IsPluggable()
    {
        var fr = new VoiceLanguagePolicy("fr");
        var ranked = VoiceRanker.Rank(OutOfTheBox, fr);
        Assert.Single(ranked);
        Assert.Equal("Thomas", ranked[0].BaseName);
    }
}
