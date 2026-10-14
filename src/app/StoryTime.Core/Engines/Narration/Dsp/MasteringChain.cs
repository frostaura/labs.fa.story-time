namespace StoryTime.Core.Engines.Narration.Dsp;

/// <summary>Kind of one static EQ band.</summary>
public enum EqKind { HighPass, LowPass, Peaking, LowShelf, HighShelf }

/// <summary>One static EQ band (Q is ignored by shelves; gain by pass filters).</summary>
public sealed record EqBand(EqKind Kind, double FrequencyHz, double GainDb = 0, double Q = Biquad.ButterworthQ)
{
    public Biquad Build(int sampleRate) => Kind switch
    {
        EqKind.HighPass => Biquad.HighPass(sampleRate, FrequencyHz, Q),
        EqKind.LowPass => Biquad.LowPass(sampleRate, FrequencyHz, Q),
        EqKind.Peaking => Biquad.Peaking(sampleRate, FrequencyHz, GainDb, Q),
        EqKind.LowShelf => Biquad.LowShelf(sampleRate, FrequencyHz, GainDb),
        _ => Biquad.HighShelf(sampleRate, FrequencyHz, GainDb),
    };
}

/// <summary>Defaults act only on true sibilant peaks (review round 1: −12 dB / 8 dB max lisped
/// every "s" by 6–10 dB on top of the 7 kHz shelf).</summary>
public sealed record DeEsserSettings(
    double CentreHz = 6500, double Q = 1.0, double ThresholdDb = -8.0, double Ratio = 3.0, double MaxReductionDb = 4.0);

public sealed record CompressorSettings(
    double ThresholdDb = -26.0, double Ratio = 2.5, double AttackMs = 12.0, double ReleaseMs = 180.0,
    double KneeDb = 6.0, double MakeupDb = 2.0);

/// <summary>Look-ahead limiter on the normalised voice (ceiling = profile ceiling − margin).</summary>
public sealed record LimiterSettings(double LookaheadMs = 5, double ReleaseMs = 60, double MarginDb = 0.5, double MaxReductionDb = 10);

public sealed record ReverbSettings(
    double DecaySeconds = 0.35, double PreDelaySeconds = 0.010, double WetLowHz = 200, double WetHighHz = 5000,
    double WetDb = -17.0);

/// <summary>
/// Every number of the SoothingNarrator mastering stage, as data — so a re-voiced chain (e.g.
/// the founder's "loud whisper" bedtime character) is a new profile, not a code change.
/// Order is fixed: per-utterance <see cref="VoiceStages"/> (pitch/time, and whatever voice
/// reshaping comes next) → pre-EQ bands → de-esser → post-EQ bands → compressor → reverb; then,
/// outside the chain, loudness normalisation, the voice-only peak limiter, and the (never
/// compressed) pad mix.
/// </summary>
public sealed record MasteringProfile
{
    public required string Name { get; init; }
    /// <summary>Per-utterance voice reshaping, in order, on each trimmed 48 kHz utterance before
    /// it is laid on the timeline (see <see cref="IVoiceStage"/>). Empty: the voice as rendered.</summary>
    public IReadOnlyList<IVoiceStage> VoiceStages { get; init; } = [];
    /// <summary>EQ before the de-esser.</summary>
    public required IReadOnlyList<EqBand> PreEq { get; init; }
    /// <summary>Null skips the de-esser.</summary>
    public DeEsserSettings? DeEsser { get; init; } = new();
    /// <summary>EQ after the de-esser.</summary>
    public required IReadOnlyList<EqBand> PostEq { get; init; }
    /// <summary>Null skips the compressor.</summary>
    public CompressorSettings? Compressor { get; init; } = new();
    /// <summary>Null skips the room.</summary>
    public ReverbSettings? Reverb { get; init; } = new();
    /// <summary>The raw voice is levelled to this (BS.1770) before the chain, so absolute
    /// thresholds behave the same on every voice.</summary>
    public double PreChainLufs { get; init; } = -17.0;
    /// <summary>Integrated loudness of the delivered voice.</summary>
    public double TargetLufs { get; init; } = -18.0;
    public double PeakCeilingDbtp { get; init; } = -2.0;
    /// <summary>Peak limiter on the normalised voice stem (null: the loudness gain is instead
    /// lowered until the stem's true peak fits the ceiling). Never applied to the voice + pad mix.</summary>
    public LimiterSettings? Limiter { get; init; } = new();
    /// <summary>How far under the voice the pad sits, LU.</summary>
    public double PadBelowVoiceLu { get; init; } = 28.0;

    /// <summary>The chain measured in the voice lab (voice-lab.md, 2026-10-14):
    /// <code>
    /// HP 70 → +2.5 dB @180 (Q 0.9) → −1.5 dB @3.2k (Q 1) → de-ess → HS −3 dB @7k → notch −9 dB @9.7k (Q 2.5) → LP 12k
    /// → comp −26 dB 2.5:1 12/180 ms knee 6 +2 → room 0.35 s / 10 ms / 200–5k / −17 dB
    /// → −18 LUFS, −2 dBTP; pad 28 LU under.
    /// </code></summary>
    public static MasteringProfile VoiceLab { get; } = new()
    {
        Name = "voice-lab",
        PreEq =
        [
            new EqBand(EqKind.HighPass, 70),
            new EqBand(EqKind.Peaking, 180, 2.5, 0.9),
            new EqBand(EqKind.Peaking, 3200, -1.5, 1.0),
        ],
        PostEq =
        [
            new EqBand(EqKind.HighShelf, 7000, -3.0),
            // Compact voices carry a narrow fizz at ~9.7 kHz (+11–15 dB over 9 kHz, review rounds 1–2).
            new EqBand(EqKind.Peaking, 9700, -9.0, 2.5),
            new EqBand(EqKind.LowPass, 12000),
        ],
    };

