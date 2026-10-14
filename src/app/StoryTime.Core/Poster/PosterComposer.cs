using StoryTime.Contracts;
using StoryTime.Core.Abstractions;

namespace StoryTime.Core.Poster;

/// <summary>Deterministic poster composition, formation contract v2. The LLM (or the
/// template engine) emits a per-layer formation — which motifs, where, how large, how
/// glowing — from a ~31-shape vocabulary, and Validate() lets that composition THROUGH
/// wherever it is sane, repairing only rule violations instead of re-bucketing everything
/// into fixed slots. The invariants that survive from v1: every poster is quiet and
/// grounded (terrain always present, grounded motifs sit on a ground line, boats need
/// water), at most one moon that nothing stands under, a bounded motif budget, and a
/// single warm accent family (≤2 warm-lit elements; fireflies exempt). Same spec → same
/// output; repair is idempotent (Validate(Validate(s)) == Validate(s)). Rasterization
/// lives in the App layer (SkiaPosterRenderer); Core stays graphics-free.</summary>
public sealed class PosterComposer : IPosterComposer
{
    /// <summary>Bump when composition rules or shape painters change enough that cached
    /// thumbnails must re-render. Stamped into thumbnail filenames (…-thumb-v{N}.png) so
    /// platform image caches (Glide) can never serve stale art. v5: formation contract,
    /// 31-shape vocabulary, gradient skies + glows. v7: extent-aware crop-safe focal zone.
    /// v8: shooting stars rare + moon-clear (founder artifact fix), crescent inner shadow,
    /// corner vignette, sky-gradient dither.</summary>
    public const int ArtVersion = 8;

    public const int LayerCount = 4;

    /// <summary>Max parallax travel (device-independent points) per depth 0..3. Rewritten
    /// twice on 2026-07-31 against physical-iPhone evidence, both times because the numbers
    /// were below the perception threshold rather than because the motion was broken:
    /// [4,9,15,24] → [6,14,24,38] (front layer read as static) → [12,20,30,40] (BACK layer
    /// read as static — a 6 pt star drift on a 226 pt poster is invisible).
    ///
    /// The 6:1 spread of the original design contract is deliberately compressed to 3.3:1.
    /// Depth ordering is what sells the illusion; a spread that wide simply parks the back
    /// layers below the eye's threshold. `ParallaxPosterView` bleeds every layer by the max
    /// value here, so raising the top value also widens the rendered layer and crops the
    /// composition harder — keep it ≤ ~20% of the poster edge.</summary>
    public static readonly double[] ParallaxTravelByDepth = [12, 20, 30, 40];

    // ---- Zones (public so tests pin the contract) ----
    public const double MoonScaleMin = 0.10, MoonScaleMax = 0.24;
    public const double MoonYMin = 0.08, MoonYMax = 0.30;
    public const double MoonXMin = 0.14, MoonXMax = 0.86;
    /// <summary>Nothing tall/focal stands within this horizontal distance of the moon.</summary>
    public const double MoonClearanceX = 0.18;
    /// <summary>A shooting star's HEAD keeps this horizontal distance from the moon; its
    /// tail is additionally flipped to sweep away from the moon, so the streak can never
    /// lie across the disc (the founder's "arbitrary line" artifact).</summary>
    public const double ShootingStarClearanceX = 0.26;
    /// <summary>Shooting stars are an occasional delight, not a fixture: one poster in
    /// four (seed-hashed, deterministic) may carry one.</summary>
    public const int ShootingStarChancePercent = 25;
    public const double FocalScaleMin = 0.14, FocalScaleMax = 0.34;
    public const double FocalYMin = 0.70, FocalYMax = 0.88;
    /// <summary>Tight enough that the home carousel's 3:4 center-crop of the square art
    /// (visible x ≈ 0.14..0.86) can never clip the focal silhouette.</summary>
    public const double FocalXMin = 0.22, FocalXMax = 0.78;
    /// <summary>Terrain crests live here; lower bound relaxed from v1's 0.80 so staggered
    /// ridge lines (far mountain high, near hill low) can build real depth.</summary>
    public const double TerrainYMin = 0.52, TerrainYMax = 0.92;
    public const double WaterYMin = 0.58, WaterYMax = 0.86;
    public const int StarsMin = 4, StarsMax = 14;
    /// <summary>Stars stay in the upper 60% of the canvas.</summary>
    public const double StarFieldMaxY = 0.58;
    /// <summary>Non-scatter, non-terrain motifs per poster (structures + creatures + flora).</summary>
    public const int MotifBudget = 6;
    /// <summary>Warm-lit elements per poster (fox, windows, lanterns, beams; fireflies exempt).</summary>
    public const int WarmAccentBudget = 2;

    public const string Cream = "#F3E9DC";
    public const string Amber = "#E8A857";
    private const string WarmGlow = "#F2C580";
    private const string AuroraTeal = "#7FD8B4";

