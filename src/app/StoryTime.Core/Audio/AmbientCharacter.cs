using StoryTime.Contracts;

namespace StoryTime.Core.Audio;

/// <summary>The musical character a palette resolves to: harmony, register, texture and
/// motion parameters for the ambient composer. Chord voicings are semitone offsets from
/// <see cref="RootMidi"/>; the arp scale is the pitch set sparse plucks draw from
/// (an octave above the pad register).</summary>
public sealed record AmbientCharacter(
    string Name,
    int RootMidi,
    int[][] Chords,          // 4 voicings, back-to-front over the loop
    int[] ArpScale,          // semitone offsets for pluck events
    double TempoBpm,         // felt as slow swells + the pluck grid, ~48–56
    double NoiseLevel,       // pink-noise wash gain 0..~0.2 (pre-normalization, relative)
    double NoiseWaveDepth,   // 0..1 amplitude LFO on the wash (ocean waves / wind gusts)
    double NoiseCutoffHz,    // wash lowpass
    double CutoffHz,         // master lowpass base
    double LfoDepth,         // master-cutoff LFO movement 0..1 (fraction of base)
    double ArpPerChord,      // average pluck events per chord segment
    double PadBrightness);   // 0..1 harmonic content of the pad wavetable

/// <summary>Palette → musical-character map. Twelve palettes fold into six distinct,
/// hand-designed characters so the beds feel curated rather than randomized.</summary>
public static class AmbientMoodMap
{
    // -- Six characters ------------------------------------------------------------------

    /// <summary>Dorian calm — Dm7 · B♭maj7 · Gm7 · Cmaj7 around D3. Night skies and harbors.</summary>
    public static readonly AmbientCharacter NightCalm = new(
        "night-calm", 50,
        [[-12, 0, 7, 10, 15], [-4, 0, 3, 7, 12], [-7, 0, 3, 8, 12], [-2, 2, 5, 9, 14]],
        [12, 15, 17, 19, 22, 24, 27, 29],
        52, 0.05, 0.20, 800, 900, 0.35, 3.0, 0.50);

    /// <summary>Warm major-7 — Fmaj7 · Dm7 · B♭maj7 · C9sus around F3. Ember and lantern light.</summary>
    public static readonly AmbientCharacter WarmEmber = new(
        "warm-ember", 53,
        [[-12, 0, 4, 7, 11], [-15, -3, 4, 7, 11], [-7, 0, 4, 9, 12], [-17, -5, 2, 9, 14]],
        [12, 14, 16, 19, 21, 23, 26, 28],
        56, 0.03, 0.10, 900, 1100, 0.30, 4.0, 0.70);

    /// <summary>Slow waves — Am9 · Fmaj7 · Cmaj · G6 around A2, heavy filtered-noise surf.</summary>
    public static readonly AmbientCharacter OceanWaves = new(
        "ocean-waves", 45,
        [[-12, 0, 7, 15, 19], [-4, 3, 7, 12, 15], [-9, 3, 10, 15, 19], [-2, 5, 10, 14, 19]],
        [12, 15, 17, 19, 22, 24, 27],
        48, 0.16, 0.85, 700, 750, 0.25, 2.0, 0.40);

    /// <summary>Forest hush — Em9 · Cmaj7 · G · D around E3, a breath of wind in the pines.</summary>
    public static readonly AmbientCharacter ForestNight = new(
        "forest-night", 52,
        [[-12, 0, 7, 10, 14], [-4, 0, 3, 7, 12], [-9, 3, 10, 15], [-2, 2, 5, 10]],
        [12, 15, 17, 19, 22, 24, 27],
        52, 0.07, 0.35, 800, 850, 0.30, 3.0, 0.45);

    /// <summary>Lydian mist — Cmaj7 · G · Am · Fmaj7♯11 around C3, airy with soft wind.</summary>
    public static readonly AmbientCharacter MistDream = new(
        "mist-dream", 48,
        [[-12, 0, 4, 7, 11], [-5, 2, 7, 14], [-3, 0, 4, 12, 16], [-7, 0, 4, 9, 11]],
        [12, 14, 16, 19, 21, 23, 26, 28],
        54, 0.12, 0.50, 900, 1000, 0.40, 3.0, 0.55);

    /// <summary>Plum lull — Bm9 · Gmaj7(♯11) · Em9 · A around B2, dusky and sparse.</summary>
    public static readonly AmbientCharacter PlumLull = new(
        "plum-lull", 47,
        [[-12, 0, 7, 10, 14], [-4, 3, 7, 12, 14], [-7, 5, 8, 12], [-2, 5, 10, 14]],
        [12, 15, 17, 19, 22, 24],
        50, 0.06, 0.25, 750, 800, 0.30, 2.5, 0.50);

    /// <summary>Resolve any palette to its character. Total — unknown values fall back to NightCalm.</summary>
    public static AmbientCharacter ForPalette(PosterPalette palette) => palette switch
    {
        PosterPalette.NightHarbor => NightCalm,
        PosterPalette.StarfieldViolet => NightCalm,
        PosterPalette.EmberSky => WarmEmber,
        PosterPalette.LanternGlow => WarmEmber,
        PosterPalette.OceanDeep => OceanWaves,
        PosterPalette.TidePool => OceanWaves,
        PosterPalette.MoonMeadow => ForestNight,
        PosterPalette.PineForest => ForestNight,
        PosterPalette.CloudDrift => MistDream,
        PosterPalette.MistValley => MistDream,
        PosterPalette.SnowHush => MistDream,
        PosterPalette.PlumDusk => PlumLull,
        _ => NightCalm,
    };

    /// <summary>Poster mood vocabulary (calm/dreamy/cozy/gentle/wonder/sleepy per poster.gbnf)
    /// nudges the character rather than replacing it — same key, different breath.</summary>
    public static AmbientCharacter ApplyMood(AmbientCharacter c, string mood) => mood.ToLowerInvariant() switch
    {
        "sleepy" => c with { TempoBpm = c.TempoBpm * 0.94, ArpPerChord = c.ArpPerChord * 0.7, CutoffHz = c.CutoffHz * 0.9 },
        "dreamy" => c with { LfoDepth = Math.Min(0.6, c.LfoDepth * 1.3), ArpPerChord = c.ArpPerChord * 0.85 },
        "cozy" => c with { PadBrightness = Math.Min(1.0, c.PadBrightness + 0.1), NoiseLevel = c.NoiseLevel * 0.8 },
        "gentle" => c with { ArpPerChord = c.ArpPerChord * 0.8 },
        "wonder" => c with { ArpPerChord = c.ArpPerChord * 1.2, CutoffHz = c.CutoffHz * 1.1 },
        _ => c, // "calm" and anything unexpected
    };
}
