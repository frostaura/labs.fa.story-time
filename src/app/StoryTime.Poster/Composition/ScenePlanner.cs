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
            _focalX = 0.5 + _side * (1.0 / 6.0) + r.Jitter(0.025);
        }

        private Rng R(string label) => _root.Fork(label);
        private bool Has(PosterFeature f) => _spec.Features.Contains(f);
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
            PlanSky();
            PlanAmbient();
        }

        public PosterPlan ToPlan() => new()
        {
            Spec = _spec,
            Colors = _c,
            Horizon = _horizon,
            Side = _side,
            Moon = _moon,
            Stars = _stars,
            MilkyWay = _milky,
            Clouds = _clouds,
            Aurora = _aurora,
            ShootingStar = _shooting,
            Bands = _bands,
            Water = _water,
            Placements = _props.OrderBy(p => p.Depth).ThenBy(p => p.Y).ToList(),
            Ambient = _ambient,
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
        private static double GroundedX(TerrainProfile ground, double x)
        {
            if (ground.Y(x) < 0.97) return x;
            for (var d = 0.02; d < 0.9; d += 0.02)
            {
                if (x + d <= 0.92 && ground.Y(x + d) < 0.93) return x + d + 0.04;
                if (x - d >= 0.08 && ground.Y(x - d) < 0.93) return x - d - 0.04;
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
                if (!blocked && ground.Y(x) < 1.0)
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

        private void MoonlitSea()
        {
            var r = R("sea");
            _horizon = 0.60 + r.Jitter(0.02);
            var headSide = Has(PosterFeature.Lighthouse) ? _side : (r.Chance(0.5) ? 1 : -1);

            // Far: the open sea, a long low headland on one side, a whisper of an isle.
            Band(1, Hills(_horizon, r.Range(0.045, 0.075), 2.2, "sea-head", 4, SideEnvelope(headSide, r.Range(0.35, 0.5)), _horizon + 0.004),
                0.08, TerrainStyle.Earth, fog: 0.3);
            Band(1, Hills(_horizon, 0.022, 4, "sea-isle", 2, Window(0.5 - headSide * r.Range(0.18, 0.3), 0.07), _horizon + 0.004),
                0.0, TerrainStyle.Earth, fog: 0.2, rim: false);
            _water = new WaterPlan(1, _horizon, Glade: true);

            // Mid: a rocky point on the headland side, when a lighthouse needs a perch.
            if (Has(PosterFeature.Lighthouse))
            {
                var cliff = Band(2, Mountains(_horizon + 0.14, r.Range(0.09, 0.12), 2.6, "sea-cliff",
                    SideEnvelope(headSide, 0.32, 0.09), 1.25), 0.55, TerrainStyle.Earth);
                var lx = 0.5 + headSide * r.Range(0.30, 0.36);
                Place(Prop.Lighthouse, 2, lx, cliff.Profile.Y(lx) + 0.006, r.Range(0.24, 0.28), "lighthouse",
                    flip: false, feature: PosterFeature.Lighthouse, lit: true);
            }

            else
            {
                // A low rocky point on the far side: the mid plane is never empty.
                Band(2, Mountains(_horizon + 0.12, r.Range(0.04, 0.06), 3.2, "sea-rocks",
                    SideEnvelope(-headSide, 0.22, 0.06), 1.25), 0.5, TerrainStyle.Earth);
            }

            // Near: a dune shore along the bottom, grass at the edges.
            var shore = Band(3, Hills(0.955, 0.06, 1.6, "sea-shore", 3), 1.0, TerrainStyle.Earth);
            Grass(3, shore.Profile, 1.0, "sea-grass");
            Place(Prop.Reeds, 3, 0.5 - _side * r.Range(0.36, 0.44), shore.Profile.Y(0.5 - _side * 0.4) + 0.01, r.Range(0.12, 0.16), "sea-reeds");
        }

        private void ForestClearing()
        {
            var r = R("forest");
            _horizon = 0.64 + r.Jitter(0.02);
            Band(1, Mountains(_horizon + 0.01, r.Range(0.12, 0.17), 1.5, "forest-mtn"), 0.0, TerrainStyle.Earth, fog: 0.4, snowCaps: false);
            var treeline = Band(1, Hills(_horizon + 0.035, 0.03, 3, "forest-far"), 0.28, TerrainStyle.Earth, fog: 0.25);
            Scatter(Prop.Pine, 1, treeline.Profile, -0.12, 1.12, 0.028, 0.05, 0.085, "forest-far-pines", 0.28);

            var meadow = Band(2, Hills(_horizon + 0.15, 0.035, 1.8, "forest-meadow"), 0.58, TerrainStyle.Earth);
            // Tall pines crowd both edges, leaving the clearing open around the focal third.
            var clear = (_focalX - 0.2, _focalX + 0.2);
            Scatter(Prop.Pine, 2, meadow.Profile, -0.15, 0.30, 0.055, 0.20, 0.32, "forest-pines-l", 0.58, [clear]);
            Scatter(Prop.Pine, 2, meadow.Profile, 0.70, 1.15, 0.055, 0.20, 0.32, "forest-pines-r", 0.58, [clear]);

            var floor = Band(3, Hills(0.95, 0.045, 1.4, "forest-floor"), 1.0, TerrainStyle.Earth);
            Grass(3, floor.Profile, 1.0, "forest-grass");
            var edge = 0.5 - _side * 0.53; // big framing pine just past the moon-side edge
            Place(Prop.Pine, 3, edge, floor.Profile.Y(edge) + 0.02, r.Range(0.55, 0.68), "forest-frame", variant: 1);
            Place(Prop.Fern, 3, 0.5 - _side * 0.34, floor.Profile.Y(0.5 - _side * 0.34) + 0.012, 0.09, "forest-fern");
        }

        private void MountainLake()
        {
            var r = R("lake");
            _horizon = 0.60 + r.Jitter(0.02);
            Band(1, Mountains(_horizon, r.Range(0.25, 0.30), 1.3, "lake-mtn-a"), 0.0, TerrainStyle.Earth, fog: 0.35, snowCaps: true);
            Band(1, Mountains(_horizon, r.Range(0.13, 0.17), 2.1, "lake-mtn-b"), 0.18, TerrainStyle.Earth, fog: 0.4, snowCaps: true);
            var line = Band(1, Hills(_horizon + 0.004, 0.012, 5, "lake-line"), 0.32, TerrainStyle.Earth, rim: false);
            Scatter(Prop.Pine, 1, line.Profile, -0.12, 1.12, 0.022, 0.035, 0.06, "lake-far-pines", 0.32);
            _water = new WaterPlan(1, _horizon + 0.006, Glade: true);

            var shoreSide = -_side;
            var shore = Band(2, Hills(_horizon + 0.17, 0.07, 2.2, "lake-shore", 3, SideEnvelope(shoreSide, 0.36), 1.25), 0.6, TerrainStyle.Earth);
            Scatter(Prop.Pine, 2, shore.Profile, shoreSide > 0 ? 0.72 : -0.15, shoreSide > 0 ? 1.15 : 0.28, 0.05, 0.16, 0.26, "lake-pines", 0.6);

            var near = Band(3, Hills(0.965, 0.05, 1.6, "lake-near"), 1.0, TerrainStyle.Earth);
            Place(Prop.Rock, 3, 0.5 + _side * 0.42, near.Profile.Y(0.5 + _side * 0.42) + 0.02, 0.12, "lake-rock");
            Place(Prop.Reeds, 3, 0.5 - _side * 0.40, near.Profile.Y(0.5 - _side * 0.40) + 0.01, 0.14, "lake-reeds");
            Grass(3, near.Profile, 0.6, "lake-grass");
        }

        private void FireflyMeadow()
        {
            var r = R("meadow");
            _horizon = 0.66 + r.Jitter(0.02);
            Band(1, Hills(_horizon, r.Range(0.06, 0.09), 1.6, "meadow-far"), 0.05, TerrainStyle.Earth, fog: 0.35);
            Band(1, Hills(_horizon + 0.05, 0.06, 2.0, "meadow-far2"), 0.32, TerrainStyle.Earth, fog: 0.3);
            var hill = Band(2, Hills(_horizon + 0.16, r.Range(0.06, 0.09), 1.3, "meadow-hill"), 0.62, TerrainStyle.Earth);
            var tx = 0.5 - _side * r.Range(0.26, 0.32);
            Place(Prop.RoundTree, 2, tx, hill.Profile.Y(tx) + 0.008, r.Range(0.26, 0.32), "meadow-tree");
            var near = Band(3, Hills(0.95, 0.05, 1.5, "meadow-near"), 1.0, TerrainStyle.Earth);
            Grass(3, near.Profile, 1.8, "meadow-grass");
            Place(Prop.Bush, 3, 0.5 - _side * 0.42, near.Profile.Y(0.5 - _side * 0.42) + 0.03, r.Range(0.22, 0.28), "meadow-bush");
            _fireflies = true;
        }

        private void SnowyVillage()
        {
            var r = R("snow");
            _horizon = 0.62 + r.Jitter(0.02);
            Band(1, Mountains(_horizon + 0.01, r.Range(0.18, 0.24), 1.4, "snow-mtn"), 0.0, TerrainStyle.Snow, fog: 0.3, snowCaps: true);
            var slope = Band(2, Hills(_horizon + 0.14, 0.05, 1.5, "snow-slope"), 0.5, TerrainStyle.Snow);
            // The village gathers on the side away from the focal subject.
            var vc = 0.5 - _side * 0.12;
            var count = 3 + r.Int(3);
            var w = 0.11;
            var start = vc - (count - 1) * w / 2;
            for (var i = 0; i < count; i++)
            {
                var hx = start + i * w + r.Jitter(0.015);
                Place(Prop.House, 2, hx, slope.Profile.Y(hx) + 0.012, r.Range(0.10, 0.14), $"snow-house-{i}",
                    variant: r.Int(3), lit: r.Chance(0.55), feature: PosterFeature.Village);
            }
            Scatter(Prop.Pine, 2, slope.Profile, -0.12, start - 0.06, 0.05, 0.12, 0.2, "snow-pines-a", 0.5, variant: 2);
            Scatter(Prop.Pine, 2, slope.Profile, start + count * w, 1.12, 0.06, 0.12, 0.2, "snow-pines-b", 0.5,
                [(_focalX - 0.1, _focalX + 0.1)], variant: 2);
            var drift = Band(3, Hills(0.94, 0.05, 1.3, "snow-drift"), 1.0, TerrainStyle.Snow);
            // A short run of fence posts leading out of frame on the village side.
            var fx = 0.5 - _side * 0.30;
            Place(Prop.Fence, 3, fx, drift.Profile.Y(fx) + 0.012, 0.26, "snow-fence", flip: _side > 0);
            var edge = 0.5 + _side * 0.54;
            Place(Prop.Pine, 3, edge, drift.Profile.Y(edge) + 0.02, r.Range(0.5, 0.6), "snow-frame", variant: 3);
            _snowing = Has(PosterFeature.Snowfall) || r.Chance(0.6);
        }

        private void DesertOasis()
        {
            var r = R("desert");
            _horizon = 0.66 + r.Jitter(0.02);
            Band(1, Dunes(_horizon, r.Range(0.06, 0.09), 1.4, "desert-far"), 0.05, TerrainStyle.Sand, fog: 0.3);
            Band(1, Dunes(_horizon + 0.06, 0.06, 1.9, "desert-far2"), 0.32, TerrainStyle.Sand, fog: 0.25);
            var mid = Band(2, Hills(_horizon + 0.16, 0.03, 1.2, "desert-mid", 2), 0.6, TerrainStyle.Sand);
            var ox = 0.5 - _side * r.Range(0.12, 0.2);
            Place(Prop.Pool, 2, ox, mid.Profile.Y(ox) + 0.025, r.Range(0.20, 0.26), "oasis-pool", flip: false);
            var palms = 2 + r.Int(2);
            for (var i = 0; i < palms; i++)
            {
                var px = ox + (i - (palms - 1) / 2.0) * 0.09 + r.Jitter(0.02);
                Place(Prop.Palm, 2, px, mid.Profile.Y(px) + 0.012, r.Range(0.22, 0.30), $"oasis-palm-{i}", flip: px > ox);
            }
            var near = Band(3, Dunes(0.97, 0.09, 1.1, "desert-near"), 1.0, TerrainStyle.Sand);
            _ = near;
        }

        private void IslandCove()
        {
            var r = R("cove");
            _horizon = 0.60 + r.Jitter(0.02);
            Band(1, Hills(_horizon, 0.02, 4, "cove-isle", 2, Window(0.5 + _side * r.Range(0.12, 0.25), 0.06), _horizon + 0.004),
                0.0, TerrainStyle.Earth, fog: 0.2, rim: false);
            _water = new WaterPlan(1, _horizon, Glade: true);
            var isleSide = -_side;
            var isle = Band(2, Hills(_horizon + 0.10, 0.06, 2.4, "cove-island", 3, SideEnvelope(isleSide, 0.34, 0.1), 1.25),
                0.55, TerrainStyle.Sand);
            var palms = 2 + r.Int(2);
            for (var i = 0; i < palms; i++)
            {
                var px = 0.5 + isleSide * (0.30 + i * 0.07 + r.Jitter(0.02));
                Place(Prop.Palm, 2, px, isle.Profile.Y(px) + 0.01, r.Range(0.20, 0.28), $"cove-palm-{i}", flip: isleSide < 0);
            }
            var beach = Band(3, Hills(0.965, 0.035, 1.3, "cove-beach", 2), 1.0, TerrainStyle.Sand);
            Grass(3, beach.Profile, 0.5, "cove-grass");
            var px0 = 0.5 + _side * 0.52;
            Place(Prop.Palm, 3, px0, beach.Profile.Y(px0) + 0.02, r.Range(0.62, 0.72), "cove-frame-palm", flip: _side > 0);
        }

        private void StarryHills()
        {
            var r = R("hills");
            _horizon = 0.70 + r.Jitter(0.02);
            Band(1, Hills(_horizon, 0.05, 1.7, "hills-far"), 0.05, TerrainStyle.Earth, fog: 0.35);
            Band(1, Hills(_horizon + 0.045, 0.06, 1.4, "hills-far2"), 0.3, TerrainStyle.Earth, fog: 0.25);
            var hill = Band(2, Hills(_horizon + 0.15, r.Range(0.09, 0.12), 1.1, "hills-mid"), 0.62, TerrainStyle.Earth);
            if (!Has(PosterFeature.Tent) && !Has(PosterFeature.Telescope) && !Has(PosterFeature.Windmill) && r.Chance(0.6))
            {
                var tx = 0.5 - _side * r.Range(0.22, 0.3);
                Place(Prop.RoundTree, 2, tx, hill.Profile.Y(tx) + 0.008, r.Range(0.2, 0.26), "hills-tree");
            }
            var near = Band(3, Hills(0.955, 0.05, 1.5, "hills-near"), 1.0, TerrainStyle.Earth);
            Grass(3, near.Profile, 0.8, "hills-grass");
        }

        private void ReefAtDusk()
        {
            var r = R("reef");
            _underwater = true;
            _horizon = 0.70 + r.Jitter(0.02);
            Band(1, Bumps(_horizon, 0.04, 0.09, 0.8, "reef-far"), 0.05, TerrainStyle.Reef, fog: 0.55, rim: false);
            var mid = Band(2, Bumps(_horizon + 0.13, 0.05, 0.11, 0.7, "reef-mid"), 0.55, TerrainStyle.Reef, fog: 0.2);
            Scatter(Prop.BranchCoral, 2, mid.Profile, -0.05, 1.05, 0.16, 0.10, 0.16, "reef-corals", 0.55,
                [(_focalX - 0.12, _focalX + 0.12)]);
            Place(Prop.FanCoral, 2, 0.5 - _side * 0.3, mid.Profile.Y(0.5 - _side * 0.3) + 0.01, r.Range(0.16, 0.22), "reef-fan");
            var floor = Band(3, Bumps(0.97, 0.05, 0.12, 0.6, "reef-floor"), 1.0, TerrainStyle.Reef);
            Place(Prop.Kelp, 3, 0.5 - _side * 0.47, floor.Profile.Y(0.5 - _side * 0.47) + 0.02, r.Range(0.5, 0.62), "reef-kelp-a");
            Place(Prop.Kelp, 3, 0.5 + _side * 0.5, floor.Profile.Y(0.5 + _side * 0.5) + 0.02, r.Range(0.36, 0.46), "reef-kelp-b");
            Place(Prop.BranchCoral, 3, 0.5 + _side * 0.36, floor.Profile.Y(0.5 + _side * 0.36) + 0.015, r.Range(0.13, 0.18), "reef-near-coral");
        }

        private void CloudKingdom()
        {
            var r = R("clouds");
            _horizon = 0.66 + r.Jitter(0.02);
            Band(1, Bumps(_horizon + 0.04, 0.035, 0.1, 1.0, "ck-far"), 0.1, TerrainStyle.Cloud, fog: 0.2);
            Band(2, Bumps(_horizon + 0.2, 0.06, 0.17, 1.0, "ck-mid"), 0.5, TerrainStyle.Cloud, fog: 0.1);
            Band(3, Bumps(1.04, 0.1, 0.22, 1.0, "ck-near"), 0.85, TerrainStyle.Cloud);
        }

        private void TownRooftops()
        {
            var r = R("town");
            _horizon = 0.64 + r.Jitter(0.02);
            Band(1, Roofs(_horizon + 0.04, 0.06, 0.11, 0.05, 0.10, "town-far", tower: true), 0.12, TerrainStyle.Roofs, fog: 0.3, rim: false);
            Band(2, Roofs(_horizon + 0.17, 0.10, 0.17, 0.08, 0.16, "town-mid", tower: false), 0.55, TerrainStyle.Roofs, fog: 0.1);
            // The near roof: one long ridge across the bottom, a chimney at the edge.
            var near = Band(3, Hills(0.90, 0.012, 0.8, "town-near", 2), 1.0, TerrainStyle.Roofs);
            var cx = 0.5 - _side * 0.38;
            Place(Prop.Trunk, 3, cx, near.Profile.Y(cx) + 0.01, 0.16, "town-chimney", variant: 1);
        }

        private TerrainProfile Roofs(double wallTop, double wMin, double wMax, double roofMin, double roofMax, string label, bool tower)
        {
            var r = R(label);
            var list = new List<Building>();
            var x = -0.3 + r.Range(0, 0.05);
            var i = 0;
            var towerUsed = false;
            while (x < 1.3)
            {
                var w = r.Range(wMin, wMax);
                var top = wallTop + r.Jitter(0.025);
                var roof = Math.Clamp(w * r.Range(0.3, 0.5), roofMin * 0.5, roofMax);
                var kind = tower && !towerUsed && x > 0.15 && x < 0.85 && r.Chance(0.25) ? RoofKind.Dome
                    : r.Chance(0.15) ? RoofKind.Flat : RoofKind.Gable;
                if (kind == RoofKind.Dome) { towerUsed = true; top -= 0.035; roof = w * 0.45; }
                list.Add(new Building(x, x + w, top, roof, kind, r.Chance(0.5), r.Range(0.2, 0.8), 1 + r.Int(3),
                    (uint)(r.NextULong() & 0xFFFFFFFF)));
                x += w - r.Range(0.002, 0.012); // overlapping walls: never a sliver of sky between houses
                i++;
            }
            return new TerrainProfile { Kind = TerrainKind.Rooftops, Base = wallTop + 0.05, Buildings = list };
        }

        private void JungleCanopy()
        {
            var r = R("jungle");
            _horizon = 0.62 + r.Jitter(0.02);
            Band(1, Bumps(_horizon, 0.025, 0.06, 0.8, "jungle-far"), 0.05, TerrainStyle.Canopy, fog: 0.45, rim: false);
            Band(2, Bumps(_horizon + 0.12, 0.05, 0.10, 0.75, "jungle-mid"), 0.5, TerrainStyle.Canopy, fog: 0.15);
            var tx = 0.5 - _side * r.Range(0.43, 0.47);
            Place(Prop.Trunk, 2, tx, _horizon + 0.2, r.Range(0.4, 0.5), "jungle-trunk", flip: _side > 0);
            Place(Prop.Vines, 2, 0.5 + _side * 0.44, 0.0, r.Range(0.32, 0.42), "jungle-vines-a");
            Place(Prop.Vines, 2, 0.5 - _side * 0.47, 0.0, r.Range(0.26, 0.36), "jungle-vines-b");
            var floor = Band(3, Hills(0.97, 0.03, 1.4, "jungle-floor", 2), 1.0, TerrainStyle.Canopy);
            Place(Prop.BigLeaf, 3, 0.5 - _side * 0.50, floor.Profile.Y(0.5 - _side * 0.5) + 0.02, r.Range(0.42, 0.5), "jungle-leaf-a", flip: _side > 0);
            Place(Prop.BigLeaf, 3, 0.5 + _side * 0.53, floor.Profile.Y(0.5 + _side * 0.53) + 0.02, r.Range(0.34, 0.42), "jungle-leaf-b", flip: _side < 0, variant: 2);
            Place(Prop.Fern, 3, 0.5 + _side * 0.30, floor.Profile.Y(0.5 + _side * 0.3) + 0.01, 0.12, "jungle-fern");
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

            foreach (var f in _spec.Features)
            {
                if (PosterSpecRules.IsCreature(f)) continue;
                var asFocal = _focal is null && !structureUsedFocal && (PosterSpecRules.IsStructure(f) || PosterSpecRules.IsVehicle(f));
                var x = GroundedX(mid, asFocal ? _focalX : otherX);
                switch (f)
                {
                    case PosterFeature.Lighthouse:
                        if (_spec.Scene == PosterScene.IslandCove)
                        {
                            var isle = _bands.Last(b => b.Depth == 2).Profile;
                            var lx = 0.5 - _side * 0.34;
                            Place(Prop.Lighthouse, 2, lx, isle.Y(lx) + 0.006, r.Range(0.2, 0.24), "lighthouse", flip: false, feature: f, lit: true);
                        }
                        break; // MoonlitSea placed it on its cliff already
                    case PosterFeature.Cabin:
                        Place(Prop.Cabin, 2, x, mid.Y(x) + 0.01, asFocal ? r.Range(0.20, 0.24) : r.Range(0.15, 0.18), "cabin", feature: f, lit: true);
                        break;
                    case PosterFeature.Village:
                        if (_spec.Scene is PosterScene.SnowyVillage or PosterScene.TownRooftops) break; // the scene IS a village
                        for (var i = 0; i < 4; i++)
                        {
                            var hx = otherX + (i - 1.5) * 0.045;
                            Place(Prop.House, 1, hx, far.Y(hx) + 0.006, r.Range(0.045, 0.06), $"far-house-{i}", variant: r.Int(3),
                                lit: r.Chance(0.6), feature: f, tone: 0.3);
                        }
                        break;
                    case PosterFeature.Castle:
                    {
                        var depth = _spec.Scene is PosterScene.CloudKingdom ? 1 : 1;
                        var cx = 0.5 - _side * r.Range(0.05, 0.14);
                        Place(Prop.Castle, depth, cx, Ground(1).Y(cx) + 0.012, r.Range(0.17, 0.22), "castle", feature: f, lit: true, tone: 0.2);
                        break;
                    }
                    case PosterFeature.Windmill:
                        Place(Prop.Windmill, 2, x, mid.Y(x) + 0.01, asFocal ? 0.30 : 0.24, "windmill", feature: f);
                        break;
                    case PosterFeature.Tent:
                        Place(Prop.Tent, 2, x, mid.Y(x) + 0.008, asFocal ? 0.2 : 0.15, "tent", feature: f, lit: true);
                        break;
                    case PosterFeature.Lantern:
                    {
                        var lx = _focal is not null ? _focal.X - _side * 0.11 : x;
                        Place(Prop.Lantern, 3, lx, near.Y(lx) + 0.01, 0.2, "lantern", feature: f, lit: true);
                        break;
                    }
                    case PosterFeature.Telescope:
                        Place(Prop.Telescope, 2, x, mid.Y(x) + 0.006, asFocal ? 0.2 : 0.16, "telescope", flip: _side > 0, feature: f);
                        break;
                    case PosterFeature.Boat:
                    case PosterFeature.Sailboat:
                        if (_water is null) break;
                        var by = _water.Line + r.Range(0.06, 0.12);
                        var bx = asFocal ? _focalX : 0.5 + _side * r.Range(0.02, 0.1);
                        Place(f == PosterFeature.Boat ? Prop.Boat : Prop.Sailboat, 2, bx, by,
                            f == PosterFeature.Boat ? r.Range(0.12, 0.15) : r.Range(0.17, 0.21), "boat", feature: f, lit: f == PosterFeature.Boat);
                        break;
                    case PosterFeature.Balloon:
                    {
                        var moonX = 0.5 - _side * 0.25;
                        var bx2 = moonX + _side * 0.24;
                        Place(Prop.Balloon, 2, bx2, r.Range(0.40, 0.46), r.Range(0.15, 0.19), "balloon", feature: f, lit: true);
                        break;
                    }
                    case PosterFeature.Train:
                    {
                        var tx = 0.5 + r.Jitter(0.08);
                        Place(Prop.Train, 1, tx, far.Y(tx) + 0.004, r.Range(0.05, 0.06), "train", flip: _side > 0, feature: f, lit: true, tone: 0.3);
                        break;
                    }
                    case PosterFeature.Birds:
                        Place(Prop.Birds, 1, 0.5 + _side * r.Range(0.05, 0.15), r.Range(0.36, 0.44), r.Range(0.12, 0.16), "birds", feature: f, tone: 0.2);
                        break;
                    case PosterFeature.Flowers:
                        for (var i = 0; i < 9; i++)
                        {
                            var fx = r.Chance(0.5) ? r.Range(0.02, 0.32) : r.Range(0.68, 0.98);
                            Place(Prop.Flower, 3, fx, near.Y(fx) + r.Range(0.004, 0.03), r.Range(0.035, 0.055), $"flower-{i}", feature: f);
                        }
                        break;
                    case PosterFeature.Mushrooms:
                    {
                        var mx = _focal is not null ? _focal.X + _side * 0.14 : otherX;
                        for (var i = 0; i < 3; i++)
                        {
                            var x2 = mx + i * 0.03 * _side;
                            Place(Prop.Mushroom, 3, x2, near.Y(x2) + 0.012, r.Range(0.035, 0.06), $"mushroom-{i}", feature: f, lit: i == 0);
                        }
                        break;
                    }
                    case PosterFeature.Fireflies:
                        _fireflies = true;
                        break;
                    case PosterFeature.Fish:
                    {
                        var fx = _focal is not null ? 0.5 - _side * 0.12 : _focalX;
                        var school = Place(Prop.FishSchool, 3, fx, r.Range(0.42, 0.5), r.Range(0.3, 0.38), "fish", flip: _side < 0, feature: f);
                        _focal ??= school;
                        break;
                    }
                    case PosterFeature.Snowfall:
                        _snowing = true;
                        break;
                }
                if (asFocal && f is not (PosterFeature.Lighthouse or PosterFeature.Village or PosterFeature.Castle
                        or PosterFeature.Train or PosterFeature.Birds or PosterFeature.Flowers or PosterFeature.Mushrooms
                        or PosterFeature.Fireflies or PosterFeature.Snowfall))
                {
                    structureUsedFocal = true;
                    _focal = _props.LastOrDefault(p => p.Feature == f);
                }
            }
        }

        private Placement PlaceCreature(PosterFeature creature, TerrainProfile near, TerrainProfile mid, Rng r)
        {
            var x = _focalX;
            var faceIn = _side > 0; // face toward the centre / the moon
            switch (creature)
            {
                case PosterFeature.Whale:
                    if (_underwater)
                        return Place(Prop.Whale, 1, 0.5 - _side * 0.12, r.Range(0.44, 0.5), r.Range(0.42, 0.5), "whale", flip: !faceIn, variant: 1, feature: creature, tone: 0.1);
                    return Place(Prop.Whale, 2, x, (_water?.Line ?? _horizon) + r.Range(0.07, 0.1), r.Range(0.16, 0.2), "whale", flip: faceIn, feature: creature);
                case PosterFeature.Otter:
                    return Place(Prop.Otter, 2, x, (_water?.Line ?? _horizon) + r.Range(0.08, 0.12), r.Range(0.19, 0.23), "otter", flip: faceIn, feature: creature);
                case PosterFeature.Turtle:
                    if (_underwater)
                        return Place(Prop.Turtle, 2, x, r.Range(0.46, 0.54), r.Range(0.17, 0.21), "turtle", flip: faceIn, feature: creature);
                    return Place(Prop.Turtle, 3, x, near.Y(x) + 0.008, r.Range(0.16, 0.2), "turtle", flip: faceIn, variant: 1, feature: creature);
                case PosterFeature.Owl:
                {
                    var ox = 0.5 + _side * 0.33;
                    return Place(Prop.Owl, 3, ox, _horizon - 0.02 + r.Jitter(0.03), r.Range(0.15, 0.18), "owl", flip: _side < 0, feature: creature);
                }
                case PosterFeature.Dinosaur:
                    x = GroundedX(mid, x);
                    return Place(Prop.Dinosaur, 2, x, mid.Y(x) + 0.01, r.Range(0.3, 0.36), "dinosaur", flip: faceIn, feature: creature);
                case PosterFeature.Cat:
                    return Place(Prop.Cat, 3, x, near.Y(x) + 0.004, r.Range(0.19, 0.23), "cat", flip: faceIn, feature: creature);
                case PosterFeature.Camel:
                    return Place(Prop.Camel, 3, x, near.Y(x) + 0.008, r.Range(0.24, 0.28), "camel", flip: faceIn, feature: creature);
                case PosterFeature.Deer:
                    return Place(Prop.Deer, 3, x, near.Y(x) + 0.008, r.Range(0.24, 0.28), "deer", flip: faceIn, feature: creature);
                case PosterFeature.Bear:
                    return Place(Prop.Bear, 3, x, near.Y(x) + 0.008, r.Range(0.23, 0.27), "bear", flip: faceIn, feature: creature);
                case PosterFeature.Hedgehog:
                    return Place(Prop.Hedgehog, 3, x, near.Y(x) + 0.008, r.Range(0.16, 0.19), "hedgehog", flip: faceIn, feature: creature);
                case PosterFeature.Rabbit:
                    return Place(Prop.Rabbit, 3, x, near.Y(x) + 0.008, r.Range(0.21, 0.25), "rabbit", flip: faceIn, feature: creature);
                case PosterFeature.Fox:
                default:
                {
                    var curled = _spec.Mood is PosterMood.Sleepy or PosterMood.Calm or PosterMood.Dreamy && r.Chance(0.45);
                    return Place(curled ? Prop.FoxCurled : Prop.Fox, 3, x, near.Y(x) + 0.008,
                        curled ? r.Range(0.2, 0.24) : r.Range(0.24, 0.28), "fox", flip: faceIn, feature: creature);
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
                    var radius = time == PosterTimeOfDay.Moonlit ? r.Range(0.058, 0.072) : r.Range(0.042, 0.056);
                    var phase = time switch
                    {
                        PosterTimeOfDay.Moonlit => r.Chance(0.6) ? 0 : r.Range(0.15, 0.3),
                        PosterTimeOfDay.Twilight => r.Range(0.35, 0.65),
                        _ => r.Range(0.62, 0.78),
                    };
                    var mx = 0.5 - _side * r.Range(0.22, 0.28);
                    var my = TitleSafe.Bottom + radius + r.Range(0.03, 0.09);
                    my = Math.Min(my, _horizon - radius - 0.12);
                    _moon = new MoonPlan(mx, my, radius, phase, LitFromRight: _side < 0, Glow: _c.MoonGlowStrength);
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
                _shooting = new ShootingStarPlan(sx, r.Range(TitleSafe.Bottom + 0.04, TitleSafe.Bottom + 0.1), _side > 0 ? 200 + r.Range(8, 22) : -20 - r.Range(8, 22), r.Range(0.14, 0.2));
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
                if (inTitle) b = Math.Min(b, 0.16);
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
            for (var i = 0; i < 260; i++)
            {
                var t = r.Range(-0.9, 0.9);
                var off = r.Bell() * band.Width * 0.45;
                var x = band.CenterX + dx * t - dy * off;
                var y = band.CenterY + dy * t + dx * off;
                if (y > _horizon - 0.03 || y < -0.2 || x < -0.2 || x > 1.2) continue;
                if (_moon is not null && Dist(x, y, _moon.X, _moon.Y) < _moon.R * 3) continue;
                var inTitle = TitleSafe.Contains(x, y);
                var alpha = r.Range(0.12, inTitle ? 0.3 : 0.5) * Noise.SmoothStep(_horizon - 0.02, _horizon - 0.15, y);
                if (alpha < 0.05) continue;
                list.Add(new StarPlan(x, y, r.Range(0.0011, 0.0022), alpha, r.Chance(0.3) ? 1 : 0, false));
            }
            return list;
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
            foreach (var s in _stars.Where(s => s.Alpha > 0.55 && !TitleSafe.Contains(s.X, s.Y)).Take(18))
            {
                if (!r.Chance(0.6)) continue;
                _ambient.Add(new AmbientParticle(AmbientKind.Twinkle, 0, s.X, s.Y, s.Size, s.Alpha,
                    r.Range(0, Math.PI * 2), r.Range(2.5, 6), r.Range(0.18, 0.32), Pack(ScenePalette.Cream)));
            }

            if (_fireflies)
            {
                var n = 9 + r.Int(6);
                for (var i = 0; i < n; i++)
                {
                    var depth = r.Chance(0.6) ? 2 : 3;
                    var x = r.Range(0.06, 0.94);
                    var ground = Ground(depth).Y(x);
                    var y = ground - r.Range(0.02, 0.14);
                    if (y < _horizon - 0.06) y = _horizon - 0.06 + r.Range(0, 0.04);
                    _ambient.Add(new AmbientParticle(AmbientKind.Firefly, depth, x, y, r.Range(0.004, 0.007) * (depth == 3 ? 1.3 : 1),
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
                for (var i = 0; i < 46; i++)
                {
                    var depth = 1 + r.Int(3);
                    _ambient.Add(new AmbientParticle(AmbientKind.Snowflake, depth, r.Range(-0.1, 1.1), r.Range(0, 1),
                        0.0025 * depth + r.Range(0, 0.002), r.Range(0.45, 0.85) * (0.6 + depth * 0.15), r.Range(0, Math.PI * 2),
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
