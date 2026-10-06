using System.Text.Json;
using ModelContextProtocol.Protocol;
using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Mcp;

[Collection("CliProcessGlobals")]
public sealed class IndexStatusWorkspacesTests : IDisposable
{
    private const string HomeEnvironmentVariable = "SQUEEZR_HOME";
    private const string WorkspaceName = "vis-payload";
    private const string RepoName = "vis-repo";
    private const string RootToken = "<ROOT>";

    // Captured from the pre-065/T2 build (7c5b0b8) over this exact fixture with the temp workspace
    // root replaced by <ROOT>: R3 promises a one-workspace payload stays byte-for-byte today's output.
    private const string SingleWorkspaceDataBytes = """
        {"workspace":"vis-payload","rootPath":"<ROOT>","repos":[{"name":"vis-repo","rootPath":"<ROOT>/repo","files":1,"symbols":3,"chunks":3}],"embeddings":{"pending":3,"coverage":0},"qdrant":{"available":false}}
        """;

    private static readonly string[] BaselineDataKeys =
        ["embeddings", "qdrant", "repos", "rootPath", "workspace"];

    private static readonly string[] DeclaredEntryKeys =
        ["current", "name", "rootPath", "stale", "storeKey"];

    // Cost-shape pins: spec R2 keeps this read path at registry + capped stat calls, so the tool may
    // neither re-probe roots nor open store dirs itself — both would bypass the snapshot's cap. The three
    // listing helpers are pinned as well because they delegate to the whole store-walk while keeping every
    // payload value: no value-based test can see that cost, only the source can. A helper introduced later
    // under a name nobody listed here still passes — the lexical limit of this guard, same as T1's.
    private static readonly string[] ForbiddenInToolSource =
        ["OrphanScanner", "WorkspaceListing", "FleetListing", "RegistryRepair", "WorkspaceRegistry.Load",
         "Directory.Exists", "Directory.EnumerateDirectories", "SqueezrPaths.StoreDir"];

    private readonly string? _previousHome;
    private readonly string _home;

