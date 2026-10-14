using SkiaSharp;
using StoryTime.Contracts;
using StoryTime.Poster;
using StoryTime.Poster.Spec;

var outDir = args.Length > 0 && args[0] is not ("focus" or "layers" or "one" or "creatures" or "perf" or "rand") ? args[0] : "/private/tmp/storytime-posters-v2";
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
        File.WriteAllBytes(Path.Combine("/private/tmp/storytime-posters-v2", $"dbg-layer{l.Depth}.png"), PosterEngine.EncodePng(l.Image));
    }
    return;
}

if (args.Length > 0 && args[0] == "one")
{
    // `one <index>`: the contact-sheet spec #index at 700 px, for close inspection.
    var idx = int.Parse(args[1]);
    var scenesAll = Enum.GetValues<PosterScene>();
    var scene1 = scenesAll[idx % scenesAll.Length];
    var rng1 = new StoryTime.Poster.Core.Rng(1000 + idx);
    var compatible1 = Enum.GetValues<PosterFeature>().Where(f => PosterSpecRules.IsCompatible(scene1, f)).ToList();
    rng1.Shuffle(compatible1);
    // Same draw order as the contact sheet, so `one N` is exactly sheet poster #N.
    var features1 = compatible1.Take(1 + rng1.Int(3)).ToList();
    var spec1 = PosterSpecRules.Normalize(new PosterSpec
    {
        Scene = scene1,
        TimeOfDay = Enum.GetValues<PosterTimeOfDay>()[(idx / scenesAll.Length + idx) % 4],
        Mood = Enum.GetValues<PosterMood>()[rng1.Int(6)],
        Palette = rng1.Chance(0.6) ? PosterSpecRules.DefaultPalette(scene1) : (PosterPalette)rng1.Int(12),
        Features = features1,
        Seed = 7919 * (idx + 1),
    });
    using var big1 = PosterEngine.RenderFlattened(spec1, 700, 700);
    File.WriteAllBytes(Path.Combine(outDir, $"one-{idx}.png"), PosterEngine.EncodePng(big1, fast: false));
    Console.WriteLine($"{spec1.Scene} {spec1.TimeOfDay} [{string.Join(",", spec1.Features)}]");
    var plan1 = PosterEngine.Plan(spec1);
    foreach (var p in plan1.Placements.Where(p => p.Depth >= 2 && p.Prop is not StoryTime.Poster.Composition.Prop.GrassTuft))
        Console.WriteLine($"  d{p.Depth} {p.Prop} x={p.X:0.00} y={p.Y:0.00} s={p.Size:0.00} flip={p.Flip}{(ReferenceEquals(p, plan1.Focal) ? " HERO" : "")}");
    return;
}

