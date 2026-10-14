using StoryTime.Contracts;
using StoryTime.Poster.Color;

namespace StoryTime.Poster.Composition;

/// <summary>Every colour one poster uses, derived in OKLCH from (palette, scene, time of
/// day, mood). Palettes are hue families, not hex lists: the sky hue and the land hue are
/// the palette's identity, time of day sets the lightness ramp, mood nudges chroma, warmth
/// and haze. Depth is built by atmospheric perspective — each ground band is mixed toward
/// the horizon haze in OKLab by its distance — so a far ridge is lighter, greyer and
/// bluer than the near one by construction.
///
/// Hearth anchors: the moon and stars are the app's cream (#F3E9DC), lit windows and
/// lanterns the app's amber (#E8A857). Those two never change with the palette.</summary>
public sealed class ScenePalette
{
    public static readonly Oklch Cream = Oklab.FromHex("#F3E9DC").ToOklch();
    public static readonly Oklch Amber = Oklab.FromHex("#E8A857").ToOklch();
    public static readonly Oklch AmberSoft = Oklab.FromHex("#F2C580").ToOklch();

    public required Oklch SkyZenith { get; init; }
    public required Oklch SkyMid { get; init; }
    public required Oklch SkyHorizon { get; init; }
    /// <summary>Warm (dusk) or cool afterglow band hugging the horizon.</summary>
    public required Oklch HorizonGlow { get; init; }
    public required double HorizonGlowStrength { get; init; }
    public required Oklch Haze { get; init; }
    /// <summary>Atmospheric haze amount 0..1 (mood "Dreamy" raises it).</summary>
    public required double HazeAmount { get; init; }
    public required Oklch LandNear { get; init; }
    public required Oklch Foliage { get; init; }
    public required Oklch WaterTop { get; init; }
    public required Oklch WaterDeep { get; init; }
    public required Oklch Moon { get; init; }
    public required Oklch MoonGlow { get; init; }
    public required double MoonGlowStrength { get; init; }
    public required Oklch Rim { get; init; }
    public required double RimStrength { get; init; }
    public required Oklch Window { get; init; }
    public required Oklch WindowGlow { get; init; }
    public required Oklch Snow { get; init; }
    public required Oklch CloudLit { get; init; }
    public required Oklch CloudShade { get; init; }
    public required Oklch Accent { get; init; }
    public required double StarDensity { get; init; }
    public required double Chroma { get; init; }
    public required bool Snowy { get; init; }

    /// <summary>Ground/silhouette colour at normalized depth t (0 = the farthest ridge on
    /// the horizon, 1 = the nearest foreground), atmospheric perspective included.</summary>
    public Oklch Ground(double t)
    {
        t = Math.Clamp(t, 0, 1);
        var near = LandNear;
        // Far bands sit just below the haze in lightness and lose chroma.
        var haze = Haze.ToOklab();
        var baseColor = Snowy
            ? Snow with { L = Snow.L - 0.06 * t }
            : near with { L = near.L + (1 - t) * 0.05 };
        var mixed = Oklab.Mix(baseColor.ToOklab(), haze, Math.Clamp((1 - t) * (0.72 + HazeAmount * 0.25), 0, 0.92));
        return mixed.ToOklch();
    }

