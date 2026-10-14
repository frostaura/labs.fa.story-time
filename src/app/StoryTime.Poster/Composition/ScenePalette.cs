using StoryTime.Contracts;
using StoryTime.Poster.Color;

namespace StoryTime.Poster.Composition;

/// <summary>Every colour one poster uses, derived in OKLCH from (palette, scene, time of
/// day, mood). A palette is a picture-book colour script, not a hex list: a sky hue, a
/// warm horizon glow hue, a land hue and their chroma. Time of day sets the value ramp
/// (dusk is a lit, glowing sky; starlit the deepest), mood nudges chroma, warmth and haze.
///
/// Depth is built by atmospheric perspective — each ground band is mixed toward the
/// horizon haze in OKLab by its distance — so a far ridge is lighter and greyer than the
/// near one by construction. The near ground keeps real local colour (moss, plum, umber),
/// never a black cut-out: posters must read as pictures, not as dark wallpaper.
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
    /// <summary>The transitional band between the mid sky and the glow (rose at dusk).</summary>
    public required Oklch SkyLow { get; init; }
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
    /// <summary>The colour warm light casts on the things around it (a lantern pool).</summary>
    public required Oklch WarmLight { get; init; }
    public required Oklch Snow { get; init; }
    public required Oklch CloudLit { get; init; }
    public required Oklch CloudShade { get; init; }
    public required Oklch Accent { get; init; }
    /// <summary>A second, warmer accent (flowers, blankets, pennants).</summary>
    public required Oklch Accent2 { get; init; }
    /// <summary>The night's ambient colour that local colours are graded toward.</summary>
    public required Oklch Ambient { get; init; }
    /// <summary>How much of a creature's local colour survives the night (0..1).</summary>
    public required double LocalColor { get; init; }
    public required double StarDensity { get; init; }
    public required double Chroma { get; init; }
    public required bool Snowy { get; init; }

    /// <summary>Ground/silhouette colour at normalized depth t (0 = the farthest ridge on
    /// the horizon, 1 = the nearest foreground), atmospheric perspective included.</summary>
    public Oklch Ground(double t)
    {
        t = Math.Clamp(t, 0, 1);
        var near = LandNear;
        var haze = Haze.ToOklab();
        var baseColor = Snowy
            ? Snow with { L = Snow.L - 0.06 * t }
            : near with { L = near.L + (1 - t) * 0.08 };
        var mixed = Oklab.Mix(baseColor.ToOklab(), haze, Math.Clamp((1 - t) * (0.72 + HazeAmount * 0.25), 0, 0.92));
        return mixed.ToOklch();
    }

    /// <summary>A creature's or object's local colour, graded into this night: value pulled
    /// toward the scene's light level, hue nudged toward the ambient, chroma kept so a fox
    /// still reads orange and a turtle green — the picture-book convention.</summary>
    public Oklch Grade(Oklch local, double keep = 1.0)
    {
        var k = Math.Clamp(LocalColor * keep, 0, 1);
        var mixed = Oklab.Mix(Ambient.ToOklab(), local.ToOklab(), k).ToOklch();
        return mixed with { L = Math.Clamp(local.L * (0.55 + 0.4 * k) + Ambient.L * 0.18, 0.12, 0.86) };
    }

    /// <summary>A palette's colour script: sky (zenith) hue, glow hue, land hue, chroma.</summary>
    internal readonly record struct Script(double SkyHue, double GlowHue, double LandHue, double SkyC, double LandC, double GlowC);

    public static ScenePalette Build(PosterSpec spec)
    {
        var f = Family(spec.Palette);
        // Two posters in the same palette are cousins, not twins: the seed nudges the sky
        // and glow hues within the family.
        var jr = new Core.Rng(spec.Seed ^ 0x5CA1E);
        f = f with { SkyHue = f.SkyHue + jr.Range(-9, 9), GlowHue = f.GlowHue + jr.Range(-18, 18) };
        var skyHue = f.SkyHue;
        var scene = spec.Scene;
        var mood = spec.Mood;

        // ---- Time of day: the sky's value ramp, how much glow, how much local colour ----
        double zL, mL, hL, glowStrength, stars, glowMix, moonGlow, rimStrength, local;
        switch (spec.TimeOfDay)
        {
            case PosterTimeOfDay.Dusk:
                zL = 0.37; mL = 0.48; hL = 0.70; glowStrength = 0.75; stars = 0.12; glowMix = 0.85; moonGlow = 0.45; rimStrength = 0.42; local = 0.92;
                break;
            case PosterTimeOfDay.Twilight:
                zL = 0.30; mL = 0.40; hL = 0.64; glowStrength = 0.45; stars = 0.45; glowMix = 0.62; moonGlow = 0.6; rimStrength = 0.42; local = 0.85;
                break;
            case PosterTimeOfDay.Starlit:
                zL = 0.21; mL = 0.285; hL = 0.47; glowStrength = 0.14; stars = 0.78; glowMix = 0.22; moonGlow = 0.55; rimStrength = 0.34; local = 0.7;
                break;
            default: // Moonlit
                zL = 0.25; mL = 0.34; hL = 0.56; glowStrength = 0.22; stars = 0.5; glowMix = 0.35; moonGlow = 0.75; rimStrength = 0.55; local = 0.76;
                break;
        }

        // Underwater the "sky" is the water column: keep it a deep, calm teal so the
        // light shafts and the title band stay quiet.
        if (spec.Scene == PosterScene.ReefAtDusk) { zL *= 0.78; mL *= 0.8; hL *= 0.82; }

        // ---- Mood: chroma, haze, warmth, lightness ----
        var chroma = 1.0;
        var haze = 0.35;
        var lift = 0.0;
        var warm = 0.0;
        var hueShift = 0.0;
        switch (mood)
        {
            case PosterMood.Cozy: warm = 0.35; chroma = 1.05; break;
            case PosterMood.Dreamy: haze = 0.6; chroma = 0.9; lift = 0.025; break;
            case PosterMood.Magical: chroma = 1.15; hueShift = 8; stars *= 1.15; break;
            case PosterMood.Sleepy: lift = -0.03; chroma = 0.85; stars *= 0.6; haze = 0.45; break;
            case PosterMood.Curious: chroma = 1.08; lift = 0.01; break;
        }
        skyHue += hueShift;
        var glowHue = f.GlowHue + (warm > 0 ? -6 : 0);

        var zenith = new Oklch(zL + lift, f.SkyC * chroma, skyHue + 4);
        var mid = new Oklch(mL + lift, f.SkyC * chroma * 1.05, skyHue);
        // The low sky turns toward the glow through the shortest hue arc (blue → rose →
        // peach at dusk) in LCH, so a dusk ramp blushes instead of greying out.
        var low = new Oklch((mL + hL) / 2 + lift, Math.Max(f.SkyC, f.GlowC * 0.7) * chroma, HueLerp(skyHue, glowHue, glowMix * 0.55));
        var horizon = new Oklch(hL + lift, Math.Max(f.SkyC * 0.9, f.GlowC * glowMix) * chroma, HueLerp(skyHue, glowHue, glowMix));
        var glow = new Oklch(Math.Min(0.86, hL + 0.1 + lift), f.GlowC * chroma * (0.6 + 0.6 * glowMix), glowHue);
        if (warm > 0) glowStrength = Math.Min(1, glowStrength + warm * 0.25);

        var hazeColor = Oklab.Mix(horizon.ToOklab(), mid.ToOklab(), 0.35).ToOklch();

        // ---- Land: local colour kept, values a clear step below the sky ----
        var landL = scene switch
        {
            PosterScene.DesertOasis => 0.24,
            PosterScene.CloudKingdom => 0.34,
            PosterScene.ReefAtDusk => 0.24,
            _ => 0.215,
        } + (spec.TimeOfDay switch { PosterTimeOfDay.Dusk => 0.04, PosterTimeOfDay.Twilight => 0.02, PosterTimeOfDay.Starlit => -0.03, _ => 0 }) + lift * 0.5;
        var landHue = warm > 0 ? HueLerp(f.LandHue, 50, 0.2) : f.LandHue;
        if (scene == PosterScene.DesertOasis) landHue = HueLerp(landHue, 55, 0.6);
        var landNear = new Oklch(landL, Math.Min(0.09, f.LandC * chroma + warm * 0.012), landHue);
        var foliage = new Oklch(landL + 0.02, Math.Min(0.1, f.LandC * chroma * 1.25 + 0.01), HueLerp(landHue, 145, 0.35));

        var snowy = scene == PosterScene.SnowyVillage;
        var snow = new Oklch((spec.TimeOfDay == PosterTimeOfDay.Dusk ? 0.6 : spec.TimeOfDay == PosterTimeOfDay.Twilight ? 0.52 : 0.5) + lift,
            0.035, HueLerp(skyHue, 265, 0.5));

        var waterTop = horizon with { L = hL * 0.86 + lift, C = horizon.C * 0.85 };
        var waterDeep = zenith with { L = zL * 0.8 + lift, C = zenith.C * 0.95 };

        // A hair dimmer than the cream title, so the moon never out-shouts it.
        // A warm ivory, dimmer than the cream title and the lantern core: the moon sets the
        // night but never outranks the hero's warm light.
        var moon = new Oklch(0.84, 0.035, 85);
        var moonGlowColor = Oklab.Mix(Cream.ToOklab(), horizon.ToOklab(), 0.45).ToOklch();
        var rim = spec.TimeOfDay == PosterTimeOfDay.Dusk
            ? new Oklch(0.8, 0.09, HueLerp(glowHue, 60, 0.5))
            : Oklab.Mix(Cream.ToOklab(), mid.ToOklab(), 0.35).ToOklch();

        var window = Amber with { L = Amber.L + warm * 0.03 };
        var windowGlow = AmberSoft;
        var warmLight = new Oklch(0.82, 0.11, 70);

        var cloudLit = Oklab.Mix(Cream.ToOklab(), low.ToOklab(), scene == PosterScene.CloudKingdom ? 0.4 : 0.55).ToOklch();
        var cloudShade = mid with { L = mL + 0.05 + lift, C = mid.C * 0.85 };

        // Small details (flowers, balloon stripes, blankets) from the palette's own
        // complement and its glow — kept soft, never candy.
        var accent = new Oklch(0.68, 0.085 * chroma, skyHue + 150);
        var accent2 = new Oklch(0.72, 0.1 * chroma, HueLerp(glowHue, 15, 0.5));
        var ambient = Oklab.Mix(mid.ToOklab(), horizon.ToOklab(), 0.3).ToOklch();

        return new ScenePalette
        {
            SkyZenith = zenith,
            SkyMid = mid,
            SkyLow = low,
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
            WarmLight = warmLight,
            Snow = snow,
            CloudLit = cloudLit,
            CloudShade = cloudShade,
            Accent = accent,
            Accent2 = accent2,
            Ambient = ambient,
            LocalColor = Math.Clamp(local * (mood == PosterMood.Sleepy ? 0.92 : 1.0), 0, 1),
            StarDensity = Math.Clamp(stars, 0, 1.2),
            Chroma = chroma,
            Snowy = snowy,
        };
    }

    /// <summary>Each palette's colour script. Calm and bedtime-appropriate, but with real
    /// range: amber and rose dusks, teal, moss, sunset gold and soft lilac alongside the
    /// blues — no longer a set of navies.</summary>
    internal static Script Family(PosterPalette p) => p switch
    {
        //                                      sky  glow  land  skyC   landC  glowC
        PosterPalette.NightHarbor => new Script(232, 52, 222, 0.070, 0.045, 0.10),     // blue hour, peach rim
        PosterPalette.MoonMeadow => new Script(236, 20, 140, 0.060, 0.060, 0.09),      // lilac to rose, moss
        PosterPalette.OceanDeep => new Script(214, 186, 204, 0.072, 0.050, 0.07),      // deep teal
        PosterPalette.PlumDusk => new Script(338, 48, 328, 0.075, 0.050, 0.11),        // rose-plum, peach
        PosterPalette.PineForest => new Script(214, 78, 150, 0.064, 0.062, 0.10),      // teal-blue over pines, gold
        PosterPalette.CloudDrift => new Script(228, 352, 330, 0.055, 0.04, 0.1),         // powder blue, pink, rose clouds
        PosterPalette.StarfieldViolet => new Script(288, 330, 286, 0.080, 0.050, 0.09),// violet, orchid
        PosterPalette.LanternGlow => new Script(42, 72, 38, 0.060, 0.050, 0.12),       // amber dusk
        PosterPalette.TidePool => new Script(196, 32, 176, 0.068, 0.050, 0.10),        // teal, coral
        PosterPalette.SnowHush => new Script(222, 334, 236, 0.055, 0.030, 0.08),        // rose-blue snow
        PosterPalette.EmberSky => new Script(14, 62, 28, 0.055, 0.05, 0.11),          // sunset gold
        PosterPalette.MistValley => new Script(188, 102, 162, 0.05, 0.052, 0.09),      // misty teal, pale gold, sage land
        _ => new Script(232, 52, 222, 0.070, 0.045, 0.10),
    };

    /// <summary>Shortest-arc hue interpolation in degrees.</summary>
    public static double HueLerp(double a, double b, double t)
    {
        var d = ((b - a) % 360 + 540) % 360 - 180;
        return (a + d * t + 360) % 360;
    }
}
