using System.Runtime.Versioning;
using Shouldly;
using Squeezr.Cli.Commands;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Cli;

// The fold loop measured where its decision lives, without racing a child process: a plan is a list of
// claims, the loop walks it, and whatever the pass knows after a failure must be in the outcome. The two
// failures modelled here are the two the CLI cannot stage deterministically — a row that vanishes between
// planning and removal, and the tail of a fold that stopped early.
[Collection("ProcessGlobals")]
public sealed class WorktreeFolderTests : IDisposable
{
    private const string HomeEnvironmentVariable = "SQUEEZR_HOME";

    private readonly string? _previousEnv;
    private readonly string _home;
    private readonly List<string> _createdDirs = [];

    public WorktreeFolderTests()
    {
        _previousEnv = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        _home = Tracked(TestDirs.NewTempDir());
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _home);
        SqueezrHome.Reset();
        Directory.CreateDirectory(_home);
        File.WriteAllText(WorkspaceRegistry.FilePath, "[]");
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
    }

    [Fact]
    public async Task RunAsync_RowVanishesBetweenPlanAndLoop_RefusalJoinsTheLedger_AndTheTailStaysUnattempted()
    {
        var (rootA, keyA, storeA) = WriteRow("lane-a");
        var (rootB, keyB, storeB) = WriteRow("lane-b");
        var (rootC, keyC, storeC) = WriteRow("lane-c");
        WorkspaceRegistry.RemoveByStoreKey(keyB);

        var outcome = await WorktreeFolder.RunAsync(
            [
                new WorktreeDropClaim(keyA, "lane-a", rootA),
                new WorktreeDropClaim(keyB, "lane-b", rootB),
                new WorktreeDropClaim(keyC, "lane-c", rootC),
            ],
            CancellationToken.None);

        outcome.Halted.ShouldBeOfType<WorkspaceException>();
        outcome.Stores.Select(store => store.StoreKey).ShouldBe([keyA, keyB],
            "the gone row joins the ledger instead of carrying every removal fact up the stack with it");
        outcome.Stores[1].Failure.ShouldNotBeNull().ShouldContain(keyB);
        Directory.Exists(storeA).ShouldBeFalse("the store that did go stays a fact of this run");
        Directory.Exists(storeB).ShouldBeTrue();
        outcome.NotAttempted.ShouldBe(1, "a three-row plan that died on the second row is not a two-row plan");
        Directory.Exists(storeC).ShouldBeTrue();
        WorkspaceRegistry.Load().Select(row => row.StoreKey).ShouldBe([keyC]);
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task RunAsync_StoreUnremovable_FailedClaimIsLedgered_AndEveryLaterClaimIsCounted()
    {
        var (rootA, keyA, storeA) = WriteRow("lane-a");
        var (rootB, keyB, storeB) = WriteRow("lane-b");
        var (rootC, keyC, _) = WriteRow("lane-c");
        File.SetUnixFileMode(storeB, UnixFileMode.None);
        try
        {
            var outcome = await WorktreeFolder.RunAsync(
                [
                    new WorktreeDropClaim(keyA, "lane-a", rootA),
                    new WorktreeDropClaim(keyB, "lane-b", rootB),
                    new WorktreeDropClaim(keyC, "lane-c", rootC),
                ],
                CancellationToken.None);

            outcome.Halted.ShouldBeOfType<WorkspaceStoreRemovalException>();
            outcome.Stores.Select(store => store.StoreKey).ShouldBe([keyA, keyB]);
            outcome.Stores[1].Failure.ShouldBe("a store dir could not be removed");
            Directory.Exists(storeA).ShouldBeFalse();
            Directory.Exists(storeB).ShouldBeTrue();
            outcome.NotAttempted.ShouldBe(1);
            WorkspaceRegistry.Load().Select(row => row.StoreKey).ShouldBe([keyB, keyC]);
        }
        finally
        {
            File.SetUnixFileMode(
                storeB, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task RunAsync_AllClaimsClearable_LedgerIsWholeAndNothingIsNotAttempted()
    {
        var (rootA, keyA, _) = WriteRow("lane-a");
        var (rootB, keyB, _) = WriteRow("lane-b");

        var outcome = await WorktreeFolder.RunAsync(
            [
                new WorktreeDropClaim(keyA, "lane-a", rootA),
                new WorktreeDropClaim(keyB, "lane-b", rootB),
            ],
            CancellationToken.None);

        outcome.Halted.ShouldBeNull();
        outcome.Stores.Select(store => store.StoreKey).ShouldBe([keyA, keyB]);
        outcome.NotAttempted.ShouldBe(0);
        WorkspaceRegistry.Load().ShouldBeEmpty();
    }

    private (string Root, string StoreKey, string StoreDir) WriteRow(string name)
    {
        var root = Tracked(TestDirs.NewTempDir());
        var storeKey = WorkspaceStoreKeys.Create(name, root);
        WorkspaceRegistry.Add(new WorkspaceRegistryEntry(
            name, root, storeKey, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        var storeDir = Path.Combine(SqueezrHome.WorkspacesDir, storeKey);
        Directory.CreateDirectory(storeDir);
        File.WriteAllText(Path.Combine(storeDir, "meta.db"), "x");
        return (root, storeKey, storeDir);
    }

    private string Tracked(string path)
    {
        _createdDirs.Add(path);
        return path;
    }
}