    // 12 curated palettes → 4-step depth ramp (far → near), all dark + calm.
    private static readonly Dictionary<PosterPalette, string[]> Ramps = new()
    {
        [PosterPalette.NightHarbor] = ["#3A2E52", "#281F3C", "#1C1630", "#141021"],
        [PosterPalette.MoonMeadow] = ["#33402F", "#263021", "#1B2417", "#12180F"],
        [PosterPalette.OceanDeep] = ["#1E3A40", "#152A30", "#0F2026", "#0A161B"],
        [PosterPalette.PlumDusk] = ["#40303E", "#2E2230", "#221924", "#171019"],
        [PosterPalette.PineForest] = ["#28362A", "#1A2620", "#141E16", "#0E150F"],
        [PosterPalette.CloudDrift] = ["#3A3A4E", "#2C2C3E", "#202030", "#161624"],
        [PosterPalette.StarfieldViolet] = ["#322850", "#251C3E", "#1A142E", "#100C1E"],
        [PosterPalette.LanternGlow] = ["#3E2E20", "#2E2118", "#201710", "#150E0A"],
        [PosterPalette.TidePool] = ["#1E3A34", "#152A26", "#0F201C", "#0A1613"],
        [PosterPalette.SnowHush] = ["#2E3440", "#232834", "#191E28", "#11141C"],
        [PosterPalette.EmberSky] = ["#46281E", "#341E16", "#24140E", "#180D09"],
        [PosterPalette.MistValley] = ["#2C343A", "#20272E", "#161C22", "#0E1318"],
    };

    // ---- Shape categories (the composer's knowledge of the vocabulary) ----

    private static bool IsScatter(PosterShape s) => s is PosterShape.Stars or PosterShape.StarCluster
        or PosterShape.Snowfall or PosterShape.FireflyScatter or PosterShape.FlowerScatter;

    private static bool IsCelestialPrimary(PosterShape s) => s is PosterShape.Moon or PosterShape.Crescent;

    private static bool IsSkyExtra(PosterShape s) => s is PosterShape.Cloud or PosterShape.MistBand
        or PosterShape.Aurora or PosterShape.BirdFlock or PosterShape.ShootingStar;

    private static bool IsTerrain(PosterShape s) => s is PosterShape.Hills or PosterShape.Mountain
        or PosterShape.ForestRow or PosterShape.Path;

    private static bool IsWaterBound(PosterShape s) => s is PosterShape.Boat or PosterShape.Sailboat
        or PosterShape.WhaleTail;

    private static bool IsFlora(PosterShape s) => s is PosterShape.Pine or PosterShape.RoundTree
        or PosterShape.Reeds;

    private static bool IsStructure(PosterShape s) => s is PosterShape.Lighthouse or PosterShape.WindowGlow
        or PosterShape.Lantern or PosterShape.Tent;

    private static bool IsCreature(PosterShape s) => s is PosterShape.FoxSilhouette or PosterShape.Rabbit
        or PosterShape.Owl or PosterShape.DeerSilhouette;

    /// <summary>Shapes that may carry the poster's warm light.</summary>
    private static bool IsWarmShape(PosterShape s) => s is PosterShape.FoxSilhouette or PosterShape.WindowGlow
        or PosterShape.Lantern or PosterShape.Tent or PosterShape.Lighthouse;

    // Per-shape scale clamps (visual sanity — a whale fluke is never half the canvas).
    private static (double Min, double Max) ScaleRange(PosterShape s) => s switch
    {
        PosterShape.Moon or PosterShape.Crescent => (MoonScaleMin, MoonScaleMax),
        PosterShape.Hills => (0.45, 1.0),
        PosterShape.Mountain => (0.35, 0.95),
        PosterShape.ForestRow => (0.30, 1.0),
        PosterShape.Path => (0.25, 0.7),
        PosterShape.Water => (0.5, 1.0),
        PosterShape.Cloud => (0.08, 0.26),
        PosterShape.MistBand => (0.30, 1.0),
        PosterShape.Aurora => (0.40, 1.0),
        PosterShape.ShootingStar => (0.08, 0.22),
        PosterShape.BirdFlock => (0.10, 0.24),
        PosterShape.Pine => (0.07, 0.20),
        PosterShape.RoundTree => (0.09, 0.22),
        PosterShape.Reeds => (0.08, 0.20),
        PosterShape.Lighthouse => (0.12, 0.24),
        PosterShape.WindowGlow => (0.16, 0.32),
        PosterShape.Lantern => (0.10, 0.20),
        PosterShape.Tent => (0.14, 0.28),
        PosterShape.Boat => (0.06, 0.14),
        PosterShape.Sailboat => (0.08, 0.18),
        PosterShape.WhaleTail => (0.08, 0.18),
        PosterShape.FoxSilhouette or PosterShape.Rabbit or PosterShape.Owl or PosterShape.DeerSilhouette
            => (FocalScaleMin, FocalScaleMax),
        _ => (0.02, 1.0),
    };

