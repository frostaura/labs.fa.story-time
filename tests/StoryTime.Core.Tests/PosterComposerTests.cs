using StoryTime.Contracts;
using StoryTime.Core.Poster;
using Xunit;

namespace StoryTime.Core.Tests;

public class PosterComposerTests
{
    private readonly PosterComposer _composer = new();

    /// <summary>A deliberately chaotic spec — the shape of the LLM output that produced the
    /// original "visual mess" posters: oversized fox, floating window rectangles, mid-air
    /// boat, giant bottom moon, shapes on the wrong layers.</summary>
    private static PosterSpec ChaosSpec() => new()
    {
        Palette = PosterPalette.NightHarbor,
        Seed = 12345,
        Layers =
        [
            new PosterLayer { Depth = 0, Elements = [
                new PosterElement { Shape = PosterShape.WindowGlow, X = .3, Y = .55, Scale = .5 },
                new PosterElement { Shape = PosterShape.Pine, X = .25, Y = .3, Scale = .9 } ] },
            new PosterLayer { Depth = 1, Elements = [
                new PosterElement { Shape = PosterShape.FoxSilhouette, X = .6, Y = .5, Scale = 1.0 },
                new PosterElement { Shape = PosterShape.WindowGlow, X = .2, Y = .4, Scale = .4 } ] },
            new PosterLayer { Depth = 2, Elements = [
                new PosterElement { Shape = PosterShape.Boat, X = .5, Y = .3, Scale = .6 },
                new PosterElement { Shape = PosterShape.FoxSilhouette, X = .1, Y = .2, Scale = .8 } ] },
            new PosterLayer { Depth = 3, Elements = [
                new PosterElement { Shape = PosterShape.Moon, X = .5, Y = .9, Scale = .8 },
                new PosterElement { Shape = PosterShape.Stars, X = .5, Y = .9, Scale = .3, Density = 1 } ] },
        ],
    };

    /// <summary>A sane v2 formation — moon, staggered terrain, pine, fox — that the
    /// composer should let through essentially untouched.</summary>
    private static PosterSpec QuietSpec(int seed = 7) => new()
    {
        Palette = PosterPalette.PineForest,
        Seed = seed,
        Layers =
        [
            new PosterLayer { Depth = 0, Elements = [
                new PosterElement { Shape = PosterShape.Stars, X = .5, Y = .3, Scale = .05, Density = .5 },
                new PosterElement { Shape = PosterShape.Moon, X = .68, Y = .18, Scale = .16 } ] },
            new PosterLayer { Depth = 1, Elements = [
                new PosterElement { Shape = PosterShape.Hills, X = .18, Y = .84, Scale = .85 } ] },
            new PosterLayer { Depth = 2, Elements = [
                new PosterElement { Shape = PosterShape.Pine, X = .78, Y = .86, Scale = .14, Density = .6 } ] },
            new PosterLayer { Depth = 3, Elements = [
                new PosterElement { Shape = PosterShape.FoxSilhouette, X = .32, Y = .8, Scale = .27 } ] },
        ],
    };

    /// <summary>A rich v2 formation exercising the expanded vocabulary.</summary>
    private static PosterSpec RichSpec(int seed = 21) => new()
    {
        Palette = PosterPalette.SnowHush,
        Seed = seed,
        Layers =
        [
            new PosterLayer { Depth = 0, Elements = [
                new PosterElement { Shape = PosterShape.Crescent, X = .3, Y = .16, Scale = .15, Glow = .5 },
                new PosterElement { Shape = PosterShape.Stars, X = .5, Y = .3, Scale = .05, Density = .6 },
                new PosterElement { Shape = PosterShape.Snowfall, X = .5, Y = .5, Scale = .02, Density = .6 },
                new PosterElement { Shape = PosterShape.MistBand, X = .5, Y = .6, Scale = .6, Tint = .4 } ] },
            new PosterLayer { Depth = 1, Elements = [
                new PosterElement { Shape = PosterShape.Mountain, X = .7, Y = .6, Scale = .6, Tint = .5 },
                new PosterElement { Shape = PosterShape.ForestRow, X = .5, Y = .8, Scale = 1.0 } ] },
            new PosterLayer { Depth = 2, Elements = [
                new PosterElement { Shape = PosterShape.Pine, X = .2, Y = .87, Scale = .12, Density = .5 } ] },
            new PosterLayer { Depth = 3, Elements = [
                new PosterElement { Shape = PosterShape.DeerSilhouette, X = .66, Y = .83, Scale = .26, FlipX = true } ] },
        ],
    };

