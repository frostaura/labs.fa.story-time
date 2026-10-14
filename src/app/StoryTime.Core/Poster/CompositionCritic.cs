using StoryTime.Contracts;

namespace StoryTime.Core.Poster;

/// <summary>One judged poster candidate: its component scores (each 0..1 except the
/// crowding penalty, which subtracts) and the weighted total.</summary>
public sealed record CompositionScore(
    string Candidate,
    double Balance,
    double DepthUsage,
    double Focal,
    double Relevance,
    double PaletteFit,
    double CrowdingPenalty,
    double Total)
{
    public override string ToString() =>
        $"{Candidate}: total {Total:F3} (balance {Balance:F2}, depth {DepthUsage:F2}, focal {Focal:F2}, " +
        $"relevance {Relevance:F2}, palette {PaletteFit:F2}, crowding -{CrowdingPenalty:F2})";
}

/// <summary>Deterministic poster judge — no model, pure math over a VALIDATED spec. The
/// poster stage asks the LLM for two formations, derives one from the template engine as a
/// safety net, and this critic picks the winner. Scores, in weight order: focal presence +
/// placement (0.25), rule-of-thirds mass balance (0.20), motif-story relevance (0.20),
/// depth usage across the four layers (0.15), palette-mood fit (0.10), minus a crowding
/// penalty from pairwise bounding-box IoU (up to 0.10). Same spec + outline → same score,
/// on every platform (no RNG, no dictionaries iterated in unspecified order).</summary>
public sealed class CompositionCritic
{
    // ---- weights (public so tests pin the contract) ----
    public const double FocalWeight = 0.25;
    public const double BalanceWeight = 0.20;
    public const double RelevanceWeight = 0.20;
    public const double DepthWeight = 0.15;
    public const double PaletteWeight = 0.10;
    public const double CrowdingWeight = 0.10;

    /// <summary>Scores one validated candidate against the story outline.</summary>
    public CompositionScore Score(string candidate, PosterSpec validated, StoryOutline outline)
    {
        var all = validated.Layers.SelectMany(l => l.Elements).ToList();
        var story = StoryText(outline);

        var balance = ScoreBalance(all);
        var depth = ScoreDepthUsage(validated);
        var focal = ScoreFocal(validated);
        var relevance = ScoreRelevance(all, story);
        var palette = ScorePaletteFit(validated.Palette, story);
        var crowding = ScoreCrowding(validated);

        var total = FocalWeight * focal + BalanceWeight * balance + RelevanceWeight * relevance
                  + DepthWeight * depth + PaletteWeight * palette - CrowdingWeight * crowding;
        return new CompositionScore(candidate, balance, depth, focal, relevance, palette, crowding, total);
    }

    /// <summary>Judges candidates and returns the winner's index (ties break toward the
    /// earlier candidate, so LLM candidates listed first beat the template on equal art).</summary>
    public (int WinnerIndex, IReadOnlyList<CompositionScore> Scores) Judge(
        IReadOnlyList<(string Name, PosterSpec Validated)> candidates, StoryOutline outline)
    {
        var scores = candidates.Select(c => Score(c.Name, c.Validated, outline)).ToList();
        var winner = 0;
        for (var i = 1; i < scores.Count; i++)
            if (scores[i].Total > scores[winner].Total) winner = i;
        return (winner, scores);
    }

    // ---------------- components ----------------

    /// <summary>Rule-of-thirds mass balance: visual mass (scale, creatures doubled — they
    /// pull the eye) per thirds column. Full marks when no column hoards more than half the
    /// mass; a bonus when the focal and the moon anchor DIFFERENT thirds (the asymmetric
    /// composition the art direction asks for).</summary>
    private static double ScoreBalance(List<PosterElement> all)
    {
        Span<double> columns = stackalloc double[3];
        PosterElement? moon = null, focal = null;
        foreach (var e in all)
        {
            if (IsScatterish(e.Shape) || IsTerrainOrWater(e.Shape)) continue;
            var mass = e.Scale * (IsCreature(e.Shape) ? 2.0 : 1.0);
            columns[ColumnOf(e.X)] += mass;
            if (e.Shape is PosterShape.Moon or PosterShape.Crescent) moon = e;
            if (IsFocalCandidate(e.Shape)) focal ??= e;
        }
        var totalMass = columns[0] + columns[1] + columns[2];
        if (totalMass <= 0) return 0.3; // an empty sky is "balanced" but boring
        var maxShare = Math.Max(columns[0], Math.Max(columns[1], columns[2])) / totalMass;
        var spread = 1 - Math.Clamp((maxShare - 0.5) / 0.5, 0, 1);
        var asymmetry = moon is not null && focal is not null && ColumnOf(moon.X) != ColumnOf(focal.X) ? 1.0 : 0.0;
        return Math.Clamp(0.75 * spread + 0.25 * asymmetry, 0, 1);
    }

