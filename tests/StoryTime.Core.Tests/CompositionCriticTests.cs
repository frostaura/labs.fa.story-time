using StoryTime.Contracts;
using StoryTime.Core.Poster;
using Xunit;

namespace StoryTime.Core.Tests;

/// <summary>The deterministic poster judge (multi-candidate formation, founder mandate
/// 2026-07-30) and the composer's shooting-star occasionality gate.</summary>
public class CompositionCriticTests
{
    private readonly CompositionCritic _critic = new();
    private readonly PosterComposer _composer = new();

    private static StoryOutline OceanOutline() => new()
    {
        Title = "The Whale Who Hummed the Tide",
        Settings = ["the silver shore", "the tide pools"],
        Characters = [new CharacterCard { Name = "Nia", Kind = "whale" }],
        Seed = 11,
    };

    private static PosterSpec Spec(params (int Depth, PosterElement[] Elements)[] layers) => new()
    {
        Palette = PosterPalette.OceanDeep,
        Seed = 5,
        Layers = [.. layers.Select(l => new PosterLayer { Depth = l.Depth, Elements = [.. l.Elements] })],
    };

    private static PosterElement E(PosterShape shape, double x, double y, double scale) =>
        new() { Shape = shape, X = x, Y = y, Scale = scale };

    /// <summary>A well-formed 4-layer composition: moon left third, fox right third.</summary>
    private static PosterSpec GoodSpec() => Spec(
        (0, [E(PosterShape.Moon, .30, .18, .16), E(PosterShape.Stars, .5, .3, .05)]),
        (1, [E(PosterShape.Hills, .7, .8, .8)]),
        (2, [E(PosterShape.Water, .5, .7, .9), E(PosterShape.WhaleTail, .3, .7, .14)]),
        (3, [E(PosterShape.FoxSilhouette, .66, .82, .26)]));

    // ---------- determinism ----------

    [Fact]
    public void Score_IsDeterministic_AcrossCallsAndInstances()
    {
        var outline = OceanOutline();
        var a = _critic.Score("x", GoodSpec(), outline);
        var b = _critic.Score("x", GoodSpec(), outline);
        var c = new CompositionCritic().Score("x", GoodSpec(), outline);
        Assert.Equal(a, b);
        Assert.Equal(a, c);
    }

    // ---------- scoring preferences ----------

    [Fact]
    public void Score_PrefersFocalPresence()
    {
        var outline = OceanOutline();
        var withFocal = GoodSpec();
        var without = Spec(
            (0, [E(PosterShape.Moon, .30, .18, .16), E(PosterShape.Stars, .5, .3, .05)]),
            (1, [E(PosterShape.Hills, .7, .8, .8)]),
            (2, [E(PosterShape.Water, .5, .7, .9)]),
            (3, []));
        var scored = _critic.Score("with", withFocal, outline);
        var scoredWithout = _critic.Score("without", without, outline);
        Assert.True(scored.Focal >= 0.6, $"focal presence scores strongly (was {scored.Focal})");
        Assert.Equal(0, scoredWithout.Focal);
        Assert.True(scored.Total > scoredWithout.Total, "a poster with a focal beats a hollow one");
    }

    [Fact]
    public void Score_PrefersAsymmetricBalance_OverOneCrowdedColumn()
    {
        var outline = OceanOutline();
        var balanced = GoodSpec(); // moon left third, focal right third
        var lopsided = Spec(
            (0, [E(PosterShape.Moon, .70, .18, .16), E(PosterShape.Stars, .5, .3, .05)]),
            (1, [E(PosterShape.Hills, .7, .8, .8)]),
            (2, [E(PosterShape.Water, .5, .7, .9), E(PosterShape.Lantern, .72, .8, .14)]),
            (3, [E(PosterShape.FoxSilhouette, .70, .82, .26)]));
        Assert.True(_critic.Score("balanced", balanced, outline).Balance
                  > _critic.Score("lopsided", lopsided, outline).Balance);
    }

