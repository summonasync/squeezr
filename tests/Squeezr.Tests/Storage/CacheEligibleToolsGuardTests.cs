using System.Text.RegularExpressions;
using Shouldly;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Storage;

public sealed partial class CacheEligibleToolsGuardTests
{
    [Fact]
    public void ServeToolsRecordingTelemetry_MatchTheEligibleSetExactly()
    {
        var mismatch = ToolSourceFiles()
            .Select(path => Scan(path, File.ReadAllText(path)))
            .SelectMany(scan => scan.Tools.Select(tool => (scan.File, tool, scan.HoldsCache)))
            .Where(row => CacheEligibleTools.Includes(row.tool) != row.HoldsCache)
            .Select(row => $"{row.File}: \"{row.tool}\" recorded by a tool that {(row.HoldsCache ? "holds" : "lacks")} the response cache")
            .ToList();

        mismatch.ShouldBeEmpty();
    }

    [Fact]
    public void GuardSeesTheCacheHolderAndTheCachelessSite()
    {
        var toolsDir = Path.Combine(FindRepoRoot(), "src", "Squeezr.Cli", "Mcp", "Tools");
        var codeSearch = Scan(Path.Combine(toolsDir, "CodeSearchTool.cs"), File.ReadAllText(Path.Combine(toolsDir, "CodeSearchTool.cs")));
        codeSearch.HoldsCache.ShouldBeTrue();
        codeSearch.Tools.ShouldContain("code_search");

        var expandSpan = Scan(Path.Combine(toolsDir, "ExpandSpanTool.cs"), File.ReadAllText(Path.Combine(toolsDir, "ExpandSpanTool.cs")));
        expandSpan.HoldsCache.ShouldBeFalse();
        expandSpan.Tools.ShouldContain("expand_span");
    }

    [Fact]
    public void GuardIsNotBlind_ToACachedToolRecordingOutsideTheSet()
    {
        Scan("T.cs", """
            public void Run()
            {
                QueryTelemetry.Record(root, id, "repo_map", null, 1, false, 0, 1);
            }
            """).Tools.ShouldContain("repo_map");

        Mismatches("T.cs", "QueryTelemetry.Record(root, id, \"repo_map\", null, 1, false, 0, 1);")
            .ShouldNotBeEmpty();
    }

    private static List<string> Mismatches(string file, string source)
    {
        var scan = Scan(file, source);
        return scan.Tools
            .Where(tool => CacheEligibleTools.Includes(tool) != scan.HoldsCache)
            .Select(tool => $"{file}: {tool}")
            .ToList();
    }

    private static (string File, bool HoldsCache, List<string> Tools) Scan(string file, string source) =>
        (Path.GetFileName(file),
            source.Contains("ToolResponseCache", StringComparison.Ordinal),
            RecordedTool().Matches(source)
                .Select(match => match.Groups["tool"].Value)
                .Distinct(StringComparer.Ordinal)
                .ToList());

    private static IEnumerable<string> ToolSourceFiles()
    {
        var toolsDir = Path.Combine(FindRepoRoot(), "src", "Squeezr.Cli", "Mcp", "Tools");
        Directory.Exists(toolsDir).ShouldBeTrue();
        return Directory.EnumerateFiles(toolsDir, "*.cs", SearchOption.TopDirectoryOnly);
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Squeezr.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException(
            $"Repository root (Squeezr.slnx) not found above {AppContext.BaseDirectory}; " +
            "the cache-eligible guard cannot run outside a checkout.");
    }

    [GeneratedRegex(@"QueryTelemetry\.Record\([^;""']*?""(?<tool>[a-z_]+)""", RegexOptions.Singleline)]
    private static partial Regex RecordedTool();
}