    /// <summary>Repairs any spec into a valid formation. Deterministic and idempotent:
    /// the LLM's composition passes through wherever it is sane; only violations are
    /// clamped, relocated, or dropped (by priority) — never re-imagined.</summary>
    public PosterSpec Validate(PosterSpec spec)
    {
        var palette = Enum.IsDefined(spec.Palette) ? spec.Palette : PosterPalette.NightHarbor;
        var seed = spec.Seed;

        // Gather every finite, enum-valid element, remembering the emitted layer.
        var all = new List<(PosterElement E, int Layer)>();
        var layerIndex = 0;
        foreach (var layer in spec.Layers ?? [])
        {
            foreach (var e in layer?.Elements ?? [])
            {
                if (e is not null && Enum.IsDefined(e.Shape))
                {
                    var (sMin, sMax) = ScaleRange(e.Shape);
                    all.Add((new PosterElement
                    {
                        Shape = e.Shape,
                        X = Clamp01(e.X),
                        Y = Clamp01(e.Y),
                        Scale = Math.Clamp(double.IsFinite(e.Scale) ? e.Scale : (sMin + sMax) / 2, sMin, sMax),
                        Density = Clamp01(e.Density),
                        Tint = Clamp01(e.Tint),
                        Glow = Clamp01(e.Glow),
                        FlipX = e.FlipX,
                    }, layer?.Depth is >= 0 and <= 3 ? layer.Depth : layerIndex));
                }
            }
            layerIndex = Math.Min(layerIndex + 1, 3);
        }

        var sky = new List<PosterElement>();
        var far = new List<PosterElement>();
        var mid = new List<PosterElement>();
        var fore = new List<PosterElement>();

        // ---- Celestial primary: at most ONE moon/crescent, in the upper sky ----
        var moonIn = FirstOf(all, IsCelestialPrimary);
        PosterElement? moon = null;
        if (moonIn is not null)
        {
            moon = Clone(moonIn);
            moon.X = Math.Clamp(moon.X, MoonXMin, MoonXMax);
            moon.Y = Math.Clamp(moon.Y, MoonYMin, MoonYMax);
            moon.Glow = Math.Max(moon.Glow, 0.30);
            sky.Add(moon);
        }

        // ---- Star field: always present (density from the spec's stars if any) ----
        var starsIn = FirstOf(all, s => s == PosterShape.Stars);
        sky.Add(new PosterElement
        {
            Shape = PosterShape.Stars, X = 0.5, Y = 0.30, Scale = 0.05,
            Density = starsIn is null ? 0.35 : (starsIn.Density > 0 ? starsIn.Density : 0.35),
        });

        // ---- Sky extras, each bounded to its own zone ----
        TakeInto(sky, all, s => s == PosterShape.StarCluster, 1, e =>
        {
            e.Y = Math.Clamp(e.Y, 0.06, 0.45);
            e.Scale = Math.Clamp(e.Scale, 0.08, 0.2);
        });
        TakeInto(sky, all, s => s == PosterShape.Aurora, 1, e =>
        {
            e.X = Math.Clamp(e.X, 0.2, 0.8);
            e.Y = Math.Clamp(e.Y, 0.05, 0.30);
        });
        if (ShootingStarAllowed(seed))
            TakeInto(sky, all, s => s == PosterShape.ShootingStar, 1, e => PlaceShootingStar(e, moon));
        TakeInto(sky, all, s => s == PosterShape.Cloud, 2, e => e.Y = Math.Clamp(e.Y, 0.10, 0.45));
        TakeInto(sky, all, s => s == PosterShape.BirdFlock, 1, e => e.Y = Math.Clamp(e.Y, 0.15, 0.48));
        TakeInto(sky, all, s => s == PosterShape.Snowfall, 1, e => { e.X = 0.5; e.Y = 0.5; });
        TakeInto(sky, all, s => s == PosterShape.MistBand, 2, e => e.Y = Math.Clamp(e.Y, 0.30, 0.78));

        // ---- Terrain: 1–3 masses, staggered crests, always at least one ----
        var terrain = all.Where(t => IsTerrain(t.E.Shape) && t.E.Shape != PosterShape.Path)
            .Select(t => t.E).Take(3).ToList();
        foreach (var t in terrain)
        {
            t.Y = t.Shape == PosterShape.Mountain
                ? Math.Clamp(t.Y, TerrainYMin, 0.78)
                : Math.Clamp(t.Y, 0.60, TerrainYMax);
        }
        if (terrain.Count == 0)
        {
            terrain.Add(new PosterElement
            {
                Shape = PosterShape.Hills, X = (seed & 1) == 0 ? 0.22 : 0.78, Y = 0.78, Scale = 0.85,
            });
            terrain.Add(new PosterElement
            {
                Shape = PosterShape.Hills, X = (seed & 1) == 0 ? 0.80 : 0.20, Y = 0.86, Scale = 0.75, Tint = 0,
            });
        }
        far.AddRange(terrain.OrderBy(t => t.Y));
        var groundLine = terrain.Max(t => t.Y); // the NEAREST crest is the walkable ground

        var pathIn = FirstOf(all, s => s == PosterShape.Path);
        if (pathIn is not null)
        {
            pathIn.Y = Math.Clamp(pathIn.Y, 0.55, 0.85);
            pathIn.X = Math.Clamp(pathIn.X, 0.2, 0.8);
            far.Add(pathIn);
        }

        // ---- Water: at most one; boats/whales require it and ride the waterline ----
        var water = FirstOf(all, s => s == PosterShape.Water);
        double? waterLine = null;
        if (water is not null)
        {
            water.X = 0.5;
            water.Y = Math.Clamp(water.Y, WaterYMin, WaterYMax);
            waterLine = water.Y;
            mid.Add(water);
        }

        // ---- Focal: ONE creature (fox first), else a warm structure; on the ground ----
        var focalIn = FirstOf(all, s => s == PosterShape.FoxSilhouette)
                   ?? FirstOf(all, IsCreature)
                   ?? FirstOf(all, s => s is PosterShape.WindowGlow or PosterShape.Tent or PosterShape.Lantern);
        PosterElement? focal = null;
        if (focalIn is not null)
        {
            focal = Clone(focalIn);
            focal.Y = Math.Clamp(Math.Max(focal.Y, groundLine + 0.02), FocalYMin, FocalYMax);
            focal.Scale = Math.Clamp(focal.Scale, FocalScaleMin, FocalScaleMax);
            // Extent-aware X clamp: each silhouette reaches a known distance past its anchor
            // per side (the fox's tail is 0.8×scale). The WHOLE shape must stay on the square
            // canvas (a tail tip may enter the carousel's crop margin, but never leave the art).
            var (extL, extR) = FocalExtents(focal.Shape);
            if (focal.FlipX) (extL, extR) = (extR, extL);
            var xMin = Math.Max(FocalXMin, 0.03 + extL * focal.Scale);
            var xMax = Math.Min(FocalXMax, 0.97 - extR * focal.Scale);
            if (xMin > xMax) xMin = xMax = (xMin + xMax) / 2;
            focal.X = Math.Clamp(focal.X, xMin, xMax);
            if (moon is not null && Math.Abs(focal.X - moon.X) < MoonClearanceX)
                focal.X = ShiftClear(focal.X, moon.X, MoonClearanceX, xMin, xMax);
            fore.Add(focal);
        }

        // ---- Water-bound motifs (after focal so they can dodge it) ----
        foreach (var wb in all.Where(t => IsWaterBound(t.E.Shape)).Select(t => t.E))
        {
            if (waterLine is null) break; // no water → they would float mid-air; drop
            if (mid.Count(e => IsWaterBound(e.Shape)) >= 2) break;
            wb.Y = waterLine.Value;
            wb.X = Math.Clamp(wb.X, 0.18, 0.82);
            if (focal is not null && Math.Abs(wb.X - focal.X) < 0.20)
                wb.X = ShiftClear(wb.X, focal.X, 0.20, 0.18, 0.82);
            if (moon is not null && wb.Shape == PosterShape.Sailboat && Math.Abs(wb.X - moon.X) < MoonClearanceX)
                wb.X = ShiftClear(wb.X, moon.X, MoonClearanceX, 0.18, 0.82);
            mid.Add(wb);
        }

        // ---- Grounded structures & flora ----
        foreach (var (g, emittedLayer) in all.Where(t =>
                     (IsStructure(t.E.Shape) || IsFlora(t.E.Shape) || IsCreature(t.E.Shape))
                     && !ReferenceEquals(t.E, focalIn)))
        {
            // Only ONE creature per poster — extra creatures make it a petting zoo.
            if (IsCreature(g.Shape)) continue;

            var target = emittedLayer >= 3 && IsFlora(g.Shape) ? fore : mid;
            var capacity = target == fore ? 3 : 2; // fore: focal + 2 framing · mid: 2 grounded motifs
            if (target.Count(e => !IsWaterBound(e.Shape) && e.Shape != PosterShape.Water) >= capacity) continue;

            // Only the truly tall structure dodges the moon; small trees never reach it.
            if (moon is not null && g.Shape == PosterShape.Lighthouse
                && Math.Abs(g.X - moon.X) < MoonClearanceX)
                g.X = ShiftClear(g.X, moon.X, MoonClearanceX, 0.10, 0.90);
            if (focal is not null && Math.Abs(g.X - focal.X) < 0.16 && target == mid)
                g.X = ShiftClear(g.X, focal.X, 0.16, 0.08, 0.92);

            // Base on the ground (or the waterline for a lighthouse on a shore poster).
            g.Y = g.Shape == PosterShape.Lighthouse && waterLine is not null
                ? waterLine.Value
                : Math.Clamp(Math.Max(g.Y, groundLine), 0.62, 0.94);
            target.Add(g);
        }

        // ---- Ground scatters ----
        TakeInto(mid, all, s => s == PosterShape.FireflyScatter, 1, e =>
        {
            // Fireflies hover just over the ground — never floating in the open sky.
            e.Y = Math.Clamp(Math.Max(e.Y, groundLine + 0.01), 0.55, 0.88);
            e.Glow = Math.Max(e.Glow, 0.6);
        });
        TakeInto(fore, all, s => s == PosterShape.FlowerScatter, 1, e => e.Y = Math.Clamp(e.Y, groundLine, 0.94));

        // ---- Budgets: ≤6 motifs (non-scatter, non-terrain, non-water), warm lights ≤2 ----
        EnforceMotifBudget(sky, mid, fore);
        EnforceWarmBudget(mid, fore);

        return new PosterSpec
        {
            Palette = palette,
            Mood = string.IsNullOrWhiteSpace(spec.Mood) ? "calm" : spec.Mood,
            Seed = seed,
            Layers =
            [
                new PosterLayer { Depth = 0, Elements = sky },
                new PosterLayer { Depth = 1, Elements = far },
                new PosterLayer { Depth = 2, Elements = mid },
                new PosterLayer { Depth = 3, Elements = fore },
            ],
        };
    }