    /// <summary>All four depth layers carrying at least one element = full marks.</summary>
    private static double ScoreDepthUsage(PosterSpec validated) =>
        validated.Layers.Count(l => l.Elements.Count > 0) / 4.0;

    /// <summary>A foreground focal (creature or lit dwelling), placed well: presence is
    /// most of the score; placement rewards a base inside the focal band and an anchor near
    /// a thirds line rather than dead center or hugging an edge.</summary>
    private static double ScoreFocal(PosterSpec validated)
    {
        var fore = validated.Layers.FirstOrDefault(l => l.Depth == 3)?.Elements ?? [];
        var focal = fore.FirstOrDefault(e => IsFocalCandidate(e.Shape));
        if (focal is null) return 0;
        var yFit = focal.Y is >= PosterComposer.FocalYMin and <= PosterComposer.FocalYMax ? 1.0
            : 1.0 - Math.Clamp(Math.Min(Math.Abs(focal.Y - PosterComposer.FocalYMin), Math.Abs(focal.Y - PosterComposer.FocalYMax)) / 0.15, 0, 1);
        var thirdsDistance = Math.Min(Math.Abs(focal.X - 1.0 / 3), Math.Abs(focal.X - 2.0 / 3));
        var xFit = 1.0 - Math.Clamp(thirdsDistance / 0.22, 0, 1);
        return Math.Clamp(0.6 + 0.2 * yFit + 0.2 * xFit, 0, 1);
    }

    /// <summary>Motif-story relevance: each interest group the outline's words light up
    /// wants at least one of its shapes on the poster. No groups lit → neutral 0.5.</summary>
    private static double ScoreRelevance(List<PosterElement> all, string story)
    {
        var groups = 0;
        var hits = 0;
        foreach (var (keywords, shapes) in InterestGroups)
        {
            if (!ContainsAny(story, keywords)) continue;
            groups++;
            if (all.Any(e => shapes.Contains(e.Shape))) hits++;
        }
        return groups == 0 ? 0.5 : (double)hits / groups;
    }

    /// <summary>Palette-mood fit: the chosen palette should belong to a family the story's
    /// words point at. No pointer → neutral 0.5; a pointed-at family missed → 0.25.</summary>
    private static double ScorePaletteFit(PosterPalette palette, string story)
    {
        var pointed = false;
        foreach (var (keywords, palettes) in PaletteGroups)
        {
            if (!ContainsAny(story, keywords)) continue;
            pointed = true;
            if (palettes.Contains(palette)) return 1.0;
        }
        return pointed ? 0.25 : 0.5;
    }

    /// <summary>Pairwise bounding-box IoU over grounded motifs (structures, creatures,
    /// flora, water-bound) — overlapping silhouettes read as clutter. 0 = clean.</summary>
    private static double ScoreCrowding(PosterSpec validated)
    {
        var boxes = validated.Layers
            .Where(l => l.Depth >= 2)
            .SelectMany(l => l.Elements)
            .Where(e => IsGroundedMotif(e.Shape))
            .Select(BoxOf)
            .ToList();
        var overlap = 0.0;
        for (var i = 0; i < boxes.Count; i++)
            for (var j = i + 1; j < boxes.Count; j++)
                overlap += IoU(boxes[i], boxes[j]);
        return Math.Clamp(overlap * 3.0, 0, 1);
    }

    // ---------------- shape knowledge ----------------

    private static bool IsScatterish(PosterShape s) => s is PosterShape.Stars or PosterShape.StarCluster
        or PosterShape.Snowfall or PosterShape.FireflyScatter or PosterShape.FlowerScatter
        or PosterShape.MistBand or PosterShape.Aurora;

    private static bool IsTerrainOrWater(PosterShape s) => s is PosterShape.Hills or PosterShape.Mountain
        or PosterShape.ForestRow or PosterShape.Path or PosterShape.Water;

    private static bool IsCreature(PosterShape s) => s is PosterShape.FoxSilhouette or PosterShape.Rabbit
        or PosterShape.Owl or PosterShape.DeerSilhouette or PosterShape.WhaleTail;

    private static bool IsFocalCandidate(PosterShape s) => IsCreature(s)
        || s is PosterShape.WindowGlow or PosterShape.Tent or PosterShape.Lantern;

    private static bool IsGroundedMotif(PosterShape s) => IsCreature(s)
        || s is PosterShape.WindowGlow or PosterShape.Tent or PosterShape.Lantern or PosterShape.Lighthouse
        or PosterShape.Pine or PosterShape.RoundTree or PosterShape.Reeds
        or PosterShape.Boat or PosterShape.Sailboat;

