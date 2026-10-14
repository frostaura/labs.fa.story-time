using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StoryTime.Contracts;
using StoryTime.Core;
using StoryTime.Core.Abstractions;
using StoryTime.Core.Engines;
using StoryTime.Core.Engines.Sherpa;
using StoryTime.Core.Pipeline;
using StoryTime.Harness;

// StoryTime.Harness — headless pipeline runs on the dev Mac (real llama.cpp + sherpa-onnx).
//   weave   --age 5 --length 10 --interests foxes,ocean --voice ember [--seed N] [--out DIR]
//   outline --age 5 --interests foxes [--seed N]
//   tts     --text "..." | --file story.txt --voice willow [--out DIR] [--report]
//   ambient --palette NightHarbor --seed 7 [--mood calm] [--out DIR]
//   bench

var args0 = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
var opts = ParseOptions(args.Skip(1).ToArray());

return args0 switch
{
    "weave" => await WeaveAsync(opts),
    "outline" => await OutlineAsync(opts),
    "poster" => await PosterAsync(opts),
    "tts" => await TtsAsync(opts),
    "ambient" => Ambient(opts),
    "bench" => await BenchAsync(opts),
    "prose" => Prose(opts),
    "titles" => await TitlesAsync(opts),
    _ => Help(),
};

// ---------------- commands ----------------

async Task<int> WeaveAsync(Dictionary<string, string> o)
{
    var outDir = OutDir(o);
    var profile = ProfileFrom(o);
    await using var services = BuildServices(o, outDir);
    var pipeline = services.GetRequiredService<StoryPipeline>();
    var telemetry = new WeaveTelemetry();
    if (o.ContainsKey("timings")) pipeline.Telemetry = telemetry;
    var store = services.GetRequiredService<IStoryStore>();
    await store.InitializeAsync();
    await store.SaveProfileAsync(profile);

    Console.WriteLine($"Weaving: age {profile.Age}, {(int)profile.Length} min, voice {profile.Voice}, interests: {string.Join(", ", profile.Interests)}");
    Console.WriteLine($"Output:  {outDir}");
    var sw = Stopwatch.StartNew();
    var progress = new Progress<WeaveProgress>(p => Console.WriteLine($"  [{p.Stage,-9}] {p.Percent,3}%"));
    var story = await pipeline.WeaveAsync(profile, progress, CancellationToken.None);
    sw.Stop();

    if (story.State == StoryState.Failed)
    {
        Console.Error.WriteLine("WEAVE FAILED (gentle failure path). Check logs above.");
        return 1;
    }

    var words = story.FullText.Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries).Length;
    File.WriteAllText(Path.Combine(outDir, "story.txt"), story.FullText);
    File.WriteAllText(Path.Combine(outDir, "poster.json"), story.PosterSpecJson);
    File.WriteAllText(Path.Combine(outDir, "timings.json"), story.TimestampsJson);

    Console.WriteLine();
    Console.WriteLine($"Title:     {story.Title}");
    Console.WriteLine($"Words:     {words}");
    Console.WriteLine($"Narration: {story.DurationSeconds:F1}s → {story.AudioPath}");
    Console.WriteLine($"Teaser:    {WavAudio.ReadDurationSeconds(story.TeaserPath):F1}s → {story.TeaserPath}");
    Console.WriteLine($"Wall:      {sw.Elapsed.TotalSeconds:F0}s total");
    if (o.ContainsKey("timings"))
        PrintTimings(telemetry, services.GetRequiredService<IStoryEngine>(), sw.Elapsed.TotalSeconds);
    Console.WriteLine();
    Console.WriteLine("---- story ----");
    Console.WriteLine(story.FullText);
    return 0;
}

