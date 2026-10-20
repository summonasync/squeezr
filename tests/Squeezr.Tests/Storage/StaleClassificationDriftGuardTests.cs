using System.Text.RegularExpressions;
using Shouldly;

namespace Squeezr.Tests.Storage;

public sealed partial class StaleClassificationDriftGuardTests
{
    private static readonly string[] CoreVerdictConsumers = ["WorkspaceListing.cs", "FleetListing.cs"];

    [Fact]
    public void ListingLayer_DoesNotProbeRegistryRootPathsItself()
    {
        var outputDir = Path.Combine(
            FindRepoRoot(), "src", "Squeezr.Cli", "Output");

        Directory.Exists(outputDir).ShouldBeTrue();
        var files = Directory.EnumerateFiles(outputDir, "*.cs").ToList();
        foreach (var expected in CoreVerdictConsumers)
        {
            files.ShouldContain(f => Path.GetFileName(f) == expected);
        }

        files.SelectMany(OffendingLines).ToList().ShouldBeEmpty();
    }

    [Fact]
    public void ListingLayer_ConsumesTheCoreScanVerdict()
    {
        var outputDir = Path.Combine(FindRepoRoot(), "src", "Squeezr.Cli", "Output");

        foreach (var file in CoreVerdictConsumers)
        {
            var source = File.ReadAllText(Path.Combine(outputDir, file));
            source.ShouldContain("OrphanScanner.Scan");
            source.ShouldContain(".Stale");
        }
    }

    [Fact]
    public void CoreOwnsTheStalenessProbe()
    {
        var scanner = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "Squeezr.Core", "Storage", "OrphanScanner.cs"));

        LocalStalenessProbe().IsMatch(scanner).ShouldBeTrue();
    }

    [Fact]
    public void GuardIsNotBlind_ToThePatternItReplaces()
    {
        LocalStalenessProbe().IsMatch("Stale: !Directory.Exists(entry.RootPath),").ShouldBeTrue();
        LocalStalenessProbe().IsMatch("if (!Directory.Exists(canonicalRoot))").ShouldBeFalse();
    }

    [Fact]
    public void ListingBanIsNotBlind_ToRenamedArgsAndOtherProbeShapes()
    {
        foreach (var probe in new[]
        {
            "private static bool IsGone(string path) => !Directory.Exists(path);",
            "Stale: File.GetAttributes(entry.RootPath) != FileAttributes.Directory,",
            "Stale: new DirectoryInfo(entry.RootPath).Exists,",
            "if (FileSystemInfo.CreateFromString(x).Exists)",
        })
        {
            AnyFileSystemProbe().IsMatch(probe).ShouldBeTrue(probe);
        }

        // a probe carried in from a variable is out of lexical reach: the guard would have to type-check to see it,
        // and a false-positive-prone ".Exists" sweep would also outlaw WorkspaceManifestFile.Exists
        AnyFileSystemProbe().IsMatch("Stale: alreadyResolved.Exists,").ShouldBeFalse();
    }

    private static IEnumerable<string> OffendingLines(string file)
    {
        var lines = File.ReadAllLines(file);
        for (var i = 0; i < lines.Length; i++)
        {
            var match = AnyFileSystemProbe().Match(lines[i]);
            if (match.Success)
            {
                yield return $"{Path.GetFileName(file)}:{i + 1}: '{match.Value}' in {lines[i].Trim()}";
            }
        }
    }

    // The usage surface lives in Core, where whole-directory sweeps would outlaw the registry, the
    // scanner and the telemetry writers. Core is covered per file, only where the probe is genuinely
    // gone — the ban keeps meaning "listing layers do not probe" without pretending Storage is clean.
    private static readonly (string Project, string Relative)[] BannedProbeFiles =
    [
        ("Squeezr.Cli", Path.Combine("Output", "WorkspaceListing.cs")),
        ("Squeezr.Cli", Path.Combine("Output", "FleetListing.cs")),
        ("Squeezr.Core", Path.Combine("Storage", "UsageAnalytics.cs")),
    ];

    public static TheoryData<string, string> ProbeBanFiles()
    {
        var data = new TheoryData<string, string>();
        foreach (var (project, relative) in BannedProbeFiles)
        {
            data.Add(project, relative);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ProbeBanFiles))]
    public void ProbeBannedSurface_ContainsNoFilesystemExistenceProbe(string project, string relative)
    {
        var path = Path.Combine(FindRepoRoot(), "src", project, relative);

        File.Exists(path).ShouldBeTrue($"{project}/{relative} is on the probe ban list but missing");
        OffendingLines(path).ShouldBeEmpty();
    }

    [Fact]
    public void UsageSurface_ConsumesTheCoreScanVerdict()
    {
        var loader = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "Squeezr.Core", "Storage", "UsageRollupLoader.cs"));

        loader.ShouldContain("OrphanScanner.Scan");
        loader.ShouldContain(".Stale");
    }

    [Fact]
    public void UsageBanIsNotBlind_ToTheProbeShapeItReplaced()
    {
        AnyFileSystemProbe().IsMatch("Stale: !Directory.Exists(workspace.RootPath),").ShouldBeTrue();
    }

    [Fact]
    public void ProbeBan_StaysPerFile_TheScannerKeepsItsOwnProbe()
    {
        var scanner = Path.Combine(FindRepoRoot(), "src", "Squeezr.Core", "Storage", "OrphanScanner.cs");

        BannedProbeFiles.ShouldNotContain(file => file.Relative.EndsWith("OrphanScanner.cs", StringComparison.Ordinal));
        OffendingLines(scanner).ShouldNotBeEmpty();
    }

    [GeneratedRegex(@"Directory\.Exists\([^)]*RootPath")]
    private static partial Regex LocalStalenessProbe();

    // any filesystem existence probe is a second source of truth in the listing layer, whatever it is called
    [GeneratedRegex(@"(Directory|File|FileSystemInfo)\.(Exists|GetAttributes|Create\w*)\(|new DirectoryInfo\(")]
    private static partial Regex AnyFileSystemProbe();

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
            "the stale-classification drift guard cannot run outside a checkout.");
    }
}
