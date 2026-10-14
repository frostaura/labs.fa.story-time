using StoryTime.Contracts;
using StoryTime.Poster.Color;
using StoryTime.Poster.Core;
using StoryTime.Poster.Spec;

namespace StoryTime.Poster.Composition;

/// <summary>Turns a spec into a <see cref="PosterPlan"/>. Composition rules, all binding:
///
///   * The horizon sits on (or just below) the lower third — the sky is the poster's calm
///     negative space, and its top band is reserved for a title (TitleSafe).
///   * The focal subject stands on a vertical third; the moon takes the opposite third, in
///     the upper-middle sky, never inside the title band and never behind anything tall.
///   * Depth is four bands: sky (0) → far (1) → mid (2) → near (3), lighter and hazier
///     toward the horizon; foreground silhouettes frame the edges, not the centre.
///   * One warm light family (amber), small and few. Nothing scary, nothing loud.
///
/// Every random choice comes from a labelled fork of the spec seed, so changing how one
/// layer is built never reshuffles another (and goldens stay stable per layer).</summary>
public static class ScenePlanner
{
    public static readonly NormRect TitleSafe = new(0.10, 0.05, 0.90, 0.27);

    public static PosterPlan Plan(PosterSpec input)
    {
        var spec = PosterSpecRules.Normalize(input);
        var b = new Builder(spec);
        b.Build();
        return b.ToPlan();
    }

    private sealed class Builder
    {
        private readonly PosterSpec _spec;
        private readonly ScenePalette _c;
        private readonly Rng _root;
        private readonly int _side;
        private readonly double _focalX;
        private readonly PosterLayout _layout;
        private readonly List<TerrainBand> _bands = [];
        private readonly List<Placement> _props = [];
        private readonly List<AmbientParticle> _ambient = [];
        private readonly List<CloudPlan> _clouds = [];
        private List<StarPlan> _stars = [];
        private double _horizon;
        private WaterPlan? _water;
        private MoonPlan? _moon;
        private MilkyWayPlan? _milky;
        private AuroraPlan? _aurora;
        private ShootingStarPlan? _shooting;
        private Placement? _focal;
        private bool _underwater;
        private bool _snowing;
        private bool _fireflies;

        public Builder(PosterSpec spec)
        {
            _spec = spec;
            _c = ScenePalette.Build(spec);
            _root = new Rng(spec.Seed);
            var r = _root.Fork("composition");
            _side = r.Chance(0.5) ? 1 : -1;
            // Two or three composition archetypes per scene, chosen by seed.
            var layouts = AllowedLayouts(spec.Scene);
            _layout = layouts[R("layout").Int(layouts.Length)];
            _focalX = _layout == PosterLayout.Center
                ? 0.5 + r.Jitter(0.02)
                : 0.5 + _side * (1.0 / 6.0) + r.Jitter(0.025);
        }

        private static PosterLayout[] AllowedLayouts(PosterScene scene) => scene switch
        {
            PosterScene.ReefAtDusk or PosterScene.CloudKingdom => [PosterLayout.Third, PosterLayout.Center],
            PosterScene.JungleCanopy or PosterScene.IslandCove => [PosterLayout.Third, PosterLayout.Center],
            _ => [PosterLayout.Third, PosterLayout.Center, PosterLayout.High],
        };

        /// <summary>The scene's horizon for this layout: the centred hero is seen from low
        /// (more sky above it), the high vantage looks out across the land.</summary>
        private double H(double baseY) => _layout switch
        {
            PosterLayout.Center => Math.Min(0.75, baseY + 0.03),
            PosterLayout.High => baseY - 0.08,
            _ => baseY,
        };

        /// <summary>Hero target height as a fraction of the poster (what is actually drawn).</summary>
        private double HeroHeight(Rng r) => _layout == PosterLayout.Center ? r.Range(0.27, 0.32) : r.Range(0.25, 0.3);

        private double HeroSize(Prop prop, int variant, Rng r) =>
            Math.Min(0.75, HeroHeight(r) / StoryTime.Poster.Render.PropPainter.HeightFactor(prop, variant));

        private Rng R(string label) => _root.Fork(label);
        private bool Has(PosterFeature f) => _spec.Features.Contains(f);
        private bool CreatureListed => _spec.Features.Any(PosterSpecRules.IsCreature);
        private uint Seed(string label) => (uint)(R(label).NextULong() & 0xFFFFFFFF);

        public void Build()
        {
            switch (_spec.Scene)
            {
                case PosterScene.MoonlitSea: MoonlitSea(); break;
                case PosterScene.ForestClearing: ForestClearing(); break;
                case PosterScene.MountainLake: MountainLake(); break;
                case PosterScene.FireflyMeadow: FireflyMeadow(); break;
                case PosterScene.SnowyVillage: SnowyVillage(); break;
                case PosterScene.DesertOasis: DesertOasis(); break;
                case PosterScene.IslandCove: IslandCove(); break;
                case PosterScene.StarryHills: StarryHills(); break;
                case PosterScene.ReefAtDusk: ReefAtDusk(); break;
                case PosterScene.CloudKingdom: CloudKingdom(); break;
                case PosterScene.TownRooftops: TownRooftops(); break;
                case PosterScene.JungleCanopy: JungleCanopy(); break;
            }
            PlaceFeatures();
            PlaceDefaultHero();
            PlaceFraming();
            ClearHeroZone();
            EnsureWarmLight();
            SeparateSubjects();
            FitHeroInFrame();
            KeepLightsOffHero();
            KeepHeroBelowTitle();
            PlanSky();
            PlanAmbient();
        }

        public PosterPlan ToPlan() => new()
        {
            Spec = _spec,
            Colors = _c,
            Horizon = _horizon,
            Side = _side,
            Layout = _layout,
            Moon = _moon,
            Stars = _stars,
            MilkyWay = _milky,
            Clouds = _clouds,
            Aurora = _aurora,
            ShootingStar = _shooting,
            Bands = _bands,
            Water = _water,
            // Within a layer: framing clusters first (lights, heroes and flowers stand in front of
            // them), then by base line.
            Placements = _props.OrderBy(p => p.Depth).ThenBy(p => p.Prop == Prop.FrameCluster ? 0 : 1).ThenBy(p => p.Y).ToList(),
            Ambient = _ambient,
            Lights = _props.Where(p => p.Lit && p.Prop is not Prop.Train).Select(LightFor).ToList(),
            Focal = _focal,
            TitleSafe = TitleSafe,
            Underwater = _underwater,
        };

        // ------------------------------------------------------------------ helpers

        private TerrainBand Band(int depth, TerrainProfile profile, double tone, TerrainStyle style,
            double fog = 0, bool rim = true, bool snowCaps = false)
        {
            var band = new TerrainBand { Depth = depth, Profile = profile, Tone = tone, Style = style, Fog = fog, Rim = rim, SnowCaps = snowCaps };
            _bands.Add(band);
            return band;
        }

        private TerrainProfile Hills(double baseY, double amp, double freq, string label, int octaves = 3,
            Func<double, double>? envelope = null, double? floor = null) => new()
        {
            Kind = TerrainKind.Hills, Base = baseY, Amp = amp, Freq = freq, Phase = R(label).Range(0, 50),
            Seed = Seed(label), Octaves = octaves, Envelope = envelope, Floor = floor,
        };

        private TerrainProfile Mountains(double baseY, double amp, double freq, string label,
            Func<double, double>? envelope = null, double? floor = null) => new()
        {
            Kind = TerrainKind.Mountains, Base = baseY, Amp = amp, Freq = freq, Phase = R(label).Range(0, 50),
            Seed = Seed(label), Octaves = 5, Envelope = envelope, Floor = floor,
        };

        private TerrainProfile Dunes(double baseY, double amp, double freq, string label) => new()
        {
            Kind = TerrainKind.Dunes, Base = baseY, Amp = amp, Freq = freq, Phase = R(label).Range(0, 50), Seed = Seed(label),
        };

        /// <summary>A row of circular bumps (cloud banks, canopy crowns, reef mounds).</summary>
        private TerrainProfile Bumps(double baseY, double rMin, double rMax, double heightK, string label,
            Func<double, double>? envelope = null)
        {
            var r = R(label);
            var bumps = new List<(double, double, double)>();
            var x = -0.45 + r.Range(0, rMax);
            while (x < 1.45)
            {
                var radius = r.Range(rMin, rMax);
                bumps.Add((x, radius, heightK * r.Range(0.75, 1.0)));
                x += radius * r.Range(0.85, 1.25);
            }
            return new TerrainProfile { Kind = TerrainKind.Bumps, Base = baseY, Bumps = bumps, Envelope = envelope };
        }

        private static Func<double, double> SideEnvelope(int side, double reach, double soft = 0.12) =>
            side > 0
                ? x => Noise.SmoothStep(1 - reach - soft, 1 - reach + soft, x)
                : x => 1 - Noise.SmoothStep(reach - soft, reach + soft, x);

        private static Func<double, double> Window(double center, double halfWidth, double soft = 0.08) =>
            x => Noise.SmoothStep(center - halfWidth - soft, center - halfWidth + soft, x)
                 * (1 - Noise.SmoothStep(center + halfWidth - soft, center + halfWidth + soft, x));

        private Placement Place(Prop prop, int depth, double x, double y, double size, string label,
            bool? flip = null, int variant = 0, PosterFeature? feature = null, bool lit = false, double tone = -1)
        {
            var r = R(label);
            var p = new Placement(prop, depth, x, y, size, flip ?? r.Chance(0.5), variant, (uint)(r.NextULong() & 0xFFFFFFFF),
                feature, lit, tone);
            _props.Add(p);
            return p;
        }

        /// <summary>The x nearest to <paramref name="x"/> where the profile has real ground
        /// (partial bands — a headland, an island shore — sink below the frame elsewhere).</summary>
        private double GroundedX(TerrainProfile ground, double x)
        {
            // "Real ground" = solidly above the shoreline fade, with room for a footprint.
            var solid = Math.Min(0.92, _horizon + 0.22);
            bool Ok(double v) => v is >= 0.12 and <= 0.88 && ground.Y(v - 0.09) < solid && ground.Y(v) < solid && ground.Y(v + 0.09) < solid
                                 && !_props.Any(p => p.Prop == Prop.Pool && Math.Abs(v - p.X) < p.Size * 0.62)
                                 && !_props.Any(p => p.Prop == Prop.Palm && p.Depth == 2 && Math.Abs(v - p.X) < 0.07)
                                 && (_water is null || ground.Y(v - 0.09) < _horizon + 0.16 && ground.Y(v + 0.09) < _horizon + 0.16);
            if (Ok(x)) return x;
            for (var d = 0.02; d < 0.9; d += 0.02)
            {
                if (Ok(x + d)) return x + d;
                if (Ok(x - d)) return x - d;
            }
            return x;
        }

        private TerrainProfile Ground(int depth) =>
            _bands.LastOrDefault(b => b.Depth == depth)?.Profile
            ?? _bands.LastOrDefault(b => b.Depth < depth)?.Profile
            ?? new TerrainProfile { Kind = TerrainKind.Flat, Base = 0.92 };

        /// <summary>Scatters props along a band, skipping the keep-out spans.</summary>
        private void Scatter(Prop prop, int depth, TerrainProfile ground, double x0, double x1, double spacing,
            double sizeMin, double sizeMax, string label, double tone, (double A, double B)[]? keepOut = null,
            int variant = 0, double sink = 0.004)
        {
            var r = R(label);
            var x = x0 + r.Range(0, spacing);
            var i = 0;
            while (x < x1)
            {
                var blocked = keepOut?.Any(k => x > k.A && x < k.B) ?? false;
                if (!blocked && ground.Y(x) < 1.0 && !(spacing < 0.04 && r.Chance(0.2)))
                    Place(prop, depth, x, ground.Y(x) + sink, r.Range(sizeMin, sizeMax), $"{label}-{i}", variant: variant, tone: tone);
                x += spacing * r.Range(0.6, 1.4);
                i++;
            }
        }