async Task<int> OutlineAsync(Dictionary<string, string> o)
{
    var profile = ProfileFrom(o);
    await using var services = BuildServices(o, OutDir(o));
    var engine = (LlamaStoryEngine)services.GetRequiredService<IStoryEngine>();
    var outline = await engine.GenerateOutlineAsync(profile, null, TemplateStoryEngine.SceneCount(profile.Length), CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(outline, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

// poster --age 5 --interests foxes [--seed N] [--out DIR] — runs the multi-candidate poster
// stage (two LLM samples + template safety, judged by CompositionCritic; scores print via
// the engine's log lines) and writes the winning spec to poster.json.
async Task<int> PosterAsync(Dictionary<string, string> o)
{
    var profile = ProfileFrom(o);
    var outDir = OutDir(o);
    await using var services = BuildServices(o, outDir);
    var engine = (LlamaStoryEngine)services.GetRequiredService<IStoryEngine>();
    var outline = await engine.GenerateOutlineAsync(profile, null, TemplateStoryEngine.SceneCount(profile.Length), CancellationToken.None);
    Console.WriteLine($"Outline:  \"{outline.Title}\" — settings: {string.Join(", ", outline.Settings)}");
    var sw = Stopwatch.StartNew();
    var spec = await engine.GeneratePosterSpecAsync(outline, CancellationToken.None);
    sw.Stop();
    var path = Path.Combine(outDir, "poster.json");
    File.WriteAllText(path, JsonSerializer.Serialize(spec, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Poster stage: {sw.Elapsed.TotalSeconds:F1}s wall → {path}");
    return 0;
}

async Task<int> TtsAsync(Dictionary<string, string> o)
{
    var text = o.TryGetValue("file", out var textFile)
        ? File.ReadAllText(textFile)
        : o.GetValueOrDefault("text") ?? "Hello from StoryTime. This is the Kokoro voice test.";
    var voice = VoiceFrom(o);
    var outDir = OutDir(o);
    await using var services = BuildServices(o, outDir);
    var tts = services.GetRequiredService<ITtsEngine>();
    // Audition flags: --sid N renders a raw Kokoro speaker id through the persona pipeline;
    // --no-mastering switches the cozy chain off (legacy −1 dBFS peak chain) for A/B.
    if (o.ContainsKey("no-mastering")) AudioShaper.CozyMastering = false;
    if (tts is SherpaTtsEngine sherpa && int.TryParse(o.GetValueOrDefault("sid"), out var sidOverride))
    {
        sherpa.SpeakerIdOverride = sidOverride;
        Console.WriteLine($"Speaker id override: {sidOverride} (mastering {(AudioShaper.CozyMastering ? "cozy" : "legacy")})");
    }
    var script = SentenceSplitter.SplitScript(text);
    var sentences = script.SentenceList;
    var outPath = Path.Combine(outDir, "tts.wav");
    var sw = Stopwatch.StartNew();
    var manifest = await tts.SynthesizeAsync(script, voice, outPath, null, CancellationToken.None);
    sw.Stop();
    Console.WriteLine($"Sentences: {sentences.Count} ({script.ParagraphStartSet.Count} paragraph breaks)");
    Console.WriteLine($"Audio:     {manifest.DurationSeconds:F1}s → {outPath}");
    Console.WriteLine($"Synth:     {sw.Elapsed.TotalSeconds:F1}s wall  (RTF {sw.Elapsed.TotalSeconds / Math.Max(manifest.DurationSeconds, 0.01):F2})");
    foreach (var t in manifest.Timestamps)
        Console.WriteLine($"  [{t.Index,2}] {t.StartSeconds,7:F2} → {t.EndSeconds,7:F2}  {sentences[t.Index][..Math.Min(50, sentences[t.Index].Length)]}");
    if (o.ContainsKey("report")) PacingReport(script);
    return 0;
}

// weave --timings table: per-stage wall (stage spans OVERLAP under the overlapped
// strategy — their sum minus total is the overlap saving), token accounting from the
// llama engine, and narration RTF.
static void PrintTimings(WeaveTelemetry t, IStoryEngine engine, double outerWallSeconds)
{
    Console.WriteLine();
    Console.WriteLine($"---- timings ({t.Strategy}) ----");
    Console.WriteLine($"  outline:    {t.OutlineSeconds,7:F1}s");
    Console.WriteLine($"  scenes:     {t.ScenesSeconds,7:F1}s   (scene gen + stitch + polish)");
    Console.WriteLine($"  poster:     {t.PosterSeconds,7:F1}s");
    Console.WriteLine($"  narration:  {t.NarrationSeconds,7:F1}s   (audio {t.NarrationAudioSeconds:F1}s → RTF {(t.NarrationAudioSeconds > 0 ? t.NarrationSeconds / t.NarrationAudioSeconds : 0):F2})");
    Console.WriteLine($"  total:      {t.TotalSeconds,7:F1}s   (outer wall {outerWallSeconds:F1}s)");
    var stageSum = t.OutlineSeconds + t.ScenesSeconds + t.PosterSeconds + t.NarrationSeconds;
    Console.WriteLine($"  stage sum:  {stageSum,7:F1}s   → overlap saving {Math.Max(0, stageSum - t.TotalSeconds):F1}s");
    if (engine is LlamaStoryEngine llama)
    {
        var s = llama.Stats;
        Console.WriteLine($"  llm calls:  {s.Calls}   prompt {s.PromptTokens} tok ({s.CachedPromptTokens} KV-cached, {s.PromptTokens - s.CachedPromptTokens} ingested)   generated {s.GeneratedTokens} tok");
    }
}

// Pacing report: the ProsodyDirector plan is a pure function of the script, so this
// recomputation is exactly what the engine rendered (same gaps, same order).
static void PacingReport(NarrationScript script)
{
    var gaps = ProsodyDirector.PlanGaps(script);
    Console.WriteLine();
    Console.WriteLine("---- pacing report (gap before sentence [i]) ----");
    foreach (var g in gaps)
    {
        var next = script.SentenceList[g.BeforeIndex];
        Console.WriteLine($"  [{g.BeforeIndex,2}] {g.Describe(),-52}  → \"{next[..Math.Min(38, next.Length)]}\"");
    }
    var s = ProsodyReport.Summarize(gaps);
    Console.WriteLine();
    Console.WriteLine($"  gaps: {s.GapCount}   mean {s.MeanSeconds:0.000}s   std {s.StdSeconds:0.000}s   CV {s.CV:0.00}   min {s.MinSeconds:0.00}s   max {s.MaxSeconds:0.00}s");
    Console.WriteLine($"  longest near-identical run (±{ProsodyReport.NearIdenticalWindowSeconds * 1000:0}ms): {s.LongestNearIdenticalRun}");
    Console.WriteLine($"  rule share: {string.Join("  ", s.RuleShare.Select(kv => $"{kv.Key} {kv.Value:P0}"))}");
}

async Task<int> BenchAsync(Dictionary<string, string> o)
{
    var outDir = OutDir(o);
    await using var services = BuildServices(o, outDir);

    Console.WriteLine("== LLM bench: 200-token generation (Qwen3-1.7B Q4_K_M) ==");
    var engine = (LlamaStoryEngine)services.GetRequiredService<IStoryEngine>();
    var r = await engine.BenchmarkAsync(200, CancellationToken.None);
    Console.WriteLine($"prompt:   {r.PromptTokens} tok @ {r.PromptTokensPerSecond:F1} tok/s");
    Console.WriteLine($"generate: {r.GeneratedTokens} tok @ {r.GenerateTokensPerSecond:F1} tok/s");

    Console.WriteLine();
    Console.WriteLine("== TTS bench: 10 sentences (Kokoro-82M int8, speed 0.92) ==");
    var tts = services.GetRequiredService<ITtsEngine>();
    var sentences = Enumerable.Range(1, 10)
        .Select(i => $"This is bench sentence number {i}, spoken gently for a sleepy little listener tonight.")
        .ToList();
    var sw = Stopwatch.StartNew();
    var manifest = await tts.SynthesizeAsync(sentences, NarratorVoice.Ember, Path.Combine(outDir, "bench-tts.wav"), null, CancellationToken.None);
    sw.Stop();
    Console.WriteLine($"audio:    {manifest.DurationSeconds:F1}s in {sw.Elapsed.TotalSeconds:F1}s wall → RTF {sw.Elapsed.TotalSeconds / Math.Max(manifest.DurationSeconds, 0.01):F2}");
    return 0;
}

int Ambient(Dictionary<string, string> o)
{
    var paletteText = o.GetValueOrDefault("palette", "NightHarbor");
    if (!Enum.TryParse<PosterPalette>(paletteText, ignoreCase: true, out var palette))
    {
        Console.Error.WriteLine($"Unknown palette '{paletteText}'. One of: {string.Join(", ", Enum.GetNames<PosterPalette>())}");
        return 2;
    }
    var seed = int.TryParse(o.GetValueOrDefault("seed"), out var s) ? s : 7;
    var mood = o.GetValueOrDefault("mood", "calm");
    var outDir = OutDir(o);

    var ctx = new StoryTime.Core.Audio.AmbientContext { Palette = palette, Mood = mood, Seed = seed };
    var sw = Stopwatch.StartNew();
    var loop = StoryTime.Core.Audio.AmbientComposer.Render(ctx);
    sw.Stop();
    var path = Path.Combine(outDir, $"ambient-{paletteText.ToLowerInvariant()}-{mood}-{seed}.wav");
    StoryTime.Core.Audio.AmbientComposer.WriteWav(path, loop);

    var peak = loop.Peak();
    var rms = loop.Rms();
    var seamDelta = Math.Max(
        Math.Abs(loop.Left[0] - loop.Left[^1]),
        Math.Abs(loop.Right[0] - loop.Right[^1]));
    Console.WriteLine($"Palette:  {palette} → character '{StoryTime.Core.Audio.AmbientMoodMap.ForPalette(palette).Name}', mood {mood}, seed {seed}");
    Console.WriteLine($"Loop:     {loop.DurationSeconds:F1}s @ {loop.SampleRate} Hz stereo");
    Console.WriteLine($"Peak:     {20 * Math.Log10(peak):F1} dBFS   RMS: {20 * Math.Log10(rms):F1} dBFS");
    Console.WriteLine($"Seam:     |x[0]-x[N-1]| = {seamDelta:E2}");
    Console.WriteLine($"Render:   {sw.ElapsedMilliseconds} ms");
    Console.WriteLine($"Wav:      {path} ({new FileInfo(path).Length / 1024.0 / 1024.0:F1} MB)");
    return 0;
}

// prose --file story.txt | --text "..." — sentence-rhythm report (founder title/prose
// quality pass 2026-07-31). Metrics come from ProseMetrics so tests, harness and any
// future in-app gauge agree byte-for-byte.
int Prose(Dictionary<string, string> o)
{
    string text;
    if (o.TryGetValue("file", out var file)) text = File.ReadAllText(file);
    else if (o.TryGetValue("text", out var inline)) text = inline;
    else { Console.Error.WriteLine("prose needs --file <path> or --text \"...\""); return 2; }

    var r = ProseMetrics.Analyze(text);
    Console.WriteLine("---- prose rhythm report ----");
    Console.WriteLine($"  sentences:            {r.SentenceCount}");
    Console.WriteLine($"  avg sentence length:  {r.AvgWords:0.0} words");
    Console.WriteLine($"  std / CV:             {r.StdWords:0.0} / {r.CV:0.00}");
    Console.WriteLine($"  short (<{ProseMetrics.ShortSentenceWords}w) share:    {r.ShortShare:P0}");
    Console.WriteLine($"  longest short run:    {r.LongestShortRun}");
    Console.WriteLine($"  distinct-word ratio:  {r.DistinctWordRatio:0.00}");
    return 0;
}

// titles --count 30 [--llm 4] [--age 6] [--interests foxes,ocean] — title-variety report:
// N template-engine titles across seeds (fast path, no models), pairwise token-Jaccard and
// shape classification; --llm K adds K real-LLM outline titles (needs ~/StoryTimeModels).
async Task<int> TitlesAsync(Dictionary<string, string> o)
{
    var profile = ProfileFrom(o);
    var count = int.TryParse(o.GetValueOrDefault("count"), out var n) ? n : 30;

    var titles = new List<string>();
    for (var seed = 1; seed <= count; seed++)
    {
        var engine = new TemplateStoryEngine(() => seed * 7919);
        var outline = await engine.GenerateOutlineAsync(profile, null, TemplateStoryEngine.SceneCount(profile.Length), CancellationToken.None);
        titles.Add(outline.Title);
        // Feed the full history forward as negatives (the template guard takes any list;
        // the pipeline threads the last 8 for the LLM prompt budget).
        profile.RecentTitles = [.. titles];
    }
    PrintTitleReport("template", titles);

    if (int.TryParse(o.GetValueOrDefault("llm"), out var llmCount) && llmCount > 0)
    {
        var llmProfile = ProfileFrom(o);
        var llmTitles = new List<string>();
        await using var services = BuildServices(o, OutDir(o));
        var engine = (LlamaStoryEngine)services.GetRequiredService<IStoryEngine>();
        for (var i = 0; i < llmCount; i++)
        {
            engine.Seed = (uint)(1000 + i * 313);
            var outline = await engine.GenerateOutlineAsync(llmProfile, null, TemplateStoryEngine.SceneCount(llmProfile.Length), CancellationToken.None);
            llmTitles.Add(outline.Title);
            llmProfile.RecentTitles = llmTitles.TakeLast(8).ToList();
            Console.WriteLine($"  llm title {i + 1}: \"{outline.Title}\"");
        }
        PrintTitleReport("llm", llmTitles);
    }
    return 0;

    static void PrintTitleReport(string label, List<string> titles)
    {
        Console.WriteLine($"---- title variety report ({label}, n={titles.Count}) ----");
        foreach (var t in titles) Console.WriteLine($"  [{TitleMetrics.ShapeOf(t),-10}] {t}");
        var pairs = new List<double>();
        for (var i = 0; i < titles.Count; i++)
            for (var j = i + 1; j < titles.Count; j++)
                pairs.Add(TitleMetrics.TokenJaccard(titles[i], titles[j]));
        var shapes = titles.Select(TitleMetrics.ShapeOf).Distinct().Count();
        Console.WriteLine($"  distinct titles: {titles.Distinct(StringComparer.OrdinalIgnoreCase).Count()}/{titles.Count}");
        Console.WriteLine($"  distinct shapes: {shapes}");
        if (pairs.Count > 0)
            Console.WriteLine($"  pairwise token-Jaccard: mean {pairs.Average():0.000}  max {pairs.Max():0.000}");
    }
}

int Help()
{
    Console.WriteLine("""
        StoryTime.Harness — headless story pipeline on the dev Mac

        Commands:
          weave   --age 5 --length 10 --interests foxes,ocean --voice ember [--seed N] [--out DIR]
                  [--timings per-stage wall + tokens] [--strategy overlapped|sequential]
          outline --age 5 --interests foxes [--seed N]
          poster  --age 5 --interests foxes [--seed N] [--out DIR]   (multi-candidate + critic)
          tts     --text "..." | --file story.txt --voice willow [--out DIR] [--report pacing table]
                  [--sid N raw Kokoro speaker audition] [--no-mastering legacy -1dBFS chain]
          ambient --palette NightHarbor --seed 7 [--mood calm] [--out DIR]
          bench
          prose   --file story.txt | --text "..."   (sentence-rhythm report)
          titles  --count 30 [--llm 4] [--age 6] [--interests foxes,ocean]   (variety report)

        Models:  $STORYTIME_MODEL_ROOT (default ~/StoryTimeModels) via scripts/fetch-models.sh
        Natives: $STORYTIME_NATIVE_ROOT (default ~/StoryTimeNative) via scripts/build-native.sh
        """);
    return 2;
}

// ---------------- wiring ----------------

ServiceProvider BuildServices(Dictionary<string, string> o, string outDir)
{
    var services = new ServiceCollection();
    services.AddLogging(b => b.AddSimpleConsole(c => c.SingleLine = true).SetMinimumLevel(LogLevel.Information));

    // Platform-style registrations FIRST (Core TryAdd* fills the rest) — mirrors the app heads.
    services.AddSingleton<IModelProvider, DesktopModelProvider>();
    services.AddSingleton<INotificationService, ConsoleNotificationService>();
    services.AddSingleton<IStoryEngine>(sp =>
    {
        var engine = new LlamaStoryEngine(sp.GetRequiredService<IModelProvider>(),
            sp.GetRequiredService<ILogger<LlamaStoryEngine>>());
        if (o.TryGetValue("seed", out var seedText) && uint.TryParse(seedText, out var seed))
            engine.Seed = seed;
        return engine;
    });
    services.AddSingleton<ITtsEngine>(sp => new SherpaTtsEngine(sp.GetRequiredService<IModelProvider>(),
        sp.GetRequiredService<ILogger<SherpaTtsEngine>>()));

    services.AddStoryTimeCore(new StoryTimeOptions
    {
        DatabasePath = Path.Combine(outDir, "storytime.db3"),
        MediaDirectory = outDir,
        ForcedWeaveStrategy = o.GetValueOrDefault("strategy")?.ToLowerInvariant() switch
        {
            "sequential" => StoryTime.Core.Abstractions.WeaveStrategy.Sequential,
            "overlapped" => StoryTime.Core.Abstractions.WeaveStrategy.Overlapped,
            _ => null,
        },
    });
    return services.BuildServiceProvider();
}

static Dictionary<string, string> ParseOptions(string[] rest)
{
    var o = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < rest.Length; i++)
    {
        if (!rest[i].StartsWith("--")) continue;
        var key = rest[i][2..];
        var value = i + 1 < rest.Length && !rest[i + 1].StartsWith("--") ? rest[++i] : "true";
        o[key] = value;
    }
    return o;
}

static ChildProfile ProfileFrom(Dictionary<string, string> o) => new()
{
    Name = o.GetValueOrDefault("name", "Riley"),
    Age = int.TryParse(o.GetValueOrDefault("age"), out var age) ? Math.Clamp(age, 3, 8) : 5,
    Interests = (o.GetValueOrDefault("interests") ?? "foxes,ocean").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
    Voice = VoiceFrom(o),
    Length = o.GetValueOrDefault("length") switch { "5" => StoryLength.Five, "15" => StoryLength.Fifteen, _ => StoryLength.Ten },
    Mode = StoryMode.OneShot,
};

static NarratorVoice VoiceFrom(Dictionary<string, string> o) =>
    (o.GetValueOrDefault("voice") ?? "ember").ToLowerInvariant() switch
    {
        "willow" => NarratorVoice.Willow,
        "sage" => NarratorVoice.Sage,
        _ => NarratorVoice.Ember,
    };

static string OutDir(Dictionary<string, string> o)
{
    var dir = o.GetValueOrDefault("out")
              ?? Path.Combine(Path.GetTempPath(), "storytime-harness", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
    Directory.CreateDirectory(dir);
    return dir;
}

namespace StoryTime.Harness
{
    internal sealed class ConsoleNotificationService : StoryTime.Core.Abstractions.INotificationService
    {
        public Task<bool> RequestPermissionAsync() => Task.FromResult(true);
        public Task ShowStoryReadyAsync(string childName, string storyTitle)
        {
            Console.WriteLine($"[notify] Story ready for {childName}: {storyTitle}");
            return Task.CompletedTask;
        }
    }
}