    private static IEnumerable<PosterElement> All(PosterSpec spec) => spec.Layers.SelectMany(l => l.Elements);

    // ---------- structural rules ----------

    [Fact]
    public void Validate_AlwaysProducesExactlyFourLayers()
    {
        Assert.Equal(new[] { 0, 1, 2, 3 }, _composer.Validate(ChaosSpec()).Layers.Select(l => l.Depth));
        Assert.Equal(new[] { 0, 1, 2, 3 }, _composer.Validate(new PosterSpec()).Layers.Select(l => l.Depth));
        Assert.Equal(new[] { 0, 1, 2, 3 }, _composer.Validate(RichSpec()).Layers.Select(l => l.Depth));
    }

    [Fact]
    public void Validate_AtMostOneMoon_ClampedIntoTheUpperSky()
    {
        var result = _composer.Validate(ChaosSpec());
        var moons = All(result).Where(e => e.Shape is PosterShape.Moon or PosterShape.Crescent).ToList();
        var moon = Assert.Single(moons);
        Assert.InRange(moon.Scale, PosterComposer.MoonScaleMin, PosterComposer.MoonScaleMax);
        Assert.InRange(moon.Y, PosterComposer.MoonYMin, PosterComposer.MoonYMax);
        Assert.InRange(moon.X, PosterComposer.MoonXMin, PosterComposer.MoonXMax);
        Assert.True(moon.Glow >= 0.30, "the moon always glows a little");
    }

    [Fact]
    public void Validate_LetsASaneFormationThroughUnchanged()
    {
        var result = _composer.Validate(QuietSpec());
        var moon = All(result).Single(e => e.Shape == PosterShape.Moon);
        Assert.Equal(0.68, moon.X, 3);
        Assert.Equal(0.18, moon.Y, 3);
        var fox = All(result).Single(e => e.Shape == PosterShape.FoxSilhouette);
        Assert.Equal(0.32, fox.X, 3);
        Assert.Equal(0.86, fox.Y, 3);     // grounded onto the hill crest (0.84) + margin
        Assert.Equal(0.27, fox.Scale, 3);
        var pine = All(result).Single(e => e.Shape == PosterShape.Pine);
        Assert.Equal(0.78, pine.X, 3);    // small trees are never re-anchored sideways
    }

    [Fact]
    public void Validate_RichFormationSurvivesWithItsVocabularyIntact()
    {
        var result = _composer.Validate(RichSpec());
        Assert.Contains(All(result), e => e.Shape == PosterShape.Crescent);
        Assert.Contains(All(result), e => e.Shape == PosterShape.Snowfall);
        Assert.Contains(All(result), e => e.Shape == PosterShape.MistBand);
        Assert.Contains(All(result), e => e.Shape == PosterShape.Mountain);
        Assert.Contains(All(result), e => e.Shape == PosterShape.ForestRow);
        var deer = All(result).Single(e => e.Shape == PosterShape.DeerSilhouette);
        Assert.True(deer.FlipX, "FlipX carries through repair");
        Assert.Equal(3, result.Layers.Single(l => l.Elements.Contains(deer)).Depth);
    }

    [Fact]
    public void Validate_ExactlyOneFocalCreature_InZone_OnLayerThree()
    {
        var result = _composer.Validate(ChaosSpec());
        // Chaos emitted two foxes and two windows: one fox survives as THE focal on layer 3;
        // at most one cottage may remain as midground company (warm budget covers both).
        var focal = Assert.Single(All(result), e => e.Shape == PosterShape.FoxSilhouette);
        Assert.InRange(focal.Scale, PosterComposer.FocalScaleMin, PosterComposer.FocalScaleMax);
        Assert.InRange(focal.Y, PosterComposer.FocalYMin, PosterComposer.FocalYMax);
        Assert.InRange(focal.X, PosterComposer.FocalXMin, PosterComposer.FocalXMax);
        Assert.Equal(3, result.Layers.Single(l => l.Elements.Contains(focal)).Depth);
        Assert.True(All(result).Count(e => e.Shape == PosterShape.WindowGlow) <= 1);
    }