    /// <summary>"E — warm mom": the founder's "loud whisper, like mom telling bedtime stories"
    /// brief (2026-10-14) — lower and slower, warm low-mids, softened presence and air, a small
    /// room, quieter delivery; deliberately no added noise or breath layer.
    /// <code>
    /// voice: pitch −1 st, tempo ×0.93 (resample + WSOLA)
    /// HP 65 → +4 dB @200 (Q 0.9) → +1.5 dB @380 (Q 1) → −3 dB @2.5k (Q 1) → de-ess → HS −4 dB @5k → LP 9k
    /// → comp −28 dB 2.5:1 10/220 ms knee 6 +2 → small room 0.35 s / 10 ms / 200–5k / −20 dB
    /// → −19 LUFS, −3 dBTP; pad 28 LU under.
    /// </code>
    /// The voice-lab 9.7 kHz fizz notch is not carried over: the 5 kHz shelf plus the 9 kHz
    /// low-pass already take ≈ 9 dB out at 9.7 kHz.</summary>
    public static MasteringProfile WarmMomE { get; } = new()
    {
        Name = "E-warm-mom",
        VoiceStages = [new PitchTimeStage(-1.0, 0.93)],
        PreEq =
        [
            new EqBand(EqKind.HighPass, 65),
            new EqBand(EqKind.Peaking, 200, 4.0, 0.9),
            new EqBand(EqKind.Peaking, 380, 1.5, 1.0),
            new EqBand(EqKind.Peaking, 2500, -3.0, 1.0),
        ],
        PostEq =
        [
            new EqBand(EqKind.HighShelf, 5000, -4.0),
            new EqBand(EqKind.LowPass, 9000),
        ],
        Compressor = new CompressorSettings(ThresholdDb: -28.0, Ratio: 2.5, AttackMs: 10.0, ReleaseMs: 220.0),
        Reverb = new ReverbSettings(WetDb: -20.0),
        TargetLufs = -19.0,
        PeakCeilingDbtp = -3.0,
        PadBelowVoiceLu = 28.0,
    };

    /// <summary>"F": E, a touch lower and slower — pitch −1.5 st, tempo ×0.92.</summary>
    public static MasteringProfile WarmMomF { get; } = WarmMomE with
    {
        Name = "F-lower-slower",
        VoiceStages = [new PitchTimeStage(-1.5, 0.92)],
    };

    /// <summary>"G": E, darker and a little roomier — low-pass 7.5 kHz, room wet −19 dB.
    /// The founder's pick of the E/F/G audition (2026-10-14): warmest and darkest.</summary>
    public static MasteringProfile WarmMomG { get; } = WarmMomE with
    {
        Name = "G-warm-dark",
        PostEq =
        [
            new EqBand(EqKind.HighShelf, 5000, -4.0),
            new EqBand(EqKind.LowPass, 7500),
        ],
        Reverb = new ReverbSettings(WetDb: -19.0),
    };

    /// <summary>Every named profile, by name and by audition letter (case-insensitive).</summary>
    public static IReadOnlyList<MasteringProfile> All { get; } = [WarmMomG, WarmMomE, WarmMomF, VoiceLab];

    /// <summary>A named profile: its <see cref="Name"/> or its leading audition letter ("E", "F", "G").</summary>
    public static MasteringProfile Named(string name) =>
        All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)
            || (name.Length == 1 && p.Name.StartsWith(name + "-", StringComparison.OrdinalIgnoreCase)))
        ?? throw new ArgumentException($"no mastering profile '{name}' (have: {string.Join(", ", All.Select(p => p.Name))})", nameof(name));

    /// <summary>The profile the app ships — one line to switch (founder pick, 2026-10-14: G).</summary>
    public static MasteringProfile Default => WarmMomG;
}

/// <summary>
/// The SoothingNarrator mastering chain: one streaming processor over the continuous story
/// timeline (so the compressor never re-attacks at a splice and the room tail rings into the
/// pauses), configured by a <see cref="MasteringProfile"/>. Evolves the 2026-07-31 cozy shelves.
/// </summary>
public sealed class MasteringChain
{
    private readonly Biquad[] _pre, _post;
    private readonly DeEsser? _deEsser;
    private readonly Compressor? _compressor;
    private readonly RoomReverb? _reverb;

    public MasteringChain(int sampleRate, MasteringProfile? profile = null, bool reverb = true, bool compressor = true)
    {
        Profile = profile ?? MasteringProfile.Default;
        _pre = Profile.PreEq.Select(b => b.Build(sampleRate)).ToArray();
        _post = Profile.PostEq.Select(b => b.Build(sampleRate)).ToArray();
        if (Profile.DeEsser is { } d) _deEsser = new DeEsser(sampleRate, d);
        if (compressor && Profile.Compressor is { } c)
            _compressor = new Compressor(sampleRate, c.ThresholdDb, c.Ratio, c.AttackMs, c.ReleaseMs, c.KneeDb, c.MakeupDb);
        if (reverb && Profile.Reverb is { } r) _reverb = new RoomReverb(sampleRate, r);
    }

    public MasteringProfile Profile { get; }
    public double MaxCompressionDb => _compressor?.MaxGainReductionDb ?? 0;
    public double MaxDeEssDb => _deEsser?.MaxReductionSeenDb ?? 0;

    public void Process(Span<float> block)
    {
        foreach (var f in _pre) f.Process(block);
        _deEsser?.Process(block);
        foreach (var f in _post) f.Process(block);
        _compressor?.Process(block);
        _reverb?.Process(block);
    }
}
