using System.Runtime.Versioning;
using System.Text.Json;
using Shouldly;
using Squeezr.Cli.Commands;
using Squeezr.Semantic.Embeddings;
using Squeezr.Core.Configuration;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Tests.Indexing.Ignore;
using Squeezr.Semantic.Configuration;

namespace Squeezr.Tests.Cli;

// The verbs as a user meets them: a lane store that dies with its tree, the older door still working, and the
// registry sweep taking the lane row a `git worktree remove` left behind. Every home here is a fresh pinned
// directory, and every minted config is rewired to a dead port before a verb can point at a collection, so
// the Qdrant of this machine is never the store under test.
[Collection("ProcessGlobals")]
public sealed class WorktreeDropCliTests : IDisposable
{
    private readonly string _home;
    private readonly List<string> _createdDirs = [];

    public WorktreeDropCliTests()
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
    public async Task WorktreeDrop_LinkedLane_ClearsStoreDirAndRowAndReadsAsUnregisteredAfterwards()
    {
        var lane = await MintedLane("feat/116-drop-basic");
        var storeKey = lane.StoreKey;
        var storeDir = StoreDir(storeKey);
        Directory.Exists(storeDir).ShouldBeTrue();

        var drop = await Run(lane.ChildRoot, "worktree", "drop", "--json");

        drop.ExitCode.ShouldBe(0);
        using var report = JsonDocument.Parse(drop.StdOut.Trim());
        var payload = report.RootElement;
        payload.GetProperty("rootPath").GetString().ShouldBe(lane.RegisteredRoot);
        payload.GetProperty("isLinked").GetBoolean().ShouldBeTrue();
        payload.GetProperty("branch").GetString().ShouldBe("feat/116-drop-basic");
        var store = payload.GetProperty("stores").EnumerateArray().Single();
        store.GetProperty("storeKey").GetString().ShouldBe(storeKey);
        store.GetProperty("removedPaths").EnumerateArray().Select(path => path.GetString())
            .ShouldBe([storeDir]);
        store.GetProperty("collection").GetString()
            .ShouldBe(QdrantVectorStore.GetCollectionName(lane.RegisteredRoot));
        store.GetProperty("collectionState").GetString().ShouldBe(WorkspaceStoreDropper.CollectionUnavailable,
            "this suite points at a dead port, and the report says the collection was left in place");
        drop.StdErr.ShouldContain("warning: Qdrant is not available");
        Directory.Exists(storeDir).ShouldBeFalse();
        RegistryRows().ShouldNotContain(row => row.GetProperty("storeKey").GetString() == storeKey);

        var listed = await Run(lane.MainRoot, "workspace", "list", "--json");

        listed.ExitCode.ShouldBe(0);
        listed.StdOut.ShouldNotContain(storeKey, Case.Sensitive,
            "a dropped lane stops being named by the list surface too");

        var status = await Run(lane.ChildRoot, "status", "--json");

        status.ExitCode.ShouldBe(0);
        using var statusDoc = JsonDocument.Parse(status.StdOut.Trim());
        statusDoc.RootElement.GetProperty("name").GetString().ShouldBe("cli-main",
            "with its own row gone, the tree resolves to the parent again, which is the honest unregistered read");

        var remint = await Run(lane.ChildRoot, "worktree", "init", "--json");

        remint.ExitCode.ShouldBe(0);
        using var minted = JsonDocument.Parse(remint.StdOut.Trim());
        minted.RootElement.GetProperty("state").GetString().ShouldBe("Registered",
            "a fresh mint, not AlreadyRegistered: the row this verb took is really gone");
        Directory.Exists(storeDir).ShouldBeTrue();
    }

    [Fact]
    public async Task WorktreeDrop_PathArgumentFromOutsideTheTree_DropsTheSameStore()
    {
        var lane = await MintedLane("feat/116-drop-arg");

        var drop = await Run(lane.MainRoot, "worktree", "drop", lane.ChildRoot, "--json");

        drop.ExitCode.ShouldBe(0);
        JsonDocument.Parse(drop.StdOut.Trim()).RootElement.GetProperty("stores")
            .EnumerateArray().Single().GetProperty("storeKey").GetString().ShouldBe(lane.StoreKey);
        Directory.Exists(StoreDir(lane.StoreKey)).ShouldBeFalse();
        RegistryRows().ShouldNotContain(row => row.GetProperty("storeKey").GetString() == lane.StoreKey);
    }