    [Fact]
    public void Validate_FocalNeverStandsUnderTheMoon()
    {
        var spec = new PosterSpec
        {
            Seed = 5,
            Layers = [ new PosterLayer { Elements = [
                new PosterElement { Shape = PosterShape.Moon, X = .5, Y = .18, Scale = .16 },
                new PosterElement { Shape = PosterShape.Rabbit, X = .52, Y = .8, Scale = .24 } ] } ],
        };
        var result = _composer.Validate(spec);
        var moon = All(result).Single(e => e.Shape == PosterShape.Moon);
        var rabbit = All(result).Single(e => e.Shape == PosterShape.Rabbit);
        Assert.True(Math.Abs(rabbit.X - moon.X) >= PosterComposer.MoonClearanceX,
            $"rabbit at {rabbit.X} under moon at {moon.X}");
    }

    [Fact]
    public void Validate_TerrainAlwaysPresent_InTheTerrainZone()
    {
        foreach (var spec in new[] { ChaosSpec(), QuietSpec(), RichSpec(), new PosterSpec { Seed = 3 } })
        {
            var terrain = All(_composer.Validate(spec)).Where(e =>
                e.Shape is PosterShape.Hills or PosterShape.Mountain or PosterShape.ForestRow).ToList();
            Assert.InRange(terrain.Count, 1, 3);
            Assert.All(terrain, t => Assert.InRange(t.Y, PosterComposer.TerrainYMin, PosterComposer.TerrainYMax));
        }
    }

    [Fact]
    public void Validate_WaterBoundMotifsRequireWater_AndRideTheWaterline()
    {
        // Chaos spec has a boat but no water — the floating boat must be dropped.
        Assert.DoesNotContain(All(_composer.Validate(ChaosSpec())), e => e.Shape == PosterShape.Boat);

        var withWater = new PosterSpec
        {
            Seed = 9,
            Layers = [ new PosterLayer { Elements = [
                new PosterElement { Shape = PosterShape.Water, X = .5, Y = .8, Scale = .9 },
                new PosterElement { Shape = PosterShape.Sailboat, X = .4, Y = .3, Scale = .6 },
                new PosterElement { Shape = PosterShape.WhaleTail, X = .7, Y = .2, Scale = .9 } ] } ],
        };
        var repaired = _composer.Validate(withWater);
        var water = All(repaired).Single(e => e.Shape == PosterShape.Water);
        var sail = All(repaired).Single(e => e.Shape == PosterShape.Sailboat);
        var whale = All(repaired).Single(e => e.Shape == PosterShape.WhaleTail);
        Assert.Equal(water.Y, sail.Y);
        Assert.Equal(water.Y, whale.Y);
        Assert.InRange(sail.Scale, 0.08, 0.18);
        Assert.InRange(whale.Scale, 0.08, 0.18);

        // Whale with NO water is dropped too.
        var dry = new PosterSpec
        {
            Seed = 9,
            Layers = [ new PosterLayer { Elements = [
                new PosterElement { Shape = PosterShape.WhaleTail, X = .5, Y = .5, Scale = .2 } ] } ],
        };
        Assert.DoesNotContain(All(_composer.Validate(dry)), e => e.Shape == PosterShape.WhaleTail);
    }

    [Fact]
    public void Validate_BoatNeverHidesBehindTheFocal()
    {
        var spec = new PosterSpec
        {
            Seed = 11,
            Layers = [ new PosterLayer { Elements = [
                new PosterElement { Shape = PosterShape.FoxSilhouette, X = .68, Y = .8, Scale = .27 },
                new PosterElement { Shape = PosterShape.Water, X = .5, Y = .78, Scale = .9 },
                new PosterElement { Shape = PosterShape.Boat, X = .66, Y = .78, Scale = .1 } ] } ],
        };
        var repaired = _composer.Validate(spec);
        var focal = All(repaired).Single(e => e.Shape == PosterShape.FoxSilhouette);
        var boat = All(repaired).Single(e => e.Shape == PosterShape.Boat);
        Assert.True(Math.Abs(boat.X - focal.X) >= 0.2, $"boat at {boat.X} hides behind focal at {focal.X}");
    }

