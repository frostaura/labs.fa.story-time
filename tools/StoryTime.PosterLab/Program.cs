using SkiaSharp;
using StoryTime.Contracts;
using StoryTime.Poster;
using StoryTime.Poster.Spec;

var outDir = args.Length > 0 && args[0] is not ("focus" or "layers") ? args[0] : "/private/tmp/storytime-posters";
Directory.CreateDirectory(outDir);

if (args.Length > 0 && args[0] == "layers")
{
    var spec = PosterSpecRules.Normalize(new PosterSpec { Scene = Enum.Parse<PosterScene>(args[1]), Seed = 4242 });
    var plan = PosterEngine.Plan(spec);
    foreach (var b in plan.Bands) Console.WriteLine($"band d{b.Depth} {b.Profile.Kind} base={b.Profile.Base:0.00} y(0.5)={b.Profile.Y(0.5):0.000} y(0.2)={b.Profile.Y(0.2):0.000}");
    foreach (var p in plan.Placements) Console.WriteLine($"prop d{p.Depth} {p.Prop} x={p.X:0.00} y={p.Y:0.00} s={p.Size:0.00}");
    using var set = PosterEngine.RenderLayers(spec, 400, 400);
    foreach (var l in set.Layers)
    {
        Console.WriteLine($"layer {l.Depth} bounds={l.Bounds} img={l.Image.Width}x{l.Image.Height}");
        File.WriteAllBytes(Path.Combine("/private/tmp/storytime-posters", $"dbg-layer{l.Depth}.png"), PosterEngine.EncodePng(l.Image));
    }
    return;
}

// Focus mode: `focus <Scene> [count]` renders larger posters of one scene for inspection.
if (args.Length > 0 && args[0] == "focus")
{
    var focusScene = Enum.Parse<PosterScene>(args[1]);
    var count = args.Length > 2 ? int.Parse(args[2]) : 6;
    const int big = 520;
    using var fs = SKSurface.Create(new SKImageInfo(3 * big, ((count + 2) / 3) * big));
    fs.Canvas.Clear(new SKColor(0x20, 0x18, 0x13));
    for (var i = 0; i < count; i++)
    {
        var fr = new StoryTime.Poster.Core.Rng(31 * i + 5);
        var feats = Enum.GetValues<PosterFeature>().Where(f => PosterSpecRules.IsCompatible(focusScene, f)).ToList();
        fr.Shuffle(feats);
        var spec = PosterSpecRules.Normalize(new PosterSpec
        {
            Scene = focusScene, TimeOfDay = (PosterTimeOfDay)(i % 4), Mood = (PosterMood)fr.Int(6),
            Palette = i % 2 == 0 ? PosterSpecRules.DefaultPalette(focusScene) : (PosterPalette)fr.Int(12),
            Features = feats.Take(1 + fr.Int(3)).ToList(), Seed = 4242 + 97 * i,
        });
        using var img = PosterEngine.RenderFlattened(spec, big - 6, big - 6);
        fs.Canvas.DrawImage(img, (i % 3) * big + 3, (i / 3) * big + 3);
        Console.WriteLine($"{i}: {spec.TimeOfDay} {spec.Mood} {spec.Palette} [{string.Join(",", spec.Features)}]");
    }
    using var fsnap = fs.Snapshot();
    var fpath = Path.Combine(outDir, $"focus-{focusScene}.png");
    File.WriteAllBytes(fpath, PosterEngine.EncodePng(fsnap, fast: false));
    Console.WriteLine(fpath);
    return;
}

// ---- 48+ posters: every scene × 4 seeds, varied time/mood/palette/features ----
var scenes = Enum.GetValues<PosterScene>();
var times = Enum.GetValues<PosterTimeOfDay>();
var moods = Enum.GetValues<PosterMood>();
var specs = new List<PosterSpec>();
for (var i = 0; i < scenes.Length * 5; i++)
{
    var scene = scenes[i % scenes.Length];
    var rng = new StoryTime.Poster.Core.Rng(1000 + i);
    var compatible = Enum.GetValues<PosterFeature>().Where(f => PosterSpecRules.IsCompatible(scene, f)).ToList();
    rng.Shuffle(compatible);
    var features = compatible.Take(1 + rng.Int(3)).ToList();
    specs.Add(PosterSpecRules.Normalize(new PosterSpec
    {
        Scene = scene,
        TimeOfDay = times[(i / scenes.Length + i) % times.Length],
        Mood = moods[rng.Int(moods.Length)],
        Palette = rng.Chance(0.6) ? PosterSpecRules.DefaultPalette(scene) : (PosterPalette)rng.Int(12),
        Features = features,
        Seed = 7919 * (i + 1),
    }));
}

const int tile = 300;
var cols = 6;
var sheetIndex = 0;
foreach (var chunk in specs.Chunk(24))
{
    var rows = (chunk.Length + cols - 1) / cols;
    var labelH = 34;
    using var surface = SKSurface.Create(new SKImageInfo(cols * tile, rows * (tile + labelH)));
    var c = surface.Canvas;
    c.Clear(new SKColor(0x20, 0x18, 0x13));
    using var font = new SKFont(SKTypeface.Default, 11);
    using var text = new SKPaint { Color = new SKColor(0xF3, 0xE9, 0xDC), IsAntialias = true };
    for (var i = 0; i < chunk.Length; i++)
    {
        var spec = chunk[i];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var img = PosterEngine.RenderFlattened(spec, tile - 8, tile - 8);
        var ms = sw.ElapsedMilliseconds;
        var x = (i % cols) * tile + 4;
        var y = (i / cols) * (tile + labelH) + 4;
        c.DrawImage(img, x, y);
        c.DrawText($"#{sheetIndex * 24 + i} {spec.Scene} · {spec.TimeOfDay} · {spec.Mood}", x, y + tile - 8 + 13, font, text);
        c.DrawText($"{spec.Palette} · {string.Join(",", spec.Features)} · {ms}ms", x, y + tile - 8 + 27, font, text);
    }
    using var snap = surface.Snapshot();
    var path = Path.Combine(outDir, $"contact-sheet-{sheetIndex + 1}.png");
    File.WriteAllBytes(path, PosterEngine.EncodePng(snap, fast: false));
    Console.WriteLine(path);
    sheetIndex++;
}

// ---- Parallax previews: 3 frames (tilt left / centre / right) for 6 posters ----
var previewIdx = new[] { 0, 13, 26, 40, 44, 58 }; // sea, forest, lake, snow village, reef, town
foreach (var idx in previewIdx)
{
    var spec = specs[idx % specs.Count];
    using var set = PosterEngine.RenderLayers(spec, 452, 452);
    using var strip = SKSurface.Create(new SKImageInfo(452 * 3 + 16, 452));
    strip.Canvas.Clear(new SKColor(0x20, 0x18, 0x13));
    var tilts = new[] { -1.0, 0.0, 1.0 };
    for (var k = 0; k < 3; k++)
    {
        using var frame = PosterEngine.Composite(set, tilts[k], tilts[k] * 0.35, ambientTime: k * 1.7);
        strip.Canvas.DrawImage(frame, k * (452 + 8), 0);
    }
    using var snap = strip.Snapshot();
    var path = Path.Combine(outDir, $"parallax-{idx:00}-{spec.Scene}.png");
    File.WriteAllBytes(path, PosterEngine.EncodePng(snap, fast: false));
    Console.WriteLine($"{path}  render={set.RenderTime.TotalMilliseconds:0}ms mem={set.Bytes / 1048576.0:0.0}MB");
}
