using System.Text.Json;
using Shouldly;
using Squeezr.Cli.Output;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Cli;

[Collection("CliProcessGlobals")]
public sealed class StatusWorkspacesTests : IDisposable
{
    private const string HomeEnvironmentVariable = "SQUEEZR_HOME";
    private const string CurrentName = "vis-status-current";
    private const string LiveSiblingName = "vis-status-live";
    private const string GoneSiblingName = "vis-status-gone";

    // Cost-shape pin over the two T4 surfaces: the read path stays at the snapshot's registry +
    // capped stat calls, so neither file may reach around it the way index_status's guard outlaws.
    // Lexical like the MCP pin — a helper introduced under a fresh name still passes; the value
    // tests above hold the payload side.
    private static readonly string[] ForbiddenInT4Sources =
    [
        "OrphanScanner", "WorkspaceListing", "FleetListing", "RegistryRepair", "WorkspaceRegistry.Load",
        "Directory.Exists", "Directory.EnumerateDirectories", "SqueezrPaths.StoreDir"
    ];

    private static readonly string[] DeclaredEntryKeys =
        ["current", "name", "rootPath", "stale", "storeKey"];

    private readonly string? _previousEnv;
    private readonly string _home;
    private readonly string _cwd;
    private readonly string _liveRoot;
    private readonly string _goneRoot;
    private readonly List<string> _createdDirs = [];

    public StatusWorkspacesTests()
    {
        _previousEnv = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        _home = TestDirs.NewTempDir();
        _cwd = TestDirs.NewTempDir();
        _liveRoot = TestDirs.NewTempDir();
        _goneRoot = TestDirs.NewTempDir();
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _home);
        SqueezrHome.Reset();

