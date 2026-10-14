using System.Collections.Concurrent;
using System.Text.Json;
using StoryTime.Contracts;
using StoryTime.Poster;
using StoryTime.Poster.Composition;
using StoryTime.Poster.Spec;

namespace StoryTime.App.Graphics;

/// <summary>Rendered parallax layers for one (spec, size, travel) — encoded PNGs plus
/// placement metadata. Small enough to keep many in memory (PNG, not raw RGBA); the
/// decoded bitmaps live only in the Image views on screen.</summary>
public sealed class RenderedPoster
{
    public required PosterPlan Plan { get; init; }
    public required int BleedPx { get; init; }
    public required int VisibleWidthPx { get; init; }
    public required int VisibleHeightPx { get; init; }
    public required byte[] Backdrop { get; init; }
    public required byte[] Vignette { get; init; }
    public required IReadOnlyList<RenderedLayer> Layers { get; init; }
    public required bool AmbientBaked { get; init; }
    public long Bytes => Backdrop.Length + Vignette.Length + Layers.Sum(l => (long)l.Png.Length);
}

public sealed record RenderedLayer(int Depth, byte[] Png, int Left, int Top, int Right, int Bottom, float TravelPx);

/// <summary>Two-level cache of rendered poster layers: an in-memory LRU (bounded by encoded
/// bytes) in front of a disk cache under the app's cache directory. A poster is rendered
/// once per size — scrolling the carousel back, reopening the player or relaunching the
/// app costs a PNG decode, not a render. Keys carry the engine's ArtVersion, so a new
/// engine never serves old art. Renders run off the main thread and are de-duplicated.</summary>
public static class PosterLayerCache
{
    private const long MemoryBudgetBytes = 48L * 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly LinkedList<(string Key, RenderedPoster Poster)> Lru = new();
    private static readonly Dictionary<string, LinkedListNode<(string Key, RenderedPoster Poster)>> Index = new();
    private static readonly ConcurrentDictionary<string, Task<RenderedPoster>> InFlight = new();
    private static long _bytes;

    private static string DiskRoot => Path.Combine(FileSystem.CacheDirectory, $"poster-layers-v{PosterEngine.ArtVersion}");

    public static string KeyFor(PosterSpec spec, int widthPx, int heightPx, double travelScale, bool bake) =>
        $"{PosterSpecRules.CacheKey(spec)}_{widthPx}x{heightPx}_t{(int)Math.Round(travelScale * 100)}_{(bake ? "b" : "a")}";

    public static Task<RenderedPoster> GetAsync(PosterSpec spec, int widthPx, int heightPx, double travelScale, bool bakeAmbient)
    {
        var key = KeyFor(spec, widthPx, heightPx, travelScale, bakeAmbient);
        lock (Gate)
        {
            if (Index.TryGetValue(key, out var node))
            {
                Lru.Remove(node);
                Lru.AddFirst(node);
                return Task.FromResult(node.Value.Poster);
            }
        }
        return InFlight.GetOrAdd(key, k => Task.Run(() =>
        {
            try
            {
                var poster = TryLoad(k, spec) ?? Render(k, spec, widthPx, heightPx, travelScale, bakeAmbient);
                Remember(k, poster);
                return poster;
            }
            finally
            {
                InFlight.TryRemove(k, out _);
            }
        }));
    }

    private static RenderedPoster Render(string key, PosterSpec spec, int w, int h, double travelScale, bool bake)
    {
        using var set = PosterEngine.RenderLayers(spec, w, h, travelScale, bake);
        var layers = set.Layers.Select(l => new RenderedLayer(l.Depth, PosterEngine.EncodePng(l.Image),
            l.Bounds.Left, l.Bounds.Top, l.Bounds.Right, l.Bounds.Bottom, l.Travel)).ToList();
        var poster = new RenderedPoster
        {
            Plan = set.Plan,
            BleedPx = set.Viewport.BleedX,
            VisibleWidthPx = w,
            VisibleHeightPx = h,
            Backdrop = PosterEngine.EncodePng(set.Backdrop),
            Vignette = PosterEngine.EncodePng(set.Vignette),
            Layers = layers,
            AmbientBaked = set.AmbientBaked,
        };
        TrySave(key, poster);
        return poster;
    }

    private static void Remember(string key, RenderedPoster poster)
    {
        lock (Gate)
        {
            if (Index.ContainsKey(key)) return;
            var node = Lru.AddFirst((key, poster));
            Index[key] = node;
            _bytes += poster.Bytes;
            while (_bytes > MemoryBudgetBytes && Lru.Last is { } last && last != node)
            {
                Lru.RemoveLast();
                Index.Remove(last.Value.Key);
                _bytes -= last.Value.Poster.Bytes;
            }
        }
    }

    private sealed record DiskMeta(int Bleed, int W, int H, bool Baked, List<DiskLayer> Layers);
    private sealed record DiskLayer(int Depth, int Left, int Top, int Right, int Bottom, float Travel);

    private static void TrySave(string key, RenderedPoster p)
    {
        try
        {
            var dir = Path.Combine(DiskRoot, key);
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "backdrop.png"), p.Backdrop);
            File.WriteAllBytes(Path.Combine(dir, "vignette.png"), p.Vignette);
            foreach (var l in p.Layers) File.WriteAllBytes(Path.Combine(dir, $"layer{l.Depth}.png"), l.Png);
            var meta = new DiskMeta(p.BleedPx, p.VisibleWidthPx, p.VisibleHeightPx, p.AmbientBaked,
                p.Layers.Select(l => new DiskLayer(l.Depth, l.Left, l.Top, l.Right, l.Bottom, l.TravelPx)).ToList());
            // Meta last: its presence marks a complete entry.
            File.WriteAllText(Path.Combine(dir, "meta.json"), JsonSerializer.Serialize(meta));
        }
        catch (IOException) { /* the cache is an optimisation; rendering already succeeded */ }
        catch (UnauthorizedAccessException) { }
    }

    private static RenderedPoster? TryLoad(string key, PosterSpec spec)
    {
        try
        {
            var dir = Path.Combine(DiskRoot, key);
            var metaPath = Path.Combine(dir, "meta.json");
            if (!File.Exists(metaPath)) return null;
            var meta = JsonSerializer.Deserialize<DiskMeta>(File.ReadAllText(metaPath));
            if (meta is null || meta.Layers.Count != 4) return null;
            return new RenderedPoster
            {
                // The plan is cheap and deterministic: re-plan for the ambient particles.
                Plan = PosterEngine.Plan(spec),
                BleedPx = meta.Bleed,
                VisibleWidthPx = meta.W,
                VisibleHeightPx = meta.H,
                Backdrop = File.ReadAllBytes(Path.Combine(dir, "backdrop.png")),
                Vignette = File.ReadAllBytes(Path.Combine(dir, "vignette.png")),
                Layers = meta.Layers.Select(l => new RenderedLayer(l.Depth, File.ReadAllBytes(Path.Combine(dir, $"layer{l.Depth}.png")),
                    l.Left, l.Top, l.Right, l.Bottom, l.Travel)).ToList(),
                AmbientBaked = meta.Baked,
            };
        }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
