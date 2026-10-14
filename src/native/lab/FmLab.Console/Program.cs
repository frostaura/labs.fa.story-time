using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FmLab;
using StoryTime.Contracts;
using StoryTime.Core.Engines.AppleFoundation;
using StoryTime.Core.Pipeline;
using StoryTime.Core.Poster;

// Usage: FmLab.Console <fmlabhost path> <output dir> [sceneCount]
var host = args[0];
var outDir = args[1];
var sceneCount = args.Length > 2 ? int.Parse(args[2]) : 5;

using var bridge = new PipeBridge(host);
var availability = bridge.GetAvailability();
Console.WriteLine($"availability: {availability}");

var timings = new List<(string Stage, double Seconds, int Words)>();
var engine = new AppleFoundationStoryEngine(bridge, new SafetyFilter(), () => 20261014);
var profile = new ChildProfile
{
    Name = "Mila", Age = 5, Interests = ["Ocean", "Stars"], Length = StoryLength.Five, Mode = StoryMode.OneShot,
};

var sw = Stopwatch.StartNew();
var outline = await engine.GenerateOutlineAsync(profile, null, sceneCount, CancellationToken.None);
timings.Add(("outline", sw.Elapsed.TotalSeconds, 0));
Console.WriteLine($"outline {sw.Elapsed.TotalSeconds:0.0}s: {outline.Title}");

var scenes = new List<string>();
foreach (var scene in outline.Scenes)
{
    sw.Restart();
    var tail = scenes.Count > 0 ? StoryPipeline.SplitSentences(scenes[^1]).LastOrDefault() ?? string.Empty : string.Empty;
    var text = await engine.GenerateSceneAsync(profile, outline, scene, tail, CancellationToken.None);
    scenes.Add(text);
    var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
    timings.Add(($"scene {scene.Id + 1} (+digest)", sw.Elapsed.TotalSeconds, words));
    Console.WriteLine($"scene {scene.Id + 1}: {sw.Elapsed.TotalSeconds:0.0}s, {words} words");
}

var polishedWindows = new List<string>();
for (var start = 0; start < scenes.Count; start += StoryPipeline.ScenesPerWindow)
{
    sw.Restart();
    var window = string.Join("\n\n", scenes.Skip(start).Take(StoryPipeline.ScenesPerWindow));
    var isFinal = start + StoryPipeline.ScenesPerWindow >= scenes.Count;
    var prev = polishedWindows.Count > 0 ? StoryPipeline.SplitSentences(polishedWindows[^1]).LastOrDefault() ?? string.Empty : string.Empty;
    var polished = await engine.PolishWindowAsync(profile, window, prev, isFinal, CancellationToken.None);
    polishedWindows.Add(polished);
    timings.Add(($"polish window {polishedWindows.Count}", sw.Elapsed.TotalSeconds, polished.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length));
    Console.WriteLine($"polish window {polishedWindows.Count}: {sw.Elapsed.TotalSeconds:0.0}s");
}

sw.Restart();
var poster = new PosterComposer().Validate(await engine.GeneratePosterSpecAsync(outline, CancellationToken.None));
timings.Add(("poster", sw.Elapsed.TotalSeconds, 0));
Console.WriteLine($"poster {sw.Elapsed.TotalSeconds:0.0}s: {poster.Palette}");

var safety = new SafetyFilter();
var full = string.Join("\n\n", polishedWindows);
var md = new StringBuilder();
md.AppendLine($"# {outline.Title}").AppendLine();
md.AppendLine($"availability: {availability}").AppendLine();
md.AppendLine("| stage | seconds | words |").AppendLine("|---|---|---|");
foreach (var (stage, seconds, words) in timings) md.AppendLine($"| {stage} | {seconds:0.0} | {words} |");
md.AppendLine($"| **total** | {timings.Sum(t => t.Seconds):0.0} | {full.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length} |").AppendLine();
md.AppendLine($"lexicon flags on final text: [{string.Join(", ", safety.Check(full))}]").AppendLine();
md.AppendLine("## Outline").AppendLine().AppendLine("```json").AppendLine(JsonSerializer.Serialize(outline, new JsonSerializerOptions { WriteIndented = true })).AppendLine("```").AppendLine();
md.AppendLine("## Raw scenes").AppendLine();
for (var i = 0; i < scenes.Count; i++) md.AppendLine($"### {i + 1}").AppendLine().AppendLine(scenes[i]).AppendLine();
md.AppendLine("## Polished story").AppendLine().AppendLine(full).AppendLine();
md.AppendLine("## Poster").AppendLine().AppendLine("```json").AppendLine(JsonSerializer.Serialize(poster)).AppendLine("```").AppendLine();
md.AppendLine("## Engine events").AppendLine();
foreach (var e in engine.RecentEvents) md.AppendLine($"- {e.Stage}: {e.What} ({e.Code}) {e.Detail}");

var path = Path.Combine(outDir, $"story-{DateTime.Now:HHmmss}.md");
File.WriteAllText(path, md.ToString());
Console.WriteLine($"wrote {path}");