        private void Grass(int depth, TerrainProfile ground, double density, string label, (double, double)[]? keepOut = null)
        {
            var r = R(label);
            var n = (int)(10 * density) + r.Int(4);
            for (var i = 0; i < n; i++)
            {
                // Grass gathers toward the edges: framing, never clutter in the middle.
                var t = r.Next();
                var x = t < 0.5 ? r.Range(-0.08, 0.30) : r.Range(0.70, 1.08);
                if (keepOut?.Any(k => x > k.Item1 && x < k.Item2) ?? false) continue;
                Place(Prop.GrassTuft, depth, x, ground.Y(x) + 0.01, r.Range(0.05, 0.10), $"{label}-{i}");
            }
        }

        // ------------------------------------------------------------------ scenes
        //
        // Near bands crest around y 0.86–0.90 so a downward tilt never squeezes the
        // foreground to a sliver; framing silhouettes keep ≥40% of themselves in frame.

        private void MoonlitSea()
        {
            var r = R("sea");
            _horizon = H(0.66) + r.Jitter(0.03);
            var headSide = Has(PosterFeature.Lighthouse) ? _side : (r.Chance(0.5) ? 1 : -1);

            // Far: the open sea, a long low headland on one side, a whisper of an isle.
            Band(1, Hills(_horizon, r.Range(0.045, 0.07), 2.2, "sea-head", 4, SideEnvelope(headSide, r.Range(0.35, 0.5)), _horizon + 0.004),
                0.08, TerrainStyle.Earth, fog: 0.3);
            Band(1, Hills(_horizon, 0.02, 4, "sea-isle", 2, Window(0.5 - headSide * r.Range(0.18, 0.3), 0.07), _horizon + 0.004),
                0.0, TerrainStyle.Earth, fog: 0.2, rim: false);
            _water = new WaterPlan(1, _horizon, Glade: true);

            if (Has(PosterFeature.Lighthouse))
            {
                var cliff = Band(2, Mountains(_horizon + 0.12, r.Range(0.08, 0.1), 2.6, "sea-cliff",
                    SideEnvelope(headSide, 0.32, 0.09), 1.25), 0.55, TerrainStyle.Earth);
                var lx = 0.5 + headSide * r.Range(0.30, 0.35);
                Place(Prop.Lighthouse, 2, lx, cliff.Profile.Y(lx) + 0.006, CreatureListed ? r.Range(0.2, 0.24) : HeroHeight(r) / 1.0, "lighthouse",
                    flip: false, feature: PosterFeature.Lighthouse, lit: true);
            }
            else
            {
                // A low rocky point on the far side: the mid plane is never empty.
                Band(2, Mountains(_horizon + 0.11, r.Range(0.04, 0.06), 3.2, "sea-rocks",
                    SideEnvelope(-headSide, 0.22, 0.06), 1.25), 0.5, TerrainStyle.Earth);
            }

            var shore = Band(3, Hills(0.975, 0.085, 1.5, "sea-shore", 3), 1.0, TerrainStyle.Earth);
            Grass(3, shore.Profile, 1.0, "sea-grass");
            var rx = 0.5 - _side * 0.38;
            Place(Prop.Reeds, 3, rx, shore.Profile.Y(rx) + 0.01, r.Range(0.13, 0.17), "sea-reeds");
        }

        private void ForestClearing()
        {
            var r = R("forest");
            _horizon = H(0.66) + r.Jitter(0.03);
            Band(1, Mountains(_horizon + 0.01, r.Range(0.11, 0.15), 1.5, "forest-mtn"), 0.0, TerrainStyle.Earth, fog: 0.4);
            var treeline = Band(1, Hills(_horizon + 0.035, 0.03, 3, "forest-far"), 0.28, TerrainStyle.Earth, fog: 0.25);
            Scatter(Prop.Pine, 1, treeline.Profile, -0.12, 1.12, 0.028, 0.05, 0.085, "forest-far-pines", 0.28);

            var meadow = Band(2, Hills(_horizon + 0.13, 0.035, 1.8, "forest-meadow"), 0.58, TerrainStyle.Earth);
            var clear = (_focalX - 0.2, _focalX + 0.2);
            Scatter(Prop.Pine, 2, meadow.Profile, -0.15, 0.30, 0.055, 0.19, 0.3, "forest-pines-l", 0.58, [clear]);
            Scatter(Prop.Pine, 2, meadow.Profile, 0.70, 1.15, 0.055, 0.19, 0.3, "forest-pines-r", 0.58, [clear]);

            var floor = Band(3, Hills(0.975, 0.07, 1.4, "forest-floor"), 1.0, TerrainStyle.Earth);
            Grass(3, floor.Profile, 1.0, "forest-grass");
            if (r.Chance(0.6))
            {
                var edge = 0.5 - _side * 0.47; // big framing pine on the moon side, mostly in frame
                Place(Prop.Pine, 3, edge, floor.Profile.Y(edge) + 0.03, r.Range(0.42, 0.5), "forest-frame", variant: 1);
            }
            Place(Prop.Fern, 3, 0.5 - _side * 0.3, floor.Profile.Y(0.5 - _side * 0.3) + 0.012, 0.1, "forest-fern");
        }

        private void MountainLake()
        {
            var r = R("lake");
            _horizon = H(0.64) + r.Jitter(0.03);
            Band(1, Mountains(_horizon, r.Range(0.19, 0.23), 1.3, "lake-mtn-a"), 0.0, TerrainStyle.Earth, fog: 0.35, snowCaps: true);
            Band(1, Mountains(_horizon, r.Range(0.11, 0.14), 2.1, "lake-mtn-b"), 0.18, TerrainStyle.Earth, fog: 0.4, rim: false, snowCaps: true);
            var line = Band(1, Hills(_horizon + 0.004, 0.012, 5, "lake-line"), 0.32, TerrainStyle.Earth, rim: false);
            Scatter(Prop.Pine, 1, line.Profile, -0.12, 1.12, 0.022, 0.035, 0.055, "lake-far-pines", 0.32);
            _water = new WaterPlan(1, _horizon + 0.006, Glade: true);

            // The pine shore takes the hero's third when the hero is a building on land;
            // otherwise the hero is on the water and the shore frames the far side.
            // The hero (always a creature now) stands on the near shore; the pine shore frames
            // the far third, where a cabin or tent the story asked for will stand.
            var shoreSide = -_side;
            var shore = Band(2, Hills(_horizon + 0.15, 0.06, 2.2, "lake-shore", 3, SideEnvelope(shoreSide, 0.36), 1.25), 0.6, TerrainStyle.Earth);
            Scatter(Prop.Pine, 2, shore.Profile, shoreSide > 0 ? 0.72 : -0.15, shoreSide > 0 ? 1.15 : 0.28, 0.05, 0.15, 0.24, "lake-pines", 0.6);

            var near = Band(3, Hills(0.975, 0.08, 1.6, "lake-near"), 1.0, TerrainStyle.Earth);
            var rockX = 0.5 + _side * 0.42;
            Place(Prop.Rock, 3, rockX, near.Profile.Y(rockX) + 0.02, 0.12, "lake-rock");
            Place(Prop.Reeds, 3, 0.5 - _side * 0.40, near.Profile.Y(0.5 - _side * 0.40) + 0.01, 0.14, "lake-reeds");
            Grass(3, near.Profile, 0.6, "lake-grass");
        }

        private void FireflyMeadow()
        {
            var r = R("meadow");
            _horizon = H(0.67) + r.Jitter(0.03);
            Band(1, Hills(_horizon, r.Range(0.06, 0.09), 1.6, "meadow-far"), 0.05, TerrainStyle.Earth, fog: 0.35);
            Band(1, Hills(_horizon + 0.05, 0.06, 2.0, "meadow-far2"), 0.32, TerrainStyle.Earth, fog: 0.3);
            var hill = Band(2, Hills(_horizon + 0.15, r.Range(0.06, 0.08), 1.3, "meadow-hill"), 0.62, TerrainStyle.Earth);
            var tx = 0.5 - _side * r.Range(0.26, 0.32);
            Place(Prop.RoundTree, 2, tx, hill.Profile.Y(tx) + 0.008, r.Range(0.25, 0.3), "meadow-tree");
            var near = Band(3, Hills(0.975, 0.075, 1.5, "meadow-near"), 1.0, TerrainStyle.Earth);
            Grass(3, near.Profile, 1.8, "meadow-grass");
            var bx = 0.5 - _side * 0.44;
            Place(Prop.Bush, 3, bx, near.Profile.Y(bx) + 0.03, r.Range(0.22, 0.28), "meadow-bush");
            _fireflies = true;
        }

        private void SnowyVillage()
        {
            var r = R("snow");
            _horizon = H(0.64) + r.Jitter(0.03);
            Band(1, Mountains(_horizon + 0.01, r.Range(0.16, 0.2), 1.4, "snow-mtn"), 0.0, TerrainStyle.Snow, fog: 0.3, snowCaps: true);
            var slope = Band(2, Hills(_horizon + 0.13, 0.045, 1.5, "snow-slope"), 0.5, TerrainStyle.Snow);
            // The village gathers on the side away from the focal subject.
            var vc = 0.5 - _side * r.Range(0.08, 0.18);
            var count = 3 + r.Int(3);
            var w = 0.11;
            var start = vc - (count - 1) * w / 2;
            var litIndex = r.Int(count);
            for (var i = 0; i < count; i++)
            {
                var hx = start + i * w + r.Jitter(0.015);
                Place(Prop.House, 2, hx, slope.Profile.Y(hx) + 0.012, r.Range(0.10, 0.14), $"snow-house-{i}",
                    variant: r.Int(3), lit: i == litIndex || r.Chance(0.3), feature: PosterFeature.Village);
            }
            Scatter(Prop.Pine, 2, slope.Profile, -0.12, start - 0.06, 0.05, 0.12, 0.2, "snow-pines-a", 0.5, variant: 2);
            Scatter(Prop.Pine, 2, slope.Profile, start + count * w, 1.12, 0.06, 0.12, 0.2, "snow-pines-b", 0.5,
                [(_focalX - 0.1, _focalX + 0.1)], variant: 2);
            var drift = Band(3, Hills(0.97, 0.07, 1.3, "snow-drift"), 1.0, TerrainStyle.Snow);
            var fx = 0.5 - _side * 0.30;
            Place(Prop.Fence, 3, fx, drift.Profile.Y(fx) + 0.012, 0.26, "snow-fence", flip: _side > 0);
            if (r.Chance(0.6))
            {
                var edge = 0.5 + _side * 0.47;
                Place(Prop.Pine, 3, edge, drift.Profile.Y(edge) + 0.03, r.Range(0.46, 0.54), "snow-frame", variant: 3);
            }
            _snowing = Has(PosterFeature.Snowfall) || r.Chance(0.6);
        }