    private static PosterElement? FirstOf(List<(PosterElement E, int Layer)> all, Func<PosterShape, bool> match)
        => all.FirstOrDefault(t => match(t.E.Shape)).E;

    /// <summary>Seed-hashed frequency gate for the shooting star (deterministic and stable
    /// under re-validation: the same spec always answers the same way).</summary>
    public static bool ShootingStarAllowed(int seed)
    {
        var h = (uint)seed * 2654435761u;
        h ^= h >> 16;
        return h % 100 < ShootingStarChancePercent;
    }

    /// <summary>Places a shooting star so its whole streak (head + ≈2.1×scale tail) stays
    /// on-canvas and clear of the moon, with the tail always sweeping AWAY from the disc.
    /// Deterministic and idempotent: a placed star re-validates to itself.</summary>
    private static void PlaceShootingStar(PosterElement e, PosterElement? moon)
    {
        e.Y = Math.Clamp(e.Y, 0.12, 0.28);          // below the top edge even with tail rise
        e.Scale = Math.Clamp(e.Scale, 0.08, 0.15);  // a delicate streak, never a rod
        var margin = ShootingStarClearanceX * 1.03; // float-safe idempotence (see ShiftClear)

        if (moon is null)
        {
            e.FlipX = false;                        // tail sweeps up-left
            e.X = Math.Clamp(e.X, e.Scale * 2.1 + 0.01, 0.97);
            return;
        }

        // Room on each side of the moon for head + tail; keep the emitted side when it
        // fits, otherwise take the other, shrinking the tail if the side is tight.
        var leftRoom = moon.X - margin - 0.01;      // head ≤ moon.X - margin, tail ≥ 0.01
        var rightRoom = 0.99 - (moon.X + margin);   // head ≥ moon.X + margin, tail ≤ 0.99
        var preferRight = e.X >= moon.X;
        var useRight = preferRight ? rightRoom >= e.Scale * 2.1 || rightRoom >= leftRoom
                                   : leftRoom < e.Scale * 2.1 && rightRoom > leftRoom;
        var room = useRight ? rightRoom : leftRoom;
        e.Scale = Math.Min(e.Scale, Math.Max(0.06, room / 2.1));
        var tail = e.Scale * 2.1;
        e.FlipX = useRight;                          // mirrored tail sweeps up-right
        e.X = useRight
            ? Math.Clamp(e.X, moon.X + margin, 0.99 - tail)
            : Math.Clamp(e.X, tail + 0.01, moon.X - margin);
    }