// `creatures`: the creature sheet — every creature at player size and at card size, on
// three different nights, so shape language and readability are judged side by side.
if (args.Length > 0 && args[0] == "creatures")
{
    var kinds = new (StoryTime.Poster.Composition.Prop Prop, int Variant)[]
    {
        (StoryTime.Poster.Composition.Prop.Fox, 0), (StoryTime.Poster.Composition.Prop.FoxCurled, 0), (StoryTime.Poster.Composition.Prop.Rabbit, 0),
        (StoryTime.Poster.Composition.Prop.Owl, 0), (StoryTime.Poster.Composition.Prop.Deer, 0), (StoryTime.Poster.Composition.Prop.Deer, 1),
        (StoryTime.Poster.Composition.Prop.Bear, 0), (StoryTime.Poster.Composition.Prop.Cat, 0), (StoryTime.Poster.Composition.Prop.Hedgehog, 0),
        (StoryTime.Poster.Composition.Prop.Otter, 0), (StoryTime.Poster.Composition.Prop.Whale, 0), (StoryTime.Poster.Composition.Prop.Whale, 1),
        (StoryTime.Poster.Composition.Prop.Turtle, 0), (StoryTime.Poster.Composition.Prop.Turtle, 1), (StoryTime.Poster.Composition.Prop.FishSchool, 0),
        (StoryTime.Poster.Composition.Prop.Camel, 0), (StoryTime.Poster.Composition.Prop.Dinosaur, 0), (StoryTime.Poster.Composition.Prop.Birds, 0),
    };
    var nights = new[]
    {
        new PosterSpec { Scene = PosterScene.StarryHills, TimeOfDay = PosterTimeOfDay.Dusk, Palette = PosterPalette.MoonMeadow, Mood = PosterMood.Cozy, Seed = 3 },
        new PosterSpec { Scene = PosterScene.StarryHills, TimeOfDay = PosterTimeOfDay.Moonlit, Palette = PosterPalette.NightHarbor, Mood = PosterMood.Calm, Seed = 4 },
        new PosterSpec { Scene = PosterScene.StarryHills, TimeOfDay = PosterTimeOfDay.Starlit, Palette = PosterPalette.PineForest, Mood = PosterMood.Curious, Seed = 5 },
    };
    foreach (var (tileSize, name) in new[] { (240, "creatures-player"), (84, "creatures-card") })
    {
        using var cs = SKSurface.Create(new SKImageInfo(kinds.Length * tileSize, nights.Length * tileSize));
        cs.Canvas.Clear(new SKColor(0x20, 0x18, 0x13));
        for (var n = 0; n < nights.Length; n++)
        {
            var plan = PosterEngine.Plan(nights[n]);
            plan = new StoryTime.Poster.Composition.PosterPlan
            {
                Spec = plan.Spec, Colors = plan.Colors, Horizon = plan.Horizon, Side = plan.Side, Moon = plan.Moon, Stars = plan.Stars,
                Clouds = [], Bands = plan.Bands, Placements = [], Ambient = [], TitleSafe = plan.TitleSafe, Underwater = false,
                Lights = [new StoryTime.Poster.Composition.LightPlan(0.2, 0.8, 0.1, 1, 3, StoryTime.Poster.Composition.LightKind.Lantern)],
            };
            for (var i = 0; i < kinds.Length; i++)
            {
                var (prop, variant) = kinds[i];
                var t = tileSize - 2;
                using var cell = SKSurface.Create(new SKImageInfo(t, t));
                cell.Canvas.Clear(SKColors.Black);
                using (var bgSet = PosterEngine.RenderLayers(plan, t, t, 0, true))
                using (var bgOnly = PosterEngine.Composite(bgSet)) cell.Canvas.DrawImage(bgOnly, 0, 0);
                var vp = new StoryTime.Poster.Render.PosterViewport(t, t, 0, 0);
                var hf = StoryTime.Poster.Render.PropPainter.HeightFactor(prop, variant);
                var size = prop == StoryTime.Poster.Composition.Prop.Birds ? 0.5 : 0.62 / hf;
                if (prop is StoryTime.Poster.Composition.Prop.Whale or StoryTime.Poster.Composition.Prop.Dinosaur or StoryTime.Poster.Composition.Prop.FishSchool
                    or StoryTime.Poster.Composition.Prop.FoxCurled or StoryTime.Poster.Composition.Prop.Turtle) size = Math.Min(size, 0.42);
                var y = prop == StoryTime.Poster.Composition.Prop.Birds ? 0.4 : prop == StoryTime.Poster.Composition.Prop.FishSchool ? 0.95 - size * 0.05 : 0.9;
                StoryTime.Poster.Render.PropPainter.Paint_(cell.Canvas, plan,
                    new StoryTime.Poster.Composition.Placement(prop, 3, 0.5, y, size, false, variant, 11u + (uint)n), vp);
                using var timg = cell.Snapshot();
                cs.Canvas.DrawImage(timg, i * tileSize + 1, n * tileSize + 1);
            }
        }
        using var csnap = cs.Snapshot();
        File.WriteAllBytes(Path.Combine(outDir, $"{name}.png"), PosterEngine.EncodePng(csnap, fast: false));
        Console.WriteLine(Path.Combine(outDir, $"{name}.png"));
    }
    return;
}

// `rand <i> [px]`: the test kit's random spec #i (TestKit.RandomSpec), for inspecting a
// poster a metric flagged.
if (args.Length > 0 && args[0] == "rand")
{
    var i = int.Parse(args[1]);
    var px = args.Length > 2 ? int.Parse(args[2]) : 600;
    var r = new StoryTime.Poster.Core.Rng(9_000 + i);
    var allScenes = Enum.GetValues<PosterScene>();
    var scene = allScenes[r.Int(allScenes.Length)];
    var feats = Enum.GetValues<PosterFeature>().Where(f => PosterSpecRules.IsCompatible(scene, f)).ToList();
    r.Shuffle(feats);
    var spec = PosterSpecRules.Normalize(new PosterSpec
    {
        Scene = scene,
        TimeOfDay = Enum.GetValues<PosterTimeOfDay>()[r.Int(4)],
        Mood = Enum.GetValues<PosterMood>()[r.Int(6)],
        Palette = Enum.GetValues<PosterPalette>()[r.Int(12)],
        Features = feats.Take(r.Int(4)).ToList(),
        Seed = (int)(r.NextULong() & 0x7FFFFFFF),
    });
    var plan = PosterEngine.Plan(spec);
    Console.WriteLine($"{spec.Scene} {spec.TimeOfDay} {spec.Mood} {spec.Palette} [{string.Join(",", spec.Features)}] seed {spec.Seed} layout {plan.Layout}");
    foreach (var p in plan.Placements.Where(p => p.Depth >= 2 && p.Prop is not StoryTime.Poster.Composition.Prop.GrassTuft))
        Console.WriteLine($"  d{p.Depth} {p.Prop} x={p.X:0.00} y={p.Y:0.00} s={p.Size:0.00}{(ReferenceEquals(p, plan.Focal) ? " HERO" : "")}");
    using var img = PosterEngine.RenderFlattened(spec, px, px);
    File.WriteAllBytes(Path.Combine(outDir, $"rand-{i}.png"), PosterEngine.EncodePng(img, fast: false));
    return;
}