    [Fact]
    public void Validate_OnlyOneCreaturePerPoster()
    {
        var zoo = new PosterSpec
        {
            Seed = 4,
            Layers = [ new PosterLayer { Elements = [
                new PosterElement { Shape = PosterShape.Rabbit, X = .3, Y = .8, Scale = .2 },
                new PosterElement { Shape = PosterShape.DeerSilhouette, X = .5, Y = .8, Scale = .25 },
                new PosterElement { Shape = PosterShape.Owl, X = .7, Y = .8, Scale = .2 } ] } ],
        };
        var creatures = All(_composer.Validate(zoo)).Count(e =>
            e.Shape is PosterShape.Rabbit or PosterShape.DeerSilhouette or PosterShape.Owl or PosterShape.FoxSilhouette);
        Assert.Equal(1, creatures);
    }

    [Fact]
    public void Validate_MotifBudgetHolds()
    {
        var crowded = new PosterSpec
        {
            Seed = 8,
            Layers = [ new PosterLayer { Elements = [
                new PosterElement { Shape = PosterShape.Moon, X = .7, Y = .18, Scale = .16 },
                new PosterElement { Shape = PosterShape.Hills, X = .3, Y = .8, Scale = .8 },
                new PosterElement { Shape = PosterShape.WindowGlow, X = .3, Y = .82, Scale = .25 },
                new PosterElement { Shape = PosterShape.Lantern, X = .5, Y = .85, Scale = .14 },
                new PosterElement { Shape = PosterShape.Pine, X = .15, Y = .86, Scale = .12 },
                new PosterElement { Shape = PosterShape.RoundTree, X = .85, Y = .87, Scale = .15 },
                new PosterElement { Shape = PosterShape.Tent, X = .6, Y = .84, Scale = .2 },
                new PosterElement { Shape = PosterShape.Cloud, X = .4, Y = .25, Scale = .14 },
                new PosterElement { Shape = PosterShape.MistBand, X = .5, Y = .6, Scale = .5 },
                new PosterElement { Shape = PosterShape.BirdFlock, X = .6, Y = .3, Scale = .14 },
                new PosterElement { Shape = PosterShape.ShootingStar, X = .3, Y = .12, Scale = .12 } ] } ],
        };
        var result = _composer.Validate(crowded);
        var motifs = All(result).Count(e => e.Shape is not (PosterShape.Stars or PosterShape.StarCluster
            or PosterShape.Snowfall or PosterShape.FireflyScatter or PosterShape.FlowerScatter
            or PosterShape.Hills or PosterShape.Mountain or PosterShape.ForestRow or PosterShape.Path
            or PosterShape.Water or PosterShape.Moon or PosterShape.Crescent));
        Assert.InRange(motifs, 0, PosterComposer.MotifBudget);
    }

    [Fact]
    public void Validate_IsIdempotent()
    {
        foreach (var spec in new[] { ChaosSpec(), QuietSpec(), RichSpec() })
        {
            var once = _composer.Validate(spec);
            var twice = _composer.Validate(once);
            Assert.Equal(
                once.Layers.SelectMany(l => l.Elements).Select(e => (e.Shape, e.X, e.Y, e.Scale, e.Density, e.Tint, e.Glow, e.FlipX)),
                twice.Layers.SelectMany(l => l.Elements).Select(e => (e.Shape, e.X, e.Y, e.Scale, e.Density, e.Tint, e.Glow, e.FlipX)));
        }
    }

    [Fact]
    public void Validate_HandlesNaNInfinityAndUndefinedEnums()
    {
        var spec = QuietSpec();
        spec.Palette = (PosterPalette)999;
        spec.Layers[0].Elements[0].X = double.NaN;
        spec.Layers[0].Elements[0].Scale = double.PositiveInfinity;
        spec.Layers[2].Elements[0].Shape = (PosterShape)77;
        var result = _composer.Validate(spec);
        Assert.Equal(PosterPalette.NightHarbor, result.Palette);
        Assert.All(All(result), e =>
        {
            Assert.True(double.IsFinite(e.X) && e.X is >= 0 and <= 1);
            Assert.True(double.IsFinite(e.Scale) && e.Scale is > 0 and <= 1);
        });
    }