    [Fact]
    public async Task DropStoreKey_StillEndsAWorktreeStore_ThroughTheSameDoor()
    {
        var lane = await MintedLane("feat/116-drop-key");

        var drop = await Run(lane.MainRoot, "drop", "--store-key", lane.StoreKey, "--json");

        drop.ExitCode.ShouldBe(0);
        using var result = JsonDocument.Parse(drop.StdOut.Trim());
        result.RootElement.GetProperty("removed").EnumerateArray().Select(path => path.GetString())
            .ShouldBe([StoreDir(lane.StoreKey)]);
        result.RootElement.GetProperty("hint").GetString().ShouldBe("squeezr services down");
        Directory.Exists(StoreDir(lane.StoreKey)).ShouldBeFalse();
        RegistryRows().ShouldNotContain(row => row.GetProperty("storeKey").GetString() == lane.StoreKey);
        drop.StdErr.ShouldContain("warning: Qdrant is not available", Case.Sensitive,
            "both verbs share one door, so both report the collection half the same way");
        Directory.Exists(lane.ChildRoot).ShouldBeTrue("neither verb touches the checkout");
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task WorktreeDrop_TreeCarryingTwoRowSpellings_TakesBothRowsAndBothStores()
    {
        var lane = await MintedLane("feat/116-drop-fold");
        var spelled = Tracked(TestDirs.NewTempDir());
        var linkPath = Path.Combine(spelled, "lane-link");
        Directory.CreateSymbolicLink(linkPath, lane.ChildRoot);
        var linkKey = WorkspaceStoreKeys.Create("lane-spelled-before-the-fix", linkPath);
        AppendRow("lane-spelled-before-the-fix", linkPath, linkKey);
        var linkStoreDir = StoreDir(linkKey);
        Directory.CreateDirectory(linkStoreDir);
        File.WriteAllText(Path.Combine(linkStoreDir, "meta.db"), "x");
        LaneRows([lane.StoreKey, linkKey]).Length.ShouldBe(2,
            "one tree, two rows: the state a link-shaped mint left behind");

        var drop = await Run(lane.ChildRoot, "worktree", "drop", "--json");

        drop.ExitCode.ShouldBe(0);
        using var report = JsonDocument.Parse(drop.StdOut.Trim());
        var stores = report.RootElement.GetProperty("stores").EnumerateArray().ToList();
        stores.Select(store => store.GetProperty("storeKey").GetString()).ShouldBe([lane.StoreKey, linkKey],
            "the fold takes every spelling of one root, never one of them at a time");
        stores.Select(store => store.GetProperty("registeredRootPath").GetString())
            .ShouldBe([lane.RegisteredRoot, linkPath],
                "the link spelling is reported as the row stored it, because its collection name came from it");
        Directory.Exists(StoreDir(lane.StoreKey)).ShouldBeFalse();
        Directory.Exists(linkStoreDir).ShouldBeFalse();
        LaneRows([lane.StoreKey, linkKey]).ShouldBeEmpty();

        var remint = await Run(lane.ChildRoot, "worktree", "init", "--json");

        remint.ExitCode.ShouldBe(0);
        using var reminted = JsonDocument.Parse(remint.StdOut.Trim());
        reminted.RootElement.GetProperty("state").GetString().ShouldBe("Registered");
        reminted.RootElement.GetProperty("storeKey").GetString().ShouldBe(lane.StoreKey);
        LaneRows([lane.StoreKey, linkKey]).Select(row => row.GetProperty("storeKey").GetString())
            .ShouldBe([lane.StoreKey], "the tree comes back as one row, in the store it already had");
        Directory.Exists(StoreDir(linkKey)).ShouldBeFalse("the folded spelling does not come back");
    }

    [Fact]
    public async Task WorktreeDrop_GoneDirectory_RefusesAndHandsTheRowToRepair()
    {
        var lane = await MintedLane("feat/116-drop-gone");
        TestGitRepo.RemoveWorktree(lane.MainRoot, lane.ChildRoot);
        Directory.Exists(lane.ChildRoot).ShouldBeFalse();

        var refusal = await Run(lane.MainRoot, "worktree", "drop", lane.ChildRoot, "--json");

        refusal.ExitCode.ShouldBe(1);
        refusal.StdOut.ShouldBeEmpty();
        var error = ParseStdErrError(refusal.StdErr);
        error.GetProperty("code").GetString().ShouldBe(WorkspaceException.WorktreeNotRegisteredCode);
        error.GetProperty("message").GetString()!.ShouldContain(lane.StoreKey);
        error.GetProperty("message").GetString()!.ShouldContain("squeezr workspace repair");
        Directory.Exists(StoreDir(lane.StoreKey)).ShouldBeTrue();
        LaneRows([lane.StoreKey]).ShouldHaveSingleItem().GetProperty("name").GetString()
            .ShouldBe("116-drop-gone", "the row survives the refusal; only the tree is gone");
    }

    [Fact]
    public async Task Repair_VanishedWorktreeChild_IsPrunedByTheStaleSweep_AndTheParentSurvives()
    {
        var lane = await MintedLane("feat/116-drop-reap");
        TestGitRepo.RemoveWorktree(lane.MainRoot, lane.ChildRoot);
        var parentKey = RegistryRows()
            .Single(row => row.GetProperty("name").GetString() == "cli-main")
            .GetProperty("storeKey").GetString()!;
        var parentStore = StoreDir(parentKey);

        var preview = await Run(lane.MainRoot, "workspace", "repair", "--dry-run");

        preview.ExitCode.ShouldBe(0);
        preview.StdOut.ShouldContain($"116-drop-reap ({lane.StoreKey})");
        preview.StdOut.ShouldContain("— worktree child of");
        preview.StdOut.ShouldContain(lane.RegisteredRoot);

        var sweep = await Run(lane.MainRoot, "workspace", "repair", "--yes", "--json");

        sweep.ExitCode.ShouldBe(0);
        using var report = JsonDocument.Parse(sweep.StdOut.Trim());
        report.RootElement.GetProperty("stale").EnumerateArray()
            .Single(row => row.GetProperty("storeKey").GetString() == lane.StoreKey)
            .GetProperty("parentRootPath").GetString()
            .ShouldBe(WorkspaceCanonicalPath.Canonicalize(lane.MainRoot),
                "the reaped row carried its parent, and the report says so");
        report.RootElement.GetProperty("prune").GetProperty("rows").EnumerateArray()
            .Single(row => row.GetProperty("storeKey").GetString() == lane.StoreKey)
            .GetProperty("action").GetString().ShouldBe("pruned-row-and-store");
        Directory.Exists(StoreDir(lane.StoreKey)).ShouldBeFalse();
        Directory.Exists(parentStore).ShouldBeTrue();
        RegistryRows().Select(row => row.GetProperty("name").GetString()).ShouldBe(["cli-main"],
            "a lane's death never reaches past the lane");
    }

    [Fact]
    public async Task Repair_ApplyDoor_IsReportedSeparatelyAndDeletesNothingOnAnUnreachableEndpoint()
    {
        var lane = await MintedLane("feat/116-drop-sweep");
        TestGitRepo.RemoveWorktree(lane.MainRoot, lane.ChildRoot);

        var withoutApply = await Run(lane.MainRoot, "workspace", "repair", "--yes", "--json");

        withoutApply.ExitCode.ShouldBe(0);
        using (var previewed = JsonDocument.Parse(withoutApply.StdOut.Trim()))
        {
            var collections = previewed.RootElement.GetProperty("collections");
            collections.GetProperty("apply").GetBoolean().ShouldBeFalse(
                "--yes unlocks the row sweep, not the collection arm");
            collections.GetProperty("endpoints").EnumerateArray().Select(value => value.GetString())
                .ShouldBe([TestWorkspaceConfig.DeadQdrantEndpoint]);
            collections.GetProperty("entries").GetArrayLength().ShouldBe(0,
                "the endpoint never answered, so the sweep claims nothing about it");
            collections.GetProperty("unreachableEndpoints").EnumerateArray().Select(value => value.GetString())
                .ShouldBe([TestWorkspaceConfig.DeadQdrantEndpoint]);
        }

        var applied = await Run(lane.MainRoot, "workspace", "repair", "--yes", "--apply", "--json");

        applied.ExitCode.ShouldBe(0);
        using var document = JsonDocument.Parse(applied.StdOut.Trim());
        var appliedCollections = document.RootElement.GetProperty("collections");
        appliedCollections.GetProperty("apply").GetBoolean().ShouldBeTrue();
        appliedCollections.GetProperty("entries").GetArrayLength().ShouldBe(0);
        applied.StdErr.ShouldContain("could not reach", Case.Sensitive,
            "a sweep that could not answer says so instead of reporting a clean collection set");
    }

    [Fact]
    public async Task Repair_ApplyWithDryRun_OrWithStoreKey_ReportsUsageError()
    {
        var lane = await MintedLane("feat/116-doors");

        var exclusive = await Run(lane.MainRoot, "workspace", "repair", "--dry-run", "--apply");

        exclusive.ExitCode.ShouldBe(1);
        exclusive.StdErr.ShouldContain("--dry-run and --apply are mutually exclusive");

        var scoped = await Run(lane.MainRoot, "workspace", "repair", "--apply", "--store-key", lane.StoreKey,
            "--root", lane.ChildRoot);

        scoped.ExitCode.ShouldBe(1);
        scoped.StdErr.ShouldContain("sweep the whole registry");
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task WorktreeDrop_HalfAFold_FailedStoreIsReportedNotSilenced()
    {
        var lane = await MintedLane("feat/116-drop-partial");
        var spelled = Tracked(TestDirs.NewTempDir());
        var linkPath = Path.Combine(spelled, "lane-link");
        Directory.CreateSymbolicLink(linkPath, lane.ChildRoot);
        var linkKey = WorkspaceStoreKeys.Create("lane-spelled-before-the-fix", linkPath);
        AppendRow("lane-spelled-before-the-fix", linkPath, linkKey);
        var linkStoreDir = StoreDir(linkKey);
        Directory.CreateDirectory(linkStoreDir);
        File.WriteAllText(Path.Combine(linkStoreDir, "meta.db"), "x");
        File.SetUnixFileMode(linkStoreDir, UnixFileMode.None);
        try
        {
            var drop = await Run(lane.ChildRoot, "worktree", "drop", "--json");

            drop.ExitCode.ShouldBe(1);
            using var report = JsonDocument.Parse(drop.StdOut.Trim());
            var stores = report.RootElement.GetProperty("stores").EnumerateArray().ToList();
            stores.Count.ShouldBe(2, "the fold was planned whole, so the ledger lists both rows it tried");
            stores[0].GetProperty("storeKey").GetString().ShouldBe(lane.StoreKey);
            stores[0].GetProperty("removedPaths").EnumerateArray().Select(path => path.GetString())
                .ShouldBe([StoreDir(lane.StoreKey)], "the store that did go is a fact of this run, not a lost trace");
            var failed = stores[1];
            failed.GetProperty("storeKey").GetString().ShouldBe(linkKey);
            failed.GetProperty("failure").GetString().ShouldBe("a store dir could not be removed");
            failed.GetProperty("removedPaths").GetArrayLength().ShouldBe(0);
            failed.TryGetProperty("collection", out _).ShouldBeFalse(
                "nothing was asked of a collection behind a directory this pass could not enter");
            Directory.Exists(StoreDir(lane.StoreKey)).ShouldBeFalse();
            Directory.Exists(linkStoreDir).ShouldBeTrue();
            LaneRows([lane.StoreKey, linkKey]).Select(row => row.GetProperty("storeKey").GetString())
                .ShouldBe([linkKey], "one row of the tree is gone, the other stayed and says so");
            var error = ParseStdErrError(drop.StdErr);
            error.GetProperty("code").GetString().ShouldBe(WorkspaceException.DefaultCode);
            error.GetProperty("message").GetString()!.ShouldContain(linkKey);
        }
        finally
        {
            File.SetUnixFileMode(
                linkStoreDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // Three spellings of one tree, the middle one undeletable: a fold that stops at a failure must still
    // account for the tail it never reached. A two-claim setup passes whether or not the policy counts
    // unattempted claims — there is no tail to hide — so the pin needs three rows and a poisoned middle.
    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task WorktreeDrop_FoldDiesMidway_TailClaimIsLedgeredAsNotAttemptedOnTheWire()
    {
        var lane = await MintedLane("feat/116-drop-tail");
        var poison = AddSpelledRow("lane-poisoned-middle", lane.ChildRoot, undeletable: true);
        var tail = AddSpelledRow("lane-untouched-tail", lane.ChildRoot, undeletable: false);
        try
        {
            var drop = await Run(lane.ChildRoot, "worktree", "drop", "--json");

            drop.ExitCode.ShouldBe(1);
            using var report = JsonDocument.Parse(drop.StdOut.Trim());
            var root = report.RootElement;
            root.GetProperty("notAttempted").GetInt32().ShouldBe(1,
                "a three-row plan that died on the second row must not read as a two-row plan");
            var stores = root.GetProperty("stores").EnumerateArray().ToList();
            stores.Select(store => store.GetProperty("storeKey").GetString())
                .ShouldBe([lane.StoreKey, poison.StoreKey]);
            stores[1].GetProperty("failure").GetString().ShouldBe("a store dir could not be removed");
            Directory.Exists(StoreDir(lane.StoreKey)).ShouldBeFalse();
            Directory.Exists(poison.StoreDir).ShouldBeTrue();
            Directory.Exists(tail.StoreDir).ShouldBeTrue("the tail was never asked to die");
            LaneRows([lane.StoreKey, poison.StoreKey, tail.StoreKey]).Select(row => row.GetProperty("storeKey").GetString())
                .ShouldBe([poison.StoreKey, tail.StoreKey]);
            var error = ParseStdErrError(drop.StdErr);
            error.GetProperty("message").GetString()!.ShouldContain(poison.StoreKey);
        }
        finally
        {
            File.SetUnixFileMode(
                poison.StoreDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task WorktreeDrop_FoldDiesMidway_HumanRenderNamesTheUnattemptedTail()
    {
        var lane = await MintedLane("feat/116-drop-tail-human");
        var poison = AddSpelledRow("lane-poisoned-middle", lane.ChildRoot, undeletable: true);
        var tail = AddSpelledRow("lane-untouched-tail", lane.ChildRoot, undeletable: false);
        try
        {
            var drop = await Run(lane.ChildRoot, "worktree", "drop");

            drop.ExitCode.ShouldBe(1);
            drop.StdOut.ShouldContain("Dropped 1 of 2 store(s)", Case.Sensitive,
                "the tally counts what the pass attempted, not what it planned");
            drop.StdOut.ShouldContain("1 claim(s) not attempted", Case.Sensitive,
                "the plan was three rows wide, and the reader has to see the row nobody touched");
            drop.StdOut.ShouldContain("then stopped");
        }
        finally
        {
            File.SetUnixFileMode(
                poison.StoreDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // A second link spelling of the lane tree with its own registry row and store: the shape a
    // link-shaped mint left behind. The undeletable variant wears mode 000, the same trap the half-fold
    // test uses, and the caller restores it.
    [SupportedOSPlatform("linux")]
    private (string StoreKey, string StoreDir) AddSpelledRow(string name, string childRoot, bool undeletable)
    {
        var holder = Tracked(TestDirs.NewTempDir());
        var linkPath = Path.Combine(holder, name);
        Directory.CreateSymbolicLink(linkPath, childRoot);
        var storeKey = WorkspaceStoreKeys.Create(name, linkPath);
        AppendRow(name, linkPath, storeKey);
        var storeDir = StoreDir(storeKey);
        Directory.CreateDirectory(storeDir);
        File.WriteAllText(Path.Combine(storeDir, "meta.db"), "x");
        if (undeletable)
        {
            File.SetUnixFileMode(storeDir, UnixFileMode.None);
        }

        return (storeKey, storeDir);
    }

    private async Task<Lane> MintedLane(string branch)
    {
        var main = Tracked(TestDirs.NewTempDir());
        TestGitRepo.Init(main);
        File.WriteAllText(Path.Combine(main, "seed.txt"), "seed\n");
        TestGitRepo.Add(main, "seed.txt");
        TestGitRepo.Commit(main, "seed");
        var init = await Run(main, "init", "--name", "cli-main", "--json");
        init.ExitCode.ShouldBe(0);
        WriteDeadEndpointConfig(KeyOf("cli-main"));

        var leaf = branch.Split('/')[^1];
        var child = Path.Combine(main, ".lanes", leaf);
        TestGitRepo.AddWorktree(main, child, branch);
        var mint = await Run(child, "worktree", "init", "--json");
        mint.ExitCode.ShouldBe(0);
        using var report = JsonDocument.Parse(mint.StdOut.Trim());
        var payload = report.RootElement;
        var lane = new Lane(
            main,
            child,
            payload.GetProperty("storeKey").GetString()!,
            payload.GetProperty("rootPath").GetString()!);
        WriteDeadEndpointConfig(lane.StoreKey);
        return lane;
    }

    // The child CLI is pinned to this test's home while the test process reads paths itself, so a config is
    // written where the store actually lives: under this home, by key. Every minted store gets one before any
    // verb can open a handle, because a default config names the shared endpoint of this machine and a drop
    // through it would delete a real collection.
    private void WriteDeadEndpointConfig(string storeKey)
    {
        var storeDir = StoreDir(storeKey);
        Directory.CreateDirectory(storeDir);
        File.WriteAllText(
            Path.Combine(storeDir, SqueezrPaths.ConfigFileName),
            JsonSerializer.Serialize(
                SemanticConfig.Bind(SqueezrConfig.CreateDefault()) with
                {
                    Qdrant = new QdrantConfig { Endpoint = TestWorkspaceConfig.DeadQdrantEndpoint }
                },
                SqueezrJson.FileOptions));
    }

    private string KeyOf(string name) =>
        RegistryRows().Single(row => row.GetProperty("name").GetString() == name)
            .GetProperty("storeKey").GetString()!;

    // The shape a link-shaped mint left behind: a row whose root is a symlink spelling of a tree that also
    // holds a plain row. It carries no parent link, because plain init never wrote one.
    private void AppendRow(string name, string root, string storeKey)
    {
        var rows = JsonSerializer.Deserialize<List<WorkspaceRegistryEntry>>(
            File.ReadAllText(RegistryPath), SqueezrJson.FileOptions) ?? [];
        rows.Add(new WorkspaceRegistryEntry(
            name, root, storeKey, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        File.WriteAllText(RegistryPath, JsonSerializer.Serialize(rows, SqueezrJson.FileOptions));
    }

    private record Lane(string MainRoot, string ChildRoot, string StoreKey, string RegisteredRoot);

    private JsonElement[] RegistryRows() =>
        File.Exists(RegistryPath)
            ? [.. JsonDocument.Parse(File.ReadAllText(RegistryPath)).RootElement.EnumerateArray()]
            : [];

    // The home holds the main workspace too, so a claim about a lane's rows is a claim about these keys only.
    private JsonElement[] LaneRows(string[] storeKeys) =>
        [.. RegistryRows().Where(row => row.GetProperty("storeKey").GetString() is { } key
            && storeKeys.Contains(key, StringComparer.Ordinal))];

    private static JsonElement ParseStdErrError(string stdErr)
    {
        var lines = stdErr.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using var doc = JsonDocument.Parse(lines[^1]);
        return doc.RootElement.GetProperty("error").Clone();
    }

    private string RegistryPath => Path.Combine(_home, SqueezrPaths.RegistryFileName);

    private string StoreDir(string storeKey) => Path.Combine(_home, SqueezrPaths.WorkspacesDirName, storeKey);

    private string Tracked(string path)
    {
        _createdDirs.Add(path);
        return path;
    }

    private Task<(int ExitCode, string StdOut, string StdErr)> Run(string workingDir, params string[] args) =>
        CliTestProcess.RunWithHomeAsync(_home, workingDir, args);
}