    /// <summary>Horizontal half-extents (fractions of scale) each focal silhouette reaches
    /// past its anchor in unflipped orientation, per the renderer's painters.</summary>
    private static (double Left, double Right) FocalExtents(PosterShape shape) => shape switch
    {
        PosterShape.FoxSilhouette => (0.80, 0.35),
        PosterShape.Rabbit => (0.40, 0.28),
        PosterShape.Owl => (0.55, 0.55),
        PosterShape.DeerSilhouette => (0.50, 0.55),
        PosterShape.WhaleTail => (0.55, 0.55),
        PosterShape.WindowGlow => (0.62, 0.62),
        PosterShape.Tent => (0.66, 0.66),
        PosterShape.Lantern => (0.10, 0.42),
        _ => (0.5, 0.5),
    };

    private static PosterElement Clone(PosterElement e) => new()
    {
        Shape = e.Shape, X = e.X, Y = e.Y, Scale = e.Scale,
        Density = e.Density, Tint = e.Tint, Glow = e.Glow, FlipX = e.FlipX,
    };

    private static void TakeInto(List<PosterElement> target, List<(PosterElement E, int Layer)> all,
        Func<PosterShape, bool> match, int max, Action<PosterElement> fix)
    {
        foreach (var e in all.Where(t => match(t.E.Shape)).Select(t => t.E).Take(max))
        {
            fix(e);
            target.Add(e);
        }
    }

    /// <summary>Moves x to the nearest position at least `gap` from `obstacle`, inside
    /// bounds. Shifts slightly PAST the gap so repeated validation never re-shifts
    /// (float-safe idempotence).</summary>
    private static double ShiftClear(double x, double obstacle, double gap, double min, double max)
    {
        var margin = gap * 1.03;
        var left = obstacle - margin;
        var right = obstacle + margin;
        var pickLeft = x <= obstacle ? left >= min : right > max;
        var shifted = pickLeft ? left : right;
        return Math.Clamp(shifted, min, max);
    }

    private static void EnforceMotifBudget(List<PosterElement> sky, List<PosterElement> mid, List<PosterElement> fore)
    {
        bool Counts(PosterElement e) => !IsScatter(e.Shape) && !IsTerrain(e.Shape)
            && e.Shape != PosterShape.Water && !IsCelestialPrimary(e.Shape) && e.Shape != PosterShape.Stars;

        var motifs = fore.Where(Counts).Concat(mid.Where(Counts)).Concat(sky.Where(Counts)).ToList();
        // Keep order: foreground focal first, then midground, then sky extras — extras drop last-first.
        foreach (var drop in motifs.Skip(MotifBudget))
        {
            sky.Remove(drop);
            mid.Remove(drop);
            fore.Remove(drop);
        }
    }

    private static void EnforceWarmBudget(List<PosterElement> mid, List<PosterElement> fore)
    {
        var warm = fore.Concat(mid).Where(e => IsWarmShape(e.Shape)).ToList();
        foreach (var drop in warm.Skip(WarmAccentBudget))
        {
            // A lighthouse just loses its beam at resolve time; lit structures are dropped.
            if (drop.Shape == PosterShape.Lighthouse) continue;
            mid.Remove(drop);
            fore.Remove(drop);
        }
    }

