namespace StoryTime.Core.Audio;

/// <summary>A rendered seamless ambient loop: stereo float samples at <see cref="SampleRate"/>.</summary>
public sealed class AmbientLoop
{
    public required float[] Left { get; init; }
    public required float[] Right { get; init; }
    public required int SampleRate { get; init; }

    public double DurationSeconds => (double)Left.Length / SampleRate;

    public double Peak()
    {
        double p = 0;
        for (var i = 0; i < Left.Length; i++)
        {
            var a = Math.Abs(Left[i]);
            var b = Math.Abs(Right[i]);
            if (a > p) p = a;
            if (b > p) p = b;
        }
        return p;
    }

    public double Rms()
    {
        double sum = 0;
        for (var i = 0; i < Left.Length; i++)
            sum += (double)Left[i] * Left[i] + (double)Right[i] * Right[i];
        return Math.Sqrt(sum / (2.0 * Left.Length));
    }
}

/// <summary>Deterministic DSP composer: renders a seamless-looping ambient music bed from a
/// small seed context (poster palette + mood + seed) — the musical sibling of the procedural
/// poster composer. Pure math, fully offline, zero licensing.
///
/// Sound design: three-to-five-note pad chords (each note = 3 slightly detuned wavetable
/// oscillators, slow raised-cosine swells, chords crossfading i–VI–iv–VII-style over the
/// loop), sparse pentatonic plucks (additive sine partials, exponential decay) echoed through
/// a ping-pong feedback delay, an optional filtered pink-noise wash (surf / wind), and a
/// master one-pole lowpass whose cutoff breathes with a loop-periodic LFO. Loop seamlessness
/// is by construction: all deterministic voices render into loop+tail and the tail folds back
/// onto the head; the stochastic noise wash is equal-power crossfaded across the seam.
/// Output is normalized to RMS ≈ −24 dBFS with peak capped at −6 dBFS — this bed is meant
/// to sit far under a narration voice.</summary>
public static class AmbientComposer
{
    public const int SampleRate = 24_000;
    public const double LoopSeconds = 64.0;
    private const double ChordSeconds = 16.0;
    private const double TailSeconds = 12.0;    // fold-back window: releases + delay echoes
    private const double NoiseFadeSeconds = 3.0;
    private const double AttackSeconds = 5.0;
    private const double ReleaseSeconds = 6.0;
    private const double DetuneCents = 2.4;
    private const double TargetRms = 0.06;      // ≈ −24.4 dBFS
    private const double PeakCeiling = 0.5;     // ≈ −6.0 dBFS
    internal const int WavetableSize = 4096;

    public static AmbientLoop Render(AmbientContext ctx)
    {
        var character = AmbientMoodMap.ApplyMood(AmbientMoodMap.ForPalette(ctx.Palette), ctx.Mood);
        var rng = new SplitMix64(MixSeed(ctx, character));

        var n = (int)(LoopSeconds * SampleRate);
        var tail = (int)(TailSeconds * SampleRate);
        var ext = n + tail;
        var left = new double[ext];
        var right = new double[ext];

        RenderPads(left, right, character, ref rng);
        RenderPlucks(left, right, character, ref rng);
        ApplyMasterLowpass(left, right, character);

        // Fold the tail (chord-4 release, decaying echoes) back onto the head — the loop is
        // continuous by construction because nothing deterministic still sounds at ext.
        for (var i = 0; i < tail; i++)
        {
            left[i] += left[n + i];
            right[i] += right[n + i];
        }

        AddNoiseWash(left, right, n, character, ref rng);
        RemoveDc(left, right, n);
        Normalize(left, right, n);

        var outL = new float[n];
        var outR = new float[n];
        for (var i = 0; i < n; i++)
        {
            outL[i] = (float)left[i];
            outR[i] = (float)right[i];
        }
        return new AmbientLoop { Left = outL, Right = outR, SampleRate = SampleRate };
    }

