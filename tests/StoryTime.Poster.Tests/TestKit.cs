using System.Runtime.CompilerServices;
using SkiaSharp;
using StoryTime.Contracts;
using StoryTime.Poster.Color;
using StoryTime.Poster.Core;
using StoryTime.Poster.Spec;

namespace StoryTime.Poster.Tests;

internal static class TestKit
{
    public static readonly PosterScene[] Scenes = Enum.GetValues<PosterScene>();
    public static readonly PosterPalette[] Palettes = Enum.GetValues<PosterPalette>();
    public static readonly PosterTimeOfDay[] Times = Enum.GetValues<PosterTimeOfDay>();
    public static readonly PosterMood[] Moods = Enum.GetValues<PosterMood>();

    /// <summary>A valid random spec: scene-compatible features, any palette/time/mood.</summary>
    public static PosterSpec RandomSpec(int i)
    {
        var r = new Rng(9_000 + i);
        var scene = Scenes[r.Int(Scenes.Length)];
        var feats = Enum.GetValues<PosterFeature>().Where(f => PosterSpecRules.IsCompatible(scene, f)).ToList();
        r.Shuffle(feats);
        return PosterSpecRules.Normalize(new PosterSpec
        {
            Scene = scene,
            TimeOfDay = Times[r.Int(Times.Length)],
            Mood = Moods[r.Int(Moods.Length)],
            Palette = Palettes[r.Int(Palettes.Length)],
            Features = feats.Take(r.Int(4)).ToList(),
            Seed = (int)(r.NextULong() & 0x7FFFFFFF),
        });
    }

    public static byte[] Pixels(SKImage image)
    {
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var bmp = new SKBitmap(info);
        image.ReadPixels(info, bmp.GetPixels(), info.RowBytes, 0, 0);
        return bmp.GetPixelSpan().ToArray();
    }

    public static string Hash(SKImage image) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Pixels(image)));

    public static double Luminance(byte[] px, int i) => Srgb.RelativeLuminance(px[i], px[i + 1], px[i + 2]);

    public static readonly double CreamLuminance = Srgb.RelativeLuminance(new SKColor(0xF3, 0xE9, 0xDC));

    public static string SourceDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

    public static string ReportDir
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("STORYTIME_REPORT_DIR") ?? "/private/tmp/storytime-posters-v2";
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