    [Fact]
    public void Score_PrefersStoryRelevantMotifs()
    {
        var outline = OceanOutline(); // shore / tide / whale
        var relevant = GoodSpec();    // water + whale tail
        var irrelevant = Spec(
            (0, [E(PosterShape.Moon, .30, .18, .16), E(PosterShape.Stars, .5, .3, .05)]),
            (1, [E(PosterShape.Hills, .7, .8, .8)]),
            (2, [E(PosterShape.Pine, .3, .86, .14)]),
            (3, [E(PosterShape.FoxSilhouette, .66, .82, .26)]));
        var a = _critic.Score("relevant", relevant, outline);
        var b = _critic.Score("irrelevant", irrelevant, outline);
        Assert.True(a.Relevance > b.Relevance, $"{a.Relevance} vs {b.Relevance}");
        Assert.True(a.Total > b.Total);
    }

    [Fact]
    public void Score_PenalizesCrowding()
    {
        var outline = OceanOutline();
        var spread = Spec(
            (0, [E(PosterShape.Moon, .30, .18, .16)]),
            (1, [E(PosterShape.Hills, .7, .8, .8)]),
            (2, [E(PosterShape.WindowGlow, .25, .84, .24)]),
            (3, [E(PosterShape.FoxSilhouette, .66, .82, .26)]));
        var piled = Spec(
            (0, [E(PosterShape.Moon, .30, .18, .16)]),
            (1, [E(PosterShape.Hills, .7, .8, .8)]),
            (2, [E(PosterShape.WindowGlow, .64, .84, .24)]),
            (3, [E(PosterShape.FoxSilhouette, .66, .82, .26)]));
        Assert.True(_critic.Score("piled", piled, outline).CrowdingPenalty
                  > _critic.Score("spread", spread, outline).CrowdingPenalty);
    }

    [Fact]
    public void Score_RewardsAllFourDepthsOccupied()
    {
        var outline = OceanOutline();
        var full = GoodSpec();
        var shallow = Spec(
            (0, [E(PosterShape.Moon, .30, .18, .16), E(PosterShape.Stars, .5, .3, .05)]),
            (1, [E(PosterShape.Hills, .7, .8, .8)]),
            (2, []),
            (3, []));
        Assert.Equal(1.0, _critic.Score("full", full, outline).DepthUsage, 3);
        Assert.Equal(0.5, _critic.Score("shallow", shallow, outline).DepthUsage, 3);
    }

    [Fact]
    public void Score_PaletteFit_FollowsTheStorysWords()
    {
        var outline = OceanOutline();
        var ocean = GoodSpec(); // OceanDeep
        var mismatched = GoodSpec();
        mismatched.Palette = PosterPalette.LanternGlow;
        Assert.Equal(1.0, _critic.Score("fit", ocean, outline).PaletteFit, 3);
        Assert.Equal(0.25, _critic.Score("miss", mismatched, outline).PaletteFit, 3);
    }

    // ---------- judging ----------

    [Fact]
    public void Judge_PicksTheHighestTotal_TiesBreakEarlier()
    {
        var outline = OceanOutline();
        var strong = GoodSpec();
        var weak = Spec(
            (0, [E(PosterShape.Moon, .30, .18, .16)]),
            (1, [E(PosterShape.Hills, .7, .8, .8)]),
            (2, []),
            (3, []));
        var (winner, scores) = _critic.Judge([("weak", weak), ("strong", strong)], outline);
        Assert.Equal(1, winner);
        Assert.Equal(2, scores.Count);

        var (tieWinner, _) = _critic.Judge([("a", strong), ("b", strong)], outline);
        Assert.Equal(0, tieWinner);
    }