    public ResolvedPoster Resolve(PosterSpec spec)
    {
        var valid = Validate(spec);
        var ramp = Ramps[valid.Palette];
        var rng = new DeterministicRandom(valid.Seed);
        var skyTop = Lerp(ramp[0], "#070610", 0.42);
        var skyBottom = Lerp(ramp[0], Cream, 0.14);
        var ink = Lerp(ramp[3], "#000000", 0.45);

        var moon = valid.Layers[0].Elements.FirstOrDefault(e => IsCelestialPrimary(e.Shape));
        var water = valid.Layers[2].Elements.FirstOrDefault(e => e.Shape == PosterShape.Water);

        // The single amber accent: fox → lit structure → lighthouse beam.
        var warmOrdered = valid.Layers[3].Elements.Concat(valid.Layers[2].Elements)
            .Where(e => IsWarmShape(e.Shape))
            .OrderBy(e => e.Shape == PosterShape.FoxSilhouette ? 0 : e.Shape == PosterShape.Lighthouse ? 2 : 1)
            .ToList();
        var accent = warmOrdered.FirstOrDefault();

        // Terrain depth stagger: farthest crest (smallest y) is lightest.
        var terrainColor = new Dictionary<PosterElement, string>();
        var terrainOrdered = valid.Layers[1].Elements
            .Where(e => IsTerrain(e.Shape) && e.Shape != PosterShape.Path)
            .OrderBy(e => e.Y).ToList();
        for (var i = 0; i < terrainOrdered.Count; i++)
            terrainColor[terrainOrdered[i]] = ramp[Math.Min(1 + i, 3)];
        var nearestTerrain = terrainOrdered.Count == 0 ? ramp[2] : terrainColor[terrainOrdered[^1]];

        var layers = new List<ResolvedPosterLayer>(LayerCount);
        for (var depth = 0; depth < LayerCount; depth++)
        {
            var elements = new List<ResolvedPosterElement>();
            foreach (var element in valid.Layers[depth].Elements)
            {
                switch (element.Shape)
                {
                    case PosterShape.Stars:
                        ScatterStars(elements, element, moon, rng);
                        break;
                    case PosterShape.StarCluster:
                        ScatterStarCluster(elements, element, moon, rng);
                        break;
                    case PosterShape.Snowfall:
                        ScatterSnow(elements, element, rng);
                        break;
                    case PosterShape.FireflyScatter:
                        ScatterFireflies(elements, element, rng);
                        break;
                    case PosterShape.FlowerScatter:
                        ScatterFlowers(elements, element, ramp, rng);
                        break;
                    case PosterShape.Cloud:
                        ScatterClouds(elements, element, ramp, rng);
                        break;
                    default:
                        elements.Add(ResolveSingle(element, depth, ramp, ink, accent, terrainColor, nearestTerrain));
                        break;
                }
            }

            // Moonglade: a soft mirrored moon shimmer on the water, drawn after the band.
            if (depth == 2 && water is not null && moon is not null)
            {
                var waterIdx = elements.FindIndex(e => e.Shape == PosterShape.Water);
                if (waterIdx >= 0)
                {
                    elements.Insert(waterIdx + 1, new ResolvedPosterElement
                    {
                        Shape = moon.Shape,
                        X = moon.X,
                        Y = Math.Min(water.Y + 0.015, 0.97),   // hangs straight off the waterline
                        Scale = moon.Scale * 0.9,
                        ColorHex = Cream,
                        Alpha = 0.28,
                        Glow = 0.4,
                        FlipY = true,
                    });
                }
            }

            layers.Add(new ResolvedPosterLayer
            {
                Depth = depth,
                BackgroundHex = depth == 0 ? ramp[0] : "transparent",
                BackgroundTopHex = depth == 0 ? skyTop : null,
                BackgroundBottomHex = depth == 0 ? skyBottom : null,
                ParallaxTravel = ParallaxTravelByDepth[depth],
                Elements = elements,
            });
        }

        return new ResolvedPoster { Layers = layers, BaseHex = ramp[0] };
    }

    // ---------------- scatters (all seeded, all deterministic) ----------------

    /// <summary>Seeded star sprinkle: 4–14 dots in the upper sky, never over the moon disc.
    /// A few dots render larger (the renderer gives those a 4-point sparkle).</summary>
    private static void ScatterStars(List<ResolvedPosterElement> elements, PosterElement stars,
        PosterElement? moon, DeterministicRandom rng)
    {
        var count = StarsMin + (int)Math.Round(stars.Density * (StarsMax - StarsMin));
        var placed = 0;
        var attempts = 0;
        while (placed < count && attempts < count * 8)
        {
            attempts++;
            var stratum = placed % count;
            var x = 0.06 + (stratum + rng.NextDouble()) / count * 0.88;
            var y = 0.04 + rng.NextDouble() * (StarFieldMaxY - 0.04);
            if (moon is not null)
            {
                var dx = x - moon.X;
                var dy = y - moon.Y;
                if (Math.Sqrt(dx * dx + dy * dy) < moon.Scale * 0.95) continue;
            }
            var bright = rng.NextDouble() > 0.8;
            elements.Add(new ResolvedPosterElement
            {
                Shape = PosterShape.Stars,
                X = x,
                Y = y,
                Scale = stars.Scale * (bright ? 1.35 : 0.5 + rng.NextDouble() * 0.7),
                ColorHex = Cream,
                Alpha = bright ? 1.0 : 0.55 + rng.NextDouble() * 0.45,
            });
            placed++;
        }
    }