    // ---------- resolve ----------

    [Fact]
    public void Resolve_IsDeterministic()
    {
        foreach (var spec in new Func<PosterSpec>[] { () => ChaosSpec(), () => RichSpec() })
        {
            var a = _composer.Resolve(spec());
            var b = _composer.Resolve(spec());
            Assert.Equal(
                a.Layers.SelectMany(l => l.Elements).Select(e => (e.Shape, e.X, e.Y, e.Scale, e.ColorHex, e.SecondaryColorHex, e.Glow, e.Alpha, e.FlipX, e.FlipY)),
                b.Layers.SelectMany(l => l.Elements).Select(e => (e.Shape, e.X, e.Y, e.Scale, e.ColorHex, e.SecondaryColorHex, e.Glow, e.Alpha, e.FlipX, e.FlipY)));
        }
    }

    [Fact]
    public void Resolve_StarSprinkleIsBounded_InTheUpperSky_ClearOfTheMoon()
    {
        var resolved = _composer.Resolve(QuietSpec());
        var stars = resolved.Layers[0].Elements.Where(e => e.Shape == PosterShape.Stars).ToList();
        Assert.InRange(stars.Count, PosterComposer.StarsMin, PosterComposer.StarsMax);
        var spec = _composer.Validate(QuietSpec());
        var moon = spec.Layers[0].Elements.Single(e => e.Shape == PosterShape.Moon);
        Assert.All(stars, s =>
        {
            Assert.InRange(s.Y, 0, PosterComposer.StarFieldMaxY);
            var d = Math.Sqrt((s.X - moon.X) * (s.X - moon.X) + (s.Y - moon.Y) * (s.Y - moon.Y));
            Assert.True(d >= moon.Scale * 0.95, $"star at ({s.X:F2},{s.Y:F2}) overlaps the moon disc");
        });
    }

    [Fact]
    public void Resolve_ScatterCountGrowsWithDensity()
    {
        var sparse = QuietSpec();
        sparse.Layers[0].Elements[0].Density = 0.1;
        var dense = QuietSpec();
        dense.Layers[0].Elements[0].Density = 1.0;
        int Count(PosterSpec s) => _composer.Resolve(s).Layers[0].Elements.Count(e => e.Shape == PosterShape.Stars);
        Assert.True(Count(dense) > Count(sparse));
    }

    [Fact]
    public void Resolve_SnowfallAndFirefliesExpandIntoJitteredDots()
    {
        var resolved = _composer.Resolve(RichSpec());
        var snow = resolved.Layers[0].Elements.Where(e => e.Shape == PosterShape.Snowfall).ToList();
        Assert.True(snow.Count >= 18, $"snowfall expanded to {snow.Count} flakes");
        Assert.True(snow.Select(f => f.Alpha).Distinct().Count() > 5, "flakes vary in alpha");
        Assert.True(snow.Select(f => f.Scale).Distinct().Count() > 5, "flakes vary in size");

        var fireflySpec = new PosterSpec
        {
            Seed = 13,
            Layers = [ new PosterLayer { Elements = [
                new PosterElement { Shape = PosterShape.FireflyScatter, X = .5, Y = .75, Scale = .02, Density = .8 } ] } ],
        };
        var flies = _composer.Resolve(fireflySpec).Layers[2].Elements
            .Where(e => e.Shape == PosterShape.FireflyScatter).ToList();
        Assert.True(flies.Count >= 6);
        Assert.All(flies, f => Assert.True(f.Glow > 0, "fireflies glow"));
    }

    [Fact]
    public void Resolve_AllTwelvePalettesProduceGradientSkyAndBase()
    {
        foreach (var palette in Enum.GetValues<PosterPalette>())
        {
            var spec = QuietSpec();
            spec.Palette = palette;
            var resolved = _composer.Resolve(spec);
            Assert.Equal(4, resolved.Layers.Count);
            Assert.StartsWith("#", resolved.BaseHex);
            Assert.StartsWith("#", resolved.Layers[0].BackgroundHex);
            Assert.NotNull(resolved.Layers[0].BackgroundTopHex);
            Assert.NotNull(resolved.Layers[0].BackgroundBottomHex);
            Assert.Equal("transparent", resolved.Layers[1].BackgroundHex);
            Assert.Null(resolved.Layers[1].BackgroundTopHex);
        }
    }

