using System.Text.RegularExpressions;
using Shouldly;

namespace Squeezr.Tests.Storage;

// QueryTelemetry.Record's mapped parameter is optional and trailing, so a call site that forgets the state does not
// fail to compile: its row silently lands in unsplitRows forever, which reads as "this surface knows nothing".
// The sibling guard CacheEligibleToolsGuardTests is the precedent — an omission has to be loud, so here the tree
// itself is the fixture and every Record site under src/ must name its state or sit on the declared list.
public sealed partial class QueryTelemetryRecordStateGuardTests
{
    // one site is declared stateless: the refs CLI carries no budget at all (spec req 3 gives find_references no
    // mapped state and RefsCommand asks no ceiling either), so its rows are unsplittable by definition
    private static readonly string[] DeclaredStatelessFiles = ["RefsCommand.cs"];

    private const int RecordedSitesInSrc = 16;

    [Fact]
    public void EveryRecordSiteInSrc_NamesTheMappedState_ExceptTheDeclaredStatelessOnes()
    {
        var sites = SrcRecordSites();

        sites.Count.ShouldBe(RecordedSitesInSrc,
            "the scan sees a different number of Record sites than RecordedSitesInSrc — a new Record surface must " +
            "name its state at the site AND move RecordedSitesInSrc together with the call-site map in this task's " +
            "log; editing either one alone leaves the guard describing a tree that no longer exists");

        var silent = sites.Where(site => !site.NamesState)
            .Select(site => site.File)
            .Distinct()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        silent.ShouldBe([.. DeclaredStatelessFiles],
            "a Record site that omits mapped: becomes an unsplittable row in silence — name the state, or add the " +
            "file to DeclaredStatelessFiles and record why it has none");

        sites.Single(site => site.File == "HookPreReadCommand.cs").WritesExplicitNull.ShouldBeTrue();
    }

    [Fact]
    public void GuardSeesTheStateNamedWhereItIsKnownAndAbsentWhereItIsDeclared()
    {
        var sites = SrcRecordSites();

        sites.Single(site => site.File == "SymbolLookupTool.cs").NamesState.ShouldBeTrue();
        sites.Single(site => site.File == "CodePackTool.cs").NamesState.ShouldBeTrue();
        sites.Single(site => site.File == "SymbolCommand.cs").NamesState.ShouldBeTrue();
        sites.Single(site => site.File == "ExpandSpanTool.cs").NamesState.ShouldBeTrue();
        sites.Single(site => site.File == "RefsCommand.cs").NamesState.ShouldBeFalse();
    }

    [Fact]
    public void GuardIsNotBlind_ToASiteThatDropsTheMappedArgument()
    {
        Scan("""
            public void Run()
            {
                QueryTelemetry.Record(root, id, "symbol_lookup", parameters, 1, false, 0, 1);
            }
            """).NamesState.ShouldBeFalse();

        Scan("""
            public void Run()
            {
                QueryTelemetry.Record(root, id, "symbol_lookup", parameters, 1, false, 0, 1, mapped: false);
            }
            """).NamesState.ShouldBeTrue();

        // the wrapped multi-line form is what the real sites look like, so a late named argument must not read silent
        Scan("""
            public void Run()
            {
                QueryTelemetry.Record(root, id, "code_pack", parameters,
                    answer.TokensUsed, answer.Truncated, hits, sw.ElapsedMilliseconds,
                    cacheInvalidated: invalidated is not null,
                    baselineTokens: answer.Cost?.BaselineTokens,
                    mapped: MappedOf(answer));
            }
            """).NamesState.ShouldBeTrue();

        Scan("QueryTelemetry.Record(root, id, \"search\", null, 1, false, 0, 1, mapped: null);")
            .WritesExplicitNull.ShouldBeTrue();
    }

    [Fact]
    public void ScanIgnoresBuildOutputOnBothSeparatorForms()
    {
        IsBuildOutput("/repo/src/Squeezr.Cli/obj/Debug/net10.0/generated/RegexEmitter.cs").ShouldBeTrue();
        IsBuildOutput("""C:\repo\src\Squeezr.Cli\obj\Debug\net10.0\RegexEmitter.cs""").ShouldBeTrue();
        IsBuildOutput("""C:\repo\src\Squeezr.Core\bin\Release\net10.0\VersionInfo.cs""").ShouldBeTrue();

        IsBuildOutput("/repo/src/Squeezr.Cli/Mcp/Tools/CodePackTool.cs").ShouldBeFalse();
        IsBuildOutput("/repo/src/ObjectCache.cs").ShouldBeFalse();
    }

    private static (bool NamesState, bool WritesExplicitNull) Scan(string source)
    {
        var call = RecordCalls(source).ShouldHaveSingleItem();
        return (call.Contains("mapped:", StringComparison.Ordinal), ExplicitNull().IsMatch(call));
    }

    private static List<(string File, bool NamesState, bool WritesExplicitNull)> SrcRecordSites() =>
        SourceFiles()
            .SelectMany(path => RecordCalls(File.ReadAllText(path))
                .Select(call => (Path.GetFileName(path),
                    call.Contains("mapped:", StringComparison.Ordinal),
                    ExplicitNull().IsMatch(call)))
                .ToList())
            .ToList();

    private static IEnumerable<string> RecordCalls(string source) =>
        RecordCall().Matches(source).Select(match => match.Value);

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(Path.Combine(FindRepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(static path => !IsBuildOutput(path));

    // segment test on both slash kinds, not a "/obj/" substring: MSBuild hands this tree backslashes on Windows
    // (the installer target, feature 049), where a forward-slash literal never matches and the scan would walk the
    // generated sources under obj/ instead of the checkout
    private static bool IsBuildOutput(string path) =>
        path.Split(PathSeparators).Any(static segment =>
            segment.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("bin", StringComparison.OrdinalIgnoreCase));

    private static readonly char[] PathSeparators = ['/', '\\'];

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
            "the recorded-state guard cannot run outside a checkout.");
    }

    // one call runs from its opening paren to the last one before the statement's semicolon: no recorded argument
    // holds a semicolon, so the span cannot bleed into the next statement
    [GeneratedRegex(@"QueryTelemetry\.Record\([^;]*\)", RegexOptions.Singleline)]
    private static partial Regex RecordCall();

    [GeneratedRegex(@"mapped:\s*null\b", RegexOptions.Singleline)]
    private static partial Regex ExplicitNull();
}