    /// <summary>A tight knot of 7–12 faint stars — a "little constellation" patch.</summary>
    private static void ScatterStarCluster(List<ResolvedPosterElement> elements, PosterElement cluster,
        PosterElement? moon, DeterministicRandom rng)
    {
        var count = 7 + rng.Next(6);
        for (var i = 0; i < count; i++)
        {
            var angle = rng.NextDouble() * Math.PI * 2;
            var r = cluster.Scale * (0.2 + rng.NextDouble() * 0.9);
            var x = Clamp01(cluster.X + Math.Cos(angle) * r);
            var y = Math.Clamp(cluster.Y + Math.Sin(angle) * r * 0.6, 0.03, StarFieldMaxY);
            if (moon is not null && Math.Sqrt((x - moon.X) * (x - moon.X) + (y - moon.Y) * (y - moon.Y)) < moon.Scale) continue;
            elements.Add(new ResolvedPosterElement
            {
                Shape = PosterShape.Stars,
                X = x,
                Y = y,
                Scale = 0.03 + rng.NextDouble() * 0.03,
                ColorHex = Cream,
                Alpha = 0.4 + rng.NextDouble() * 0.5,
            });
        }
    }

    /// <summary>Slow snow: 18–36 soft dots over the whole canvas, fainter when small.</summary>
    private static void ScatterSnow(List<ResolvedPosterElement> elements, PosterElement snow, DeterministicRandom rng)
    {
        var count = 18 + (int)Math.Round((snow.Density > 0 ? snow.Density : 0.5) * 18);
        for (var i = 0; i < count; i++)
        {
            var size = 0.4 + rng.NextDouble() * 0.8;
            elements.Add(new ResolvedPosterElement
            {
                Shape = PosterShape.Snowfall,
                X = 0.02 + ((i % count) + rng.NextDouble()) / count * 0.96,
                Y = 0.03 + rng.NextDouble() * 0.88,
                Scale = 0.012 * size,
                ColorHex = Cream,
                Alpha = 0.25 + size * 0.4,
            });
        }
    }

    /// <summary>6–12 warm glowing motes hovering over the ground zone.</summary>
    private static void ScatterFireflies(List<ResolvedPosterElement> elements, PosterElement flies, DeterministicRandom rng)
    {
        var count = 6 + (int)Math.Round((flies.Density > 0 ? flies.Density : 0.5) * 6);
        for (var i = 0; i < count; i++)
        {
            elements.Add(new ResolvedPosterElement
            {
                Shape = PosterShape.FireflyScatter,
                X = Clamp01(flies.X + (rng.NextDouble() - 0.5) * 0.8),
                // Hover close to the anchor's ground band — never drift up into the sky.
                Y = Math.Clamp(flies.Y + (rng.NextDouble() - 0.5) * 0.22, Math.Max(0.5, flies.Y - 0.05), 0.92),
                Scale = 0.008 + rng.NextDouble() * 0.008,
                ColorHex = WarmGlow,
                Glow = 0.8,
                GlowColorHex = Amber,
                Alpha = 0.6 + rng.NextDouble() * 0.4,
            });
        }
    }

    /// <summary>8–14 tiny pale blossoms sprinkled along the foreground ground band.</summary>
    private static void ScatterFlowers(List<ResolvedPosterElement> elements, PosterElement flowers,
        string[] ramp, DeterministicRandom rng)
    {
        var count = 8 + (int)Math.Round((flowers.Density > 0 ? flowers.Density : 0.5) * 6);
        for (var i = 0; i < count; i++)
        {
            elements.Add(new ResolvedPosterElement
            {
                Shape = PosterShape.FlowerScatter,
                X = 0.04 + ((i % count) + rng.NextDouble()) / count * 0.92,
                Y = Math.Clamp(flowers.Y + rng.NextDouble() * (0.96 - flowers.Y), flowers.Y, 0.96),
                Scale = 0.010 + rng.NextDouble() * 0.010,
                ColorHex = Lerp(Cream, ramp[0], 0.25),
                Alpha = 0.5 + rng.NextDouble() * 0.5,
            });
        }
    }

    /// <summary>1–2 soft cloud clusters in the mid-upper sky.</summary>
    private static void ScatterClouds(List<ResolvedPosterElement> elements, PosterElement cloud,
        string[] ramp, DeterministicRandom rng)
    {
        var count = 1 + (cloud.Density > 0.25 ? 1 : 0);
        for (var i = 0; i < count; i++)
        {
            elements.Add(new ResolvedPosterElement
            {
                Shape = PosterShape.Cloud,
                X = Clamp01(cloud.X + (i == 0 ? 0 : (rng.NextDouble() - 0.5) * 0.5)),
                Y = Math.Clamp(cloud.Y + (i == 0 ? 0 : 0.08 + rng.NextDouble() * 0.06), 0.12, 0.5),
                Scale = cloud.Scale * (i == 0 ? 1.0 : 0.65),
                ColorHex = Lerp(ramp[0], Cream, 0.22 - cloud.Tint * 0.10),
                Alpha = 0.9,
                FlipX = i == 1,
            });
        }
    }

    // ---------------- single-element resolution ----------------