        private void DesertOasis()
        {
            var r = R("desert");
            _horizon = H(0.67) + r.Jitter(0.03);
            Band(1, Dunes(_horizon, r.Range(0.06, 0.09), 1.4, "desert-far"), 0.05, TerrainStyle.Sand, fog: 0.3);
            Band(1, Dunes(_horizon + 0.06, 0.06, 1.9, "desert-far2"), 0.32, TerrainStyle.Sand, fog: 0.25);
            var mid = Band(2, Dunes(_horizon + 0.16, 0.05, 1.3, "desert-mid"), 0.6, TerrainStyle.Sand);
            var ox = 0.5 - _side * (_layout == PosterLayout.Center ? r.Range(0.33, 0.37) : r.Range(0.12, 0.2));
            Place(Prop.Pool, 2, ox, mid.Profile.Y(ox) + 0.025, r.Range(0.2, 0.25), "oasis-pool", flip: false);
            // Palms root together on the far bank and fan apart at the crowns.
            var palms = 1 + r.Int(3);
            var bank = ox + (r.Chance(0.5) ? 1 : -1) * r.Range(0.12, 0.15);   // on the bank beside the pool
            for (var i = 0; i < palms; i++)
            {
                var px = bank + (i - (palms - 1) / 2.0) * 0.035;
                Place(Prop.Palm, 2, px, mid.Profile.Y(px) + 0.008, r.Range(0.22, 0.3), $"oasis-palm-{i}", flip: px < bank);
            }
            Place(Prop.Reeds, 2, ox + 0.1, mid.Profile.Y(ox + 0.1) + 0.02, 0.07, "oasis-reeds");
            Band(3, Dunes(0.99, 0.12, 1.1, "desert-near"), 1.0, TerrainStyle.Sand);
        }

        private void IslandCove()
        {
            var r = R("cove");
            _horizon = H(0.64) + r.Jitter(0.03);
            Band(1, Hills(_horizon, 0.02, 4, "cove-isle", 2, Window(0.5 + _side * r.Range(0.12, 0.25), 0.06), _horizon + 0.004),
                0.0, TerrainStyle.Earth, fog: 0.2, rim: false);
            _water = new WaterPlan(1, _horizon, Glade: true);
            var isleSide = _side;   // the island shares the subject's third; the moon gets open sky
            var isle = Band(2, Hills(_horizon + 0.09, 0.07, 2.0, "cove-island", 3, SideEnvelope(isleSide, 0.32, 0.18), 1.25),
                0.66, TerrainStyle.Earth);
            var palms = 2 + r.Int(2);
            for (var i = 0; i < palms; i++)
            {
                var px = 0.5 + isleSide * (0.33 + i * 0.05 + r.Jitter(0.015));
                if (isle.Profile.Y(px) > _horizon + 0.07) continue;   // palms only on the island itself
                Place(Prop.Palm, 2, px, isle.Profile.Y(px) - 0.004, r.Range(0.2, 0.27), $"cove-palm-{i}", flip: isleSide > 0 == (i % 2 == 0));
            }
            var beach = Band(3, Hills(0.98, 0.06, 1.3, "cove-beach", 2), 1.0, TerrainStyle.Sand);
            Grass(3, beach.Profile, 0.5, "cove-grass");
            if (r.Chance(0.35))
            {
                var px0 = 0.5 - _side * 0.46;
                Place(Prop.Palm, 3, px0, beach.Profile.Y(px0) + 0.03, r.Range(0.48, 0.55), "cove-frame-palm", flip: _side > 0);
            }
        }

        private void StarryHills()
        {
            var r = R("hills");
            _horizon = H(0.71) + r.Jitter(0.03);
            Band(1, Hills(_horizon, 0.05, 1.7, "hills-far"), 0.05, TerrainStyle.Earth, fog: 0.35);
            Band(1, Hills(_horizon + 0.04, 0.06, 1.4, "hills-far2"), 0.3, TerrainStyle.Earth, fog: 0.25);
            var hill = Band(2, Hills(_horizon + 0.13, r.Range(0.08, 0.11), 1.1, "hills-mid"), 0.62, TerrainStyle.Earth);
            if (!Has(PosterFeature.Tent) && !Has(PosterFeature.Telescope) && !Has(PosterFeature.Windmill) && r.Chance(0.6))
            {
                var tx = 0.5 - _side * r.Range(0.22, 0.3);
                Place(Prop.RoundTree, 2, tx, hill.Profile.Y(tx) + 0.008, r.Range(0.2, 0.26), "hills-tree");
            }
            var near = Band(3, Hills(0.975, 0.07, 1.5, "hills-near"), 1.0, TerrainStyle.Earth);
            Grass(3, near.Profile, 0.8, "hills-grass");
        }

        private void ReefAtDusk()
        {
            var r = R("reef");
            _underwater = true;
            _horizon = H(0.73) + r.Jitter(0.03);
            Band(1, Bumps(_horizon, 0.04, 0.09, 0.8, "reef-far"), 0.05, TerrainStyle.Reef, fog: 0.3, rim: false);
            var mid = Band(2, Bumps(_horizon + 0.12, 0.05, 0.11, 0.7, "reef-mid"), 0.55, TerrainStyle.Reef, fog: 0.2);
            Scatter(Prop.BranchCoral, 2, mid.Profile, -0.05, 1.05, 0.16, 0.10, 0.16, "reef-corals", 0.55,
                [(_focalX - 0.12, _focalX + 0.12)], sink: 0.006);
            var fanX = 0.5 - _side * 0.3;
            Place(Prop.FanCoral, 2, fanX, mid.Profile.Y(fanX) + 0.006, r.Range(0.17, 0.22), "reef-fan");
            var floor = Band(3, Bumps(0.99, 0.05, 0.12, 0.7, "reef-floor"), 1.0, TerrainStyle.Reef);
            Place(Prop.Kelp, 3, 0.5 - _side * 0.44, floor.Profile.Y(0.5 - _side * 0.44) + 0.006, r.Range(0.5, 0.6), "reef-kelp-a");
            Place(Prop.Kelp, 3, 0.5 + _side * 0.46, floor.Profile.Y(0.5 + _side * 0.46) + 0.006, r.Range(0.36, 0.44), "reef-kelp-b");
            Place(Prop.BranchCoral, 3, 0.5 + _side * 0.33, floor.Profile.Y(0.5 + _side * 0.33) + 0.006, r.Range(0.13, 0.17), "reef-near-coral");
        }

        private void CloudKingdom()
        {
            var r = R("clouds");
            _horizon = H(0.70) + r.Jitter(0.03);
            Band(1, Bumps(_horizon + 0.03, 0.03, 0.1, 1.0, "ck-far"), 0.1, TerrainStyle.Cloud, fog: 0.2);
            Band(2, Bumps(_horizon + 0.18, 0.05, 0.16, 1.0, "ck-mid"), 0.5, TerrainStyle.Cloud, fog: 0.1);
            Band(3, Bumps(1.05, 0.09, 0.22, 1.0, "ck-near"), 0.85, TerrainStyle.Cloud);
        }

        private void TownRooftops()
        {
            var r = R("town");
            _horizon = H(0.64) + r.Jitter(0.03);
            Band(1, Roofs(_horizon + 0.04, 0.06, 0.11, 0.05, 0.10, "town-far", tower: true), 0.12, TerrainStyle.Roofs, fog: 0.3, rim: false);
            Band(2, Roofs(_horizon + 0.17, 0.10, 0.17, 0.08, 0.16, "town-mid", tower: false), 0.55, TerrainStyle.Roofs, fog: 0.1);
            // The near roof: a long, gently sagging ridge across the bottom.
            var near = Band(3, Hills(0.93, 0.035, 1.1, "town-near", 2), 1.0, TerrainStyle.Roofs);
            var cx = 0.5 - _side * 0.44;
            Place(Prop.Trunk, 3, cx, near.Profile.Y(cx) + 0.012, 0.16, "town-chimney", variant: 1);
        }

        private TerrainProfile Roofs(double wallTop, double wMin, double wMax, double roofMin, double roofMax, string label, bool tower)
        {
            var r = R(label);
            var list = new List<Building>();
            var x = -0.3 + r.Range(0, 0.05);
            var towerUsed = false;
            var lit = tower ? 0.07 : 0.16;
            while (x < 1.3)
            {
                var w = r.Range(wMin, wMax);
                var top = wallTop + r.Jitter(0.025);
                var roof = Math.Clamp(w * r.Range(0.3, 0.5), roofMin * 0.5, roofMax);
                var kind = tower && !towerUsed && x > 0.15 && x < 0.85 && r.Chance(0.25) ? RoofKind.Dome
                    : r.Chance(0.15) ? RoofKind.Flat : RoofKind.Gable;
                if (kind == RoofKind.Dome) { towerUsed = true; top -= 0.035; roof = w * 0.45; }
                list.Add(new Building(x, x + w, top, roof, kind, r.Chance(0.5), r.Range(0.2, 0.8), 1 + r.Int(3),
                    (uint)(r.NextULong() & 0xFFFFFFFF), lit));
                x += w - r.Range(0.002, 0.012); // overlapping walls: never a sliver of sky between houses
            }
            return new TerrainProfile { Kind = TerrainKind.Rooftops, Base = wallTop + 0.05, Buildings = list };
        }

        private void JungleCanopy()
        {
            var r = R("jungle");
            _horizon = H(0.64) + r.Jitter(0.03);
            Band(1, Bumps(_horizon, 0.02, 0.045, 0.95, "jungle-far"), 0.05, TerrainStyle.Canopy, fog: 0.4, rim: false);
            Band(1, Bumps(_horizon + 0.05, 0.03, 0.06, 0.95, "jungle-far2"), 0.28, TerrainStyle.Canopy, fog: 0.3);
            var mid = Band(2, Bumps(_horizon + 0.15, 0.05, 0.10, 0.9, "jungle-mid"), 0.55, TerrainStyle.Canopy, fog: 0.12);
            // The framing trunk stands on the focal side, leaving the moon's sky open.
            var tx = 0.5 + _side * r.Range(0.43, 0.46);
            Place(Prop.Trunk, 2, tx, _horizon + 0.2, r.Range(0.4, 0.5), "jungle-trunk", flip: _side < 0);
            Place(Prop.Vines, 2, 0.5 + _side * 0.45, 0.0, r.Range(0.32, 0.4), "jungle-vines-a");
            Place(Prop.Vines, 2, 0.5 + _side * 0.5, 0.0, r.Range(0.26, 0.34), "jungle-vines-b");
            Place(Prop.Fern, 2, 0.5 + _side * 0.2, mid.Profile.Y(0.5 + _side * 0.2) + 0.02, 0.12, "jungle-mid-fern");
            var floor = Band(3, Hills(0.985, 0.05, 1.4, "jungle-floor", 2), 1.0, TerrainStyle.Canopy);
            // Leaf fans frame both bottom corners and climb the side edges (never the title band).
            Place(Prop.BigLeaf, 3, 0.5 - _side * 0.44, floor.Profile.Y(0.5 - _side * 0.44) + 0.02, r.Range(0.5, 0.58), "jungle-leaf-a", flip: _side > 0);
            Place(Prop.BigLeaf, 3, 0.5 + _side * 0.46, floor.Profile.Y(0.5 + _side * 0.46) + 0.02, r.Range(0.4, 0.48), "jungle-leaf-b", flip: _side < 0, variant: 2);
            Place(Prop.BigLeaf, 3, 0.5 + _side * 0.5, 0.62, r.Range(0.28, 0.34), "jungle-leaf-c", flip: _side < 0, variant: 2);
            Place(Prop.Fern, 3, 0.5 + _side * 0.28, floor.Profile.Y(0.5 + _side * 0.28) + 0.01, 0.12, "jungle-fern");
            _fireflies = true;
        }

        // ------------------------------------------------------------------ features