    [Fact]
    public async Task Judge_RealStory_TemplateCandidatesProduceAWinnerDeterministically()
    {
        // The template engine now builds all three sub-variants and judges them — the
        // same outline must always crown the same winner.
        var engine = new Core.Engines.TemplateStoryEngine();
        var outline = new StoryOutline { Title = "t", Settings = ["the silver shore", "the tide pools"], Seed = 87 };
        var a = await engine.GeneratePosterSpecAsync(outline, CancellationToken.None);
        var b = await engine.GeneratePosterSpecAsync(outline, CancellationToken.None);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(a), System.Text.Json.JsonSerializer.Serialize(b));
        Assert.Equal(4, a.Layers.Count);
    }

    // ---------- shooting-star occasionality gate (the founder's line artifact) ----------

    private static PosterSpec StarSpec(int seed, double ssX = .32, double moonX = .30) => new()
    {
        Palette = PosterPalette.StarfieldViolet,
        Seed = seed,
        Layers =
        [
            new PosterLayer { Depth = 0, Elements = [
                E(PosterShape.Crescent, moonX, .15, .14),
                E(PosterShape.Stars, .5, .3, .05),
                E(PosterShape.ShootingStar, ssX, .10, .20) ] },
            new PosterLayer { Depth = 1, Elements = [E(PosterShape.Hills, .7, .8, .8)] },
            new PosterLayer { Depth = 2, Elements = [] },
            new PosterLayer { Depth = 3, Elements = [E(PosterShape.FoxSilhouette, .66, .82, .26)] },
        ],
    };

    private static int FindSeed(bool allowed)
    {
        for (var seed = 1; seed < 500; seed++)
            if (PosterComposer.ShootingStarAllowed(seed) == allowed) return seed;
        throw new InvalidOperationException("no seed found");
    }

    [Fact]
    public void ShootingStar_IsOccasional_NotAFixture()
    {
        var allowed = Enumerable.Range(0, 400).Count(PosterComposer.ShootingStarAllowed);
        Assert.InRange(allowed / 400.0, 0.10, 0.40); // ≈1 in 4, never a fixture, never extinct
    }

    [Fact]
    public void ShootingStar_DroppedWhenTheSeedSaysNo()
    {
        var blocked = _composer.Validate(StarSpec(FindSeed(false)));
        Assert.DoesNotContain(blocked.Layers.SelectMany(l => l.Elements),
            e => e.Shape == PosterShape.ShootingStar);
    }

    [Fact]
    public void ShootingStar_WhenAllowed_StaysClearOfTheMoon_AndOnCanvas()
    {
        var result = _composer.Validate(StarSpec(FindSeed(true)));
        var all = result.Layers.SelectMany(l => l.Elements).ToList();
        var ss = Assert.Single(all, e => e.Shape == PosterShape.ShootingStar);
        var moon = Assert.Single(all, e => e.Shape is PosterShape.Moon or PosterShape.Crescent);

        Assert.True(Math.Abs(ss.X - moon.X) >= PosterComposer.ShootingStarClearanceX,
            $"head {ss.X:F2} too close to moon {moon.X:F2}");
        Assert.Equal(ss.X > moon.X, ss.FlipX); // tail always sweeps AWAY from the disc
        Assert.InRange(ss.Y, 0.12, 0.28);
        Assert.InRange(ss.Scale, 0.06, 0.15);
        var tail = ss.Scale * 2.1;
        if (ss.FlipX) Assert.True(ss.X + tail <= 0.995, "mirrored tail stays on canvas");
        else Assert.True(ss.X - tail >= 0.005, "tail stays on canvas");
    }

    [Fact]
    public void ShootingStar_PlacementIsIdempotent()
    {
        var spec = StarSpec(FindSeed(true));
        var once = _composer.Validate(spec);
        var twice = _composer.Validate(once);
        Assert.Equal(
            once.Layers.SelectMany(l => l.Elements).Select(e => (e.Shape, e.X, e.Y, e.Scale, e.FlipX)),
            twice.Layers.SelectMany(l => l.Elements).Select(e => (e.Shape, e.X, e.Y, e.Scale, e.FlipX)));
    }
}
