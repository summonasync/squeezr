using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Storage;

[Collection("ProcessGlobals")]
public sealed partial class WorkspaceSiblingSnapshotTests : IDisposable
{
    private const string HomeEnvironmentVariable = "SQUEEZR_HOME";

    private readonly string? _previousEnv;
    private readonly string _home;
    private readonly List<string> _createdDirs = [];
    private readonly List<string> _createdFiles = [];

    public WorkspaceSiblingSnapshotTests()
    {
        _previousEnv = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        _home = NewTempDir();
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _home);
        SqueezrHome.Reset();
    }

    [Theory]
    [InlineData(2, 2, 0)]
    [InlineData(12, 12, 0)]
    [InlineData(13, 12, 1)]
    [InlineData(25, 12, 13)]
    public void Build_RegistryRows_ListUpToCapAndCountTheRestAsOmitted(int rows, int listed, int omitted)
    {
        var registry = Enumerable.Range(0, rows).Select(index => Row($"vis-row-{index}")).ToArray();
        WriteRegistry(registry);

        var snapshot = WorkspaceSiblingSnapshot.Build(null);

        snapshot.Workspaces.Select(row => row.StoreKey).ShouldBe(registry.Take(listed).Select(row => row.StoreKey));
        snapshot.Omitted.ShouldBe(omitted);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Build_RegistryBelowTwoRows_ReturnsTheEmptySnapshot(int rows)
    {
        WriteRegistry(Enumerable.Range(0, rows).Select(index => Row($"vis-solo-{index}")).ToArray());

        var snapshot = WorkspaceSiblingSnapshot.Build(null);

        snapshot.Workspaces.ShouldBeEmpty();
        snapshot.Omitted.ShouldBe(0);
    }

    [Fact]
    public void Build_MissingRoots_MarkStale_AndRowsCarryTheRegistryFields()
    {
        var live = Row("vis-live");
        var goneWithStore = Row("vis-gone-store", live: false);
        var goneBare = Row("vis-gone-bare", live: false);
        WriteRegistry(live, goneWithStore, goneBare);
        WriteStoreManifest(goneWithStore.StoreKey, goneWithStore.Name, goneWithStore.RootPath);

        var snapshot = WorkspaceSiblingSnapshot.Build(null);

        StaleOf(snapshot, live.StoreKey).ShouldBeFalse();
        StaleOf(snapshot, goneWithStore.StoreKey).ShouldBeTrue();
        StaleOf(snapshot, goneBare.StoreKey).ShouldBeTrue();

        var first = snapshot.Workspaces.Single(row => row.StoreKey == live.StoreKey);
        first.Name.ShouldBe(live.Name);
        first.RootPath.ShouldBe(live.RootPath);
        first.Current.ShouldBeFalse();
    }

    [Fact]
    public void Build_CurrentRoot_IsListedFirstAndStaysInsideTheCap()
    {
        var registry = Enumerable.Range(0, 13).Select(index => Row($"vis-ord-{index}")).ToArray();
        var current = registry[^1];
        WriteRegistry(registry);

        var snapshot = WorkspaceSiblingSnapshot.Build(current.RootPath);

        snapshot.Workspaces[0].StoreKey.ShouldBe(current.StoreKey);
        snapshot.Workspaces[0].Current.ShouldBeTrue();
        snapshot.Workspaces.Count(row => row.Current).ShouldBe(1);
        snapshot.Workspaces.Skip(1).Select(row => row.StoreKey).ShouldBe(registry[..11].Select(row => row.StoreKey));
        snapshot.Omitted.ShouldBe(1);
    }

    [Fact]
    public void Build_CurrentRootAbsentFromRegistry_MarksNoRowCurrent()
    {
        WriteRegistry(Row("vis-stray-live"), Row("vis-stray-gone", live: false));

        var snapshot = WorkspaceSiblingSnapshot.Build(NewTempDir());

        snapshot.Workspaces.ShouldAllBe(row => !row.Current);
        snapshot.Omitted.ShouldBe(0);
    }

    [Fact]
    public void Build_StoreDirsHoldingOrphanJunk_KeepTheResultWhileTheOrphanScanFlips()
    {
        var live = Row("vis-junk-live");
        var gone = Row("vis-junk-gone", live: false);
        WriteRegistry(live, gone);

        var before = WorkspaceSiblingSnapshot.Build(live.RootPath);
        StaleOf(before, gone.StoreKey).ShouldBeTrue();
        OrphanScanner.Scan().Orphans.ShouldBeEmpty();

        var strayRoot = Canonical(NewTempDir());
        WriteStoreManifest(WorkspaceStoreKeys.Create("vis-junk-stray", strayRoot), "vis-junk-stray", strayRoot);
        WriteStoreManifest("vis-junk-legacy", "vis-junk-legacy", null);
        WriteStoreManifest(gone.StoreKey, gone.Name, gone.RootPath);
        WriteRawStoreManifest("vis-junk-garbage", "{ not json");
        Directory.CreateDirectory(Path.Combine(SqueezrHome.WorkspacesDir, "vis-junk-bare-dir"));

        var after = WorkspaceSiblingSnapshot.Build(live.RootPath);

        Shape(after).ShouldBe(Shape(before));
        after.Omitted.ShouldBe(before.Omitted);

        var scan = OrphanScanner.Scan();
        scan.Orphans.Select(orphan => orphan.Name).ShouldContain("vis-junk-stray");
        scan.Orphans.Select(orphan => orphan.Name).ShouldContain("vis-junk-legacy");
    }

    [Fact]
    public void Build_StaleRows_MatchTheOrphanScannerVerdictOnTheSameRegistry()
    {
        var live = Row("vis-diff-live");
        var goneWithStore = Row("vis-diff-gone-store", live: false);
        var goneBare = Row("vis-diff-gone-bare", live: false);
        WriteRegistry(live, Row("vis-diff-second"), goneWithStore, goneBare);
        WriteStoreManifest(goneWithStore.StoreKey, goneWithStore.Name, goneWithStore.RootPath);

        StaleKeys(WorkspaceSiblingSnapshot.Build(live.RootPath))
            .ShouldBe(StaleKeysFromScanner());
    }

    [Theory]
    [InlineData("live-dir", false)]
    [InlineData("gone-dir", true)]
    [InlineData("regular-file", true)]
    public void Build_StaleRowOnEveryRootKind_MatchesTheScannerAndThePinnedVerdict(string rootKind, bool expectedStale)
    {
        var anchor = Row("vis-kind-anchor");
        var probed = RowForRootKind("vis-kind-probed", rootKind);
        WriteRegistry(anchor, probed);

        var snapshot = WorkspaceSiblingSnapshot.Build(null);

        StaleOf(snapshot, probed.StoreKey).ShouldBe(expectedStale);
        StaleKeys(snapshot).ShouldBe(StaleKeysFromScanner());
    }

    [Fact]
    public void StalenessPredicate_IsTheSameSourceFormAsTheOrphanScannerVerdict()
    {
        var snapshot = ReadCoreSource("WorkspaceSiblingSnapshot.cs");
        var scanner = ReadCoreSource("OrphanScanner.cs");

        StalenessProbes(snapshot).ShouldBe(StalenessProbes(scanner));
        StalenessProbes(snapshot).ShouldNotBeEmpty();
    }

    [Fact]
    public void ExistenceProbes_RunInsideTheCappedWindow()
    {
        ProbeBudgetViolations(ReadCoreSource("WorkspaceSiblingSnapshot.cs")).ShouldBeEmpty();
    }

    [Fact]
    public void ProbeBudgetGuardIsNotBlind_ToTheShapesItOutlaws()
    {
        ProbeBudgetViolations(ProbeInsideTheWindow).ShouldBeEmpty();
        ProbeBudgetViolations(ProbeHoistedIntoAKeySet).ShouldHaveSingleItem();
        ProbeBudgetViolations(ProbeMaterializedBeforeTheWindow).ShouldHaveSingleItem();
        ProbeBudgetViolations(AttributesHoistedOverAllRows).ShouldHaveSingleItem();
        ProbeBudgetViolations(WindowMissingAltogether).ShouldHaveSingleItem();

        // a verdict carried in from a variable is out of lexical reach, exactly like the drift guard admits:
        // this pin guards the probe's position, its values are guarded by the differential tests
        ProbeBudgetViolations("foreach (var entry in rows.Take(Cap)) { listed.Add(row with { Stale = alreadyResolved }); }")
            .ShouldBeEmpty();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _previousEnv);
        SqueezrHome.Reset();
        foreach (var dir in _createdDirs)
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        foreach (var file in _createdFiles)
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    private static bool StaleOf(WorkspaceSiblingSnapshot snapshot, string storeKey) =>
        snapshot.Workspaces.Single(row => row.StoreKey == storeKey).Stale;

    // record equality compares IReadOnlyList members by reference, so the no-scan proof needs a value projection
    private static List<(string Name, string RootPath, string StoreKey, bool Current, bool Stale)> Shape(
        WorkspaceSiblingSnapshot snapshot) =>
        [.. snapshot.Workspaces.Select(row => (row.Name, row.RootPath, row.StoreKey, row.Current, row.Stale))];

    private static IReadOnlyList<string> StaleKeys(WorkspaceSiblingSnapshot snapshot) =>
        [.. snapshot.Workspaces
            .Where(row => row.Stale)
            .Select(row => row.StoreKey)
            .OrderBy(key => key, StringComparer.Ordinal)];

    private static IReadOnlyList<string> StaleKeysFromScanner() =>
        [.. OrphanScanner.Scan().Stale
            .Select(stale => stale.StoreKey)
            .OrderBy(key => key, StringComparer.Ordinal)];

    private static List<string> StalenessProbes(string source)
    {
        var found = new List<string>();
        foreach (Match match in StalenessProbe().Matches(source))
        {
            found.Add(match.Value);
        }

        return found;
    }

    // the same shape the stale-classification drift guard pins in OrphanScanner: any rename of the predicate fails here too
    [GeneratedRegex(@"!Directory\.Exists\([A-Za-z_]\w*\.RootPath\)")]
    private static partial Regex StalenessProbe();

    // cap-bounded probes are the cost contract of this snapshot; hoisting the probe over the whole registry —
    // the shape WorkspaceListing uses today — keeps every per-row value, so no value-based test can see it
    private static List<string> ProbeBudgetViolations(string source)
    {
        var window = ProbeWindow().Match(source);
        if (!window.Success)
        {
            return ["the source has no Take(Cap) window, so existence probes run on every registry row"];
        }

        var offenders = new List<string>();
        foreach (Match probe in FileSystemProbes().Matches(source))
        {
            if (probe.Index < window.Index)
            {
                offenders.Add($"line {SourceLine(source, probe.Index)}: '{probe.Value}' runs before the Take(Cap) window");
            }
        }

        return offenders;
    }

    private static int SourceLine(string source, int index) => source[..index].Count(c => c == '\n') + 1;

    [GeneratedRegex(@"\.Take\(Cap\)")]
    private static partial Regex ProbeWindow();

    [GeneratedRegex(@"(Directory|File|FileSystemInfo)\.(Exists|GetAttributes|Create\w*)\(|new DirectoryInfo\(")]
    private static partial Regex FileSystemProbes();

    private const string ProbeInsideTheWindow = """
        var listed = new List<WorkspaceSiblingEntry>(Cap);
        foreach (var entry in OrderByCurrent(entries, currentStoreKey).Take(Cap))
        {
            listed.Add(new WorkspaceSiblingEntry(entry.Name, entry.RootPath, entry.StoreKey,
                Stale: !Directory.Exists(entry.RootPath)));
        }
        """;

    private const string ProbeHoistedIntoAKeySet = """
        var goneKeys = entries.Where(entry => !Directory.Exists(entry.RootPath))
            .Select(entry => entry.StoreKey).ToHashSet();
        foreach (var entry in OrderByCurrent(entries, currentStoreKey).Take(Cap))
        {
            listed.Add(new WorkspaceSiblingEntry(entry.Name, entry.RootPath, entry.StoreKey,
                Stale: goneKeys.Contains(entry.StoreKey)));
        }
        """;

    private const string ProbeMaterializedBeforeTheWindow = """
        foreach (var entry in OrderByCurrent(entries, currentStoreKey)
            .Select(e => (e, S: !Directory.Exists(e.RootPath))).ToList().Select(pair => pair.e).Take(Cap))
        {
            listed.Add(pair.e);
        }
        """;

    private const string AttributesHoistedOverAllRows = """
        var goneRoots = entries.Select(entry => entry.RootPath)
            .Where(root => File.GetAttributes(root) != FileAttributes.Directory).ToHashSet();
        foreach (var entry in OrderByCurrent(entries, currentStoreKey).Take(Cap))
        {
            listed.Add(new WorkspaceSiblingEntry(entry.Name, entry.RootPath, entry.StoreKey, Stale: false));
        }
        """;

    private const string WindowMissingAltogether = """
        foreach (var entry in OrderByCurrent(entries, currentStoreKey))
        {
            listed.Add(new WorkspaceSiblingEntry(entry.Name, entry.RootPath, entry.StoreKey,
                Stale: !Directory.Exists(entry.RootPath)));
        }
        """;

    private static string ReadCoreSource(string fileName)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Squeezr.slnx")))
            {
                return File.ReadAllText(Path.Combine(dir.FullName, "src", "Squeezr.Core", "Storage", fileName));
            }
        }

        throw new InvalidOperationException($"Repository root (Squeezr.slnx) not found above {AppContext.BaseDirectory}.");
    }

    private WorkspaceRegistryEntry Row(string name, bool live = true)
    {
        var root = Canonical(NewTempDir());
        if (!live)
        {
            Directory.Delete(root);
        }

        return Entry(name, root);
    }

    private WorkspaceRegistryEntry RowForRootKind(string name, string rootKind) => rootKind switch
    {
        "gone-dir" => Row(name, live: false),
        "regular-file" => Entry(name, RegularFilePath()),
        _ => Row(name),
    };

    private string RegularFilePath()
    {
        var root = Canonical(NewTempDir());
        Directory.Delete(root);
        File.WriteAllText(root, "a regular file standing in for a workspace root");
        _createdFiles.Add(root);
        return root;
    }

    private static WorkspaceRegistryEntry Entry(string name, string root)
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkspaceRegistryEntry(name, root, WorkspaceStoreKeys.Create(name, root), now, now);
    }

    private void WriteRegistry(params WorkspaceRegistryEntry[] entries) =>
        File.WriteAllText(
            WorkspaceRegistry.FilePath,
            JsonSerializer.Serialize(entries, SqueezrJson.FileOptions));

    private void WriteStoreManifest(string storeKey, string name, string? canonicalRoot) =>
        WriteRawStoreManifest(
            storeKey,
            JsonSerializer.Serialize(new WorkspaceManifest { Name = name, RootPath = canonicalRoot }, SqueezrJson.FileOptions));

    private void WriteRawStoreManifest(string storeKey, string manifestBody)
    {
        var storeDir = Path.Combine(SqueezrHome.WorkspacesDir, storeKey);
        Directory.CreateDirectory(storeDir);
        File.WriteAllText(Path.Combine(storeDir, SqueezrPaths.StoreManifestFileName), manifestBody);
    }

    private string Canonical(string path) => WorkspaceCanonicalPath.Canonicalize(path);

    private string NewTempDir()
    {
        var dir = TestDirs.NewTempDir();
        _createdDirs.Add(dir);
        return dir;
    }
}