        private void PlaceFeatures()
        {
            var r = R("features");
            var near = Ground(3);
            var mid = Ground(2);
            var far = Ground(1);
            var otherX = 0.5 - _side * r.Range(0.18, 0.24);   // the third opposite the focal
            var structureUsedFocal = false;

            var creature = _spec.Features.FirstOrDefault(PosterSpecRules.IsCreature, (PosterFeature)(-1));
            if (Enum.IsDefined(creature)) _focal = PlaceCreature(creature, near, mid, r);
            // Children choose a book by its character: when the story lists no creature, a
            // scene-native companion becomes the hero. It is scenery, not a feature — the spec
            // stays exactly what was asked, and the story's own props still appear beside it.
            else if (Companion() is { } companion) _focal = PlaceCreature(companion, near, mid, R("companion"), asFeature: false);

            foreach (var f in _spec.Features)
            {
                if (PosterSpecRules.IsCreature(f)) continue;
                var asFocal = _focal is null && !structureUsedFocal && f is not (PosterFeature.Lantern or PosterFeature.Train)
                              && (PosterSpecRules.IsStructure(f) || PosterSpecRules.IsVehicle(f));
                var x = GroundedX(mid, asFocal ? _focalX : otherX);
                switch (f)
                {
                    case PosterFeature.Lighthouse:
                        if (_spec.Scene == PosterScene.IslandCove)
                        {
                            var isle = _bands.Last(b => b.Depth == 2).Profile;
                            var lx = GroundedX(isle, 0.5 + _side * 0.3);
                            Place(Prop.Lighthouse, 2, lx, isle.Y(lx) + 0.006, CreatureListed ? r.Range(0.2, 0.24) : HeroHeight(r), "lighthouse", flip: false, feature: f, lit: true);
                        }
                        break; // MoonlitSea placed it on its cliff already
                    case PosterFeature.Cabin:
                        Place(Prop.Cabin, 2, x, mid.Y(x) + 0.01, asFocal ? HeroHeight(r) / 0.88 : r.Range(0.15, 0.18), "cabin", feature: f, lit: true);
                        break;
                    case PosterFeature.Village:
                        if (_spec.Scene is PosterScene.SnowyVillage or PosterScene.TownRooftops) break; // the scene IS a village
                        for (var i = 0; i < 4; i++)
                        {
                            var hx = otherX + (i - 1.5) * 0.068;
                            Place(Prop.House, 1, hx, far.Y(hx) + 0.006, r.Range(0.075, 0.09), $"far-house-{i}", variant: r.Int(3),
                                lit: r.Chance(0.6), feature: f, tone: 0.3);
                        }
                        break;
                    case PosterFeature.Castle:
                    {
                        var depth = _spec.Scene is PosterScene.CloudKingdom ? 1 : 1;
                        var cx = _focal is null ? _focalX : 0.5 - _side * r.Range(0.12, 0.18);
                        Place(Prop.Castle, depth, cx, Ground(1).Y(cx) + (_spec.Scene == PosterScene.CloudKingdom ? 0.03 : 0.012),
                            _focal is null ? HeroHeight(r) / 1.05 : r.Range(0.17, 0.22), "castle", feature: f, lit: true,
                            tone: _spec.Scene == PosterScene.CloudKingdom ? 0.3 : 0.42);
                        break;
                    }
                    case PosterFeature.Windmill:
                        Place(Prop.Windmill, 2, x, mid.Y(x) + 0.01, asFocal ? HeroHeight(r) / 1.08 : 0.24, "windmill", feature: f);
                        break;
                    case PosterFeature.Tent:
                        Place(Prop.Tent, 2, x, mid.Y(x) + 0.008, asFocal ? HeroHeight(r) / 0.78 : 0.17, "tent", feature: f, lit: true);
                        break;
                    case PosterFeature.Lantern:
                    {
                        // A street lamp belongs to a village or town; everywhere else it is a
                        // small camp lantern set on the ground beside the hero.
                        var street = _spec.Scene is PosterScene.SnowyVillage or PosterScene.TownRooftops;
                        var lx = _focal is not null ? _focal.X - _side * 0.17 : x;
                        Place(Prop.Lantern, 3, lx, near.Y(lx) + 0.012, street ? 0.22 : 0.11, "lantern", feature: f, lit: true,
                            variant: street ? 1 : 0);
                        break;
                    }
                    case PosterFeature.Telescope:
                        if (_spec.Scene == PosterScene.TownRooftops)
                            Place(Prop.Telescope, 3, x, near.Y(x) + 0.004, asFocal ? HeroHeight(r) / 0.78 : 0.21, "telescope", flip: _side > 0, feature: f);
                        else
                            Place(Prop.Telescope, 2, x, mid.Y(x) + 0.006, asFocal ? HeroHeight(r) / 0.78 : 0.23, "telescope", flip: _side > 0, feature: f);
                        break;
                    case PosterFeature.Boat:
                    case PosterFeature.Sailboat:
                        if (_water is null) break;
                        var by = _water.Line + r.Range(0.06, 0.12);
                        var bx = asFocal ? _focalX : 0.5 + _side * r.Range(0.02, 0.1);
                        Place(f == PosterFeature.Boat ? Prop.Boat : Prop.Sailboat, 2, bx, by,
                            asFocal ? HeroHeight(r) / (f == PosterFeature.Boat ? 0.9 : 0.97) : f == PosterFeature.Boat ? r.Range(0.13, 0.16) : r.Range(0.18, 0.22),
                            "boat", feature: f, lit: f == PosterFeature.Boat);
                        break;
                    case PosterFeature.Balloon:
                    {
                        var moonX = 0.5 - _side * 0.25;
                        var bx2 = moonX + _side * 0.24;
                        Place(Prop.Balloon, 2, bx2, r.Range(0.42, 0.47), asFocal ? HeroHeight(r) / 1.2 : r.Range(0.15, 0.18), "balloon", feature: f, lit: true);
                        break;
                    }
                    case PosterFeature.Train:
                    {
                        // On rails along the mid ground, never on a ridge line.
                        var tx = GroundedX(mid, 0.5 + r.Jitter(0.06));
                        Place(Prop.Train, 2, tx, mid.Y(tx) + 0.004, r.Range(0.065, 0.075), "train", flip: _side > 0, feature: f, lit: true);
                        break;
                    }
                    case PosterFeature.Birds:
                        // A little flock crossing the open sky on the moon's side, clear of the hero.
                        Place(Prop.Birds, 1, 0.5 - _side * r.Range(0.02, 0.1), r.Range(0.33, 0.37), r.Range(0.29, 0.33), "birds", feature: f, tone: 0.2);
                        break;
                    case PosterFeature.Flowers:
                        for (var i = 0; i < 9; i++)
                        {
                            // Flowers grow up through the framing, in front of it.
                            var fx = r.Chance(0.5) ? r.Range(0.03, 0.3) : r.Range(0.7, 0.97);
                            Place(Prop.Flower, 3, fx, 1.0 + r.Range(0.005, 0.03), r.Range(0.09, 0.12), $"flower-{i}", feature: f);
                        }
                        break;
                    case PosterFeature.Mushrooms:
                    {
                        var mx = _focal is not null ? _focal.X + _side * 0.14 : otherX;
                        for (var i = 0; i < 3; i++)
                        {
                            var x2 = mx + i * 0.03 * _side;
                            Place(Prop.Mushroom, 3, x2, near.Y(x2) + 0.012, r.Range(0.05, 0.08), $"mushroom-{i}", feature: f, lit: i == 0);
                        }
                        break;
                    }
                    case PosterFeature.Fireflies:
                        _fireflies = true;
                        break;
                    case PosterFeature.Fish:
                    {
                        var fx = _focal is not null ? 0.5 - _side * 0.12 : _focalX;
                        var fsz = _focal is null ? HeroHeight(r) / 0.8 : r.Range(0.24, 0.3);
                        var school = Place(Prop.FishSchool, 3, fx, 0.5 + fsz * 0.45, fsz, "fish", flip: _side < 0, feature: f);
                        _focal ??= school;
                        break;
                    }
                    case PosterFeature.Snowfall:
                        _snowing = true;
                        break;
                }
                // A featured structure outranks scenery: clear default trees it would hide behind.
                if (PosterSpecRules.IsStructure(f) && _props.LastOrDefault(p => p.Feature == f) is { } placed)
                    _props.RemoveAll(p => p.Feature is null && p.Depth >= placed.Depth && p.Depth <= placed.Depth + 1
                                          && p.Prop is Prop.RoundTree or Prop.Pine && p.Size > 0.1
                                          && Math.Abs(p.X - placed.X) < 0.18);
                if (asFocal && f is not (PosterFeature.Lighthouse or PosterFeature.Village or PosterFeature.Castle
                        or PosterFeature.Train or PosterFeature.Birds or PosterFeature.Flowers or PosterFeature.Mushrooms
                        or PosterFeature.Fireflies or PosterFeature.Snowfall))
                {
                    structureUsedFocal = true;
                    _focal = _props.LastOrDefault(p => p.Feature == f);
                }
            }
        }

        /// <summary>Every poster has a subject. When the spec names none, the scene supplies a
        /// quiet one (scenery props, not features — the spec stays exactly what was asked).</summary>
        private void PlaceDefaultHero()
        {
            // With a companion as the hero, the scene's home prop (boat, cabin, tent, castle…)
            // still appears — smaller, on the other third — unless the story brought its own.
            var companionHero = _focal is { Feature: null };
            if (_focal is not null && !companionHero) return;
            if (companionHero && _spec.Features.Any(f => f != PosterFeature.Lantern && (PosterSpecRules.IsStructure(f) || PosterSpecRules.IsVehicle(f))))
                return;
            if (companionHero) { PlaceHomeProp(secondary: true); return; }
            // A featured landmark (castle, lighthouse, village, train) already is the subject.
            if (_props.FirstOrDefault(p => p.Feature is PosterFeature.Castle or PosterFeature.Lighthouse
                    or PosterFeature.Windmill) is { } landmark)
            {
                _focal = landmark;
                return;
            }
            PlaceHomeProp(secondary: false);
        }

        private void PlaceHomeProp(bool secondary)
        {
            var r = R("hero");
            var mid = Ground(2);
            var near = Ground(3);
            var x = secondary ? 0.5 - _side * r.Range(0.2, 0.26) : _focalX;
            if (secondary && _layout == PosterLayout.Center) x = 0.5 - _side * r.Range(0.28, 0.32);
            var k = secondary ? 0.72 : 1.0;
            Placement Hero(Placement p) { if (!secondary) _focal = p; return p; }
            switch (_spec.Scene)
            {
                case PosterScene.MoonlitSea or PosterScene.IslandCove or PosterScene.MountainLake when _water is not null:
                    Hero(Place(Prop.Boat, 2, x, _water.Line + r.Range(0.11, 0.15) * (secondary ? 0.7 : 1), k * HeroHeight(r) / 0.9, "hero-boat", lit: true));
                    break;
                case PosterScene.ForestClearing or PosterScene.StarryHills or PosterScene.FireflyMeadow or PosterScene.DesertOasis:
                {
                    x = GroundedX(mid, x);
                    var tent = _spec.Scene is PosterScene.StarryHills or PosterScene.DesertOasis;
                    _props.RemoveAll(p => p.Feature is null && p.Depth == 2 && p.Prop is Prop.RoundTree or Prop.Pine && Math.Abs(p.X - x) < 0.2);
                    Hero(Place(tent ? Prop.Tent : Prop.Cabin, 2, x, mid.Y(x) + 0.01, k * HeroHeight(r) / (tent ? 0.8 : 0.88), "hero-home", lit: true));
                    break;
                }
                case PosterScene.SnowyVillage:
                    Hero(Place(Prop.Snowman, secondary ? 2 : 3, x, (secondary ? mid : near).Y(x) + 0.01, k * HeroHeight(r), "hero-snowman", flip: x > 0.5));
                    break;
                case PosterScene.TownRooftops:
                    Hero(Place(Prop.House, 2, x, Math.Min(0.86, _horizon + 0.25), k * HeroHeight(r) / 0.9, "hero-house", variant: 1, lit: true));
                    break;
                case PosterScene.ReefAtDusk:
                {
                    var hsz = k * HeroHeight(r) / 0.8;
                    Hero(Place(Prop.FishSchool, 3, x, (secondary ? 0.42 : 0.5) + hsz * 0.45, hsz, "hero-fish", flip: _side < 0));
                    break;
                }
                case PosterScene.CloudKingdom:
                    Hero(Place(Prop.Castle, 1, x, Ground(1).Y(x) + 0.03, k * HeroHeight(r) / 1.05, "hero-castle", lit: true, tone: 0.45));
                    break;
                case PosterScene.JungleCanopy:
                {
                    // A ring of giant glowing mushrooms: the tallest is the subject.
                    Place(Prop.Mushroom, 3, x - 0.08 * k, near.Y(x - 0.08) + 0.012, k * r.Range(0.1, 0.13), "hero-glow-0", lit: true);
                    Place(Prop.Mushroom, 3, x + 0.09 * k, near.Y(x + 0.09) + 0.012, k * r.Range(0.13, 0.16), "hero-glow-1", lit: !secondary);
                    Hero(Place(Prop.Mushroom, 3, x, near.Y(x) + 0.014, k * HeroHeight(r), "hero-glow-2", lit: true));
                    break;
                }
            }
        }

