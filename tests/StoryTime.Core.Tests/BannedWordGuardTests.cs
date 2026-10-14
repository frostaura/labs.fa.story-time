using System.Text.RegularExpressions;
using Xunit;

namespace StoryTime.Core.Tests;

/// <summary>Founder rule (2026-10-14, docs/design/copy-glossary.md): the old textile-metaphor
/// words never appear anywhere in the repository — not in copy, identifiers, file names,
/// routes, environment variables, BGTask ids, logs, comments, prompts, story templates, tests,
/// scripts, docs, history notes or the design mockups. This walks every text file from the
/// repository root (only build output and scratch are skipped) so they can never creep back.
/// The pattern is assembled from pieces, so this file passes its own scan.</summary>
public sealed class BannedWordGuardTests
{
    /// <summary>Case-insensitive; matches inside longer words on purpose (identifiers, and
    /// look-alikes such as "bl" + "oom", which are reworded rather than whitelisted).</summary>
    public static readonly Regex Pattern = new(
        string.Join('|', "we" + "av", "wo" + "ven", "lo" + "om"),
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Bytes sniffed to tell text from binary: a NUL in the first 8 KB means binary
    /// (images, fonts, archives). Everything else — any extension, or none — is scanned.</summary>
    private const int SniffBytes = 8192;

    /// <summary>Build output and scratch; never source.</summary>
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "build", ".tmp", "DerivedData", "node_modules", ".git", ".vs", ".idea",
    };

    [Fact]
    public void Repository_source_never_contains_the_banned_words()
    {
        var root = RepositoryRoot();
        var hits = new List<string>();
        foreach (var file in SourceFiles(root))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (Pattern.IsMatch(relative)) hits.Add($"{relative} (path)");
            if (!IsText(file)) continue;

            var lineNumber = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNumber++;
                if (Pattern.IsMatch(line)) hits.Add($"{relative}:{lineNumber}: {line.Trim()}");
            }
        }

        Assert.True(hits.Count == 0,
            "Banned words found (founder rule, docs/design/copy-glossary.md) — use make / making / story / soft hum:\n"
            + string.Join('\n', hits));
    }

    [Fact]
    public void Guard_actually_scans_the_repository()
    {
        var root = RepositoryRoot();
        var files = SourceFiles(root).Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).ToList();
        Assert.Contains("README.md", files);
        Assert.Contains("docs/design/copy-glossary.md", files);
        Assert.Contains("src/app/StoryTime.Core/Pipeline/StoryPipeline.cs", files);
        Assert.Contains("src/app/StoryTime.App/Platforms/iOS/Info.plist", files);
        Assert.Contains("tests/StoryTime.Core.Tests/BannedWordGuardTests.cs", files);
        Assert.Contains("docs/prd.md", files);
        Assert.Contains("docs/design/Interactive.dc.html", files);
        Assert.Contains(".gitignore", files);
        Assert.True(IsText(Path.Combine(root, "docs", "design", "Storyboard.dc.html")));
        Assert.DoesNotContain(files, f => f.StartsWith(".git/", StringComparison.Ordinal));
        Assert.DoesNotContain(files, f => f.Contains("/bin/", StringComparison.Ordinal) || f.Contains("/obj/", StringComparison.Ordinal));
        Assert.Matches(Pattern, "Story" + "We" + "aver");
        Assert.Matches(Pattern, "wo" + "ven");
        Assert.Matches(Pattern, "LO" + "OM");
        Assert.DoesNotMatch(Pattern, "Making tonight's story, with a soft hum");
    }

    /// <summary>Every file under the repository root, minus build output and scratch.</summary>
    private static IEnumerable<string> SourceFiles(string root) => Walk(root);

    private static bool IsText(string file)
    {
        var buffer = new byte[SniffBytes];
        using var stream = File.OpenRead(file);
        var read = stream.Read(buffer, 0, buffer.Length);
        return Array.IndexOf(buffer, (byte)0, 0, read) < 0;
    }

    private static IEnumerable<string> Walk(string dir)
    {
        if (!Directory.Exists(dir)) yield break;
        foreach (var file in Directory.EnumerateFiles(dir))
            yield return file;
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            var name = Path.GetFileName(sub);
            // Lower-case bundle suffixes (*.xcframework, *.app, *.xcarchive) are build products;
            // ordinal so the StoryTime.App project folder is still scanned.
            if (SkippedDirectories.Contains(name) || name.EndsWith(".xcframework", StringComparison.Ordinal)
                || name.EndsWith(".app", StringComparison.Ordinal) || name.EndsWith(".xcarchive", StringComparison.Ordinal)) continue;
            foreach (var file in Walk(sub))
                yield return file;
        }
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "fa.story-time.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("Repository root (fa.story-time.slnx) not found above " + AppContext.BaseDirectory);
    }
}
