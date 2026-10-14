using System.Runtime.InteropServices;
using StoryTime.Contracts;
using StoryTime.Core.Audio;
using StoryTime.Core.Engines.Narration.Dsp;

namespace StoryTime.Core.Engines.Narration;

/// <summary>One utterance to synthesise.</summary>
public sealed record UtteranceRequest(string Text, string VoiceId, float Rate, float Pitch, int SegmentIndex);

/// <summary>One synthesised utterance: mono PCM at the voice's native rate.</summary>
public sealed record UtterancePcm(float[] Samples, int SampleRate);

/// <summary>Renders single utterances. iOS: AVSpeechSynthesizer.WriteUtterance; the Mac
/// voice-lab harness: pre-rendered files. Implementations render ONE utterance at a time and
/// honour cancellation.</summary>
public interface IUtteranceSynthesizer
{
    Task<UtterancePcm> RenderAsync(UtteranceRequest request, CancellationToken ct);
}

/// <summary>The voice to narrate with, plus the voices to fall back to (in order) when it
/// renders silence or times out.</summary>
public sealed record VoiceChoice
{
    public required string VoiceId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public IReadOnlyList<string> Fallbacks { get; init; } = [];
}

/// <summary>Where one planned segment landed in the output.</summary>
public sealed record SegmentPlacement(int Index, int SentenceIndex, long StartFrame, long EndFrame, double PlannedPauseSeconds, PauseKind Pause);

/// <summary>Everything a render produced, with its measurements.</summary>
public sealed record NarrationRenderResult
{
    public required NarrationManifest Manifest { get; init; }
    public required string VoiceId { get; init; }
    public required IReadOnlyList<SegmentPlacement> Placements { get; init; }
    public required long TotalFrames { get; init; }
    /// <summary>Integrated loudness of the delivered file (voice + pad), LUFS.</summary>
    public required double IntegratedLufs { get; init; }
    public required double TruePeakDbtp { get; init; }
    /// <summary>Loudness the voice stem was normalised to (before the ending ramp), LUFS.</summary>
    public required double VoiceLufs { get; init; }
    /// <summary>Linear pad gain applied (0 when the pad is off).</summary>
    public required double PadGain { get; init; }
    public required double MaxCompressionDb { get; init; }
    public required double MaxDeEssDb { get; init; }
    public required int VoiceFallbacks { get; init; }
}

/// <summary>Optional stem outputs for measurement (the voice-lab harness).</summary>
public sealed record NarrationStems(string? VoiceStemPath, string? PadStemPath);

/// <summary>The voice could not produce audible audio for a segment.</summary>
public sealed class VoiceRenderException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// SoothingNarrator: renders a <see cref="NarrationPlan"/> to a 48 kHz mono 16-bit WAV.
/// <list type="number">
/// <item>Each segment is synthesised alone, sanity-checked (RMS above −60 dBFS, within 4×
/// its expected duration — otherwise the next voice), edge-trimmed at its native rate,
/// resampled to 48 kHz and streamed to a file on disk (never the whole story in RAM).</item>
/// <item>The timeline is laid out from the measured pause map; sentence timestamps fall out.</item>
/// <item>Analysis pass: the timeline runs through the <see cref="MasteringChain"/> with the
/// dialogue gain applied, metered (BS.1770 + true peak).</item>
/// <item>Render pass: the chain re-runs (it is deterministic) with the full performance
/// gains, the loudness gain (profile: −18 LUFS, −2 dBTP ceiling) and the pad (28 LU under),
/// faded in/out, TPDF-dithered and written. The voice + pad mix is never compressed.</item>
/// </list>
/// </summary>
public sealed class SoothingNarrator(IUtteranceSynthesizer synthesizer, MasteringProfile? profile = null)
{
    public const int SampleRate = Resampler.TargetRate;
    private const int Block = 4096;

    /// <summary>The mastering stage in force (EQ, de-ess, compressor, room, loudness, pad level).</summary>
    public MasteringProfile Profile { get; } = profile ?? MasteringProfile.Default;

    /// <summary>Same-voice retries for one segment before moving to the next voice.</summary>
    public int RetriesPerSegment { get; init; } = 1;