        /// <summary>One shared placement pass for subjects (features, heroes, lights): boxes
        /// inflated by the parallax difference of their layers, overlaps pushed apart toward
        /// the roomier side and re-seated on their ground. Scenery may overlap; subjects never.</summary>
        private void SeparateSubjects()
        {
            static (double L, double R, double T, double B) Box(Placement p) => p.Prop switch
            {
                Prop.FishSchool => (p.X - p.Size * 0.85, p.X + p.Size * 0.85, p.Y - p.Size * 0.85, p.Y),
                Prop.Turtle when p.Variant == 0 => (p.X - p.Size * 0.5, p.X + p.Size * 0.62, p.Y - p.Size * 0.3, p.Y + p.Size * 0.45),
                Prop.Balloon => (p.X - p.Size * 0.5, p.X + p.Size * 0.5, p.Y - p.Size * 0.6, p.Y + p.Size * 0.7),
                Prop.Pool => (p.X - p.Size * 0.55, p.X + p.Size * 0.55, p.Y - p.Size * 0.12, p.Y + p.Size * 0.12),
                _ => (p.X - p.Size * 0.55, p.X + p.Size * 0.55, p.Y - p.Size, p.Y),
            };
            var subjects = _props.Where(p => p.Feature is not null && p.Prop is not (Prop.House or Prop.Flower or Prop.Mushroom or Prop.Birds)
                                             || p.Lit || ReferenceEquals(p, _focal) || IsObstacle(p)).ToList();
            for (var pass = 0; pass < 3; pass++)
            for (var i = 0; i < subjects.Count; i++)
            for (var j = i + 1; j < subjects.Count; j++)
            {
                var a = subjects[i];
                var b = subjects[j];
                if (Math.Abs(a.Depth - b.Depth) > 1) continue;
                // The oasis pool lies back on the mid ground: a hero in front may overlap it.
                if ((a.Prop == Prop.Pool || b.Prop == Prop.Pool) && a.Depth != b.Depth) continue;
                var pad = 0.02 + Math.Abs(PosterEngineTravel(a.Depth) - PosterEngineTravel(b.Depth));
                var ba = Box(a);
                var bb = Box(b);
                var overlapX = Math.Min(ba.R, bb.R) - Math.Max(ba.L, bb.L) + pad;
                var overlapY = Math.Min(ba.B, bb.B) - Math.Max(ba.T, bb.T) + pad * 0.5;
                if (overlapX <= 0 || overlapY <= 0) continue;
                // Move the one that is not the focal subject.
                // Move the one that is not the focal subject (a pool never moves).
                if (IsObstacle(a) && IsObstacle(b)) continue;
                // A framing tree that crowds a subject simply steps out of the picture.
                var framer = IsObstacle(a) && a.Prop != Prop.Pool ? a : IsObstacle(b) && b.Prop != Prop.Pool ? b : null;
                if (framer is not null)
                {
                    _props.Remove(framer);
                    subjects.Remove(framer);
                    i = Math.Min(i, subjects.Count - 1);
                    break;
                }
                var mover = IsObstacle(b) ? a : IsObstacle(a) ? b : ReferenceEquals(b, _focal) ? a : b;
                var other = ReferenceEquals(mover, a) ? b : a;
                var dir = mover.X >= other.X ? 1 : -1;
                if (mover.X + dir * overlapX > 0.9 || mover.X + dir * overlapX < 0.1) dir = -dir;
                var nx = Math.Clamp(mover.X + dir * overlapX, 0.12, 0.88);
                var ny = mover.Y;
                if (mover.Prop is not (Prop.Boat or Prop.Sailboat or Prop.Otter or Prop.Whale or Prop.FishSchool or Prop.Balloon or Prop.Birds or Prop.Owl)
                    && !(mover.Prop == Prop.Turtle && mover.Variant == 0))
                {
                    var ground = Ground(mover.Depth);
                    var lift = mover.Y - ground.Y(mover.X);
                    nx = GroundedX(ground, nx);
                    ny = ground.Y(nx) + lift;
                }
                var moved = mover with { X = nx, Y = ny };
                var idx = _props.IndexOf(mover);
                if (idx >= 0) _props[idx] = moved;
                subjects[subjects.IndexOf(mover)] = moved;
                if (ReferenceEquals(mover, _focal)) _focal = moved;
            }
        }

        /// <summary>Half-width of what a prop draws, as a fraction of its size.</summary>
        private static double HalfWidth(Placement p) => p.Prop switch
        {
            Prop.Whale => 1.0, Prop.Dinosaur => 0.85, Prop.FoxCurled => 0.65, Prop.Hedgehog => 0.6, Prop.Camel => 0.6,
            Prop.Turtle => 0.6, Prop.FishSchool => 0.85, Prop.Fox => 0.5, Prop.Owl => 0.35, Prop.Deer => 0.45,
            Prop.Train => 6.0, Prop.Cabin or Prop.House => 0.6, Prop.Tent => 0.66, Prop.Castle => 0.5, Prop.Boat => 0.56,
            _ => 0.45,
        };

        /// <summary>The hero gets room to breathe: a landmark or tree behind it that would sit
        /// against its head steps to the hero's far side, re-seated on its own ground.</summary>
        private void ClearHeroZone()
        {
            if (_focal is not { } hero) return;
            var hw = hero.Size * HalfWidth(hero) + 0.04;
            var top = hero.Y - hero.Size * (Render.PropPainter.IsCreatureProp(hero.Prop) ? Render.PropPainter.HeightFactor(hero.Prop, hero.Variant) : 1.0) - 0.03;
            for (var i = 0; i < _props.Count; i++)
            {
                var p = _props[i];
                if (ReferenceEquals(p, hero)) continue;
                // Kelp and coral in front of a swimming hero would slice through it.
                if (p.Depth > hero.Depth && p.Prop is Prop.Kelp or Prop.BranchCoral or Prop.BigLeaf or Prop.Fern or Prop.Bush && p.Feature is null
                    && Math.Abs(p.X - hero.X) < hw + p.Size * 0.3
                    || p.Depth == hero.Depth && p.Prop is Prop.BigLeaf or Prop.Bush && p.Feature is null && Math.Abs(p.X - hero.X) < hw + p.Size * 0.25)
                {
                    _props.RemoveAt(i--);
                    continue;
                }
                if (p.Depth > hero.Depth) continue;
                if (p.Prop is not (Prop.Castle or Prop.House or Prop.Windmill or Prop.Tent or Prop.Cabin or Prop.Palm or Prop.Lighthouse
                        or Prop.Telescope or Prop.RoundTree or Prop.Balloon or Prop.Boat or Prop.Sailboat)) continue;
                var pw = p.Size * HalfWidth(p);
                var pTop = p.Y - p.Size * (p.Prop == Prop.Balloon ? 0.6 : 1.0);
                var pBottom = p.Prop == Prop.Balloon ? p.Y + p.Size * 0.7 : p.Y;
                var overlapX = Math.Min(p.X + pw, hero.X + hw) - Math.Max(p.X - pw, hero.X - hw);
                if (overlapX <= 0 || pBottom < top || pTop > hero.Y) continue;
                var dir = p.X >= hero.X ? 1 : -1;
                var nx = hero.X + dir * (hw + pw + 0.02);
                if (nx is < 0.08 or > 0.92) { dir = -dir; nx = hero.X + dir * (hw + pw + 0.02); }
                if (nx is < 0.06 or > 0.94)
                {
                    // Scenery with no room steps out (a warm light is added beside the hero after).
                    if (p.Feature is null) { _props.RemoveAt(i--); continue; }
                    nx = Math.Clamp(nx, 0.08, 0.92);
                }
                var ny = p.Y;
                if (p.Prop is not (Prop.Balloon or Prop.Boat or Prop.Sailboat))
                {
                    var ground = Ground(p.Depth);
                    var lift = p.Y - ground.Y(p.X);
                    nx = GroundedX(ground, nx);
                    // No solid ground on that side: an unlit prop steps out rather than standing on water.
                    if (ground.Y(nx) > Math.Min(0.92, _horizon + 0.22))
                    {
                        if (p.Feature is null && !p.Lit) _props.RemoveAt(i--);
                        continue;
                    }
                    ny = ground.Y(nx) + lift;
                }
                _props[i] = p with { X = nx, Y = ny };
            }
        }

        /// <summary>The whole hero stays inside the frame at every tilt: its box (half-width
        /// plus the near layer's full travel and a margin) is shrunk to fit, then slid inward.</summary>
        private void FitHeroInFrame()
        {
            if (_focal is not { } hero) return;
            var travel = Math.Abs(PosterEngine.TravelFraction[Math.Clamp(hero.Depth, 0, 3)]) + 0.03;
            var hwK = HalfWidth(hero);
            var maxHalf = 0.5 - travel;
            var size = Math.Min(hero.Size, maxHalf / hwK);
            var hw = size * hwK;
            var x = Math.Clamp(hero.X, travel + hw, 1 - travel - hw);
            if (Math.Abs(x - hero.X) < 1e-9 && Math.Abs(size - hero.Size) < 1e-9) return;
            var y = hero.Y;
            if (hero.Depth >= 2 && hero.Prop is not (Prop.Whale or Prop.Otter or Prop.Boat or Prop.Sailboat or Prop.FishSchool or Prop.Owl or Prop.Balloon)
                && !(hero.Prop == Prop.Turtle && hero.Variant == 0))
                y = Ground(hero.Depth).Y(x) + (hero.Y - Ground(hero.Depth).Y(hero.X));
            var fitted = hero with { X = x, Y = y, Size = size };
            var idx = _props.IndexOf(hero);
            if (idx >= 0) _props[idx] = fitted;
            _focal = fitted;
        }

