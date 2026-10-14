using System.Reflection;
using StoryTime.Contracts;

namespace StoryTime.Core.Engines.Llama;

/// <summary>Loads GBNF grammars embedded in StoryTime.Contracts (Grammars/*.gbnf).</summary>
internal static class GrammarStore
{
    private static readonly Dictionary<string, string> Cache = [];
    private static readonly object Gate = new();

    public static string Outline => Load("outline.gbnf");
    public static string Poster => Load("poster.gbnf");

    internal static string Load(string fileName)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(fileName, out var cached)) return cached;
            var asm = typeof(StoryOutline).GetTypeInfo().Assembly;
            var resource = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Embedded grammar '{fileName}' not found in StoryTime.Contracts");
            using var stream = asm.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            Cache[fileName] = text;
            return text;
        }
    }
}