// `perf`: where the player render time goes (full, no props, no framing, no creatures, no lights).
if (args.Length > 0 && args[0] == "perf")
{
    var perfSpecs = Enumerable.Range(0, 12).Select(i => PosterSpecRules.Normalize(new PosterSpec
    {
        Scene = (PosterScene)i, TimeOfDay = (PosterTimeOfDay)(i % 4), Mood = (PosterMood)(i % 6), Palette = PosterSpecRules.DefaultPalette((PosterScene)i),
        Features = Enum.GetValues<PosterFeature>().Where(f => PosterSpecRules.IsCompatible((PosterScene)i, f)).Skip(i % 3).Take(2).ToList(), Seed = 100 + i,
    })).ToList();
    using (PosterEngine.RenderLayers(perfSpecs[0], 200, 200)) { }
    StoryTime.Poster.Composition.PosterPlan Strip(StoryTime.Poster.Composition.PosterPlan p, Func<StoryTime.Poster.Composition.Placement, bool> keep, bool lights = true) => new()
    {
        Spec = p.Spec, Colors = p.Colors, Horizon = p.Horizon, Side = p.Side, Layout = p.Layout, Moon = p.Moon, Stars = p.Stars, MilkyWay = p.MilkyWay,
        Clouds = p.Clouds, Aurora = p.Aurora, ShootingStar = p.ShootingStar, Bands = p.Bands, Water = p.Water,
        Placements = p.Placements.Where(keep).ToList(), Ambient = p.Ambient, Lights = lights ? p.Lights : [], Focal = p.Focal, TitleSafe = p.TitleSafe, Underwater = p.Underwater,
    };
    var variants = new (string Name, Func<StoryTime.Poster.Composition.PosterPlan, StoryTime.Poster.Composition.PosterPlan> F)[]
    {
        ("full", p => p),
        ("no props", p => Strip(p, _ => false, false)),
        ("no framing", p => Strip(p, q => q.Prop != StoryTime.Poster.Composition.Prop.FrameCluster)),
        ("no creatures", p => Strip(p, q => !StoryTime.Poster.Render.PropPainter.IsCreatureProp(q.Prop))),
        ("no lights", p => Strip(p, _ => true, false)),
    };
    foreach (var (name, fn) in variants)
    {
        var ptimes = new List<double>();
        foreach (var spec in perfSpecs)
        {
            var plan = fn(PosterEngine.Plan(spec));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var set = PosterEngine.RenderLayers(plan, 678, 678);
            ptimes.Add(sw.Elapsed.TotalMilliseconds);
        }
        ptimes.Sort();
        Console.WriteLine($"{name,-14} median {ptimes[6]:0} ms  max {ptimes[^1]:0} ms");
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

// ---- Card-size sheet: all 60 at the size a Home card / Kid Shelf tile shows (~1x pt) ----
{
    const int card = 112;
    const int cardCols = 10;
    using var cardSurface = SKSurface.Create(new SKImageInfo(cardCols * card, ((specs.Count + cardCols - 1) / cardCols) * card));
    cardSurface.Canvas.Clear(new SKColor(0x20, 0x18, 0x13));
    for (var i = 0; i < specs.Count; i++)
    {
        using var img = PosterEngine.RenderFlattened(specs[i], card - 6, card - 6);
        cardSurface.Canvas.DrawImage(img, (i % cardCols) * card + 3, (i / cardCols) * card + 3);
    }
    using var cardSnap = cardSurface.Snapshot();
    var cardPath = Path.Combine(outDir, "contact-sheet-card-size.png");
    File.WriteAllBytes(cardPath, PosterEngine.EncodePng(cardSnap, fast: false));
    Console.WriteLine(cardPath);
}

// ---- Parallax previews: 3 frames (tilt left / centre / right) for 8 posters ----
var previewIdx = new[] { 0, 13, 26, 40, 44, 58, 7, 33 }; // sea, forest, lake, snow village, reef, town, hills, cove
foreach (var idx in previewIdx)
{
    var spec = specs[idx % specs.Count];
    using var set = PosterEngine.RenderLayers(spec, 452, 452);
    using var strip = SKSurface.Create(new SKImageInfo(452 * 3 + 16, 452));
    strip.Canvas.Clear(new SKColor(0x20, 0x18, 0x13));
    var tilts = new[] { -1.0, 0.0, 1.0 };
    for (var k = 0; k < 3; k++)
    {
        using var frame = PosterEngine.Composite(set, tilts[k], tilts[k] * 0.5, ambientTime: k * 1.7);  // the apps vertical factor
        strip.Canvas.DrawImage(frame, k * (452 + 8), 0);
    }
    using var snap = strip.Snapshot();
    var path = Path.Combine(outDir, $"parallax-{idx:00}-{spec.Scene}.png");
    File.WriteAllBytes(path, PosterEngine.EncodePng(snap, fast: false));
    Console.WriteLine($"{path}  render={set.RenderTime.TotalMilliseconds:0}ms mem={set.Bytes / 1048576.0:0.0}MB");
}
