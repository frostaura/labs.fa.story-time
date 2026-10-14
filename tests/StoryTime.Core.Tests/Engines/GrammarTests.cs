using StoryTime.Contracts;
using StoryTime.Core.Engines.Llama;
using Xunit;

namespace StoryTime.Core.Tests.Engines;

public class GrammarTests
{
    [Fact]
    public void OutlineGrammar_IsEmbeddedAndNonEmpty()
    {
        var text = GrammarStore.Outline;
        Assert.Contains("root ::=", text);
        Assert.Contains("\\\"title\\\":", text);
        Assert.Contains("\\\"scenes\\\":", text);
    }

    [Fact]
    public void PosterGrammar_IsEmbeddedAndNonEmpty()
    {
        var text = GrammarStore.Poster;
        Assert.Contains("root ::=", text);
        Assert.Contains("\\\"palette\\\":", text);
        Assert.Contains("\\\"layers\\\":", text);
    }

    [Fact]
    public void PosterGrammar_AllowsExactlyThePaletteEnum()
    {
        var text = GrammarStore.Poster;
        foreach (var name in Enum.GetNames<PosterPalette>())
            Assert.Contains($"\"\\\"{name}\\\"\"", text);
    }

    [Fact]
    public void PosterGrammar_AllowsExactlyTheShapeEnum()
    {
        var text = GrammarStore.Poster;
        foreach (var name in Enum.GetNames<PosterShape>())
            Assert.Contains($"\"\\\"{name}\\\"\"", text);
    }

    [Fact]
    public void PosterGrammar_PinsAllFourDepths()
    {
        var text = GrammarStore.Poster;
        for (var depth = 0; depth <= 3; depth++)
            Assert.Contains($"\\\"depth\\\":{depth}", text);
    }

    [Fact]
    public void PosterGrammar_DeclaresTheFormationFields_AndAllowsEmptyLayers()
    {
        var text = GrammarStore.Poster;
        foreach (var field in new[] { "shape", "x", "y", "scale", "density", "tint", "glow", "flipX" })
            Assert.Contains($"\\\"{field}\\\":", text);
        Assert.Contains("\"[]\"", text);            // a layer may be empty
        Assert.Contains("bool ::=", text);
        Assert.All(text, c => Assert.True(c < 128, "grammar must stay ASCII-locked"));
    }

    [Fact]
    public void ParsedPosterSample_RoundTripsThroughContractTypes()
    {
        // A minimal string the poster grammar shape describes must deserialize into PosterSpec,
        // including the v2 formation fields (tint / glow / flipX) and an empty layer.
        const string sample = """{"palette":"SnowHush","mood":"calm","layers":[{"depth":0,"elements":[{"shape":"Crescent","x":0.3,"y":0.2,"scale":0.15,"density":0,"tint":0,"glow":0.5,"flipX":false},{"shape":"Snowfall","x":0.5,"y":0.5,"scale":0.02,"density":0.6,"tint":0,"glow":0,"flipX":false}]},{"depth":1,"elements":[{"shape":"Mountain","x":0.7,"y":0.6,"scale":0.6,"density":0,"tint":0.5,"glow":0,"flipX":false}]},{"depth":2,"elements":[]},{"depth":3,"elements":[{"shape":"DeerSilhouette","x":0.35,"y":0.83,"scale":0.26,"density":0,"tint":0,"glow":0,"flipX":true}]}],"seed":123}""";
        var spec = StoryTime.Core.Engines.LlamaStoryEngine.TryParseJson<PosterSpec>(sample);
        Assert.Equal(PosterPalette.SnowHush, spec.Palette);
        Assert.Equal(4, spec.Layers.Count);
        Assert.Equal(PosterShape.Crescent, spec.Layers[0].Elements[0].Shape);
        Assert.Equal(0.5, spec.Layers[0].Elements[0].Glow);
        Assert.Equal(0.5, spec.Layers[1].Elements[0].Tint);
        Assert.Empty(spec.Layers[2].Elements);
        var deer = spec.Layers[3].Elements[0];
        Assert.Equal(PosterShape.DeerSilhouette, deer.Shape);
        Assert.True(deer.FlipX);
        Assert.Equal(123, spec.Seed);
    }

    [Fact]
    public void ParsedOutlineSample_RoundTripsThroughContractTypes()
    {
        const string sample = """{"title":"The Sleepy Fox","moral":"Kindness warms everyone.","settings":["a quiet shore"],"characters":[{"name":"Pip","kind":"fox","trait":"sleepy"}],"scenes":[{"id":1,"summary":"Pip finds a lantern.","beats":["walks the shore","sees a glow"]}]}""";
        var outline = StoryTime.Core.Engines.LlamaStoryEngine.TryParseJson<StoryOutline>(sample);
        Assert.Equal("The Sleepy Fox", outline.Title);
        Assert.Single(outline.Scenes);
        Assert.Equal("fox", outline.Characters[0].Kind);
    }

    [Fact]
    public void TryParseJson_RecoversFromSurroundingNoise()
    {
        var parsed = StoryTime.Core.Engines.LlamaStoryEngine.TryParseJson<CharacterCard>("noise {\"name\":\"Pip\",\"kind\":\"fox\",\"trait\":\"curious\"} trailing");
        Assert.Equal("Pip", parsed.Name);
    }
}
