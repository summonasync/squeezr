using System.Text.Json;
using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Semantic.Configuration;

namespace Squeezr.Tests.Cli;

// The reaper's endpoint gate as a child process answers for it: every home here is a fresh pinned directory,
// every minted config names a dead port, and no test in this file can reach a Qdrant this machine runs. That
// is what makes the split honest — the classification a real repair run performs (which endpoint is mine,
// which is another home's, which never answered) is measured here through the wire, and the deletion refusal
// itself is measured against a scripted endpoint in WorkspaceCollectionSweepTests, where a delete that
// happened would be a recorded request instead of a raid on a live store.
[Collection("ProcessGlobals")]
public sealed class WorkspaceRepairCollectionsCliTests : IDisposable
{
    private const string OwnedDeadEndpoint = "http://localhost:1";
    private const string ForeignDeadEndpoint = "http://localhost:2";

    private readonly string _home;
    private readonly List<string> _createdDirs = [];

    public WorkspaceRepairCollectionsCliTests()
    {
        _home = Tracked(TestDirs.NewTempDir());
        Directory.CreateDirectory(_home);
        File.WriteAllText(RegistryPath, "[]");
    }

    public void Dispose()
    {
        foreach (var dir in _createdDirs)
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Apply_OwnedEndpointThatNeverAnswers_FailsLoudWithItsOwnMeasuredError()
    {
        MintRow("mine-owned", OwnedDeadEndpoint, owned: true);

        var applied = await RunRepair("--yes", "--apply", "--json");

        applied.ExitCode.ShouldBe(0);
        using var document = JsonDocument.Parse(applied.StdOut.Trim());
        var collections = document.RootElement.GetProperty("collections");
        collections.GetProperty("apply").GetBoolean().ShouldBeTrue();
        collections.GetProperty("endpoints").EnumerateArray().Select(value => value.GetString())
            .ShouldBe([OwnedDeadEndpoint]);
        collections.GetProperty("entries").GetArrayLength().ShouldBe(0);
        collections.GetProperty("seenMinted").GetInt32().ShouldBe(0,
            "a port with no listener is not an empty collection set, and the ledger does not claim one");
        collections.GetProperty("unreachableEndpoints").EnumerateArray().Select(value => value.GetString())
            .ShouldBe([OwnedDeadEndpoint]);
        collections.GetProperty("notOwnedEndpoints").GetArrayLength().ShouldBe(0,
            "the sweep owns this endpoint, so the refusal it hit is reachability, not ownership");
        var fault = collections.GetProperty("endpointFaults").EnumerateArray().Single();
        fault.GetProperty("endpoint").GetString().ShouldBe(OwnedDeadEndpoint);
        var error = fault.GetProperty("error").GetString()!;
        error.StartsWith("HttpRequestException", StringComparison.Ordinal).ShouldBeTrue(
            "the report carries the error this run's own request caught, never a rerouted default");
        error.Length.ShouldBeGreaterThan("HttpRequestException".Length,
            "a bare exception name with nothing measured behind it would read as a classification, not a fault");
        applied.StdErr.ShouldContain("could not reach", Case.Sensitive);
        applied.StdErr.ShouldContain("HttpRequestException", Case.Sensitive,
            "a --json reader gets the measured fault on the warning channel too, so a dead port and a clean " +
            "endpoint cannot be read as the same answer");
    }

    [Fact]
    public async Task Apply_NonOwnedEndpoint_IsNamedAsNotOwned_AndTheDoorIsNotConsent()
    {
        MintRow("theirs-foreign", ForeignDeadEndpoint, owned: false);

        var applied = await RunRepair("--yes", "--apply", "--json");

        applied.ExitCode.ShouldBe(0);
        using var document = JsonDocument.Parse(applied.StdOut.Trim());
        var collections = document.RootElement.GetProperty("collections");
        collections.GetProperty("apply").GetBoolean().ShouldBeTrue("--apply is in hand");
        collections.GetProperty("notOwnedEndpoints").EnumerateArray().Select(value => value.GetString())
            .ShouldBe([ForeignDeadEndpoint],
                "the config that named this endpoint did not declare it its own, and that is a counted " +
                "machine-readable refusal, not a silent zero");
        collections.GetProperty("entries").GetArrayLength().ShouldBe(0);
        applied.StdErr.ShouldContain("deleted nothing on 1 non-owned endpoint(s)", Case.Sensitive,
            "a run that held --apply and deleted nothing says why on the channel warnings travel on");
        applied.StdErr.ShouldContain("--apply is not consent", Case.Sensitive);
    }

    [Fact]
    public async Task Apply_TwoProfilesTwoEndpoints_EachOneIsJudgedFromItsOwnConfig()
    {
        MintRow("mine-owned", OwnedDeadEndpoint, owned: true);
        MintRow("theirs-foreign", ForeignDeadEndpoint, owned: false);

        var applied = await RunRepair("--yes", "--apply", "--json");

        applied.ExitCode.ShouldBe(0);
        using var document = JsonDocument.Parse(applied.StdOut.Trim());
        var collections = document.RootElement.GetProperty("collections");
        collections.GetProperty("endpoints").EnumerateArray().Select(value => value.GetString())
            .ShouldBe([OwnedDeadEndpoint, ForeignDeadEndpoint],
                "two workspaces contributed two endpoints, and neither row's config answered for the other");
        collections.GetProperty("notOwnedEndpoints").EnumerateArray().Select(value => value.GetString())
            .ShouldBe([ForeignDeadEndpoint],
                "the owned half did not put its flag on the sibling, and the non-owned half did not strip it " +
                "from the owner — the classification is per workspace, resolved from that workspace's own file");
        collections.GetProperty("unreachableEndpoints").EnumerateArray().Select(value => value.GetString())
            .ShouldBe([OwnedDeadEndpoint, ForeignDeadEndpoint]);
        collections.GetProperty("endpointFaults").EnumerateArray().Select(value => value.GetProperty("endpoint").GetString())
            .ShouldBe([OwnedDeadEndpoint, ForeignDeadEndpoint],
                "each dead port carries its own fault; one dead endpoint is never reported as the other's");
    }

    [Fact]
    public void RepairCommand_CollectionDoorPassesTheOwnedTargets()
    {
        var source = File.ReadAllText(RepoFile("src/Squeezr.Cli/Commands/WorkspaceRepairCommand.cs"));

        source.ShouldContain("WorkspaceCollectionSweep.SweepTargetsOf(rows)", Case.Sensitive,
            "the destructive arm is handed the endpoints with their ownership read off the configs that named " +
            "them, never a bare address list that has already forgotten who owns it");
    }

    private Task<(int ExitCode, string StdOut, string StdErr)> RunRepair(params string[] flags) =>
        CliTestProcess.RunWithHomeAsync(_home, TestDirs.NewTempDir(), ["workspace", "repair", .. flags]);

    // A row with a live root and its own store dir: nothing here is stale or husk-shaped, so the row sweep
    // confirms with nothing to prune and --apply still opens the collection door. The config is written where
    // the child will read it — under this home, by store key — because the test process resolves paths itself
    // and would otherwise drop a config.json next to an unrelated registry lookup.
    private void MintRow(string name, string endpoint, bool owned)
    {
        var root = Tracked(TestDirs.NewTempDir());
        Directory.CreateDirectory(root);
        var registeredRoot = WorkspaceCanonicalPath.Canonicalize(root);
        var storeKey = WorkspaceStoreKeys.Create(name, registeredRoot);
        var storeDir = Path.Combine(_home, SqueezrPaths.WorkspacesDirName, storeKey);
        Directory.CreateDirectory(storeDir);
        File.WriteAllText(Path.Combine(storeDir, SqueezrPaths.MetaDbFileName), "x");
        File.WriteAllText(
            Path.Combine(storeDir, SqueezrPaths.ConfigFileName),
            JsonSerializer.Serialize(
                SemanticConfig.Bind(SqueezrConfig.CreateDefault()) with
                {
                    Qdrant = new QdrantConfig { Endpoint = endpoint, Owned = owned }
                },
                SqueezrJson.FileOptions));

        var rows = JsonSerializer.Deserialize<List<WorkspaceRegistryEntry>>(
            File.ReadAllText(RegistryPath), SqueezrJson.FileOptions) ?? [];
        rows.Add(new WorkspaceRegistryEntry(
            name, registeredRoot, storeKey, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        File.WriteAllText(RegistryPath, JsonSerializer.Serialize(rows, SqueezrJson.FileOptions));
    }

    private string RegistryPath => Path.Combine(_home, SqueezrPaths.RegistryFileName);

    private string Tracked(string path)
    {
        _createdDirs.Add(path);
        return path;
    }

    private static string RepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Squeezr.slnx")))
            {
                return Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            }
        }

        throw new InvalidOperationException($"Repository root not found above {AppContext.BaseDirectory}.");
    }
}
