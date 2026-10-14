using StoryTime.Core.Abstractions;
using StoryTime.Core.Engines.Narration;
using StoryTime.Core.Engines.Narration.Dsp;
using Xunit;

namespace StoryTime.Core.Tests.Engines;

/// <summary>Speech-like fake voice: 22.05 kHz voiced harmonics with syllable envelopes,
/// wrapped in the 0.1 s / 0.2 s of edge silence real voices add (which trimming removes).</summary>
internal sealed class FakeVoice : IUtteranceSynthesizer
{
    public const int Rate = 22050;
    public List<UtteranceRequest> Requests { get; } = [];
    /// <summary>Voice ids that render silence (forces fallback).</summary>
    public HashSet<string> SilentVoices { get; } = [];
    /// <summary>Voice ids that never finish (forces the timeout).</summary>
    public HashSet<string> HangingVoices { get; } = [];

    public async Task<UtterancePcm> RenderAsync(UtteranceRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        if (HangingVoices.Contains(request.VoiceId)) await Task.Delay(Timeout.Infinite, ct);
        var words = request.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var speech = (int)(words * 0.30 / (request.Rate / 0.44) * Rate);
        var lead = (int)(0.10 * Rate);
        var tail = (int)(0.20 * Rate);
        var x = new float[lead + speech + tail];
        if (SilentVoices.Contains(request.VoiceId)) return new UtterancePcm(x, Rate);
        var f0 = 180.0 * request.Pitch;
        for (var i = 0; i < speech; i++)
        {
            var t = i / (double)Rate;
            var syllable = Math.Pow(Math.Sin(Math.PI * ((t * 4) % 1)), 2) * 0.85 + 0.15;
            var edge = Math.Min(1, Math.Min(i, speech - i) / (0.01 * Rate));
            var v = 0.0;
            for (var h = 1; h <= 8; h++) v += Math.Sin(2 * Math.PI * f0 * h * t) / h;
            x[lead + i] = (float)(0.2 * v * syllable * edge);
        }
        return new UtterancePcm(x, Rate);
    }
}

