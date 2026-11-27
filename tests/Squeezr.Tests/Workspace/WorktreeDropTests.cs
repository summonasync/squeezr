using System.Runtime.Versioning;
using Shouldly;
using Squeezr.Cli.Commands;
using Squeezr.Semantic.Embeddings;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Tests.Indexing.Ignore;

namespace Squeezr.Tests.Workspace;

// The death side of registration, measured where the decision is made: which rows one path folds, what a
// refusal promises, and that nothing moved before the fold was settled. Removal runs through the same door
// `drop --store-key` uses, so the collections this verb clears are named by each row's own spelling.
[Collection("ProcessGlobals")]
public sealed class WorktreeDropTests : IDisposable
{
    private const string HomeEnvironmentVariable = "SQUEEZR_HOME";
    private const string LaneBranch = "feat/116-drop-lane";

    private readonly string? _previousEnv;
    private readonly string _home;
    private readonly List<string> _createdDirs = [];

    public WorktreeDropTests()
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

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task Plan_WorktreeRegisteredUnderTwoSpellings_FoldsBothRowsAndClearsBothStores()
    {
        var main = SeededRepo();
        var child = AddWorktree(main, Path.Combine(main, ".lanes", "lane-a"), LaneBranch);
        var physical = WorkspaceCanonicalPath.ResolveThroughLinks(child);
        var link = LinkTo(child);
        var linkSpelling = WorkspaceCanonicalPath.ResolveThroughLinks(link);
        linkSpelling.ShouldBe(physical, "the link and the directory are one tree");
        var physicalKey = WriteRow("lane-a", physical, out var physicalStoreDir);
        var linkKey = WriteRow("lane-a-spelled", link, out var linkStoreDir);
        var handles = new List<RecordingVectorStore>();

        var plan = WorktreeDrop.Plan(child);

        plan.RootPath.ShouldBe(physical);
        plan.IsLinked.ShouldBeTrue();
        plan.Claims.Select(claim => claim.StoreKey).ShouldBe([physicalKey, linkKey],
            "a spelling difference is one workspace, so the fold lists both rows of one tree");
        Directory.Exists(physicalStoreDir).ShouldBeTrue();
        Directory.Exists(linkStoreDir).ShouldBeTrue();

        foreach (var claim in plan.Claims)
        {
            var handle = new RecordingVectorStore();
            handles.Add(handle);
            var result = await WorkspaceStoreDropper.DropAsync(
                claim.StoreKey, claim.RegisteredRootPath, handle, CancellationToken.None);

            result.Collection.ShouldBe(QdrantVectorStore.GetCollectionName(claim.RegisteredRootPath),
                "two spellings of one root mint two collection names, and both are this tree's");
            handle.DeletedCollections.ShouldHaveSingleItem();
            result.CollectionState.ShouldBe(WorkspaceStoreDropper.CollectionRemoved);
        }

        handles.Select(handle => handle.DeletedCollections.Count).ShouldBe([1, 1]);
        WorkspaceRegistry.Load().ShouldBeEmpty();
        Directory.Exists(physicalStoreDir).ShouldBeFalse();
        Directory.Exists(linkStoreDir).ShouldBeFalse();
    }

    [Fact]
    public void Plan_PathWithNoGitMarkerUpTheTree_RefusesAsNotAWorktree()
    {
        var outside = Tracked(TestDirs.NewTempDir());

        var refusal = Should.Throw<WorkspaceException>(() => WorktreeDrop.Plan(outside));

        refusal.Code.ShouldBe(WorkspaceException.NotAWorktreeCode);
        refusal.Message.ShouldContain("no readable .git marker");
        refusal.Message.ShouldContain(RemedyText.DropCommand);
    }