    /// <summary>Floor for the per-utterance timeout.</summary>
    public TimeSpan MinUtteranceTimeout { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>Per-utterance timeout as a multiple of the expected speaking time.</summary>
    public double TimeoutFactor { get; init; } = 4.0;

    public async Task<NarrationRenderResult> RenderAsync(NarrationPlan plan, VoiceChoice voice, string outputPath,
        IProgress<double>? progress, CancellationToken ct, NarrationStems? stems = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        var workDir = outputPath + ".segments";
        try
        {
            // ---- 1. synthesise every segment to disk, falling back across voices ----
            var voices = new List<string> { voice.VoiceId };
            voices.AddRange(voice.Fallbacks.Where(v => v != voice.VoiceId));
            List<SegmentFile>? files = null;
            var usedVoice = voice.VoiceId;
            var fallbacks = 0;
            for (var v = 0; v < voices.Count; v++)
            {
                try
                {
                    files = await RenderSegmentsAsync(plan, voices[v], workDir, progress, ct).ConfigureAwait(false);
                    usedVoice = voices[v];
                    break;
                }
                catch (VoiceRenderException) when (v < voices.Count - 1)
                {
                    fallbacks++;
                }
            }
            files ??= [];

            // ---- 2. layout ----
            var placements = new List<SegmentPlacement>(files.Count);
            long cursor = Frames(plan.HeadSeconds);
            for (var k = 0; k < files.Count; k++)
            {
                if (k > 0) cursor += Frames(plan.Segments[k].PauseBeforeSeconds);
                placements.Add(new SegmentPlacement(k, plan.Segments[k].SentenceIndex, cursor, cursor + files[k].Frames,
                    k == 0 ? plan.HeadSeconds : plan.Segments[k].PauseBeforeSeconds,
                    k == 0 ? plan.HeadKind : plan.Segments[k].PauseBefore));
                cursor += files[k].Frames;
            }
            var total = cursor + Frames(plan.TailSeconds);
            var timestamps = Timestamps(plan, placements, Frames(plan.HeadSeconds));

            // Raw stem pre-gain: level the voice to PreChainLufs before the chain.
            var rawMeter = new LoudnessMeter(SampleRate);
            var timeline = new Timeline(files, placements);
            Pass(timeline, total, null, (block, _) => rawMeter.Add(block), ct);
            var rawLufs = rawMeter.IntegratedLufs;
            var preGain = rawLufs > -150 ? AudioShaper.DbToGain(Profile.PreChainLufs - rawLufs) : 1.0;

            // ---- 3. analysis pass ----
            var reference = new GainEnvelope(plan, placements, includeEnding: false);
            var chainA = new MasteringChain(SampleRate, Profile);
            var meter = new LoudnessMeter(SampleRate);
            var tp = new TruePeakMeter();
            Pass(timeline, total, chainA, (block, start) =>
            {
                reference.Apply(block, start);
                meter.Add(block);
                tp.Add(block);
            }, ct, preGain);
            progress?.Report(0.9);

            // ---- 4. gains ----
            var stemLufs = meter.IntegratedLufs;
            var ceiling = AudioShaper.DbToGain(Profile.PeakCeilingDbtp);
            var normGain = stemLufs > -150 ? AudioShaper.DbToGain(Profile.TargetLufs - stemLufs) : 1.0;
            double padGain = 0;
            for (var iteration = 0; iteration < 3; iteration++)
            {
                var voiceLufs = stemLufs + AudioShaper.GainToDb(normGain);
                padGain = plan.Pad ? NarrationPad.GainFor(Math.Min(voiceLufs, Profile.TargetLufs), SampleRate, Profile.PadBelowVoiceLu) : 0;
                var padPeak = padGain * PadPeakUnit;
                if (tp.PeakLinear * normGain + padPeak <= ceiling) break;
                normGain = Math.Max(0, (ceiling - padPeak) / Math.Max(tp.PeakLinear, 1e-9));
            }
            var finalVoiceLufs = stemLufs + AudioShaper.GainToDb(normGain);

            // ---- 5. render pass ----
            var full = new GainEnvelope(plan, placements, includeEnding: true);
            var chainB = new MasteringChain(SampleRate, Profile);
            var pad = plan.Pad ? new NarrationPad(SampleRate, plan.TimelineOffsetSeconds) : null;
            var outMeter = new LoudnessMeter(SampleRate);
            var outTp = new TruePeakMeter();
            var fadeIn = Frames(plan.PadFadeInSeconds);
            var fadeOut = Frames(plan.PadFadeOutSeconds);
            var padBlock = new float[Block];
            var dither = new Tpdf(0x5EED_57A7u);

            using var writer = new WavWriter(outputPath, SampleRate, total);
            using var voiceStem = stems?.VoiceStemPath is { } vp ? new WavWriter(vp, SampleRate, total) : null;
            using var padStem = stems?.PadStemPath is { } pp ? new WavWriter(pp, SampleRate, total) : null;
            Pass(timeline, total, chainB, (block, start) =>
            {
                full.Apply(block, start);
                for (var i = 0; i < block.Length; i++) block[i] = (float)(block[i] * normGain);
                voiceStem?.Write(block, null);
                if (pad is not null)
                {
                    var pb = padBlock.AsSpan(0, block.Length);
                    pad.Render(pb);
                    for (var i = 0; i < pb.Length; i++)
                    {
                        var f = start + i;
                        var g = padGain;
                        if (f < fadeIn) g *= RaisedCosine(f / (double)fadeIn);
                        var fromEnd = total - f;
                        if (fadeOut > 0 && fromEnd < fadeOut) g *= RaisedCosine(fromEnd / (double)fadeOut);
                        pb[i] = (float)(pb[i] * g);
                        block[i] += pb[i];
                    }
                    padStem?.Write(pb, null);
                }
                outMeter.Add(block);
                outTp.Add(block);
                writer.Write(block, dither);
            }, ct, preGain);
            progress?.Report(1.0);

            return new NarrationRenderResult
            {
                Manifest = new NarrationManifest
                {
                    AudioPath = outputPath,
                    TeaserPath = string.Empty,
                    DurationSeconds = total / (double)SampleRate,
                    Timestamps = timestamps,
                },
                VoiceId = usedVoice,
                Placements = placements,
                TotalFrames = total,
                IntegratedLufs = outMeter.IntegratedLufs,
                TruePeakDbtp = outTp.PeakDbtp,
                VoiceLufs = finalVoiceLufs,
                PadGain = padGain,
                MaxCompressionDb = chainB.MaxCompressionDb,
                MaxDeEssDb = chainB.MaxDeEssDb,
                VoiceFallbacks = fallbacks,
            };
        }
        finally
        {
            try { if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true); }
            catch (IOException) { /* best effort: scratch next to the output */ }
        }
    }