    private static ResolvedPosterElement ResolveSingle(PosterElement element, int depth, string[] ramp,
        string ink, PosterElement? accent, Dictionary<PosterElement, string> terrainColor, string nearestTerrain)
    {
        var isAccent = ReferenceEquals(element, accent);
        string color;
        string? secondary = null;
        string? glowColor = null;
        var glow = element.Glow;

        switch (element.Shape)
        {
            case PosterShape.Moon or PosterShape.Crescent:
                color = Cream;
                glowColor = Lerp(Cream, Amber, 0.25);
                break;
            case PosterShape.ShootingStar:
                color = Cream;
                break;
            case PosterShape.Aurora:
                color = Lerp(ramp[0], AuroraTeal, 0.55);
                break;
            case PosterShape.MistBand:
                color = Lerp(ramp[0], Cream, 0.20);
                break;
            case PosterShape.BirdFlock:
                color = Lerp(ink, ramp[0], element.Tint * 0.7 + 0.15);
                break;
            case PosterShape.Hills or PosterShape.Mountain or PosterShape.ForestRow:
                color = terrainColor.TryGetValue(element, out var tc) ? tc : ramp[Math.Min(depth + 1, 3)];
                if (element.Shape == PosterShape.Mountain) secondary = Lerp(color, Cream, 0.35); // faint snow cap
                break;
            case PosterShape.Path:
                color = Lerp(nearestTerrain, Cream, 0.16);
                break;
            case PosterShape.Water:
                color = ramp[Math.Min(depth + 1, 3)];
                secondary = Lerp(color, Cream, 0.14);
                break;
            case PosterShape.Reeds:
                color = Lerp(ramp[3], ink, 0.5);
                break;
            case PosterShape.Pine or PosterShape.RoundTree:
                color = depth >= 3 ? ink : ramp[Math.Min(depth + 1, 3)];
                break;
            case PosterShape.FoxSilhouette:
                color = isAccent ? Amber : ink;
                glow = 0;
                break;
            case PosterShape.Rabbit or PosterShape.Owl or PosterShape.DeerSilhouette:
                color = ink;
                secondary = element.Shape == PosterShape.Owl ? Cream : null; // two moonlit eyes
                glow = 0;
                break;
            case PosterShape.WhaleTail:
                color = Lerp(ramp[3], "#000000", 0.30);
                glow = 0;
                break;
            case PosterShape.Lighthouse:
                color = Lerp(ramp[0], Cream, 0.85);
                secondary = isAccent ? Amber : null; // beam only when it owns the warm accent
                glow = isAccent ? Math.Max(glow, 0.4) : 0;
                glowColor = WarmGlow;
                break;
            case PosterShape.WindowGlow:
                color = isAccent ? Amber : Lerp(Amber, ramp[3], 0.35);
                secondary = ramp[3]; // cottage silhouette
                glow = Math.Max(glow, 0.5);
                glowColor = Amber;
                break;
            case PosterShape.Lantern:
                color = ink;                          // post
                secondary = isAccent ? Amber : WarmGlow; // pane
                glow = Math.Max(glow, 0.55);
                glowColor = Amber;
                break;
            case PosterShape.Tent:
                color = Lerp(ramp[3], ink, 0.4);
                secondary = isAccent ? Amber : WarmGlow; // door glow
                glow = Math.Max(glow, 0.35);
                glowColor = Amber;
                break;
            case PosterShape.Boat or PosterShape.Sailboat:
                color = Lerp(ramp[3], Cream, 0.45);
                secondary = element.Shape == PosterShape.Sailboat ? Lerp(Cream, ramp[0], 0.25) : null; // sail
                glow = 0;
                break;
            default:
                color = ramp[Math.Min(depth + 1, 3)];
                break;
        }

        // Distance haze: tint pulls the element (and its secondary) toward the sky tone,
        // so details like a snow cap fade WITH their body instead of floating over it.
        if (element.Tint > 0)
        {
            color = Lerp(color, ramp[0], element.Tint * 0.8);
            if (secondary is not null) secondary = Lerp(secondary, ramp[0], element.Tint * 0.8);
        }

        return new ResolvedPosterElement
        {
            Shape = element.Shape,
            X = element.X,
            Y = element.Y,
            Scale = element.Scale,
            Density = element.Density,
            ColorHex = color,
            SecondaryColorHex = secondary,
            Glow = glow,
            GlowColorHex = glowColor,
            FlipX = element.FlipX,
        };
    }

    private static double Clamp01(double v) => double.IsFinite(v) ? Math.Clamp(v, 0, 1) : 0;

    public static string Lerp(string fromHex, string toHex, double t)
    {
        var (r1, g1, b1) = ParseHex(fromHex);
        var (r2, g2, b2) = ParseHex(toHex);
        int L(int a, int b) => (int)Math.Round(a + (b - a) * t);
        return $"#{L(r1, r2):X2}{L(g1, g2):X2}{L(b1, b2):X2}";
    }

    private static (int R, int G, int B) ParseHex(string hex)
    {
        var h = hex.TrimStart('#');
        return (Convert.ToInt32(h[..2], 16), Convert.ToInt32(h[2..4], 16), Convert.ToInt32(h[4..6], 16));
    }

    /// <summary>Small xorshift PRNG — identical sequence on every platform/runtime,
    /// unlike System.Random whose algorithm is not contractually stable.</summary>
    internal sealed class DeterministicRandom
    {
        private ulong _state;

        public DeterministicRandom(int seed) => _state = (ulong)(seed == 0 ? 88172645463325252L : seed) * 2685821657736338717UL + 1;

        public ulong NextULong()
        {
            _state ^= _state << 13;
            _state ^= _state >> 7;
            _state ^= _state << 17;
            return _state;
        }

        public double NextDouble() => (NextULong() >> 11) * (1.0 / (1UL << 53));

        public int Next(int maxExclusive) => (int)(NextULong() % (ulong)maxExclusive);
    }
}
