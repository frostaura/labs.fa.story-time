using StoryTime.Contracts;
using StoryTime.Poster.Core;

namespace StoryTime.Poster.Composition;

/// <summary>Everything the engine draws, as pure data, decided before a single pixel is
/// painted: where the horizon sits, which side the moon and the focal subject take, every
/// star, every silhouette's anchor and size. Coordinates are normalized to the VISIBLE
/// poster frame (0..1 on both axes; the overscan bleed is simply x &lt; 0 or x &gt; 1), and
/// sizes are fractions of the frame's shorter side. Tests read the plan to prove
/// composition rules (title space, moon clearance, focal on a third) without pixels.</summary>
public sealed class PosterPlan
{
    public required PosterSpec Spec { get; init; }
    public required ScenePalette Colors { get; init; }
    /// <summary>Where sky meets land/sea, 0..1 from the top.</summary>
    public required double Horizon { get; init; }
    /// <summary>+1: focal subject on the right third, moon on the left. −1: mirrored.</summary>
    public required int Side { get; init; }
    public MoonPlan? Moon { get; init; }
    public required IReadOnlyList<StarPlan> Stars { get; init; }
    public MilkyWayPlan? MilkyWay { get; init; }
    public required IReadOnlyList<CloudPlan> Clouds { get; init; }
    public AuroraPlan? Aurora { get; init; }
    public ShootingStarPlan? ShootingStar { get; init; }
    public required IReadOnlyList<TerrainBand> Bands { get; init; }
    public WaterPlan? Water { get; init; }
    public required IReadOnlyList<Placement> Placements { get; init; }
    public required IReadOnlyList<AmbientParticle> Ambient { get; init; }
    /// <summary>The focal subject (null when the scene's own landscape is the subject).</summary>
    public Placement? Focal { get; init; }
    /// <summary>Clean negative space reserved for a story title overlay.</summary>
    public required NormRect TitleSafe { get; init; }
    public required bool Underwater { get; init; }
}

public readonly record struct NormRect(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;
    public double Height => Bottom - Top;
    public bool Contains(double x, double y) => x >= Left && x <= Right && y >= Top && y <= Bottom;
}

/// <summary>Phase: 0 = full, 1 = new. Crescents are ≈0.7.</summary>
/// <summary>Depth: 0 normally; the water's layer when a moonglade must stay under the moon.</summary>
public sealed record MoonPlan(double X, double Y, double R, double Phase, bool LitFromRight, double Glow, int Depth = 0);

public sealed record StarPlan(double X, double Y, double Size, double Alpha, int Tint, bool Sparkle);

public sealed record MilkyWayPlan(double CenterX, double CenterY, double AngleDeg, double Width, uint Seed);

public sealed record CloudPlan(double X, double Y, double Width, double Height, uint Seed, double Alpha);

public sealed record AuroraPlan(double Base, double Height, double X0, double X1, uint Seed);

public sealed record ShootingStarPlan(double X, double Y, double AngleDeg, double Length);

public enum TerrainKind { Flat, Hills, Mountains, Dunes, Bumps, Rooftops }

public enum TerrainStyle { Earth, Snow, Sand, Cloud, Reef, Roofs, Canopy }

/// <summary>One silhouette band (a ridge, a dune line, a cloud bank, a row of roofs).</summary>
public sealed class TerrainBand
{
    public required int Depth { get; init; }
    public required TerrainProfile Profile { get; init; }
    /// <summary>Atmospheric depth 0 (horizon) .. 1 (nearest) — drives colour.</summary>
    public required double Tone { get; init; }
    public required TerrainStyle Style { get; init; }
    public bool Rim { get; init; } = true;
    /// <summary>Valley fog dissolving the band's base into the haze.</summary>
    public double Fog { get; init; }
    /// <summary>Snow caps on mountain crests (alpine scenes).</summary>
    public bool SnowCaps { get; init; }
}

/// <summary>A deterministic 1D ground profile: y(x) of the silhouette's top edge.</summary>
public sealed class TerrainProfile
{
    public required TerrainKind Kind { get; init; }
    public required double Base { get; init; }
    public double Amp { get; init; }
    public double Freq { get; init; } = 1;
    public double Phase { get; init; }
    public uint Seed { get; init; }
    public int Octaves { get; init; } = 3;
    /// <summary>Bumps: (center x, radius, height scale).</summary>
    public IReadOnlyList<(double X, double R, double K)> Bumps { get; init; } = [];
    /// <summary>Rooftops: buildings left→right.</summary>
    public IReadOnlyList<Building> Buildings { get; init; } = [];
    /// <summary>Optional envelope: multiplies the amplitude across x (e.g. a headland that
    /// only rises on one side).</summary>
    public Func<double, double>? Envelope { get; init; }
    /// <summary>With an envelope: where the envelope is 0 the land sinks to this y (below
    /// the frame), so a headland or island rises out of the sea instead of a flat shelf
    /// covering it.</summary>
    public double? Floor { get; init; }