    private static int ColumnOf(double x) => x < 1.0 / 3 ? 0 : x < 2.0 / 3 ? 1 : 2;

    /// <summary>Approximate silhouette bounding box: grounded shapes anchor their BASE at y
    /// and rise about one scale-height; width is about one scale.</summary>
    private static (double L, double T, double R, double B) BoxOf(PosterElement e) =>
        (e.X - e.Scale * 0.55, e.Y - e.Scale * 1.1, e.X + e.Scale * 0.55, e.Y);

    private static double IoU((double L, double T, double R, double B) a, (double L, double T, double R, double B) b)
    {
        var iw = Math.Min(a.R, b.R) - Math.Max(a.L, b.L);
        var ih = Math.Min(a.B, b.B) - Math.Max(a.T, b.T);
        if (iw <= 0 || ih <= 0) return 0;
        var inter = iw * ih;
        var union = (a.R - a.L) * (a.B - a.T) + (b.R - b.L) * (b.B - b.T) - inter;
        return union <= 0 ? 0 : inter / union;
    }

    // ---------------- story knowledge (ordered arrays — deterministic iteration) ----------------

    private static string StoryText(StoryOutline outline) => string.Join(' ',
        outline.Settings.Concat(outline.Characters.Select(c => c.Kind)).Append(outline.Title))
        .ToLowerInvariant();

    private static bool ContainsAny(string story, string[] keywords)
    {
        foreach (var k in keywords)
            if (story.Contains(k, StringComparison.Ordinal)) return true;
        return false;
    }

    private static readonly (string[] Keywords, PosterShape[] Shapes)[] InterestGroups =
    [
        (["ocean", "sea", "shore", "tide", "wave", "river", "lake", "harbor", "whale", "boat", "sail"],
            [PosterShape.Water, PosterShape.WhaleTail, PosterShape.Boat, PosterShape.Sailboat, PosterShape.Lighthouse, PosterShape.Reeds]),
        (["fox", "fern", "den"], [PosterShape.FoxSilhouette]),
        (["rabbit", "bunny", "hare"], [PosterShape.Rabbit]),
        (["owl"], [PosterShape.Owl]),
        (["deer", "fawn"], [PosterShape.DeerSilhouette]),
        (["star", "constellation", "wish", "comet"], [PosterShape.StarCluster, PosterShape.ShootingStar, PosterShape.Aurora]),
        (["snow", "winter", "frost"], [PosterShape.Snowfall, PosterShape.Aurora]),
        (["forest", "pine", "glade", "wood", "cedar", "tree"], [PosterShape.Pine, PosterShape.ForestRow, PosterShape.RoundTree]),
        (["garden", "meadow", "flower", "blossom"], [PosterShape.FlowerScatter, PosterShape.RoundTree, PosterShape.FireflyScatter]),
        (["lantern", "lamp"], [PosterShape.Lantern, PosterShape.WindowGlow]),
        (["camp", "tent"], [PosterShape.Tent]),
        (["cottage", "window", "home", "house", "valley", "workshop", "junction", "village"], [PosterShape.WindowGlow, PosterShape.Lantern]),
        (["cloud"], [PosterShape.Cloud]),
        (["mist", "fog"], [PosterShape.MistBand]),
        (["firefly", "glow"], [PosterShape.FireflyScatter, PosterShape.Lantern, PosterShape.WindowGlow]),
        (["mountain", "peak", "ridge"], [PosterShape.Mountain]),
        (["bird"], [PosterShape.BirdFlock, PosterShape.Owl]),
    ];

    private static readonly (string[] Keywords, PosterPalette[] Palettes)[] PaletteGroups =
    [
        (["ocean", "sea", "shore", "tide", "harbor", "whale", "boat"],
            [PosterPalette.OceanDeep, PosterPalette.TidePool, PosterPalette.NightHarbor]),
        (["snow", "winter", "frost"], [PosterPalette.SnowHush, PosterPalette.MistValley]),
        (["forest", "pine", "glade", "wood", "fern", "moss"],
            [PosterPalette.PineForest, PosterPalette.MoonMeadow]),
        (["star", "space", "sky", "constellation", "comet"],
            [PosterPalette.StarfieldViolet, PosterPalette.PlumDusk, PosterPalette.CloudDrift]),
        (["lantern", "lamp", "cozy", "workshop", "junction", "ember", "hearth"],
            [PosterPalette.LanternGlow, PosterPalette.EmberSky]),
        (["garden", "meadow", "flower"], [PosterPalette.MoonMeadow, PosterPalette.PlumDusk]),
        (["cloud"], [PosterPalette.CloudDrift]),
        (["mist", "fog"], [PosterPalette.MistValley]),
    ];
}