    [Fact]
    public void Resolve_ParallaxTravelIsDesignContract()
    {
        var resolved = _composer.Resolve(QuietSpec());
        Assert.Equal(new[] { 12d, 20d, 30d, 40d }, resolved.Layers.Select(l => l.ParallaxTravel));
        var travels = resolved.Layers.Select(l => l.ParallaxTravel).ToArray();
        // Strictly increasing with depth — the ordering is what reads as depth.
        Assert.True(travels.Zip(travels.Skip(1)).All(p => p.Second > p.First));
        // The back layer must stay ABOVE the perception threshold on a 226 pt poster: two
        // founder passes were lost to a back layer that moved correctly but invisibly.
        Assert.True(travels[0] >= 10, "back-layer travel below the perceptible floor");
        // ...and the spread must stay modest, or the back layers fall under it again.
        Assert.InRange(travels[^1] / travels[0], 2.5, 4.0);
    }

    [Fact]
    public void Resolve_WarmAccentsAreBounded_FoxIsTheAmberWhenPresent()
    {
        // Fox focal → fox is the single amber silhouette (fireflies exempt).
        var fox = _composer.Resolve(QuietSpec());
        var amber = fox.Layers.SelectMany(l => l.Elements)
            .Where(e => e.Shape != PosterShape.FireflyScatter
                && (e.ColorHex == PosterComposer.Amber || e.SecondaryColorHex == PosterComposer.Amber)).ToList();
        var accentElement = Assert.Single(amber);
        Assert.Equal(PosterShape.FoxSilhouette, accentElement.Shape);

        // No focal + lighthouse → the beam is the amber accent.
        var lighthouseSpec = new PosterSpec
        {
            Palette = PosterPalette.OceanDeep,
            Seed = 21,
            Layers = [ new PosterLayer { Elements = [
                new PosterElement { Shape = PosterShape.Water, X = .5, Y = .78, Scale = .9 },
                new PosterElement { Shape = PosterShape.Lighthouse, X = .68, Y = .78, Scale = .2 } ] } ],
        };
        var lh = _composer.Resolve(lighthouseSpec);
        var beamed = lh.Layers.SelectMany(l => l.Elements).Single(e => e.Shape == PosterShape.Lighthouse);
        Assert.Equal(PosterComposer.Amber, beamed.SecondaryColorHex);

        // Cottage + lantern: warm family stays bounded at two lit elements.
        var cozy = new PosterSpec
        {
            Seed = 31,
            Layers = [ new PosterLayer { Elements = [
                new PosterElement { Shape = PosterShape.WindowGlow, X = .3, Y = .82, Scale = .25 },
                new PosterElement { Shape = PosterShape.Lantern, X = .6, Y = .85, Scale = .14 },
                new PosterElement { Shape = PosterShape.Tent, X = .8, Y = .84, Scale = .2 } ] } ],
        };
        var lit = _composer.Resolve(cozy).Layers.SelectMany(l => l.Elements)
            .Count(e => e.Shape is PosterShape.WindowGlow or PosterShape.Lantern or PosterShape.Tent);
        Assert.InRange(lit, 1, PosterComposer.WarmAccentBudget);
    }

    [Fact]
    public void Resolve_MoonReflectsOnWaterAsSoftGlade()
    {
        var spec = new PosterSpec
        {
            Palette = PosterPalette.OceanDeep,
            Seed = 17,
            Layers = [ new PosterLayer { Elements = [
                new PosterElement { Shape = PosterShape.Moon, X = .68, Y = .18, Scale = .16 },
                new PosterElement { Shape = PosterShape.Water, X = .5, Y = .72, Scale = .9 } ] } ],
        };
        var resolved = _composer.Resolve(spec);
        var glade = resolved.Layers[2].Elements.SingleOrDefault(e => e.Shape == PosterShape.Moon && e.FlipY);
        Assert.NotNull(glade);
        Assert.Equal(0.68, glade.X, 3);
        Assert.True(glade.Alpha < 0.5, "reflection is faint");
        Assert.True(glade.Y > 0.72, "reflection sits below the waterline");

        // No water → no reflection element anywhere.
        var dry = QuietSpec();
        Assert.DoesNotContain(_composer.Resolve(dry).Layers.SelectMany(l => l.Elements), e => e.FlipY);
    }