        /// <summary>A fire or lantern never touches the hero: it stands a clear gap from the
        /// hero's box (moved to the roomier side, or dropped if there is no room), and a
        /// creature hero turns to face its light.</summary>
        private void KeepLightsOffHero()
        {
            if (_focal is not { } hero) return;
            var hw = hero.Size * HalfWidth(hero);
            for (var i = 0; i < _props.Count; i++)
            {
                var p = _props[i];
                if (ReferenceEquals(p, hero) || !p.Lit || p.Prop is not (Prop.Campfire or Prop.Lantern or Prop.Anemone) || p.Depth < hero.Depth - 1) continue;
                if (p.Prop == Prop.Lantern && p.Variant == 1) continue;
                var lw = p.Size * 0.6;
                var gap = 0.05;
                if (Math.Abs(p.X - hero.X) >= hw + lw + gap) continue;
                // A glow on the floor beneath a swimming hero is not touching it.
                if (p.Y - p.Size > hero.Y + 0.02) continue;
                var ground = Ground(p.Depth);
                var lift = p.Y - ground.Y(p.X);
                double? found = null;
                var first = hero.Prop == Prop.Owl ? -_side : p.X >= hero.X ? 1 : -1;   // never under the owl's branch
                foreach (var dir in new[] { first, -first })
                {
                    var nx = hero.X + dir * (hw + lw + gap);
                    if (nx is < 0.1 or > 0.9) continue;
                    if (ground.Y(nx) > (p.Depth >= 3 ? 1.0 : _horizon + 0.2)) continue;
                    found = nx;
                    break;
                }
                if (found is { } fx) { _props[i] = p with { X = fx, Y = ground.Y(fx) + lift }; continue; }
                // No room either side: the light takes the roomier side near the edge and the
                // hero makes room for it (up to a quarter smaller); the light is never dropped.
                var side = hero.Prop == Prop.Owl ? -_side : hero.X < 0.5 ? 1 : -1;
                var lx = Math.Clamp(hero.X + side * (hw + lw + gap), 0.11, 0.89);
                for (var d = 0.0; d < 0.2 && ground.Y(lx) > (p.Depth >= 3 ? 1.0 : _horizon + 0.2); d += 0.02) lx = Math.Clamp(lx - side * 0.02, 0.11, 0.89);
                _props[i] = p with { X = lx, Y = ground.Y(lx) + lift };
                var room = Math.Abs(lx - hero.X) - lw - gap * 0.5;
                if (room < hw)
                {
                    var k = Math.Max(0.9, room / hw);
                    var shrunk = hero with { Size = hero.Size * k };
                    _props[_props.IndexOf(hero)] = shrunk;
                    hero = shrunk;
                    _focal = shrunk;
                    hw = hero.Size * HalfWidth(hero);
                }
            }
            // The hero looks toward its light (the owl keeps its tree on the frame side).
            if (Render.PropPainter.IsCreatureProp(hero.Prop) && hero.Prop is not (Prop.Owl or Prop.FishSchool)
                && _props.Where(p => p.Lit && !ReferenceEquals(p, hero) && p.Depth >= 2).OrderBy(p => Math.Abs(p.X - hero.X)).FirstOrDefault() is { } light)
            {
                var face = light.X < hero.X;   // the art faces +x; flip to face left
                if (face != hero.Flip)
                {
                    var turned = hero with { Flip = face };
                    _props[_props.IndexOf(hero)] = turned;
                    _focal = turned;
                }
            }
        }

        /// <summary>The hero never climbs into the title band: on a high horizon a tall
        /// castle or lighthouse is scaled down until its tip clears it.</summary>
        private void KeepHeroBelowTitle()
        {
            if (_focal is not { } hero) return;
            var above = hero.Prop switch
            {
                Prop.Castle => 1.18,
                Prop.Lighthouse => 1.05,
                Prop.Windmill => 1.12,
                Prop.Balloon => 0.6,
                Prop.Sailboat => 1.0,
                Prop.Boat => 0.7,
                _ => Render.PropPainter.IsCreatureProp(hero.Prop) ? Render.PropPainter.HeightFactor(hero.Prop, hero.Variant) : 1.0,
            };
            var limit = TitleSafe.Bottom + 0.04;
            if (hero.Y - hero.Size * above >= limit) return;
            var fitted = hero with { Size = Math.Max(0.05, (hero.Y - limit) / above) };
            var idx = _props.IndexOf(hero);
            if (idx >= 0) _props[idx] = fitted;
            _focal = fitted;
        }

        /// <summary>Fixed things subjects must not stand in: the oasis pool and big framing
        /// trees/palms in the near layer.</summary>
        private static bool IsObstacle(Placement p) =>
            p.Prop == Prop.Pool || p.Feature is null && p.Depth == 3 && p.Size > 0.3 && p.Prop is Prop.Pine or Prop.Palm;

        private static double PosterEngineTravel(int depth) => PosterEngine.TravelFraction[Math.Clamp(depth, 0, 3)];

        /// <summary>Foreground framing: a darker, richly shaped cluster rising in each lower
        /// corner — full on the open side, lower beside the hero so it never covers it.</summary>
        private void PlaceFraming()
        {
            var r = R("framing");
            var kit = _spec.Scene switch
            {
                PosterScene.FireflyMeadow or PosterScene.StarryHills => Render.PropPainter.FrameKit.Meadow,
                PosterScene.ForestClearing => Render.PropPainter.FrameKit.Forest,
                PosterScene.SnowyVillage => Render.PropPainter.FrameKit.Snow,
                PosterScene.MoonlitSea or PosterScene.MountainLake => Render.PropPainter.FrameKit.Shore,
                PosterScene.IslandCove => Render.PropPainter.FrameKit.Beach,
                PosterScene.DesertOasis => Render.PropPainter.FrameKit.Desert,
                PosterScene.ReefAtDusk => Render.PropPainter.FrameKit.Reef,
                PosterScene.CloudKingdom => Render.PropPainter.FrameKit.Cloud,
                PosterScene.TownRooftops => Render.PropPainter.FrameKit.Roof,
                _ => Render.PropPainter.FrameKit.Jungle,
            };
            // The open side is away from the hero; a centred hero gets two even frames.
            var heroX = _focal?.X ?? _focalX;
            var open = Math.Abs(heroX - 0.5) < 0.08 ? -_side : (heroX > 0.5 ? -1 : 1);
            var bottom = kit == Render.PropPainter.FrameKit.Roof ? Ground(3).Y(0.5 + open * 0.45) + 0.02 : 1.0;
            var strong = _layout == PosterLayout.Center ? r.Range(0.37, 0.41) : r.Range(0.43, 0.47);
            var weak = _layout == PosterLayout.Center ? r.Range(0.3, 0.34) : r.Range(0.28, 0.32);
            // A hero standing close to its edge gets a lower frame on that side.
            if (Math.Abs(heroX - 0.5) > 0.24) weak *= 0.92;
            Place(Prop.FrameCluster, 3, 0.5 + open * 0.52, bottom, strong, "frame-open", flip: open > 0, variant: (int)kit);
            // The hero-side cluster is compressed sideways (+16): tall at the edge, never across the hero.
            var heroBottom = kit == Render.PropPainter.FrameKit.Roof ? Ground(3).Y(0.5 - open * 0.45) + 0.02 : 1.0;
            Place(Prop.FrameCluster, 3, 0.5 - open * 0.53, heroBottom, weak, "frame-hero", flip: open < 0, variant: (int)kit + 16);
        }

        /// <summary>Every poster carries a warm light beside its hero — a window, a lantern, a
        /// campfire, a glowing anemone on the reef: the "safe place" a sleepy eye lands on,
        /// and the value accent that makes the poster read as a picture, not wallpaper.</summary>
        private void EnsureWarmLight()
        {
            var hero = _focal;
            // A cabin, tent or glowing mushroom is its own lamp; a far castle's windows or a
            // boat's masthead light are too small to be the poster's focal glow.
            if (hero is not null && hero.Lit && hero.Prop is Prop.Cabin or Prop.Tent or Prop.Mushroom) return;
            if (hero is null && _props.Any(p => p.Lit && p.Depth >= 2)) return;
            // One dominant warm light: a lit lantern, cabin or tent the story already placed
            // near the hero is the light; a second equal one would split the focus.
            if (hero is not null && _props.Any(p => p.Lit && p.Depth >= 2 && p.Prop is Prop.Lantern or Prop.Cabin or Prop.Tent or Prop.Campfire
                    && Math.Abs(p.X - hero.X) < 0.3 && p.X is > 0.15 and < 0.85)) return;
            var r = R("warm-light");
            var dir = hero?.Prop == Prop.Owl ? -_side : _layout == PosterLayout.Center ? _side : (_focalX > 0.5 ? -1 : 1);
            var hx = hero?.X ?? _focalX;
            var reach = hero is null ? 0.0 : hero.Size * (hero.Prop is Prop.Whale or Prop.Dinosaur or Prop.FoxCurled ? 0.75 : 0.5) + 0.07;
            var x = Math.Clamp(hx + dir * reach, 0.14, 0.86);
            if (_underwater)
            {
                var floor = Ground(3);
                Place(Prop.Anemone, 3, x, floor.Y(x) + 0.01, r.Range(0.14, 0.17), "warm-anemone", lit: true);
                return;
            }
            var onMid = hero is { Depth: 2 } && hero.Prop is not (Prop.Whale or Prop.Otter or Prop.Boat or Prop.Sailboat or Prop.Balloon)
                        && _spec.Scene != PosterScene.TownRooftops
                        || _spec.Scene == PosterScene.CloudKingdom;
            var ground = Ground(onMid ? 2 : 3);
            // Only on solid ground well above the frame's foot (never on the water).
            var limit = onMid ? _horizon + 0.2 : 1.0;
            if (ground.Y(x) > limit)
            {
                var best = x;
                for (var d = 0.02; d < 0.6; d += 0.02)
                {
                    if (x + d < 0.9 && ground.Y(x + d) <= limit) { best = x + d; break; }
                    if (x - d > 0.1 && ground.Y(x - d) <= limit) { best = x - d; break; }
                }
                x = best;
            }
            var wild = _spec.Scene is PosterScene.ForestClearing or PosterScene.StarryHills or PosterScene.FireflyMeadow
                or PosterScene.DesertOasis or PosterScene.MountainLake or PosterScene.IslandCove or PosterScene.JungleCanopy;
            var depth = onMid ? 2 : 3;
            if (wild && _spec.Scene != PosterScene.JungleCanopy && !(_spec.Scene == PosterScene.MountainLake && !onMid && r.Chance(0.5)))
                Place(Prop.Campfire, depth, x, ground.Y(x) + 0.008, onMid ? 0.15 : 0.17, "warm-fire", lit: true);
            else
                Place(Prop.Lantern, depth, x, ground.Y(x) + 0.012, onMid ? 0.085 : 0.11, "warm-lantern", lit: true, variant: 0);
        }

        /// <summary>The light a lit prop casts: where its glow centres and how far its pool reaches.</summary>
        private LightPlan LightFor(Placement p)
        {
            var (cy, radius, kind) = p.Prop switch
            {
                Prop.Campfire => (p.Y - p.Size * 0.3, 0.2 + p.Size * 0.6, LightKind.Campfire),
                Prop.Lantern when p.Variant == 0 => (p.Y - p.Size * 0.45, 0.12 + p.Size, LightKind.Lantern),
                Prop.Lantern => (p.Y - p.Size * 0.62, 0.1 + p.Size * 0.4, LightKind.Lantern),
                Prop.Mushroom or Prop.Anemone => (p.Y - p.Size * 0.6, 0.06 + p.Size * 0.9, LightKind.Glow),
                Prop.Boat => (p.Y - p.Size * 0.5, 0.06 + p.Size * 0.5, LightKind.Lantern),
                Prop.Lighthouse => (p.Y - p.Size * 0.82, 0.06 + p.Size * 0.5, LightKind.Window),
                _ => (p.Y - p.Size * 0.3, 0.04 + p.Size * 0.35, LightKind.Window),
            };
            var strength = p.Depth switch { 1 => 0.45, 2 => 0.85, _ => 1.0 };
            return new LightPlan(p.X, cy, radius, strength, p.Depth, kind, p.Feature, ReferenceEquals(p, _focal) || p == _focal);
        }

