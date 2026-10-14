using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using StoryTime.Core.Engines.Narration;
using StoryTime.Core.Engines.Narration.Dsp;
using StoryTime.NarrationLab;

// StoryTime voice lab (macOS). Three steps, driven by run-lab.sh:
//   plan    <story.txt> <plan.json>                       — the real SoothingPlanner's utterances
//   (swift) render-utterances.swift <plan.json> <voice> <pcm-dir>   — macOS AVSpeechSynthesizer
//   mix     <story.txt> <pcm-dir> <out.wav> [--no-pad] [--profile E|F|G|voice-lab] [--no-voice-stages]
//                                                          — the real SoothingNarrator chain + report (default: the shipped profile)
//   measure <wav>                                          — BS.1770 integrated + true peak of any 48k mono wav
//   shift-check [pcm-dir] [--profile X]                    — pitch/time stage artifacts: sines, then real utterances
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var json = new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

switch (args.FirstOrDefault())
{
    case "plan" when args.Length >= 3:
    {
        var plan = Plan(File.ReadAllText(args[1]), pad: true);
        var utterances = plan.Segments.Select(s => new { index = s.Index, text = s.Spoken, rate = s.Rate, pitch = s.Pitch }).ToList();
        File.WriteAllText(args[2], JsonSerializer.Serialize(new { utterances }, json));
        File.WriteAllText(Path.ChangeExtension(args[2], ".plan.txt"), SoothingPlanner.Describe(plan));
        Console.WriteLine($"planned {plan.Segments.Count} utterances, {plan.PlannedSilenceSeconds:0.0}s of planned silence");
        return 0;
    }
    case "mix" when args.Length >= 4:
    {
        var pad = !args.Contains("--no-pad");
        var text = File.ReadAllText(args[1]);
        var plan = Plan(text, pad);
        var outWav = args[3];
        var stems = new NarrationStems(Path.ChangeExtension(outWav, ".voice.wav"), pad ? Path.ChangeExtension(outWav, ".pad.wav") : null);
        var sw = Stopwatch.StartNew();
        var profile = ProfileArg(args);
        var narrator = new SoothingNarrator(new FileUtterances(args[2]), profile)
        {
            MeasureOutputTruePeak = true,
            Trace = (phase, elapsed) => Console.Error.WriteLine($"phase {phase}: {elapsed.TotalMilliseconds:0} ms"),
        };
        var result = await narrator.RenderAsync(plan, new VoiceChoice { VoiceId = "lab" }, outWav, null, CancellationToken.None, stems);
        sw.Stop();
        var report = Measure(text, plan, result, stems, sw.Elapsed, profile);
        File.WriteAllText(Path.ChangeExtension(outWav, ".report.json"), JsonSerializer.Serialize(report, json));
        Console.WriteLine(JsonSerializer.Serialize(report, json));
        return 0;
    }
    case "measure" when args.Length >= 2:
    {
        var x = ReadWav(args[1], out var rate);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            file = args[1],
            seconds = x.Length / (double)rate,
            integratedLufs = Math.Round(LoudnessMeter.Measure(x, rate), 2),
            truePeakDbtp = Math.Round(TruePeakMeter.MeasureDbtp(x), 2),
        }, json));
        return 0;
    }
    case "shift-check":
    {
        var profile = ProfileArg(args);
        var dir = args.Length >= 2 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : null;
        ShiftCheck.Run(profile, dir);
        return 0;
    }
    default:
        Console.Error.WriteLine("usage: plan <story> <plan.json> | mix <story> <pcm-dir> <out.wav> [--no-pad] [--profile X] | measure <wav> | shift-check [pcm-dir] [--profile X]");
        return 2;
}

static MasteringProfile ProfileArg(string[] args)
{
    var i = Array.IndexOf(args, "--profile");
    var profile = i >= 0 && i + 1 < args.Length ? MasteringProfile.Named(args[i + 1]) : MasteringProfile.Default;
    // A/B the voice stages: the same chain on the voice as rendered.
    return args.Contains("--no-voice-stages") ? profile with { Name = profile.Name + "-unshifted", VoiceStages = [] } : profile;
}

static NarrationPlan Plan(string story, bool pad) =>
    SoothingPlanner.Plan(SentenceSplitter.SplitScript(story), new SoothingOptions { Pad = pad });

static float[] ReadWav(string path, out int rate)
{
    var bytes = File.ReadAllBytes(path);
    rate = BitConverter.ToInt32(bytes, 24);
    var n = (bytes.Length - 44) / 2;
    var x = new float[n];
    for (var i = 0; i < n; i++) x[i] = BitConverter.ToInt16(bytes, 44 + 2 * i) / 32768f;
    return x;
}