        CliTestProcess.RunWithHomeAsync(_home, _cwd, "init", "--name", CurrentName, "--json")
            .GetAwaiter().GetResult().ExitCode.ShouldBe(0);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(_cwd);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _previousEnv);
        SqueezrHome.Reset();
        foreach (var dir in _createdDirs.Append(_home).Append(_cwd).Append(_liveRoot).Append(_goneRoot))
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Status_Json_SingleWorkspaceRegistry_OmitsTheWorkspacesBlock()
    {
        var status = await RunStatusAsync("--json");

        status.ExitCode.ShouldBe(0);
        using var doc = JsonDocument.Parse(status.StdOut.Trim());
        doc.RootElement.TryGetProperty("workspaces", out _).ShouldBeFalse();
        doc.RootElement.TryGetProperty("workspacesOmitted", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Status_Json_MultiWorkspaceRegistry_ListsSiblingsIdenticalToTheSnapshot()
    {
        WriteThreeRowRegistry();
        Directory.Delete(_goneRoot, recursive: true);

        var status = await RunStatusAsync("--json");

        status.ExitCode.ShouldBe(0);
        using var doc = JsonDocument.Parse(status.StdOut.Trim());
        var rows = doc.RootElement.GetProperty("workspaces").EnumerateArray().ToList();
        doc.RootElement.TryGetProperty("workspacesOmitted", out _).ShouldBeFalse();

        rows.Count.ShouldBe(3);
        ProjectionOf(rows).ShouldBe(ProjectionOf(WorkspaceSiblingSnapshot.Build(_cwd).Workspaces));

        var current = rows.Single(row => row.GetProperty("current").GetBoolean());
        current.GetProperty("name").GetString().ShouldBe(CurrentName);
        current.GetProperty("stale").GetBoolean().ShouldBeFalse();
        rows.Single(row => row.GetProperty("name").GetString() == GoneSiblingName)
            .GetProperty("stale").GetBoolean().ShouldBeTrue();
        rows.Single(row => row.GetProperty("name").GetString() == LiveSiblingName)
            .GetProperty("stale").GetBoolean().ShouldBeFalse();
        foreach (var row in rows)
        {
            KeyNames(row).ShouldBe(DeclaredEntryKeys);
        }
    }

    [Fact]
    public async Task Status_Pretty_MultiWorkspaceRegistry_RendersTableWithCurrentAndStaleMarks()
    {
        WriteThreeRowRegistry();
        Directory.Delete(_goneRoot, recursive: true);

        var status = await RunStatusAsync();

        status.ExitCode.ShouldBe(0);
        status.StdOut.ShouldContain("Workspaces");
        status.StdOut.ShouldContain(LiveSiblingName);
        status.StdOut.ShouldContain(GoneSiblingName);
        status.StdOut.ShouldContain("current");
        status.StdOut.ShouldContain("stale");
    }

    [Fact]
    public async Task Status_Pretty_SingleWorkspaceRegistry_ShowsNoWorkspacesTable()
    {
        var status = await RunStatusAsync();

        status.ExitCode.ShouldBe(0);
        status.StdOut.ShouldNotContain("Store key");
    }

    [Fact]
    public async Task Status_Json_RegistryBeyondCap_ListsTwelveRowsAndCountsTheOmitted()
    {
        WriteRegistryBeyondCap();

        var status = await RunStatusAsync("--json");

        status.ExitCode.ShouldBe(0);
        using var doc = JsonDocument.Parse(status.StdOut.Trim());
        var rows = doc.RootElement.GetProperty("workspaces").EnumerateArray().ToList();
        rows.Count.ShouldBe(WorkspaceSiblingSnapshot.Cap);
        doc.RootElement.GetProperty("workspacesOmitted").GetInt32().ShouldBe(3);
        rows.Single(row => row.GetProperty("current").GetBoolean())
            .GetProperty("name").GetString().ShouldBe(CurrentName);

        var human = await RunStatusAsync();
        human.StdOut.ShouldContain("3 more workspace(s) registered");
    }

    [Fact]
    public void StatusReport_BlockFollowsTheSnapshot_EmptyStaysAbsent()
    {
        var status = new WorkspaceManager(_cwd).GetStatus();

        var empty = WorkspaceStatusReport.Create(status, [], null, WorkspaceSiblingSnapshot.Empty);
        JsonSerializer.Serialize(empty, SqueezrJson.OutputOptions)
            .ShouldNotContain("workspaces");

        var listed = new WorkspaceSiblingSnapshot(
            [new WorkspaceSiblingEntry("vis-far", "/gone/vis-far", "vis-far-deadbeef", false, true)], 5);
        var report = WorkspaceStatusReport.Create(status, [], null, listed);
        report.Workspaces.ShouldHaveSingleItem().StoreKey.ShouldBe("vis-far-deadbeef");
        report.WorkspacesOmitted.ShouldBe(5);
        var bytes = JsonSerializer.Serialize(report, SqueezrJson.OutputOptions);
        bytes.ShouldContain("\"workspaces\":");
        bytes.ShouldContain("\"workspacesOmitted\":5");
    }

    [Fact]
    public void StatusSurfaces_WorkspaceRows_ComeFromTheSnapshotAlone()
    {
        foreach (var relative in new[]
                 {
                     Path.Combine("src", "Squeezr.Cli", "Commands", "StatusCommand.cs"),
                     Path.Combine("src", "Squeezr.Cli", "Output", "WorkspaceStatusReport.cs")
                 })
        {
            var source = File.ReadAllText(Path.Combine(RepoRoot(), relative));
            ForbiddenInT4Sources.Where(source.Contains).ShouldBeEmpty();
        }
    }

    private void WriteThreeRowRegistry()
    {
        WriteRegistry(
            CurrentRow(),
            Row(LiveSiblingName, WorkspaceCanonicalPath.Canonicalize(_liveRoot)),
            Row(GoneSiblingName, WorkspaceCanonicalPath.Canonicalize(_goneRoot)));
    }

    private void WriteRegistryBeyondCap()
    {
        var rows = new List<WorkspaceRegistryEntry> { CurrentRow() };
        for (var index = 0; index < 14; index++)
        {
            var root = TestDirs.NewTempDir();
            _createdDirs.Add(root);
            rows.Add(Row($"vis-status-row-{index}", WorkspaceCanonicalPath.Canonicalize(root)));
        }

        WriteRegistry([.. rows]);
    }

    private WorkspaceRegistryEntry CurrentRow() =>
        Row(CurrentName, WorkspaceCanonicalPath.Canonicalize(_cwd));

    private static WorkspaceRegistryEntry Row(string name, string canonicalRoot)
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkspaceRegistryEntry(
            name, canonicalRoot, WorkspaceStoreKeys.Create(name, canonicalRoot), now, now);
    }

    private static void WriteRegistry(params WorkspaceRegistryEntry[] entries) =>
        File.WriteAllText(
            WorkspaceRegistry.FilePath,
            JsonSerializer.Serialize(entries, SqueezrJson.FileOptions));

    private async Task<(int ExitCode, string StdOut, string StdErr)> RunStatusAsync(params string[] args) =>
        await CliTestProcess.RunWithHomeAsync(_home, _cwd, ["status", .. args]);

    private static List<(string Name, string RootPath, string StoreKey, bool Current, bool Stale)> ProjectionOf(
        IEnumerable<WorkspaceSiblingEntry> entries) =>
        entries.Select(entry => (entry.Name, entry.RootPath, entry.StoreKey, entry.Current, entry.Stale)).ToList();

    private static List<(string Name, string RootPath, string StoreKey, bool Current, bool Stale)> ProjectionOf(
        IEnumerable<JsonElement> rows) =>
        rows.Select(row => (row.GetProperty("name").GetString()!, row.GetProperty("rootPath").GetString()!,
            row.GetProperty("storeKey").GetString()!, row.GetProperty("current").GetBoolean(),
            row.GetProperty("stale").GetBoolean())).ToList();

    private static List<string> KeyNames(JsonElement row) =>
        row.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Squeezr.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"Repository root (Squeezr.slnx) not found above {AppContext.BaseDirectory}.");
    }
}