        /// <summary>The scene's companion creature, picked evenly by seed so the roster rotates.</summary>
        private PosterFeature? Companion()
        {
            PosterFeature[] options = _spec.Scene switch
            {
                PosterScene.MoonlitSea => [PosterFeature.Otter, PosterFeature.Fox, PosterFeature.Whale],
                PosterScene.ForestClearing => [PosterFeature.Fox, PosterFeature.Rabbit, PosterFeature.Hedgehog, PosterFeature.Bear, PosterFeature.Deer, PosterFeature.Owl],
                PosterScene.MountainLake => [PosterFeature.Deer, PosterFeature.Bear, PosterFeature.Fox, PosterFeature.Otter],
                PosterScene.FireflyMeadow => [PosterFeature.Rabbit, PosterFeature.Hedgehog, PosterFeature.Deer, PosterFeature.Fox],
                PosterScene.SnowyVillage => [PosterFeature.Fox, PosterFeature.Rabbit, PosterFeature.Cat, PosterFeature.Owl],
                PosterScene.DesertOasis => [PosterFeature.Camel, PosterFeature.Fox],
                PosterScene.IslandCove => [PosterFeature.Turtle, PosterFeature.Otter],
                PosterScene.StarryHills => [PosterFeature.Fox, PosterFeature.Rabbit, PosterFeature.Bear],
                PosterScene.ReefAtDusk => [PosterFeature.Turtle, PosterFeature.Whale],
                PosterScene.CloudKingdom => [PosterFeature.Cat, PosterFeature.Rabbit, PosterFeature.Bear, PosterFeature.Fox],
                PosterScene.TownRooftops => [PosterFeature.Cat, PosterFeature.Fox, PosterFeature.Rabbit],
                PosterScene.JungleCanopy => [PosterFeature.Dinosaur, PosterFeature.Owl],
                _ => [],
            };
            return options.Length == 0 ? null : options[R("companion-pick").Int(options.Length)];
        }

        private Placement PlaceCreature(PosterFeature creature, TerrainProfile near, TerrainProfile mid, Rng r, bool asFeature = true)
        {
            PosterFeature? tag = asFeature ? creature : null;
            var x = _focalX;
            var faceIn = _focalX > 0.5; // face toward the centre / the moon
            var waterY = (_water?.Line ?? _horizon);
            Placement OnNear(Prop prop, int variant, string label)
            {
                var size = HeroSize(prop, variant, r);
                return Place(prop, 3, x, near.Y(x) + 0.008, size, label, flip: faceIn, variant: variant, feature: tag);
            }
            switch (creature)
            {
                case PosterFeature.Whale:
                    if (_underwater)
                        return Place(Prop.Whale, 2, 0.5 - _side * 0.08, r.Range(0.7, 0.74), Math.Min(0.44, HeroSize(Prop.Whale, 1, r)), "whale", flip: !faceIn, variant: 1, feature: tag);
                    return Place(Prop.Whale, 2, x, waterY + r.Range(0.12, 0.16), HeroSize(Prop.Whale, 0, r), "whale", flip: faceIn, feature: tag);
                case PosterFeature.Otter:
                    return Place(Prop.Otter, 2, x, waterY + r.Range(0.13, 0.17), HeroSize(Prop.Otter, 0, r), "otter", flip: faceIn, feature: tag);
                case PosterFeature.Turtle:
                    if (_underwater)
                        return Place(Prop.Turtle, 2, x, r.Range(0.68, 0.72), HeroSize(Prop.Turtle, 0, r), "turtle", flip: faceIn, feature: tag);
                    return OnNear(Prop.Turtle, 1, "turtle");
                case PosterFeature.Owl:
                {
                    // On a branch reaching out from a tree at the frame edge on its side.
                    var ox = _layout == PosterLayout.Center ? _focalX : 0.5 + _side * 0.25;
                    var size = HeroSize(Prop.Owl, 0, r);
                    // The tree stands fully inside the frame even at full tilt, so the branch never floats.
                    Place(Prop.Trunk, 3, 0.5 + _side * 0.4, 1.0, Math.Min(0.6, size * 1.6), "owl-tree", flip: _side < 0);
                    return Place(Prop.Owl, 3, ox, Math.Min(0.82, near.Y(ox) - 0.1), size, "owl", flip: _side < 0, feature: tag);
                }
                case PosterFeature.Dinosaur:
                    return OnNear(Prop.Dinosaur, 0, "dinosaur");
                case PosterFeature.Cat:
                    return OnNear(Prop.Cat, 0, "cat");
                case PosterFeature.Camel:
                    return OnNear(Prop.Camel, 0, "camel");
                case PosterFeature.Deer:
                    return OnNear(Prop.Deer, _spec.Mood != PosterMood.Curious || r.Chance(0.3) ? 1 : 0, "deer");   // the folded fawn is the gentler pose
                case PosterFeature.Bear:
                    return OnNear(Prop.Bear, 0, "bear");
                case PosterFeature.Hedgehog:
                    return OnNear(Prop.Hedgehog, 0, "hedgehog");
                case PosterFeature.Rabbit:
                    return OnNear(Prop.Rabbit, 0, "rabbit");
                case PosterFeature.Fox:
                default:
                {
                    var curled = _spec.Mood is PosterMood.Sleepy or PosterMood.Calm or PosterMood.Dreamy ? r.Chance(0.6) : r.Chance(0.25);
                    return OnNear(curled ? Prop.FoxCurled : Prop.Fox, 0, "fox");
                }
            }
        }

        // ------------------------------------------------------------------ sky

        private void PlanSky()
        {
            var r = R("sky");
            var time = _spec.TimeOfDay;
            var dark = time is PosterTimeOfDay.Starlit;

            // ---- Moon: opposite third, below the title band, clear of tall silhouettes ----
            if (!_underwater)
            {
                var showMoon = time switch
                {
                    PosterTimeOfDay.Moonlit => true,
                    PosterTimeOfDay.Twilight => r.Chance(0.8),
                    PosterTimeOfDay.Starlit => r.Chance(0.6) && !Has(PosterFeature.MilkyWay),
                    _ => r.Chance(0.5),
                };
                if (showMoon)
                {
                    var radius = time == PosterTimeOfDay.Moonlit ? r.Range(0.032, 0.04) : r.Range(0.03, 0.038);
                    // Full or crescent only: a gibbous "bite" reads as an eclipse or a cookie.
                    var phase = time == PosterTimeOfDay.Moonlit ? 0 : r.Range(0.64, 0.76);
                    var mx = 0.5 - _side * r.Range(0.18, 0.32);
                    var my = Math.Max(0.36, TitleSafe.Bottom + radius + 0.02) + r.Range(0, 0.1);
                    // Never behind a ridge: keep ≥1.8 radii above every far silhouette under it,
                    // sliding outward (then shrinking) until it fits.
                    for (var attempt = 0; attempt < 12; attempt++)
                    {
                        var top = SilhouetteTop(mx - radius * 1.6, mx + radius * 1.6);
                        var limit = Math.Min(_horizon - 0.1, top) - radius * 1.8;
                        if (my <= limit) break;
                        if (limit >= TitleSafe.Bottom + radius) { my = limit; break; }
                        mx = Math.Clamp(mx - _side * 0.03, 0.12, 0.88);
                        radius = Math.Max(0.04, radius * 0.95);
                        my = TitleSafe.Bottom + radius + 0.02;
                    }
                    var moonDepth = _water is { Glade: true } ? _water.Depth : 0;
                    _moon = new MoonPlan(mx, my, radius, phase, LitFromRight: _side < 0, Glow: _c.MoonGlowStrength, Depth: moonDepth);
                }
            }

            // ---- Stars: Poisson-disc field, magnitude classes, horizon extinction ----
            var density = _c.StarDensity * (_underwater ? 0.0 : 1.0);
            if (density > 0.01) _stars = PlanStars(R("stars"), density);

            if (Has(PosterFeature.MilkyWay) || (dark && r.Chance(0.3) && _moon is null))
            {
                var angle = -_side * r.Range(28, 52);
                _milky = new MilkyWayPlan(0.5 + _side * 0.08, 0.40, angle, r.Range(0.16, 0.22), Seed("milky"));
                // Dense faint stars along the band.
                var extra = PlanBandStars(R("milky-stars"), _milky);
                _stars = [.. _stars, .. extra];
            }

            // ---- Clouds: few, long, low; never in the title band ----
            var cloudCount = Has(PosterFeature.Clouds) ? 2 + r.Int(2)
                : _spec.Scene == PosterScene.CloudKingdom ? 1 + r.Int(2)
                : _spec.Mood == PosterMood.Dreamy ? r.Int(3)
                : r.Chance(0.35) ? 1 : 0;
            if (_underwater) cloudCount = 0;
            for (var i = 0; i < cloudCount; i++)
            {
                var cr = R($"cloud-{i}");
                var w = cr.Range(0.22, 0.4);
                var h = w * cr.Range(0.16, 0.24);
                double cx, cy;
                var tries = 0;
                do
                {
                    cx = cr.Range(0.1, 0.9);
                    cy = cr.Range(TitleSafe.Bottom + h * 1.45 + 0.01, Math.Max(TitleSafe.Bottom + h * 1.45 + 0.02, _horizon - 0.08));
                    tries++;
                } while (tries < 12 && (_moon is not null && Math.Abs(cx - _moon.X) < w * 0.55 + _moon.R && Math.Abs(cy - _moon.Y) < h + _moon.R * 1.5
                                       || _clouds.Any(c => Math.Abs(c.X - cx) < (c.Width + w) * 0.45 && Math.Abs(c.Y - cy) < 0.08)));
                _clouds.Add(new CloudPlan(cx, cy, w, h, Seed($"cloud-shape-{i}"), cr.Range(0.75, 0.92)));
            }

            if (Has(PosterFeature.Aurora))
                _aurora = new AuroraPlan(Math.Min(_horizon - 0.12, r.Range(0.44, 0.5)), r.Range(0.12, 0.16), r.Range(0.05, 0.2), r.Range(0.8, 0.95), Seed("aurora"));

            if (Has(PosterFeature.ShootingStar))
            {
                var sx = 0.5 + _side * r.Range(0.06, 0.18);
                var sy = r.Range(TitleSafe.Bottom + 0.06, TitleSafe.Bottom + 0.12);
                // A shooting star and a flock never share the same patch of sky.
                if (_props.FirstOrDefault(p => p.Prop == Prop.Birds) is { } flock && Math.Abs(flock.Y - sy) < 0.1)
                    sx = 0.5 + _side * r.Range(0.12, 0.28);
                // Keep the whole streak clear of every cloud (it is drawn behind them anyway).
                for (var tries = 0; tries < 8 && _clouds.Any(c => Math.Abs(c.X - sx) < c.Width * 0.7 + 0.12 && Math.Abs(c.Y - sy) < c.Height * 2 + 0.05); tries++)
                    sx = 0.5 + _side * r.Range(0.0, 0.3);
                // The story asked for the shooting star: a decorative cloud in its way steps aside
                // (an asked-for cloud bank keeps at least one cloud).
                bool Blocks(CloudPlan c) => Math.Abs(c.X - sx) < c.Width * 0.7 + 0.12 && Math.Abs(c.Y - sy) < c.Height * 2 + 0.05;
                while (_clouds.Any(Blocks) && (_clouds.Count > 1 || !Has(PosterFeature.Clouds))) _clouds.Remove(_clouds.First(Blocks));
                if (_clouds.Any(Blocks)) sy = Math.Max(TitleSafe.Bottom + 0.03, _clouds.Min(c => c.Y - c.Height * 2.2) - 0.06);
                _shooting = new ShootingStarPlan(sx, sy, _side > 0 ? 200 + r.Range(8, 22) : -20 - r.Range(8, 22), r.Range(0.26, 0.32));
            }
        }