    /// <summary>Peak of the unit pad is well under this (four tones × two oscillators,
    /// 0.18 each, low-passed); used as a conservative bound for the ceiling.</summary>
    private const double PadPeakUnit = 1.0;

    private static long Frames(double seconds) => (long)Math.Round(seconds * SampleRate);

    private static double RaisedCosine(double x) => x <= 0 ? 0 : x >= 1 ? 1 : 0.5 - 0.5 * Math.Cos(Math.PI * x);

    /// <summary>Expected speaking time for a segment: ~2.2 words/s at rate 0.4, slower at
    /// lower rates, plus a voice's own start-up.</summary>
    public static double ExpectedSeconds(string text, float rate)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var wordsPerSecond = 2.2 * Math.Clamp(rate / 0.4, 0.5, 2.0);
        return 0.4 + words / wordsPerSecond;
    }

    private sealed record SegmentFile(string Path, long Frames);

    private async Task<List<SegmentFile>> RenderSegmentsAsync(NarrationPlan plan, string voiceId, string workDir,
        IProgress<double>? progress, CancellationToken ct)
    {
        if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        Directory.CreateDirectory(workDir);
        var files = new List<SegmentFile>(plan.Segments.Count);
        for (var k = 0; k < plan.Segments.Count; k++)
        {
            ct.ThrowIfCancellationRequested();
            var seg = plan.Segments[k];
            var request = new UtteranceRequest(seg.Spoken, voiceId, seg.Rate, seg.Pitch, k);
            float[]? resampled = null;
            Exception? last = null;
            for (var attempt = 0; attempt <= RetriesPerSegment && resampled is null; attempt++)
            {
                var timeout = TimeSpan.FromSeconds(Math.Max(MinUtteranceTimeout.TotalSeconds, TimeoutFactor * ExpectedSeconds(seg.Spoken, seg.Rate)));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linked.CancelAfter(timeout);
                try
                {
                    var pcm = await synthesizer.RenderAsync(request, linked.Token).ConfigureAwait(false);
                    if (pcm.Samples.Length == 0 || pcm.SampleRate <= 0 || AudioShaper.RmsDbfs(pcm.Samples) < AudioShaper.SanityRmsDbfs)
                    {
                        last = new VoiceRenderException($"segment {k}: silent render from {voiceId}");
                        continue;
                    }
                    var trimmed = AudioShaper.TrimEdges(pcm.Samples, pcm.SampleRate);
                    if (trimmed.Length == 0) { last = new VoiceRenderException($"segment {k}: nothing above −50 dBFS"); continue; }
                    resampled = Resampler.Resample(trimmed, pcm.SampleRate, SampleRate);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    last = new VoiceRenderException($"segment {k}: {voiceId} timed out after {timeout.TotalSeconds:0}s");
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not VoiceRenderException)
                {
                    last = ex;
                }
            }
            if (resampled is null)
                throw last as VoiceRenderException ?? new VoiceRenderException($"segment {k}: {voiceId} failed", last);

            var path = System.IO.Path.Combine(workDir, $"seg-{k:D4}.f32");
            await using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                fs.Write(MemoryMarshal.AsBytes(resampled.AsSpan()));
            files.Add(new SegmentFile(path, resampled.Length));
            progress?.Report(0.8 * (k + 1) / plan.Segments.Count);
        }
        return files;
    }

    /// <summary>One timestamp per source sentence: first segment's start → last segment's end.
    /// Sentences with no audible segment get a zero-length stamp at the running position.</summary>
    private static List<SentenceTimestamp> Timestamps(NarrationPlan plan, List<SegmentPlacement> placements, long head)
    {
        var result = new List<SentenceTimestamp>(plan.SentenceCount);
        var pos = head;
        for (var s = 0; s < plan.SentenceCount; s++)
        {
            var mine = placements.Where(p => p.SentenceIndex == s).ToList();
            if (mine.Count == 0)
            {
                result.Add(new SentenceTimestamp { Index = s, StartSeconds = pos / (double)SampleRate, EndSeconds = pos / (double)SampleRate });
                continue;
            }
            result.Add(new SentenceTimestamp
            {
                Index = s,
                StartSeconds = mine[0].StartFrame / (double)SampleRate,
                EndSeconds = mine[^1].EndFrame / (double)SampleRate,
            });
            pos = mine[^1].EndFrame;
        }
        return result;
    }

    /// <summary>Stream the dry timeline in blocks (× pre-gain), optionally through a chain.</summary>
    private static void Pass(Timeline timeline, long total, MasteringChain? chain, Action<Span<float>, long> sink,
        CancellationToken ct, double preGain = 1.0)
    {
        var buffer = new float[Block];
        timeline.Rewind();
        for (long start = 0; start < total; start += Block)
        {
            ct.ThrowIfCancellationRequested();
            var n = (int)Math.Min(Block, total - start);
            var block = buffer.AsSpan(0, n);
            timeline.Read(start, block);
            if (preGain != 1.0)
                for (var i = 0; i < n; i++) block[i] = (float)(block[i] * preGain);
            chain?.Process(block);
            sink(block, start);
        }
    }

    /// <summary>Reads the dry voice timeline (segments at their frames, silence elsewhere)
    /// straight from the segment files.</summary>
    private sealed class Timeline(List<SegmentFile> files, List<SegmentPlacement> placements)
    {
        private int _first;

        public void Rewind() => _first = 0;

        public void Read(long start, Span<float> block)
        {
            block.Clear();
            var end = start + block.Length;
            while (_first < placements.Count && placements[_first].EndFrame <= start) _first++;
            for (var k = _first; k < placements.Count && placements[k].StartFrame < end; k++)
            {
                var p = placements[k];
                var from = Math.Max(start, p.StartFrame);
                var to = Math.Min(end, p.EndFrame);
                if (to <= from) continue;
                using var fs = new FileStream(files[k].Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1);
                fs.Seek((from - p.StartFrame) * sizeof(float), SeekOrigin.Begin);
                var dest = block.Slice((int)(from - start), (int)(to - from));
                fs.ReadExactly(MemoryMarshal.AsBytes(dest));
            }
        }
    }

    /// <summary>Per-segment performance gain over the timeline. Each segment's gain holds
    /// from the midpoint of the pause before it to the midpoint of the pause after it;
    /// changes cross-fade (raised cosine, ≤ 30 ms) inside the silence, never under a word.</summary>
    private sealed class GainEnvelope
    {
        private readonly long[] _boundary;   // boundary[k] = start of segment k's region
        private readonly double[] _gain;     // linear gain per region
        private readonly long[] _half;       // half-width of the transition at boundary k

        public GainEnvelope(NarrationPlan plan, List<SegmentPlacement> placements, bool includeEnding)
        {
            var n = placements.Count;
            _boundary = new long[n];
            _gain = new double[n];
            _half = new long[n];
            for (var k = 0; k < n; k++)
            {
                var seg = plan.Segments[k];
                var db = includeEnding ? seg.GainDb : seg.GainDb - seg.EndingGainDb;
                _gain[k] = AudioShaper.DbToGain(db);
                if (k == 0) continue;
                var gapStart = placements[k - 1].EndFrame;
                var gapEnd = placements[k].StartFrame;
                _boundary[k] = (gapStart + gapEnd) / 2;
                _half[k] = Math.Min((long)(0.015 * SampleRate), Math.Max(1, (gapEnd - gapStart) / 4));
            }
        }

        public void Apply(Span<float> block, long start)
        {
            if (_gain.Length == 0) return;
            var k = 0;
            for (var i = 0; i < block.Length; i++)
            {
                var f = start + i;
                while (k + 1 < _boundary.Length && f >= _boundary[k + 1] + _half[k + 1]) k++;
                double g;
                if (k + 1 < _boundary.Length && f >= _boundary[k + 1] - _half[k + 1])
                {
                    var x = (f - (_boundary[k + 1] - _half[k + 1])) / (2.0 * _half[k + 1]);
                    var w = 0.5 - 0.5 * Math.Cos(Math.PI * Math.Clamp(x, 0, 1));
                    g = _gain[k] * (1 - w) + _gain[k + 1] * w;
                }
                else g = _gain[k];
                if (g != 1.0) block[i] = (float)(block[i] * g);
            }
        }
    }

    /// <summary>Deterministic TPDF dither, ±1 LSB at 16 bits.</summary>
    private sealed class Tpdf(uint seed)
    {
        private uint _s = seed == 0 ? 1u : seed;

        private uint Next()
        {
            _s ^= _s << 13; _s ^= _s >> 17; _s ^= _s << 5;
            return _s;
        }

        public double Lsb() => ((Next() / (double)uint.MaxValue) - (Next() / (double)uint.MaxValue));
    }

    /// <summary>Streaming mono 16-bit PCM WAV writer (length known up front).</summary>
    private sealed class WavWriter : IDisposable
    {
        private readonly FileStream _fs;
        private readonly byte[] _scratch = new byte[Block * 2];

        public WavWriter(string path, int sampleRate, long frames)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
            _fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            var dataBytes = frames * 2;
            using var w = new BinaryWriter(_fs, System.Text.Encoding.ASCII, leaveOpen: true);
            w.Write("RIFF"u8);
            w.Write((uint)(36 + dataBytes));
            w.Write("WAVE"u8);
            w.Write("fmt "u8);
            w.Write(16u);
            w.Write((ushort)1);
            w.Write((ushort)1);
            w.Write((uint)sampleRate);
            w.Write((uint)(sampleRate * 2));
            w.Write((ushort)2);
            w.Write((ushort)16);
            w.Write("data"u8);
            w.Write((uint)dataBytes);
        }

        public void Write(ReadOnlySpan<float> block, Tpdf? dither)
        {
            for (var i = 0; i < block.Length; i++)
            {
                var v = block[i] * 32767.0 + (dither?.Lsb() ?? 0);
                var s = (short)Math.Clamp(Math.Round(v), short.MinValue, short.MaxValue);
                _scratch[2 * i] = (byte)s;
                _scratch[2 * i + 1] = (byte)(s >> 8);
            }
            _fs.Write(_scratch, 0, block.Length * 2);
        }

        public void Dispose() => _fs.Dispose();
    }
}