static object Measure(string story, NarrationPlan plan, NarrationRenderResult r, NarrationStems stems, TimeSpan renderTime, MasteringProfile profile)
{
    const int rate = SoothingNarrator.SampleRate;
    var words = plan.Segments.Sum(s => s.Spoken.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
    var duration = r.TotalFrames / (double)rate;
    var speech = r.Placements.Sum(p => (p.EndFrame - p.StartFrame) / (double)rate);
    var storyTime = (r.Placements[^1].EndFrame - r.Placements[0].StartFrame) / (double)rate;

    // Pause accuracy: silent runs in the delivered voice stem (5 ms frames) between
    // consecutive utterances, against the planned pause. Reported at −45 dBFS (perceived
    // silence; the room tail decays through it within ~60 ms) and at −50 dBFS (strict).
    var voice = ReadWav(stems.VoiceStemPath!, out _);
    const int frame = rate / 200;
    var levels = new double[voice.Length / frame];
    for (var f = 0; f < levels.Length; f++) levels[f] = AudioShaper.RmsDbfs(voice.AsSpan(f * frame, frame));
    double Gap(int k, double threshold)
    {
        var mid = (int)((r.Placements[k - 1].EndFrame + r.Placements[k].StartFrame) / 2 / frame);
        var a = mid; while (a > 0 && levels[a - 1] <= threshold) a--;
        var b = mid; while (b < levels.Length && levels[b] <= threshold) b++;
        return (b - a) * frame / (double)rate;
    }
    var pauses = new List<object>();
    var errors = new List<double>();
    var strictErrors = new List<double>();
    for (var k = 1; k < r.Placements.Count; k++)
    {
        var planned = plan.Segments[k].PauseBeforeSeconds;
        var measured = Gap(k, -45);
        var strict = Gap(k, -50);
        errors.Add(measured - planned);
        strictErrors.Add(strict - planned);
        pauses.Add(new { k, kind = plan.Segments[k].PauseBefore.ToString(), planned = Math.Round(planned, 3), measured = Math.Round(measured, 3), strict = Math.Round(strict, 3) });
    }
    double? padOffset = null;
    if (stems.PadStemPath is { } pp)
    {
        var pad = ReadWav(pp, out _);
        var fadeOut = (int)(plan.PadFadeOutSeconds * rate);
        var steady = pad.AsSpan((int)(plan.PadFadeInSeconds * rate) + rate, pad.Length - fadeOut - (int)(plan.PadFadeInSeconds * rate) - 2 * rate);
        padOffset = Math.Round(LoudnessMeter.Measure(voice, rate) - LoudnessMeter.Measure(steady, rate), 2);
    }
    var sorted = errors.Select(Math.Abs).Order().ToList();

    return new
    {
        output = r.Manifest.AudioPath,
        profile = profile.Name,
        voiceStages = profile.VoiceStages.Select(v => v.Name).ToList(),
        seconds = Math.Round(duration, 2),
        words,
        wpmOverall = Math.Round(words / (duration / 60), 1),
        wpmStory = Math.Round(words / (storyTime / 60), 1),
        wpmSpeechOnly = Math.Round(words / (speech / 60), 1),
        integratedLufs = Math.Round(r.IntegratedLufs, 2),
        truePeakDbtp = Math.Round(r.TruePeakDbtp ?? double.NaN, 2),
        voiceLufs = Math.Round(r.VoiceLufs, 2),
        padLuBelowVoice = padOffset,
        maxCompressionDb = Math.Round(r.MaxCompressionDb, 1),
        maxLimitingDb = Math.Round(r.MaxLimitingDb, 1),
        maxDeEssDb = Math.Round(r.MaxDeEssDb, 1),
        pauseErrorMeanMs = Math.Round(errors.Average() * 1000, 1),
        pauseErrorMedianAbsMs = Math.Round(sorted[sorted.Count / 2] * 1000, 1),
        pauseErrorMaxAbsMs = Math.Round(sorted[^1] * 1000, 1),
        pausesWithin80ms = $"{errors.Count(e => Math.Abs(e) <= 0.080)}/{errors.Count}",
        pausesWithin80msStrict = $"{strictErrors.Count(e => Math.Abs(e) <= 0.080)}/{strictErrors.Count}",
        renderSeconds = Math.Round(renderTime.TotalSeconds, 2),
        realtimeFactor = Math.Round(duration / renderTime.TotalSeconds, 1),
        pauses,
    };
}

/// <summary>Per-utterance PCM rendered by render-utterances.swift: NNNN.f32 (Float32 LE mono)
/// + NNNN.json ({"rate":…, "text":…}).</summary>
internal sealed class FileUtterances(string dir) : IUtteranceSynthesizer
{
    public Task<UtterancePcm> RenderAsync(UtteranceRequest request, CancellationToken ct)
    {
        var stem = Path.Combine(dir, request.SegmentIndex.ToString("D4", CultureInfo.InvariantCulture));
        using var meta = JsonDocument.Parse(File.ReadAllText(stem + ".json"));
        var rate = meta.RootElement.GetProperty("rate").GetInt32();
        var text = meta.RootElement.GetProperty("text").GetString();
        if (text != request.Text) throw new InvalidOperationException($"utterance {request.SegmentIndex} text mismatch: '{text}' vs '{request.Text}'");
        var bytes = File.ReadAllBytes(stem + ".f32");
        var samples = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 4);
        return Task.FromResult(new UtterancePcm(samples, rate));
    }
}