        private List<StarPlan> PlanStars(Rng r, double density)
        {
            // Bridson Poisson-disc in the sky (with bleed). r_min scales so density 1.0 ≈ 140
            // stars over the visible sky; magnitudes follow ~3× more stars per class.
            var minDist = 0.052 / Math.Sqrt(density);
            var pts = PoissonDisc(r, -0.2, 1.2, -0.2, _horizon - 0.02, minDist);
            var stars = new List<StarPlan>(pts.Count);
            foreach (var (x, y) in pts)
            {
                var u = Math.Max(1e-6, r.Next());
                var mag = Math.Min(4.5, -Math.Log(u) / Math.Log(3)); // 0 bright … 4.5 faint
                var b = Math.Pow(2.512, -mag);                      // 1 … ~0.016
                // Extinction toward the horizon, wash-out near the moon, a hush in the title band.
                var ext = Noise.SmoothStep(_horizon - 0.02, _horizon - 0.2, y);
                var moonWash = _moon is null ? 1 : Noise.SmoothStep(_moon.R * 1.6, _moon.R * 5.5, Dist(x, y, _moon.X, _moon.Y));
                if (moonWash < 0.05) continue;
                var inTitle = TitleSafe.Contains(x, y);
                if (inTitle)
                {
                    if (r.Chance(0.85)) continue;   // a hush behind the title
                    b = Math.Min(b, 0.08);
                }
                var alpha = Math.Clamp((0.22 + 0.78 * Math.Pow(b, 0.6)) * ext * moonWash, 0, 1);
                if (alpha < 0.06) continue;
                var size = 0.0016 + 0.0042 * Math.Pow(b, 0.7);
                var tint = r.Next() switch { < 0.7 => 0, < 0.85 => 1, _ => 2 };
                var sparkle = b > 0.85 && !inTitle && r.Chance(0.35);
                stars.Add(new StarPlan(x, y, size, alpha, tint, sparkle));
            }
            return stars;
        }

        private List<StarPlan> PlanBandStars(Rng r, MilkyWayPlan band)
        {
            var list = new List<StarPlan>();
            var rad = band.AngleDeg * Math.PI / 180;
            var (dx, dy) = (Math.Cos(rad), Math.Sin(rad));
            for (var i = 0; i < 150; i++)
            {
                var t = r.Range(-0.9, 0.9);
                var off = r.Bell() * band.Width * 0.45;
                var x = band.CenterX + dx * t - dy * off;
                var y = band.CenterY + dy * t + dx * off;
                if (y > _horizon - 0.03 || y < -0.2 || x < -0.2 || x > 1.2) continue;
                if (_moon is not null && Dist(x, y, _moon.X, _moon.Y) < _moon.R * 3) continue;
                if (TitleSafe.Contains(x, y) || y < TitleSafe.Bottom + 0.03) continue;
                var alpha = r.Range(0.12, 0.42) * Noise.SmoothStep(_horizon - 0.02, _horizon - 0.15, y);
                if (alpha < 0.05) continue;
                list.Add(new StarPlan(x, y, r.Range(0.0011, 0.0022), alpha, r.Chance(0.3) ? 1 : 0, false));
            }
            return list;
        }

        /// <summary>Highest point (smallest y) of any far/mid silhouette over [x0, x1].</summary>
        private double SilhouetteTop(double x0, double x1)
        {
            var top = 1.0;
            foreach (var b in _bands.Where(b => b.Depth <= 2))
                for (var x = x0; x <= x1; x += 0.005) top = Math.Min(top, b.Profile.Y(x));
            foreach (var p in _props)
            {
                // Full-height framers (trunks, vines, the owl's tree) occlude top to bottom;
                // anything else up to its height. Parallax travel is added to the reach so a
                // tilt can never slide an occluder across the moon.
                var tall = p.Prop is Prop.Trunk && p.Variant == 0 || p.Prop is Prop.Vines || p.Prop is Prop.Owl;
                var reach = (tall ? p.Size * 0.45 + 0.04 : p.Size * 0.6) + 0.06;
                var px = p.Prop is Prop.Owl ? (p.X > 0.5 ? 0.98 : 0.02) : p.X;
                if (px + reach < x0 || px - reach > x1) continue;
                top = Math.Min(top, tall ? 0 : p.Y - p.Size * 1.1);
            }
            return top;
        }

        private static double Dist(double ax, double ay, double bx, double by) => Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));

        /// <summary>Bridson (2007) Poisson-disc sampling: k = 30 candidates per active point
        /// from the annulus [r, 2r], grid cell r/√2 so each cell holds at most one sample.</summary>
        internal static List<(double X, double Y)> PoissonDisc(Rng rng, double x0, double x1, double y0, double y1, double minDist)
        {
            var result = new List<(double, double)>();
            if (x1 <= x0 || y1 <= y0) return result;
            var cell = minDist / Math.Sqrt(2);
            var gw = (int)Math.Ceiling((x1 - x0) / cell);
            var gh = (int)Math.Ceiling((y1 - y0) / cell);
            var grid = new int[gw * gh];
            Array.Fill(grid, -1);
            var active = new List<int>();

            void Add(double x, double y)
            {
                result.Add((x, y));
                var gx = (int)((x - x0) / cell);
                var gy = (int)((y - y0) / cell);
                grid[gy * gw + gx] = result.Count - 1;
                active.Add(result.Count - 1);
            }

            Add(rng.Range(x0, x1), rng.Range(y0, y1));
            while (active.Count > 0)
            {
                var ai = rng.Int(active.Count);
                var (px, py) = result[active[ai]];
                var found = false;
                for (var k = 0; k < 30; k++)
                {
                    var ang = rng.Next() * Math.PI * 2;
                    var rad = Math.Sqrt(minDist * minDist + rng.Next() * 3 * minDist * minDist);
                    var nx = px + Math.Cos(ang) * rad;
                    var ny = py + Math.Sin(ang) * rad;
                    if (nx < x0 || nx >= x1 || ny < y0 || ny >= y1) continue;
                    var gx = (int)((nx - x0) / cell);
                    var gy = (int)((ny - y0) / cell);
                    var ok = true;
                    for (var yy = Math.Max(0, gy - 2); yy <= Math.Min(gh - 1, gy + 2) && ok; yy++)
                    for (var xx = Math.Max(0, gx - 2); xx <= Math.Min(gw - 1, gx + 2); xx++)
                    {
                        var idx = grid[yy * gw + xx];
                        if (idx < 0) continue;
                        var (qx, qy) = result[idx];
                        if ((qx - nx) * (qx - nx) + (qy - ny) * (qy - ny) < minDist * minDist) { ok = false; break; }
                    }
                    if (!ok) continue;
                    Add(nx, ny);
                    found = true;
                    break;
                }
                if (!found) active.RemoveAt(ai);
            }
            return result;
        }

        // ------------------------------------------------------------------ ambient

        private void PlanAmbient()
        {
            var r = R("ambient");
            // Twinkle: 10–20% of the brighter stars, slow (2–6 s), opacity-only (WCAG 2.3.3
            // treats opacity change as non-motion).
            bool InCloud(double x, double y) => _clouds.Any(c => Math.Abs(x - c.X) < c.Width * 0.75 && y > c.Y - c.Height * 2.0 && y < c.Y + c.Height * 0.5);
            foreach (var s in _stars.Where(s => s.Alpha > 0.55 && !TitleSafe.Contains(s.X, s.Y) && !InCloud(s.X, s.Y)).Take(18))
            {
                if (!r.Chance(0.6)) continue;
                _ambient.Add(new AmbientParticle(AmbientKind.Twinkle, 0, s.X, s.Y, s.Size, s.Alpha,
                    r.Range(0, Math.PI * 2), r.Range(2.5, 6), r.Range(0.18, 0.32), Pack(ScenePalette.Cream)));
            }

            if (_fireflies)
            {
                var n = 16 + r.Int(6);
                for (var i = 0; i < n; i++)
                {
                    var depth = r.Chance(0.6) ? 2 : 3;
                    var x = r.Range(0.06, 0.94);
                    var ground = Ground(depth).Y(x);
                    var y = ground - r.Range(0.02, 0.14);
                    if (y < _horizon - 0.06) y = _horizon - 0.06 + r.Range(0, 0.04);
                    // Never a glow dot on the hero's face or coat.
                    if (_focal is { } h && depth >= h.Depth && Math.Abs(x - h.X) < h.Size * HalfWidth(h) + 0.02
                        && y > h.Y - h.Size - 0.02 && y < h.Y + 0.02) continue;
                    _ambient.Add(new AmbientParticle(AmbientKind.Firefly, depth, x, y, r.Range(0.008, 0.012) * (depth == 3 ? 1.3 : 1),
                        r.Range(0.55, 0.95), r.Range(0, Math.PI * 2), r.Range(14, 28), r.Range(0.008, 0.018), Pack(ScenePalette.AmberSoft)));
                }
            }

            if (_water is not null && _moon is not null && _water.Glade)
            {
                for (var i = 0; i < 14; i++)
                {
                    var y = _water.Line + Math.Pow(r.Next(), 1.4) * 0.3;
                    var spread = 0.01 + (y - _water.Line) * 0.25;
                    _ambient.Add(new AmbientParticle(AmbientKind.Glint, _water.Depth, _moon.X + r.Bell() * spread, y,
                        0.006 + (y - _water.Line) * 0.05, r.Range(0.35, 0.7), r.Range(0, Math.PI * 2), r.Range(2.2, 4.5), 0.004, Pack(ScenePalette.Cream)));
                }
            }

            if (_snowing)
            {
                for (var i = 0; i < 70; i++)
                {
                    var depth = 1 + r.Int(3);
                    var dTier = r.Next() switch { < 0.7 => 1, < 0.95 => 2, _ => 3 };   // most flakes far and tiny
                    depth = dTier;
                    _ambient.Add(new AmbientParticle(AmbientKind.Snowflake, depth, r.Range(-0.1, 1.1), r.Range(0, 1),
                        0.0018 * depth + r.Range(0, 0.0012), (0.38 + depth * 0.15) * r.Range(0.8, 1.0), r.Range(0, Math.PI * 2),
                        r.Range(22, 40) / depth, r.Range(0.01, 0.025), Pack(ScenePalette.Cream)));
                }
            }

            if (_underwater)
            {
                for (var i = 0; i < 22; i++)
                {
                    var bubble = i < 8;
                    _ambient.Add(new AmbientParticle(bubble ? AmbientKind.Bubble : AmbientKind.Mote, bubble ? 2 : 1 + r.Int(2),
                        r.Range(0.02, 0.98), r.Range(0.05, _horizon), bubble ? r.Range(0.004, 0.008) : r.Range(0.002, 0.004),
                        bubble ? r.Range(0.35, 0.6) : r.Range(0.25, 0.55), r.Range(0, Math.PI * 2), r.Range(18, 34), r.Range(0.01, 0.02),
                        Pack(bubble ? ScenePalette.Cream : new Oklch(0.82, 0.08, 185))));
                }
            }
        }

        private static uint Pack(Oklch c)
        {
            var sk = c.ToSk();
            return (uint)sk;
        }
    }
}