    [Fact]
    public void Resolve_TerrainStaggersDarkerTowardTheViewer()
    {
        var spec = new PosterSpec
        {
            Palette = PosterPalette.PineForest,
            Seed = 19,
            Layers = [ new PosterLayer { Elements = [
                new PosterElement { Shape = PosterShape.Mountain, X = .7, Y = .60, Scale = .6 },
                new PosterElement { Shape = PosterShape.Hills, X = .3, Y = .74, Scale = .8 },
                new PosterElement { Shape = PosterShape.ForestRow, X = .5, Y = .85, Scale = 1.0 } ] } ],
        };
        var terrain = _composer.Resolve(spec).Layers[1].Elements
            .Where(e => e.Shape is PosterShape.Mountain or PosterShape.Hills or PosterShape.ForestRow)
            .OrderBy(e => e.Y).ToList();
        Assert.Equal(3, terrain.Count);
        Assert.Equal(3, terrain.Select(t => t.ColorHex).Distinct().Count());
        // Nearer masses are darker (lower channel sums).
        int Luma(string hex) => Convert.ToInt32(hex[1..3], 16) + Convert.ToInt32(hex[3..5], 16) + Convert.ToInt32(hex[5..7], 16);
        Assert.True(Luma(terrain[0].ColorHex) > Luma(terrain[1].ColorHex));
        Assert.True(Luma(terrain[1].ColorHex) > Luma(terrain[2].ColorHex));
    }

    [Fact]
    public void Resolve_TintFadesElementsTowardTheSky()
    {
        PosterSpec Spec(double tint) => new()
        {
            Palette = PosterPalette.PineForest,
            Seed = 23,
            Layers = [ new PosterLayer { Elements = [
                new PosterElement { Shape = PosterShape.Pine, X = .3, Y = .85, Scale = .12, Tint = tint } ] } ],
        };
        var plain = _composer.Resolve(Spec(0)).Layers[2].Elements.Single(e => e.Shape == PosterShape.Pine);
        var hazed = _composer.Resolve(Spec(0.6)).Layers[2].Elements.Single(e => e.Shape == PosterShape.Pine);
        Assert.NotEqual(plain.ColorHex, hazed.ColorHex);
        int Luma(string hex) => Convert.ToInt32(hex[1..3], 16) + Convert.ToInt32(hex[3..5], 16) + Convert.ToInt32(hex[5..7], 16);
        Assert.True(Luma(hazed.ColorHex) > Luma(plain.ColorHex), "hazed pine lifts toward the (lighter) sky tone");
    }

    [Fact]
    public void Resolve_MoonAndStarsAreCream()
    {
        var resolved = _composer.Resolve(QuietSpec());
        Assert.All(resolved.Layers[0].Elements.Where(e => e.Shape is PosterShape.Moon or PosterShape.Stars),
            e => Assert.Equal(PosterComposer.Cream, e.ColorHex));
    }

    // ---------- art version ----------

    [Fact]
    public void ArtVersion_IsStampedForThumbnailRegeneration()
    {
        Assert.True(PosterComposer.ArtVersion >= 5); // v5 = formation contract v2
        Assert.Contains($"-thumb-v{PosterComposer.ArtVersion}.png", Core.Services.PosterArtRefresher.CurrentSuffix);
    }

    [Fact]
    public void Lerp_MixesHexChannels()
    {
        Assert.Equal("#000000", PosterComposer.Lerp("#000000", "#FFFFFF", 0));
        Assert.Equal("#FFFFFF", PosterComposer.Lerp("#000000", "#FFFFFF", 1));
        Assert.Equal("#808080", PosterComposer.Lerp("#000000", "#FFFFFF", 0.5));
    }
}