    [Fact]
    public void Plan_LiveWorktreeThatOwnsNoRow_RefusesAndNamesTheMint()
    {
        var main = SeededRepo();
        var child = AddWorktree(main, Path.Combine(main, ".lanes", "lane-a"), LaneBranch);

        var refusal = Should.Throw<WorkspaceException>(() => WorktreeDrop.Plan(child));

        refusal.Code.ShouldBe(WorkspaceException.WorktreeNotRegisteredCode);
        refusal.Message.ShouldContain("owns no store to drop");
        refusal.Message.ShouldContain(RemedyText.WorktreeInitCommand);
    }

    [Fact]
    public void Plan_DirectoryAlreadyRemoved_RefusesAndHandsTheRowsToRepair()
    {
        var main = SeededRepo();
        var child = AddWorktree(main, Path.Combine(main, ".lanes", "lane-a"), LaneBranch);
        var root = WorkspaceCanonicalPath.ResolveThroughLinks(child);
        var storeKey = WriteRow("lane-a", root, out var storeDir);
        Directory.Delete(child, recursive: true);

        var refusal = Should.Throw<WorkspaceException>(() => WorktreeDrop.Plan(child));

        refusal.Code.ShouldBe(WorkspaceException.WorktreeNotRegisteredCode);
        refusal.Message.ShouldContain("does not exist");
        refusal.Message.ShouldContain(storeKey);
        refusal.Message.ShouldContain(RemedyText.WorkspaceRepairCommand);
        WorkspaceRegistry.Load().ShouldHaveSingleItem().StoreKey.ShouldBe(storeKey);
        Directory.Exists(storeDir).ShouldBeTrue("a refusal deletes nothing");
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void Plan_RowStoreKeyNotASingleSegment_RefusesBeforeAnythingMoves()
    {
        var main = SeededRepo();
        var child = AddWorktree(main, Path.Combine(main, ".lanes", "lane-a"), LaneBranch);
        var root = WorkspaceCanonicalPath.ResolveThroughLinks(child);
        var storeKey = WriteRow("lane-a", root, out var storeDir);
        var evilKey = "..";
        WorkspaceRegistry.Add(new WorkspaceRegistryEntry(
            "evil", LinkTo(child), evilKey, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            root, WorkspaceKind.Worktree, null));

        var refusal = Should.Throw<WorkspaceException>(() => WorktreeDrop.Plan(child));

        refusal.Code.ShouldBe(WorkspaceException.WorktreeNotRegisteredCode);
        refusal.Message.ShouldContain("not a single path segment");
        refusal.Message.ShouldContain(evilKey);
        refusal.Message.ShouldContain(RemedyText.WorkspaceRepairCommand);
        WorkspaceRegistry.Load().Select(row => row.StoreKey).ShouldBe([storeKey, evilKey],
            "a refusal leaves every row exactly where it was");
        Directory.Exists(storeDir).ShouldBeTrue();
    }

    [Fact]
    public void Plan_MainWorktreeOfIndexedLanes_ReportsDependentsAndClaimsOnlyItsOwnRow()
    {
        var main = SeededRepo();
        var mainRoot = WorkspaceCanonicalPath.ResolveThroughLinks(main);
        var child = AddWorktree(main, Path.Combine(main, ".lanes", "lane-a"), LaneBranch);
        var mainKey = WriteRow("main-worktree", mainRoot, out _);
        var laneKey = WriteRow("lane-a", WorkspaceCanonicalPath.ResolveThroughLinks(child), out _, mainRoot);

        var plan = WorktreeDrop.Plan(main);

        plan.IsLinked.ShouldBeFalse();
        plan.Claims.ShouldHaveSingleItem().StoreKey.ShouldBe(mainKey);
        plan.DependentLanes.ShouldBe(1);
        plan.ParentRootPath.ShouldBeNull("a primary checkout is its own main root, so it has no parent above it");
        WorkspaceRegistry.Load().Any(row => row.StoreKey == laneKey).ShouldBeTrue();
    }

    [Fact]
    public void Plan_SeparateGitDirNamingNoMainRoot_StillReapsTheRowByItsKeptLink()
    {
        var baseDir = Tracked(TestDirs.NewTempDir());
        var main = Path.Combine(baseDir, "main");
        Directory.CreateDirectory(main);
        TestGitRepo.Init(main, Path.Combine(baseDir, "gitdir"));
        File.WriteAllText(Path.Combine(main, "seed.txt"), "seed\n");
        TestGitRepo.Add(main, "seed.txt");
        TestGitRepo.Commit(main, "seed");
        var child = Path.Combine(baseDir, "lane-sep");
        TestGitRepo.AddWorktree(main, child, LaneBranch);
        var childRoot = WorkspaceCanonicalPath.ResolveThroughLinks(child);
        var mainRoot = WorkspaceCanonicalPath.ResolveThroughLinks(main);
        var storeKey = WriteRow("lane-sep", childRoot, out var storeDir, mainRoot);

        var plan = WorktreeDrop.Plan(child);

        plan.ParentRootPath.ShouldBe(mainRoot,
            "git names no main root for a separate git dir, so the link the row kept is the parent on record");
        plan.Claims.ShouldHaveSingleItem().StoreKey.ShouldBe(storeKey);
        Directory.Exists(storeDir).ShouldBeTrue();
    }

    private string SeededRepo()
    {
        var main = Tracked(TestDirs.NewTempDir());
        TestGitRepo.Init(main);
        File.WriteAllText(Path.Combine(main, "seed.txt"), "seed\n");
        TestGitRepo.Add(main, "seed.txt");
        TestGitRepo.Commit(main, "seed");
        return main;
    }

    private string AddWorktree(string main, string path, string branch)
    {
        Tracked(path);
        TestGitRepo.AddWorktree(main, path, branch);
        return path;
    }

    private string LinkTo(string target)
    {
        var holder = Tracked(TestDirs.NewTempDir());
        var link = Path.Combine(holder, "lane-link");
        Directory.CreateSymbolicLink(link, target);
        return link;
    }

    private string WriteRow(string name, string root, out string storeDir, string? parentRootPath = null)
    {
        var storeKey = WorkspaceStoreKeys.Create(name, root);
        WorkspaceRegistry.Add(new WorkspaceRegistryEntry(
            name, root, storeKey, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, parentRootPath,
            parentRootPath is null ? null : WorkspaceKind.Worktree, null));
        storeDir = Path.Combine(SqueezrHome.WorkspacesDir, storeKey);
        Directory.CreateDirectory(storeDir);
        File.WriteAllText(Path.Combine(storeDir, "meta.db"), "x");
        return storeKey;
    }

    private string Tracked(string path)
    {
        _createdDirs.Add(path);
        return path;
    }


    private sealed class RecordingVectorStore : IVectorStore
    {
        public List<string> DeletedCollections { get; } = [];

        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

        public Task EnsureCollectionAsync(int dimensions, CancellationToken ct) => Task.CompletedTask;

        public Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<VectorHit>> SearchAsync(
            float[] vector, int topK, VectorSearchFilter filter, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<long> CountAsync(CancellationToken ct) => Task.FromResult(0L);

        public Task<IReadOnlyList<ulong>> ScrollPointIdsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ulong>>([]);

        public Task<IReadOnlyList<uint>> ScrollFileIdsWithoutKindAsync(uint repoId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<uint>>([]);

        public Task SetKindPayloadsAsync(IReadOnlyDictionary<uint, string> kindByFileId, CancellationToken ct) =>
            Task.CompletedTask;

        public Task DeleteByFileIdsAsync(IReadOnlyList<uint> fileIds, CancellationToken ct) => Task.CompletedTask;

        public Task DeleteByPointIdsAsync(IReadOnlyList<ulong> pointIds, CancellationToken ct) => Task.CompletedTask;

        public Task DeleteCollectionAsync(CancellationToken ct)
        {
            DeletedCollections.Add("collection");
            return Task.CompletedTask;
        }
    }
}