    public double Y(double x)
    {
        if (Floor is { } floor && Envelope is { } envelope)
        {
            var e = Math.Clamp(envelope(x), 0, 1);
            return floor + (Raw(x, 1.0) - floor) * e;
        }
        return Raw(x, Envelope?.Invoke(x) ?? 1.0);
    }

    private double Raw(double x, double env)
    {
        switch (Kind)
        {
            case TerrainKind.Flat:
                return Base;
            case TerrainKind.Hills:
                return Base - Amp * env * (0.5 + 0.5 * Noise.Fbm1(x * Freq + Phase, Seed, Octaves, 0.45));
            case TerrainKind.Mountains:
            {
                var r = Noise.Ridged1(x * Freq + Phase, Seed, Octaves);
                var f = 0.5 + 0.5 * Noise.Fbm1(x * Freq * 0.6 + Phase + 3.1, Seed + 7, 3, 0.5);
                return Base - Amp * env * Math.Clamp(0.75 * r * 1.6 + 0.25 * f, 0, 1.25);
            }
            case TerrainKind.Dunes:
            {
                var h = 0.0;
                for (var o = 0; o < 2; o++)
                {
                    var phi = x * Freq * (o == 0 ? 1 : 2.3) + Phase + o * 0.37;
                    var cell = Math.Floor(phi);
                    var f = phi - cell;
                    var amp = 0.6 + 0.4 * Hash.Unit((int)cell, o, Seed);
                    // Long windward slope, short steep slip face.
                    var shape = f < 0.72
                        ? Math.Pow(Math.Sin(f / 0.72 * Math.PI / 2), 1.6)
                        : Math.Pow(Math.Cos((f - 0.72) / 0.28 * Math.PI / 2), 2.2);
                    h += (o == 0 ? 1.0 : 0.25) * amp * shape;
                }
                return Base - Amp * env * h / 1.25;
            }
            case TerrainKind.Bumps:
            {
                // Smooth maximum over circular bumps: organic joins, never a seam.
                const double k = 0.005;
                double acc = 0;
                foreach (var (bx, br, bk) in Bumps)
                {
                    var dx = x - bx;
                    if (Math.Abs(dx) >= br) continue;
                    var h = Math.Sqrt(br * br - dx * dx) * bk;
                    acc += Math.Exp(h / k);
                }
                var top = acc > 0 ? k * Math.Log(acc) : 0;
                return Base - Math.Max(0, top) * env;
            }
            case TerrainKind.Rooftops:
            {
                foreach (var b in Buildings)
                    if (x >= b.X0 && x <= b.X1) return b.RoofY(x);
                return Base;
            }
            default:
                return Base;
        }
    }
}

public enum RoofKind { Gable, Flat, Steep, Dome }

public sealed record Building(double X0, double X1, double WallTop, double RoofHeight, RoofKind Roof,
    bool Chimney, double ChimneyX, int Windows, uint Seed, double LitChance = 0.12)
{
    public double Center => (X0 + X1) / 2;

    public double RoofY(double x)
    {
        var w = X1 - X0;
        var u = Math.Abs((x - Center) / (w / 2));
        return Roof switch
        {
            RoofKind.Flat => WallTop,
            RoofKind.Dome => WallTop - RoofHeight * Math.Sqrt(Math.Max(0, 1 - u * u)),
            _ => WallTop - RoofHeight * Math.Max(0, 1 - u),
        };
    }
}

public sealed record WaterPlan(int Depth, double Line, bool Glade);

/// <summary>Every drawable object above the terrain.</summary>
public enum Prop
{
    // flora & scenery
    Pine, RoundTree, Palm, GrassTuft, Reeds, Rock, Bush, Fern, Kelp, BranchCoral, FanCoral, BigLeaf,
    Fence, Mushroom, Flower, Vines, Pool, Trunk,
    // structures & vehicles
    Cabin, House, Castle, Windmill, Tent, Lantern, Telescope, Boat, Sailboat, Balloon, Train, Lighthouse,
    // creatures
    Fox, FoxCurled, Rabbit, Owl, Deer, Bear, Cat, Hedgehog, Otter, Whale, Turtle, FishSchool, Camel, Dinosaur, Birds,
}

/// <summary>A prop anchored at its BASE (x, y) unless noted, at depth layer 1..3.</summary>
public sealed record Placement(Prop Prop, int Depth, double X, double Y, double Size, bool Flip, int Variant, uint Seed,
    PosterFeature? Feature = null, bool Lit = false, double Tone = -1);

public enum AmbientKind { Twinkle, Firefly, Glint, Snowflake, Bubble, Mote }

/// <summary>A gently animated particle, drawn by the ambient pass at time t. Static renders
/// (thumbnails, Reduce Motion) draw it at t = 0.</summary>
public sealed record AmbientParticle(AmbientKind Kind, int Depth, double X, double Y, double Size, double Alpha,
    double Phase, double Period, double Amp, uint Color);
