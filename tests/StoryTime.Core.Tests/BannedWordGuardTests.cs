using System.Text.RegularExpressions;
using Xunit;

namespace StoryTime.Core.Tests;

/// <summary>Founder rule (2026-10-14, docs/design/copy-glossary.md): the old textile-metaphor
/// words never appear anywhere in the product source — not in copy, identifiers, file names,
/// routes, environment variables, BGTask ids, logs, comments, prompts, story templates, tests,
/// scripts or the design docs. This scans the repository so they can never creep back.
/// The pattern is assembled from pieces, so this file passes its own scan.</summary>
public sealed class BannedWordGuardTests
{
    /// <summary>Case-insensitive; matches inside longer words on purpose (identifiers, and
    /// look-alikes such as "bl" + "oom", which are reworded rather than whitelisted).</summary>
    public static readonly Regex Pattern = new(
        string.Join('|', "we" + "av", "wo" + "ven", "lo" + "om"),
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".xaml", ".swift", ".h", ".m", ".csproj", ".props", ".targets", ".slnx", ".plist",
        ".entitlements", ".storekit", ".json", ".md", ".txt", ".sh", ".yml", ".yaml", ".xml",
        ".html", ".css", ".js", ".resx", ".strings", ".editorconfig", ".gitignore",
    };

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
            if (!TextExtensions.Contains(Path.GetExtension(file))) continue;

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
        Assert.DoesNotContain("docs/design/qa-report.md", files);
        Assert.Matches(Pattern, "Story" + "We" + "aver");
        Assert.Matches(Pattern, "wo" + "ven");
        Assert.Matches(Pattern, "LO" + "OM");
        Assert.DoesNotMatch(Pattern, "Making tonight's story, with a soft hum");
    }

    /// <summary>src/, tests/, README.md and docs/design/*.md except the historical qa-report.md
    /// (and the qa-shots/ folder, which is not a *.md file in docs/design).</summary>
    private static IEnumerable<string> SourceFiles(string root)
    {
        foreach (var dir in new[] { "src", "tests" })
        foreach (var file in Walk(Path.Combine(root, dir)))
            yield return file;

        yield return Path.Combine(root, "README.md");

        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "docs", "design"), "*.md"))
            if (!string.Equals(Path.GetFileName(file), "qa-report.md", StringComparison.OrdinalIgnoreCase))
                yield return file;
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