    /// <summary>Write the loop as a stereo 16-bit PCM WAV.</summary>
    public static void WriteWav(string path, AmbientLoop loop)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);
        long dataBytes = (long)loop.Left.Length * 4;
        w.Write("RIFF"u8);
        w.Write((uint)(36 + dataBytes));
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16u);
        w.Write((ushort)1);                    // PCM
        w.Write((ushort)2);                    // stereo
        w.Write((uint)loop.SampleRate);
        w.Write((uint)(loop.SampleRate * 4));  // byte rate
        w.Write((ushort)4);                    // block align
        w.Write((ushort)16);
        w.Write("data"u8);
        w.Write((uint)dataBytes);
        for (var i = 0; i < loop.Left.Length; i++)
        {
            w.Write((short)Math.Round(Math.Clamp(loop.Left[i], -1f, 1f) * short.MaxValue));
            w.Write((short)Math.Round(Math.Clamp(loop.Right[i], -1f, 1f) * short.MaxValue));
        }
    }

    // ---------------- pads ----------------

    private static void RenderPads(double[] left, double[] right, AmbientCharacter c, ref SplitMix64 rng)
    {
        var table = BuildPadWavetable(c.PadBrightness);
        var chordLen = (int)(ChordSeconds * SampleRate);
        var attack = (int)(AttackSeconds * SampleRate);
        var release = (int)(ReleaseSeconds * SampleRate);
        var window = chordLen + release;
        var detune = Math.Pow(2.0, DetuneCents / 1200.0);
        double[] oscDetune = [1.0 / detune, 1.0, detune];
        double[] oscPan = [0.34, 0.5, 0.66];

        for (var chord = 0; chord < c.Chords.Length; chord++)
        {
            var start = chord * chordLen;
            var voicing = c.Chords[chord];
            var toneAmp = 0.16 / Math.Sqrt(voicing.Length);
            for (var tone = 0; tone < voicing.Length; tone++)
            {
                var freq = MidiToHz(c.RootMidi + voicing[tone]);
                // Low notes slightly louder, high color notes softer.
                var amp = toneAmp * (tone == 0 ? 1.15 : tone >= 3 ? 0.8 : 1.0);
                // Alternate a gentle stereo tilt per tone for width beyond the osc spread.
                var tilt = (tone % 2 == 0 ? -1 : 1) * 0.08;
                for (var osc = 0; osc < 3; osc++)
                {
                    var phase = rng.NextDouble();
                    var step = freq * oscDetune[osc] / SampleRate;
                    var pan = Math.Clamp(oscPan[osc] + tilt, 0.15, 0.85);
                    var gainL = amp * Math.Cos(pan * Math.PI / 2);
                    var gainR = amp * Math.Sin(pan * Math.PI / 2);
                    for (var i = 0; i < window; i++)
                    {
                        double env;
                        if (i < attack) env = 0.5 - 0.5 * Math.Cos(Math.PI * i / attack);
                        else if (i < chordLen) env = 1.0;
                        else env = 0.5 + 0.5 * Math.Cos(Math.PI * (i - chordLen) / release);

                        var idx = phase * WavetableSize;
                        var i0 = (int)idx;
                        var frac = idx - i0;
                        var s = table[i0] + (table[(i0 + 1) & (WavetableSize - 1)] - table[i0]) * frac;
                        var v = s * env;
                        var at = start + i;
                        left[at] += v * gainL;
                        right[at] += v * gainR;
                        phase += step;
                        if (phase >= 1.0) phase -= 1.0;
                    }
                }
            }
        }
    }

    internal static double[] BuildPadWavetable(double brightness)
    {
        var table = new double[WavetableSize];
        for (var i = 0; i < WavetableSize; i++)
        {
            var phi = 2.0 * Math.PI * i / WavetableSize;
            table[i] = Math.Sin(phi)
                       + brightness * 0.30 * Math.Sin(2 * phi)
                       + brightness * 0.10 * Math.Sin(3 * phi);
        }
        // Normalize the table so brightness changes tone, not level.
        var peak = table.Max(Math.Abs);
        for (var i = 0; i < WavetableSize; i++) table[i] /= peak;
        return table;
    }

    // ---------------- plucks + ping-pong delay ----------------

    private static void RenderPlucks(double[] left, double[] right, AmbientCharacter c, ref SplitMix64 rng)
    {
        var ext = left.Length;
        var busL = new double[ext];
        var busR = new double[ext];
        var beat = 60.0 / c.TempoBpm;
        var chordLen = (int)(ChordSeconds * SampleRate);
        var beatsPerChord = (int)(ChordSeconds / beat);

        double[] partialAmp = [1.0, 0.35 * c.PadBrightness + 0.12, 0.15 * c.PadBrightness, 0.05 * c.PadBrightness];
        double[] partialRatio = [1.0, 2.0, 3.0, 4.02];

        for (var chord = 0; chord < c.Chords.Length; chord++)
        {
            var count = (int)Math.Clamp(Math.Round(c.ArpPerChord + (rng.NextDouble() - 0.5) * 1.5), 1, 6);
            var usedBeats = new HashSet<int>();
            for (var e = 0; e < count; e++)
            {
                // Quantize onsets to the beat grid; skip the very first beat of chord 0 so the
                // loop's opening swell leads, and never stack two plucks on one beat.
                var beatIndex = (int)(rng.NextDouble() * beatsPerChord);
                if (chord == 0 && beatIndex == 0) beatIndex = 1;
                if (!usedBeats.Add(beatIndex)) continue;

                var t0 = chord * chordLen + (int)(beatIndex * beat * SampleRate);
                var note = PickPluckNote(c, chord, ref rng);
                var freq = MidiToHz(c.RootMidi + note);
                var amp = 0.14 + rng.NextDouble() * 0.12;
                var pan = 0.25 + rng.NextDouble() * 0.5;
                var gainL = amp * Math.Cos(pan * Math.PI / 2);
                var gainR = amp * Math.Sin(pan * Math.PI / 2);
                var tau = 1.1 + rng.NextDouble() * 0.6;

                var length = Math.Min((int)(tau * 4 * SampleRate), ext - t0);
                var attack = (int)(0.006 * SampleRate);
                for (var p = 0; p < partialRatio.Length; p++)
                {
                    var f = freq * partialRatio[p];
                    if (f > SampleRate * 0.45) break;
                    var step = 2.0 * Math.PI * f / SampleRate;
                    var pTau = tau / Math.Pow(p + 1, 0.8);
                    var decayPerSample = Math.Exp(-1.0 / (pTau * SampleRate));
                    var envelope = partialAmp[p];
                    double phase = rng.NextDouble() * 2.0 * Math.PI;
                    for (var i = 0; i < length; i++)
                    {
                        var a = i < attack ? envelope * i / attack : envelope;
                        var v = Math.Sin(phase) * a;
                        busL[t0 + i] += v * gainL;
                        busR[t0 + i] += v * gainR;
                        phase += step;
                        if (phase > 2.0 * Math.PI) phase -= 2.0 * Math.PI;
                        if (i >= attack) envelope *= decayPerSample;
                        if (envelope < 1e-4) break;
                    }
                }
            }
        }

        // Ping-pong feedback delay on the pluck bus (dotted-half-beat echoes drifting L↔R).
        var delay = (int)(beat * 0.75 * SampleRate);
        const double feedback = 0.38;
        for (var i = 0; i < ext; i++)
        {
            var echoL = i >= delay ? busR[i - delay] * feedback : 0;
            var echoR = i >= delay ? busL[i - delay] * feedback : 0;
            busL[i] += echoL;
            busR[i] += echoR;
            left[i] += busL[i];
            right[i] += busR[i];
        }
    }

    private static int PickPluckNote(AmbientCharacter c, int chord, ref SplitMix64 rng)
    {
        // 60% chord tones an octave up (always consonant), 40% the character's arp scale.
        if (rng.NextDouble() < 0.6)
        {
            var voicing = c.Chords[chord];
            var tone = voicing[(int)(rng.NextDouble() * voicing.Length)];
            while (tone < 12) tone += 12;
            return tone;
        }
        return c.ArpScale[(int)(rng.NextDouble() * c.ArpScale.Length)];
    }

    // ---------------- master filter ----------------

    private static void ApplyMasterLowpass(double[] left, double[] right, AmbientCharacter c)
    {
        var ext = left.Length;
        // Cutoff breathes with a loop-periodic LFO (3 whole cycles per loop) so the filter
        // state matches across the seam.
        const double cyclesPerLoop = 3.0;
        var lfoStep = 2.0 * Math.PI * cyclesPerLoop / (LoopSeconds * SampleRate);

        double stateL = 0, stateR = 0;
        // Warm the filter on the last two seconds so the head starts near its wrap state.
        var warm = (int)(2.0 * SampleRate);
        for (var i = ext - warm; i < ext; i++)
        {
            var a = CoefficientAt(i, c, lfoStep);
            stateL += a * (left[i] - stateL);
            stateR += a * (right[i] - stateR);
        }
        for (var i = 0; i < ext; i++)
        {
            var a = CoefficientAt(i, c, lfoStep);
            stateL += a * (left[i] - stateL);
            stateR += a * (right[i] - stateR);
            left[i] = stateL;
            right[i] = stateR;
        }
    }

    private static double CoefficientAt(int i, AmbientCharacter c, double lfoStep)
    {
        // Block-rate coefficient update (every 64 samples) keeps this cheap.
        var block = i >> 6 << 6;
        var cutoff = c.CutoffHz * (1.0 + c.LfoDepth * Math.Sin(lfoStep * block));
        return 1.0 - Math.Exp(-2.0 * Math.PI * cutoff / SampleRate);
    }

    // ---------------- noise wash ----------------

    private static void AddNoiseWash(double[] left, double[] right, int n, AmbientCharacter c, ref SplitMix64 rng)
    {
        if (c.NoiseLevel <= 0.0005) return;
        var fade = (int)(NoiseFadeSeconds * SampleRate);
        var extN = n + fade;
        var noiseL = new double[extN];
        var noiseR = new double[extN];
        GeneratePinkLowpassed(noiseL, c.NoiseCutoffHz, ref rng);
        GeneratePinkLowpassed(noiseR, c.NoiseCutoffHz, ref rng);

        // Loop-periodic wave/gust LFO — 5 swells per loop, raised and squared for surf shape.
        const double swells = 5.0;
        var lfoStep = 2.0 * Math.PI * swells / (LoopSeconds * SampleRate);
        var lfoPhase = rng.NextDouble() * 2.0 * Math.PI;

        for (var i = 0; i < n; i++)
        {
            var wave = 0.5 - 0.5 * Math.Cos(lfoStep * i + lfoPhase);
            var g = c.NoiseLevel * (1.0 - c.NoiseWaveDepth + c.NoiseWaveDepth * wave * wave);
            double l = noiseL[i], r = noiseR[i];
            if (i < fade)
            {
                // Equal-power crossfade of the overrun back across the seam.
                var f = (double)i / fade;
                var a = Math.Sqrt(f);
                var b = Math.Sqrt(1 - f);
                l = l * a + noiseL[n + i] * b;
                r = r * a + noiseR[n + i] * b;
            }
            left[i] += l * g;
            right[i] += r * g;
        }
    }

    private static void GeneratePinkLowpassed(double[] buffer, double cutoffHz, ref SplitMix64 rng)
    {
        // Paul Kellet pink-noise filter, then a 2-pole lowpass for a mellow wash.
        double b0 = 0, b1 = 0, b2 = 0;
        var a = 1.0 - Math.Exp(-2.0 * Math.PI * cutoffHz / SampleRate);
        double lp1 = 0, lp2 = 0;
        for (var i = 0; i < buffer.Length; i++)
        {
            var white = rng.NextDouble() * 2.0 - 1.0;
            b0 = 0.99765 * b0 + white * 0.0990460;
            b1 = 0.96300 * b1 + white * 0.2965164;
            b2 = 0.57000 * b2 + white * 1.0526913;
            var pink = (b0 + b1 + b2 + white * 0.1848) * 0.18;
            lp1 += a * (pink - lp1);
            lp2 += a * (lp1 - lp2);
            buffer[i] = lp2;
        }
    }

    // ---------------- finishing ----------------

    private static void RemoveDc(double[] left, double[] right, int n)
    {
        double meanL = 0, meanR = 0;
        for (var i = 0; i < n; i++) { meanL += left[i]; meanR += right[i]; }
        meanL /= n;
        meanR /= n;
        for (var i = 0; i < n; i++) { left[i] -= meanL; right[i] -= meanR; }
    }

    private static void Normalize(double[] left, double[] right, int n)
    {
        double peak = 0, sum = 0;
        for (var i = 0; i < n; i++)
        {
            var a = Math.Abs(left[i]);
            var b = Math.Abs(right[i]);
            if (a > peak) peak = a;
            if (b > peak) peak = b;
            sum += left[i] * left[i] + right[i] * right[i];
        }
        var rms = Math.Sqrt(sum / (2.0 * n));
        if (peak <= 0 || rms <= 0) return;
        var scale = Math.Min(PeakCeiling / peak, TargetRms / rms);
        for (var i = 0; i < n; i++) { left[i] *= scale; right[i] *= scale; }
    }

    internal static double MidiToHz(int midi) => 440.0 * Math.Pow(2.0, (midi - 69) / 12.0);

    private static ulong MixSeed(AmbientContext ctx, AmbientCharacter character)
    {
        var h = (ulong)(uint)ctx.Seed * 0x9E3779B97F4A7C15UL;
        h ^= (ulong)(uint)(int)ctx.Palette * 0xBF58476D1CE4E5B9UL;
        foreach (var ch in character.Name) h = (h ^ ch) * 0x100000001B3UL;
        foreach (var ch in ctx.Mood) h = (h ^ ch) * 0x100000001B3UL;
        return h == 0 ? 0x2545F4914F6CDD1DUL : h;
    }

    /// <summary>Deterministic, platform-stable PRNG (SplitMix64) — System.Random's algorithm
    /// is not contractually stable across runtimes; this one is ours.</summary>
    private struct SplitMix64
    {
        private ulong _state;
        public SplitMix64(ulong seed) => _state = seed;

        private ulong Next()
        {
            _state += 0x9E3779B97F4A7C15UL;
            var z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        /// <summary>Uniform [0,1).</summary>
        public double NextDouble() => (Next() >> 11) * (1.0 / 9007199254740992.0);
    }
}