    public static ScenePalette Build(PosterSpec spec)
    {
        var (skyHue, landHue, skyC, landC) = Family(spec.Palette);
        var scene = spec.Scene;
        var mood = spec.Mood;

        // ---- Time of day: the sky's lightness ramp and the stars it allows ----
        double zL, mL, hL, glowStrength, stars, glowHue, moonGlow, rimStrength;
        switch (spec.TimeOfDay)
        {
            case PosterTimeOfDay.Dusk:
                zL = 0.29; mL = 0.38; hL = 0.55; glowStrength = 0.85; stars = 0.18; glowHue = 58; moonGlow = 0.45; rimStrength = 0.35;
                break;
            case PosterTimeOfDay.Twilight:
                zL = 0.235; mL = 0.31; hL = 0.45; glowStrength = 0.35; stars = 0.55; glowHue = 40; moonGlow = 0.65; rimStrength = 0.38;
                break;
            case PosterTimeOfDay.Starlit:
                zL = 0.165; mL = 0.215; hL = 0.315; glowStrength = 0.10; stars = 1.0; glowHue = skyHue; moonGlow = 0.55; rimStrength = 0.28;
                break;
            default: // Moonlit
                zL = 0.205; mL = 0.27; hL = 0.39; glowStrength = 0.15; stars = 0.5; glowHue = skyHue; moonGlow = 1.0; rimStrength = 0.55;
                break;
        }

        // ---- Mood: chroma, haze, warmth, lightness ----
        var chroma = 1.0;
        var haze = 0.35;
        var lift = 0.0;
        var warm = 0.0;
        var hueShift = 0.0;
        switch (mood)
        {
            case PosterMood.Cozy: warm = 0.35; chroma = 1.02; break;
            case PosterMood.Dreamy: haze = 0.6; chroma = 0.85; lift = 0.02; break;
            case PosterMood.Magical: chroma = 1.18; hueShift = 8; stars *= 1.15; break;
            case PosterMood.Sleepy: lift = -0.04; chroma = 0.78; stars *= 0.55; haze = 0.45; break;
            case PosterMood.Curious: chroma = 1.08; lift = 0.01; break;
        }
        skyHue += hueShift;

        var sky = new Oklch(0, skyC * chroma, skyHue);
        var zenith = sky with { L = zL + lift, H = skyHue + 6 };
        var mid = sky with { L = mL + lift, C = skyC * chroma * 1.05 };
        // The horizon drifts toward the glow hue and loses a little chroma (scattering).
        var horizonHue = HueLerp(skyHue, glowHue, spec.TimeOfDay == PosterTimeOfDay.Dusk ? 0.45 : 0.12);
        var horizon = new Oklch(hL + lift, skyC * chroma * 0.9, horizonHue);
        var glow = spec.TimeOfDay == PosterTimeOfDay.Dusk
            ? new Oklch(0.66 + lift, 0.10 * chroma, glowHue)
            : new Oklch(hL + 0.06 + lift, 0.05 * chroma, glowHue);
        if (warm > 0) glowStrength = Math.Min(1, glowStrength + warm * 0.25);

        var hazeColor = Oklab.Mix(horizon.ToOklab(), mid.ToOklab(), 0.25).ToOklch();

        // ---- Land: dark, calm, a quiet echo of the sky ----
        var landL = scene switch
        {
            PosterScene.DesertOasis => 0.19,
            PosterScene.CloudKingdom => 0.30,
            PosterScene.ReefAtDusk => 0.21,
            _ => 0.165,
        } + lift * 0.5;
        // Cozy warms the land a touch toward the hearth.
        var landNear = new Oklch(landL, landC * chroma + warm * 0.01, warm > 0 ? HueLerp(landHue, 45, 0.25) : landHue);
        var foliage = new Oklch(landL - 0.01, landC * chroma * 1.15, HueLerp(landHue, 150, 0.25));

        var snowy = scene == PosterScene.SnowyVillage;
        var snow = new Oklch((spec.TimeOfDay == PosterTimeOfDay.Dusk ? 0.46 : 0.41) + lift, 0.03, HueLerp(skyHue, 265, 0.6));

        var waterTop = horizon with { L = hL * 0.88 + lift, C = horizon.C * 0.9 };
        var waterDeep = zenith with { L = zL * 0.72 + lift, C = zenith.C * 0.9 };

        // A hair dimmer than the cream title, so the moon never out-shouts it.
        var moon = Cream with { L = 0.885 };
        var moonGlowColor = Oklab.Mix(Cream.ToOklab(), horizon.ToOklab(), 0.35).ToOklch();
        var rim = spec.TimeOfDay == PosterTimeOfDay.Dusk
            ? new Oklch(0.72, 0.09, 55)
            : Oklab.Mix(Cream.ToOklab(), mid.ToOklab(), 0.42).ToOklch();

        var window = Amber with { L = Amber.L + warm * 0.03 };
        var windowGlow = AmberSoft;

        var cloudLit = Oklab.Mix(Cream.ToOklab(), mid.ToOklab(), scene == PosterScene.CloudKingdom ? 0.42 : 0.62).ToOklch();
        var cloudShade = mid with { L = mL + 0.04 + lift, C = mid.C * 0.8 };

        // A soft second accent for small details (flowers, balloon stripes) from the
        // palette's own complement, kept quiet.
        var accent = new Oklch(0.62, 0.07 * chroma, skyHue + 150);

        return new ScenePalette
        {
            SkyZenith = zenith,
            SkyMid = mid,
            SkyHorizon = horizon,
            HorizonGlow = glow,
            HorizonGlowStrength = glowStrength,
            Haze = hazeColor,
            HazeAmount = haze,
            LandNear = landNear,
            Foliage = foliage,
            WaterTop = waterTop,
            WaterDeep = waterDeep,
            Moon = moon,
            MoonGlow = moonGlowColor,
            MoonGlowStrength = moonGlow,
            Rim = rim,
            RimStrength = rimStrength,
            Window = window,
            WindowGlow = windowGlow,
            Snow = snow,
            CloudLit = cloudLit,
            CloudShade = cloudShade,
            Accent = accent,
            StarDensity = Math.Clamp(stars, 0, 1.2),
            Chroma = chroma,
            Snowy = snowy,
        };
    }

    /// <summary>(sky hue, land hue, sky chroma, land chroma) for each palette family.</summary>
    internal static (double SkyHue, double LandHue, double SkyC, double LandC) Family(PosterPalette p) => p switch
    {
        PosterPalette.NightHarbor => (284, 278, 0.068, 0.040),
        PosterPalette.MoonMeadow => (252, 150, 0.055, 0.040),
        PosterPalette.OceanDeep => (226, 214, 0.062, 0.042),
        PosterPalette.PlumDusk => (326, 316, 0.064, 0.040),
        PosterPalette.PineForest => (232, 162, 0.050, 0.040),
        PosterPalette.CloudDrift => (282, 276, 0.042, 0.028),
        PosterPalette.StarfieldViolet => (296, 290, 0.080, 0.046),
        PosterPalette.LanternGlow => (262, 48, 0.045, 0.034),
        PosterPalette.TidePool => (212, 175, 0.054, 0.038),
        PosterPalette.SnowHush => (256, 250, 0.045, 0.028),
        PosterPalette.EmberSky => (22, 30, 0.036, 0.034),
        PosterPalette.MistValley => (236, 226, 0.032, 0.024),
        _ => (284, 278, 0.068, 0.040),
    };

    /// <summary>Shortest-arc hue interpolation in degrees.</summary>
    public static double HueLerp(double a, double b, double t)
    {
        var d = ((b - a) % 360 + 540) % 360 - 180;
        return (a + d * t + 360) % 360;
    }
}