public sealed class SoothingNarratorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "soothing-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private const string Story =
        "The little fox curled up by the fire. The wind hummed outside.\n\n" +
        "\"Are you sleepy?\" asked Owl. \"A little,\" said Fox. She yawned… and smiled.\n\n\n" +
        "Morning was far away. The stars kept watch.\n\n" +
        "And so the forest slept. Goodnight, little fox. Goodnight.";

    private const long Keep = 1440;   // AudioShaper.KeepSeconds at 48 kHz

    private static readonly VoiceChoice Serena = new() { VoiceId = "serena", Fallbacks = ["moira", "samantha"] };

    private static float[] ReadWav(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var n = (bytes.Length - 44) / 2;
        var x = new float[n];
        for (var i = 0; i < n; i++) x[i] = BitConverter.ToInt16(bytes, 44 + 2 * i) / 32768f;
        return x;
    }

    private async Task<(NarrationRenderResult Result, NarrationPlan Plan, FakeVoice Voice)> Render(NarrationScript script,
        SoothingOptions? options = null, string name = "story.wav", NarrationStems? stems = null, FakeVoice? voice = null)
    {
        var plan = SoothingPlanner.Plan(script, options);
        var fake = voice ?? new FakeVoice();
        var result = await new SoothingNarrator(fake) { MeasureOutputTruePeak = true }.RenderAsync(plan, Serena, Path.Combine(_dir, name), null, CancellationToken.None, stems);
        return (result, plan, fake);
    }

    [Fact]
    public async Task Splices_ExactlyThePlannedPauses()
    {
        var (result, plan, _) = await Render(SentenceSplitter.SplitScript(Story));
        Assert.Equal(plan.Segments.Count, result.Placements.Count);
        // Perceived silence = spliced gap + the next line's breathy onset beyond the kept 30 ms.
        var p = result.Placements;
        long GiveBack(SegmentPlacement s) => Math.Max(0, s.SoftHeadFrames - Keep);
        Assert.Equal((long)Math.Round(plan.HeadSeconds * 48000), p[0].StartFrame + GiveBack(p[0]));
        for (var k = 1; k < p.Count; k++)
        {
            var perceived = (p[k].StartFrame - p[k - 1].EndFrame + GiveBack(p[k])) / 48000.0;
            Assert.Equal(plan.Segments[k].PauseBeforeSeconds, perceived, 4);
            Assert.True(p[k].SoftHeadFrames >= 0.030 * 48000 - 1, "the kept 30 ms counts as soft");
        }
        var tail = (result.TotalFrames - p[^1].EndFrame) / 48000.0;
        Assert.Equal(plan.TailSeconds, tail, 4);
        Assert.Equal(result.TotalFrames / 48000.0, result.Manifest.DurationSeconds, 6);
        Assert.Equal(result.Manifest.DurationSeconds, WavAudio.ReadDurationSeconds(result.Manifest.AudioPath), 4);
    }

    [Fact]
    public async Task OneTimestampPerSentence_CoveringItsSegments()
    {
        var script = SentenceSplitter.SplitScript(Story);
        var (result, _, _) = await Render(script);
        var ts = result.Manifest.Timestamps;
        Assert.Equal(script.SentenceList.Count, ts.Count);
        Assert.Equal(Enumerable.Range(0, ts.Count), ts.Select(t => t.Index));
        for (var i = 1; i < ts.Count; i++) Assert.True(ts[i].StartSeconds >= ts[i - 1].EndSeconds);
        // "Are you sleepy?" asked Owl. → one sentence, two segments.
        var owl = result.Placements.Where(p => p.SentenceIndex == 2).ToList();
        Assert.Equal(2, owl.Count);
        Assert.Equal(owl[0].StartFrame / 48000.0, ts[2].StartSeconds, 6);
        Assert.Equal(owl[1].EndFrame / 48000.0, ts[2].EndSeconds, 6);
    }

    [Fact]
    public async Task Loudness_HitsTheShippedProfile_Minus19_AndPeaksStayUnderMinus3()
    {
        Assert.Equal(-19.0, MasteringProfile.Default.TargetLufs);
        Assert.Equal(-3.0, MasteringProfile.Default.PeakCeilingDbtp);
        var (result, _, _) = await Render(SentenceSplitter.SplitScript(Story).Take(5));   // excerpt: no ending ramp
        Assert.InRange(result.VoiceLufs, -19.05, -18.95);
        Assert.InRange(result.IntegratedLufs, -19.3, -18.7);
        Assert.True(result.TruePeakDbtp <= -3.0 + 0.05, $"true peak {result.TruePeakDbtp:0.00} dBTP");
        // Independent re-measure of the written file.
        var x = ReadWav(result.Manifest.AudioPath);
        Assert.InRange(LoudnessMeter.Measure(x, 48000), -19.35, -18.65);
        Assert.True(TruePeakMeter.MeasureDbtp(x) <= -2.9);
    }

    [Fact]
    public async Task VoiceStages_SlowTheSpokenAudio_ButNotThePlannedPauses()
    {
        var script = SentenceSplitter.SplitScript(Story).Take(5);
        var plan = SoothingPlanner.Plan(script);
        var plain = await new SoothingNarrator(new FakeVoice(), MasteringProfile.Default with { VoiceStages = [] })
            .RenderAsync(plan, Serena, Path.Combine(_dir, "plain.wav"), null, CancellationToken.None);
        var shaped = await new SoothingNarrator(new FakeVoice(), MasteringProfile.Default)
            .RenderAsync(plan, Serena, Path.Combine(_dir, "shaped.wav"), null, CancellationToken.None);
        for (var k = 0; k < plain.Placements.Count; k++)
        {
            var a = plain.Placements[k].EndFrame - plain.Placements[k].StartFrame;
            var b = shaped.Placements[k].EndFrame - shaped.Placements[k].StartFrame;
            Assert.InRange(b, a / 0.93 - 2, a / 0.93 + 2);   // tempo ×0.93 on every utterance
        }
    }

    [Fact]
    public async Task Pad_Sits28LuUnderTheVoice_AndTheTailIsMusicOnly()
    {
        var stems = new NarrationStems(Path.Combine(_dir, "voice.wav"), Path.Combine(_dir, "pad.wav"));
        var (result, plan, _) = await Render(SentenceSplitter.SplitScript(Story).Take(5), stems: stems);
        var voice = LoudnessMeter.Measure(ReadWav(stems.VoiceStemPath!), 48000);
        // Pad steady state (skip the fade-in and the fade-out).
        var pad = ReadWav(stems.PadStemPath!);
        var steady = pad.AsSpan(48000 * 4, pad.Length - 48000 * 4 - (int)(plan.PadFadeOutSeconds * 48000) - 48000);
        var padLufs = LoudnessMeter.Measure(steady, 48000);
        Assert.InRange(voice - padLufs, 27.0, 29.0);
        // Music-only tail: after the last word + room ring-out, only the pad remains.
        var mix = ReadWav(result.Manifest.AudioPath);
        var voiceStem = ReadWav(stems.VoiceStemPath!);
        var tailStart = (int)result.Placements[^1].EndFrame + 48000;
        Assert.True(Signals.Rms(voiceStem.AsSpan(tailStart)) < 1e-4, "voice must be silent in the tail");
        Assert.True(Signals.Rms(mix.AsSpan(tailStart, 48000)) > 1e-4, "pad audible in the tail");
        // Fade-out: last 50 ms near silence, fade-in: first 50 ms near silence.
        Assert.True(Signals.Rms(mix.AsSpan(mix.Length - 2400)) < 2e-4);
        Assert.True(Signals.Rms(mix.AsSpan(0, 2400)) < 2e-4);
    }

    [Fact]
    public async Task Ending_IsQuieter_ThanTheBody()
    {
        var stems = new NarrationStems(Path.Combine(_dir, "voice.wav"), null);
        var (result, plan, _) = await Render(SentenceSplitter.SplitScript(Story), new SoothingOptions { Pad = false, RateVariation = 0 }, stems: stems);
        var voice = ReadWav(stems.VoiceStemPath!);
        double SegRms(int k) => Signals.Rms(voice.AsSpan((int)result.Placements[k].StartFrame,
            (int)(result.Placements[k].EndFrame - result.Placements[k].StartFrame)));
        var last = plan.Segments.Count - 1;
        var firstBody = 0;
        var diff = 20 * Math.Log10(SegRms(last) / SegRms(firstBody));
        Assert.True(diff < -4, $"last goodnight should sit ~6 dB down, was {diff:0.0} dB");
    }

    [Fact]
    public async Task SilentVoice_FallsBackToTheNextVoice()
    {
        var fake = new FakeVoice();
        fake.SilentVoices.Add("serena");
        var (result, _, _) = await Render(SentenceSplitter.SplitScript("The fox slept. The moon rose."), voice: fake);
        Assert.Equal("moira", result.VoiceId);
        Assert.Equal(1, result.VoiceFallbacks);
        Assert.Contains(fake.Requests, r => r.VoiceId == "moira");
    }

    [Fact]
    public async Task HangingVoice_TimesOut_AndFallsBack()
    {
        var fake = new FakeVoice();
        fake.HangingVoices.Add("serena");
        var plan = SoothingPlanner.Plan(SentenceSplitter.SplitScript("The fox slept."));
        var narrator = new SoothingNarrator(fake)
        {
            MinUtteranceTimeout = TimeSpan.FromMilliseconds(50),
            TimeoutFactor = 0.05,
            RetriesPerSegment = 0,
        };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await narrator.RenderAsync(plan, Serena, Path.Combine(_dir, "t.wav"), null, CancellationToken.None);
        Assert.Equal("moira", result.VoiceId);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task AllVoicesSilent_Throws()
    {
        var fake = new FakeVoice();
        fake.SilentVoices.UnionWith(["serena", "moira", "samantha"]);
        await Assert.ThrowsAsync<VoiceRenderException>(() => Render(SentenceSplitter.SplitScript("The fox slept."), voice: fake));
    }

    [Fact]
    public async Task Cancellation_IsHonoured_AndLeavesNoScratch()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var plan = SoothingPlanner.Plan(SentenceSplitter.SplitScript(Story));
        var path = Path.Combine(_dir, "c.wav");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SoothingNarrator(new FakeVoice()).RenderAsync(plan, Serena, path, null, cts.Token));
        Assert.False(Directory.Exists(path + ".segments"));
    }

    [Fact]
    public async Task OverlappedWindows_ConcatenateSeamlessly()
    {
        // Two windows rendered separately, like the overlapped pipeline, then joined.
        var a = SentenceSplitter.SplitScript("The fox slept by the fire. The wind hummed.");
        var b = SentenceSplitter.SplitScript("Morning was far away. Goodnight, little fox.");
        var w0 = new NarrationScript { SentenceList = a.SentenceList, ParagraphStartSet = a.ParagraphStartSet, AppendFinalTail = false };
        var (r0, _, _) = await Render(w0, name: "p0.wav");
        var w1 = new NarrationScript
        {
            SentenceList = b.SentenceList,
            ParagraphStartSet = b.ParagraphStartSet,
            LeadingSilenceSeconds = PauseMap.SceneSeconds,
            AppendFinalTail = true,
            TimelineOffsetSeconds = r0.Manifest.DurationSeconds,
        };
        var (r1, _, _) = await Render(w1, name: "p1.wav");
        var joined = Path.Combine(_dir, "joined.wav");
        WavAudio.Concat(joined, [r0.Manifest.AudioPath, r1.Manifest.AudioPath]);

        // The silence across the seam is exactly the scene pause.
        var seam = (r0.TotalFrames - r0.Placements[^1].EndFrame)
                   + r1.Placements[0].StartFrame + Math.Max(0, r1.Placements[0].SoftHeadFrames - Keep);
        Assert.Equal(PauseMap.SceneSeconds, seam / 48000.0, 4);

        // The pad is continuous across the join: no jump larger than the pad's own motion.
        var x = ReadWav(joined);
        var j = (int)r0.TotalFrames;
        var jump = Math.Abs(x[j] - x[j - 1]);
        var typical = Enumerable.Range(j - 2000, 1990).Select(i => Math.Abs(x[i + 1] - x[i])).Max();
        Assert.True(jump <= typical * 1.5 + 2.0 / 32768, $"seam step {jump} vs local max step {typical}");
    }

    [Fact]
    public async Task Teaser_Renders_WithHeadAndTail_ButNoEnding()
    {
        var teaser = SentenceSplitter.SplitScript(Story).Take(3);
        var (result, plan, fake) = await Render(teaser);
        Assert.Equal(3, result.Manifest.Timestamps.Count);
        Assert.DoesNotContain(plan.Segments, s => s.InEnding);
        Assert.Equal(3.6 * 48000, result.Placements[0].StartFrame + Math.Max(0, result.Placements[0].SoftHeadFrames - Keep), 0);
        Assert.All(fake.Requests, r => Assert.True(r.Rate >= 0.40f));
    }

    [Fact]
    public async Task IsDeterministic()
    {
        var script = SentenceSplitter.SplitScript(Story).Take(4);
        var (r1, _, _) = await Render(script, name: "d1.wav");
        var (r2, _, _) = await Render(script, name: "d2.wav");
        Assert.Equal(File.ReadAllBytes(r1.Manifest.AudioPath), File.ReadAllBytes(r2.Manifest.AudioPath));
    }
}