    public IndexStatusWorkspacesTests()
    {
        _previousHome = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        _home = TestDirs.NewTempDir();
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _home);
        SqueezrHome.Reset();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _previousHome);
        SqueezrHome.Reset();
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, recursive: true);
        }
    }

    [Fact]
    public async Task IndexStatus_SingleWorkspaceRegistry_DataOmitsTheWorkspacesBlock()
    {
        var workspace = NewIndexedWorkspace();
        RegistryRows().ShouldBe(1);

        var data = (await CallIndexStatusAsync(workspace)).GetProperty("data");

        data.TryGetProperty("workspaces", out _).ShouldBeFalse();
        data.TryGetProperty("workspacesOmitted", out _).ShouldBeFalse();
        KeyNames(data).ShouldBe(BaselineDataKeys);
    }

    [Fact]
    public async Task IndexStatus_SingleWorkspaceRegistry_DataBytesEqualThePinnedBaseline()
    {
        var workspace = NewIndexedWorkspace();

        var data = (await CallIndexStatusAsync(workspace)).GetProperty("data");

        NormalizeRoots(data.GetRawText()!, workspace).ShouldBe(SingleWorkspaceDataBytes);
    }

    [Fact]
    public async Task IndexStatus_ThreeWorkspaceRegistry_DataListsSiblingsAndMarksCurrent()
    {
        var workspace = NewIndexedWorkspace();
        var live = AddRegistryRow("vis-live-sibling");
        var gone = AddRegistryRow("vis-gone-sibling", live: false);

        var data = (await CallIndexStatusAsync(workspace)).GetProperty("data");

        data.TryGetProperty("workspacesOmitted", out _).ShouldBeFalse();
        var rows = data.GetProperty("workspaces").EnumerateArray().ToList();
        rows.Select(row => row.GetProperty("name").GetString()).ToList()
            .ShouldBe([WorkspaceName, "vis-live-sibling", "vis-gone-sibling"]);

        var current = CurrentOf(rows);
        current.GetProperty("rootPath").GetString().ShouldBe(Canonical(workspace));
        rows.Count(row => row.GetProperty("current").GetBoolean()).ShouldBe(1);
        RowBy(rows, "vis-live-sibling").GetProperty("stale").GetBoolean().ShouldBeFalse();
        RowBy(rows, "vis-gone-sibling").GetProperty("stale").GetBoolean().ShouldBeTrue();
        RowBy(rows, "vis-live-sibling").GetProperty("rootPath").GetString().ShouldBe(live.RootPath);
        RowBy(rows, "vis-gone-sibling").GetProperty("storeKey").GetString().ShouldBe(gone.StoreKey);
        foreach (var row in rows)
        {
            KeyNames(row).ShouldBe(DeclaredEntryKeys);
        }
    }

    [Fact]
    public async Task IndexStatus_RegistryBeyondCap_DataListIsCappedAndOmittedIsCounted()
    {
        var workspace = NewIndexedWorkspace();
        for (var index = 0; index < 14; index++)
        {
            AddRegistryRow($"vis-row-{index}");
        }

        RegistryRows().ShouldBe(15);

        var data = (await CallIndexStatusAsync(workspace)).GetProperty("data");

        data.GetProperty("workspaces").EnumerateArray().Count().ShouldBe(WorkspaceSiblingSnapshot.Cap);
        data.GetProperty("workspacesOmitted").GetInt32().ShouldBe(3);
        CurrentOf(data.GetProperty("workspaces").EnumerateArray().ToList())
            .GetProperty("name").GetString().ShouldBe(WorkspaceName);
    }

    [Fact]
    public async Task IndexStatus_WorkspacesBlock_IsTheSnapshotProjectionRowForRow()
    {
        var workspace = NewIndexedWorkspace();
        AddRegistryRow("vis-live-sibling");
        AddRegistryRow("vis-gone-sibling", live: false);
        AddRegistryRow("vis-file-root", rootKind: "regular-file");

        var rows = (await CallIndexStatusAsync(workspace)).GetProperty("data")
            .GetProperty("workspaces").EnumerateArray().ToList();
        var snapshot = WorkspaceSiblingSnapshot.Build(workspace);

        ProjectionOf(rows).ShouldBe(snapshot.Workspaces
            .Select(row => (row.Name, row.RootPath, row.StoreKey, row.Current, row.Stale)).ToList());
    }

    [Fact]
    public async Task IndexStatus_StaleSiblingWithStoreDirJunk_KeepsTheBlockByteForByte()
    {
        var workspace = NewIndexedWorkspace();
        var gone = AddRegistryRow("vis-gone-with-store", live: false);

        var before = (await CallIndexStatusAsync(workspace)).GetProperty("data");
        var beforeBytes = NormalizeRoots(before.GetRawText()!, workspace);

        PlantStoreDirJunk(gone.StoreKey, gone.RootPath);

        var after = (await CallIndexStatusAsync(workspace)).GetProperty("data");
        NormalizeRoots(after.GetRawText()!, workspace).ShouldBe(beforeBytes);

        var rows = after.GetProperty("workspaces").EnumerateArray().ToList();
        RowBy(rows, "vis-gone-with-store").GetProperty("stale").GetBoolean().ShouldBeTrue();
        foreach (var row in rows)
        {
            KeyNames(row).ShouldBe(DeclaredEntryKeys);
        }
    }

    [Fact]
    public void IndexStatusTool_SiblingRows_ComeFromTheSnapshotAlone()
    {
        var source = ReadToolSource();

        CostViolations(source).ShouldBeEmpty();
        source.ShouldContain("WorkspaceSiblingSnapshot.Build");
    }

    [Fact]
    public void IndexStatusTool_CostGuardIsNotBlind_ToTheShapesItOutlaws()
    {
        // MUT R1 of review-065-02: the delegating call carries no value change, so this shape is the
        // whole reason WorkspaceListing is on the list — it has to redden the pin above, not just look bad.
        CostViolations(RowsFromTheSnapshot).ShouldBeEmpty();
        CostViolations(ListingLoadBehindTheRows).ShouldHaveSingleItem();
        CostViolations(FleetAndRepairBehindTheRows).ShouldBe(["FleetListing", "RegistryRepair"]);
        CostViolations(ScannerCallWithRootReProbe).ShouldBe(["OrphanScanner", "Directory.Exists"]);
        CostViolations(StoreDirsWalkedInsideTheTool)
            .ShouldBe(["Directory.EnumerateDirectories", "SqueezrPaths.StoreDir"]);
        CostViolations(RowsReadStraightFromTheRegistry).ShouldHaveSingleItem();
    }

    private static List<string> CostViolations(string source) =>
        [.. ForbiddenInToolSource.Where(source.Contains)];

    private const string RowsFromTheSnapshot = """
        var siblings = WorkspaceSiblingSnapshot.Build(root);
        var rows = BuildWorkspaceRows(siblings);
        """;

    private const string ListingLoadBehindTheRows = """
        var listing = WorkspaceListing.Load();
        var siblings = WorkspaceSiblingSnapshot.Build(root);
        var rows = BuildWorkspaceRows(listing.Orphans.Count >= 0 ? siblings : WorkspaceSiblingSnapshot.Empty);
        """;

    private const string FleetAndRepairBehindTheRows = """
        var fleet = FleetListing.Load();
        var adopted = RegistryRepair.AdoptAll();
        var siblings = WorkspaceSiblingSnapshot.Build(root);
        var rows = BuildWorkspaceRows(siblings);
        """;

    private const string ScannerCallWithRootReProbe = """
        var scan = OrphanScanner.Scan();
        var siblings = WorkspaceSiblingSnapshot.Build(root);
        var rows = BuildWorkspaceRows(siblings)
            ?.Where(row => !Directory.Exists(row.RootPath) || scan.Orphans.Count > 0).ToList();
        """;

    private const string StoreDirsWalkedInsideTheTool = """
        var siblings = WorkspaceSiblingSnapshot.Build(root);
        var junk = Directory.EnumerateDirectories(SqueezrHome.WorkspacesDir).ToList();
        var manifest = Path.Combine(SqueezrPaths.StoreDir(root), SqueezrPaths.StoreManifestFileName);
        """;

    private const string RowsReadStraightFromTheRegistry = """
        var rows = WorkspaceRegistry.Load()
            .Select(entry => new WorkspaceData(entry.Name, entry.RootPath, entry.StoreKey, false, false))
            .ToList();
        """;

    private async Task<JsonElement> CallIndexStatusAsync(string workspace)
    {
        await using var harness = await McpTestHarness.StartAsync(workspace);
        var result = await harness.Client.CallToolAsync(
            "index_status", new Dictionary<string, object?>(), cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        return ParseEnvelope(result);
    }

    private static JsonElement ParseEnvelope(CallToolResult result)
    {
        var json = result.Content.ShouldHaveSingleItem().ToString()!;
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private WorkspaceRegistryEntry AddRegistryRow(string name, bool live = true, string rootKind = "directory")
    {
        var root = Canonical(TestDirs.NewTempDir());
        if (rootKind == "regular-file")
        {
            Directory.Delete(root);
            File.WriteAllText(root, "a regular file standing in for a workspace root");
        }
        else if (!live)
        {
            Directory.Delete(root);
        }

        var now = DateTimeOffset.UtcNow;
        var entry = new WorkspaceRegistryEntry(name, root, WorkspaceStoreKeys.Create(name, root), now, now);
        WorkspaceRegistry.Add(entry);
        return entry;
    }

    private void PlantStoreDirJunk(string storeKey, string canonicalRoot)
    {
        var storeDir = Path.Combine(SqueezrHome.WorkspacesDir, storeKey);
        Directory.CreateDirectory(storeDir);
        File.WriteAllText(
            Path.Combine(storeDir, SqueezrPaths.StoreManifestFileName),
            JsonSerializer.Serialize(new WorkspaceManifest { Name = "vis-junk", RootPath = canonicalRoot },
                SqueezrJson.FileOptions));
        File.WriteAllText(Path.Combine(storeDir, "adoptable-orphan.json"), "{}");
        File.WriteAllText(Path.Combine(storeDir, "meta.db"), "junk that an orphan scan would open");
    }

    private string NewIndexedWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init(WorkspaceName);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        manager.AddRepo("repo", RepoName);
        File.WriteAllText(Path.Combine(workspace, "repo", "Alpha.cs"), """
            namespace Demo;

            public class Alpha
            {
                public string Term = "visneedle";

                public int Add(int a, int b)
                {
                    return a + b;
                }
            }
            """);
        new WorkspaceIndexer().Index(workspace);
        return workspace;
    }

    private static int RegistryRows() => WorkspaceRegistry.Load().Count;

    private static string Canonical(string path) => WorkspaceCanonicalPath.Canonicalize(path);

    private static List<string> KeyNames(JsonElement row) =>
        row.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();

    private static List<(string Name, string RootPath, string StoreKey, bool Current, bool Stale)> ProjectionOf(
        IEnumerable<JsonElement> rows) =>
        rows.Select(row => (row.GetProperty("name").GetString()!, row.GetProperty("rootPath").GetString()!,
            row.GetProperty("storeKey").GetString()!, row.GetProperty("current").GetBoolean(),
            row.GetProperty("stale").GetBoolean())).ToList();

    private static JsonElement CurrentOf(IReadOnlyList<JsonElement> rows) =>
        rows.Single(row => row.GetProperty("current").GetBoolean());

    private static JsonElement RowBy(IReadOnlyList<JsonElement> rows, string name) =>
        rows.Single(row => row.GetProperty("name").GetString() == name);

    private static string NormalizeRoots(string json, string root) =>
        json.Replace(Canonical(root), RootToken).Replace(root, RootToken);

    private static string ReadToolSource()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Squeezr.slnx")))
            {
                return File.ReadAllText(Path.Combine(dir.FullName,
                    "src", "Squeezr.Cli", "Mcp", "Tools", "IndexStatusTool.cs"));
            }
        }

        throw new InvalidOperationException($"Repository root (Squeezr.slnx) not found above {AppContext.BaseDirectory}.");
    }
}
