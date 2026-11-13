using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Tests.Indexing.Ignore;
using Squeezr.Tests.Storage;

namespace Squeezr.Tests.Workspace;

// The second tree in one store. What is pinned here is the ordering the constraint is made of: the price is named
// by the door that has indexed nothing yet, and the doubling only arrives with the pass the verb runs afterwards.
// A baseline that indexed while registering would make the notice a description of work already paid for, which is
// the difference this card's constraint exists to keep.
//
// Registration goes through the rooted-path add door, so the manifest entry and the meta.db repo row are one
// transaction here too — a repo the manifest names and the database does not, or the other way round, is the
// half-state every read leg of this product refuses to answer for.
[Collection("ProcessGlobals")]
public sealed class WorktreeBaselineTests : IDisposable
{
    private const string HomeEnvironmentVariable = "SQUEEZR_HOME";
    private const string LaneBranch = "feat/116-baseline-lane";
    private const string ParentWorkspace = "mainws";

    private readonly string? _previousEnv;
    private readonly string _home;
    private readonly List<string> _createdDirs = [];

    public WorktreeBaselineTests()
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
    public void Baseline_ParentRoot_AddsRepoMainWithItsRootedPathAndNamesThePriceBeforeAnyIndex()
    {
        var (main, lane) = FilledLane();
        // The premise counts database rows, not status tallies: a seeded store carries files without an index
        // state row, and a premise read off the wrong door would have measured nothing.
        FileCount(lane, StoreFixture.RepoName).ShouldBe(3,
            "the premise: the lane already owns the copied rows before anything joins them");
        File.Exists(SqueezrPaths.MetaDbPath(lane)).ShouldBeTrue();
        using (var probe = new MetaStore(SqueezrPaths.MetaDbPath(lane)))
        {
            probe.FindRepo(WorktreeBaseline.RepoName).ShouldBeNull("the premise: no baseline repo here yet");
        }

        var outcome = WorktreeBaseline.Register(lane, main, WorktreeFacts.Resolve(lane));

        outcome.State.ShouldBe(WorktreeBaselineState.Registered);
        outcome.RepoName.ShouldBe("main", "the naming policy is one name, not a guess per checkout");
        outcome.BaselineRootPath.ShouldBe(WorkspaceCanonicalPath.ResolveThroughLinks(main));
        // The row is the rooted-path shape AddCommand writes: an absolute parent path in the LANE manifest, which
        // is what the read legs join by and what the --repo selector resolves from.
        var entry = WorkspaceManifestFile.Load(lane).Repos
            .Single(repo => repo.Name == WorktreeBaseline.RepoName);
        Path.IsPathRooted(entry.Path).ShouldBeTrue();
        WorkspaceCanonicalPath.ResolveThroughLinks(entry.Path)
            .ShouldBe(WorkspaceCanonicalPath.ResolveThroughLinks(main));
        using (var store = new MetaStore(SqueezrPaths.MetaDbPath(lane)))
        {
            store.FindRepo(WorktreeBaseline.RepoName).ShouldNotBeNull(
                "a manifest row the database does not know is the half-state every read leg refuses");
        }

        // The price, named by the door that has indexed nothing: both roots and the doubling are in the sentence,
        // and the baseline repo still holds zero files because the pass that fills it has not run.
        FileCount(lane, WorktreeBaseline.RepoName).ShouldBe(0,
            "registering a repo indexes nothing — the pass this verb runs afterwards is what pays");
        outcome.CostNotice.ShouldNotBeNull().ShouldContain(main);
        outcome.CostNotice.ShouldNotBeNull().ShouldContain(lane);
        outcome.CostNotice.ShouldNotBeNull().ShouldContain("roughly doubles");
        outcome.CostNotice.ShouldNotBeNull().ShouldContain(WorktreeBaseline.RepoName);
    }

    [Fact]
    public void Baseline_ByTheParentsRegisteredName_ResolvesToTheSameRootAsItsPath()
    {
        var (main, lane) = FilledLane();

        var outcome = WorktreeBaseline.Register(lane, ParentWorkspace, WorktreeFacts.Resolve(lane));

        outcome.BaselineRootPath.ShouldBe(WorkspaceCanonicalPath.ResolveThroughLinks(main),
            "a workspace name and its path are one checkout, so they must land on one row");
        WorkspaceManifestFile.Load(lane).Repos.Count(repo => repo.Name == WorktreeBaseline.RepoName)
            .ShouldBe(1);
    }

    [Fact]
    public void Baseline_ARelativePath_ResolvesAgainstTheLaneRootTheWayAddDoes()
    {
        var (main, lane) = FilledLane();
        var relative = Path.GetRelativePath(lane, main).Replace('\\', '/');

        var outcome = WorktreeBaseline.Register(lane, relative, WorktreeFacts.Resolve(lane));

        outcome.BaselineRootPath.ShouldBe(WorkspaceCanonicalPath.ResolveThroughLinks(main),
            "one path spelling means one repo, so the lane's root is the base it resolves against");
    }

    [Fact]
    public void Baseline_RunTwice_OwnsOneRowAndStillOwesTheNotice()
    {
        var (main, lane) = FilledLane();
        WorktreeBaseline.Register(lane, main, WorktreeFacts.Resolve(lane));

        var second = WorktreeBaseline.Register(lane, main, WorktreeFacts.Resolve(lane));

        second.State.ShouldBe(WorktreeBaselineState.AlreadyRegistered);
        WorkspaceManifestFile.Load(lane).Repos.Count(repo => repo.Name == WorktreeBaseline.RepoName)
            .ShouldBe(1, "a second registration must not mint a second row under the same name");
        // The price is still named, in the present tense: this call registers no row and runs no pass, so it may
        // not promise a doubling it is about to pay for.
        second.CostNotice.ShouldNotBeNull().ShouldContain("roughly double");
        second.CostNotice.ShouldNotContain("this verb runs now");
        second.Reason.ShouldNotBeNull().ShouldContain("already names");
    }

    // The gap the second --baseline run opened: a record that is already closed over a manifest that just grew a
    // repo is the state in which the new tree would never be read at all, because the pass decides whether to run
    // by consulting that record. Reopening the proof is the only write this door makes outside its own row, and a
    // repeat ask that registers nothing must not reopen anything either.
    [Fact]
    public void Baseline_ASecondTreeOnAProvenLaneReopensTheProof_ARepeatAskLeavesItAsItStands()
    {
        var (main, lane) = FilledLane();
        var proof = WorkspaceManifestFile.TryRead(lane)!
            .ClosedPass(passCompleted: true, semanticArmLanded: true, obligationChunks: 0,
                coveredChunks: null, code: null);
        WorkspaceManifestFile.WriteReconcileAt(SqueezrPaths.ManifestPath(lane), lane, proof);
        WorkspaceManifestFile.TryRead(lane)!.CoordinatesProven.ShouldBeTrue("the premise: a closed proof");
        var before = Take();

        var added = WorktreeBaseline.Register(lane, main, WorktreeFacts.Resolve(lane));

        added.State.ShouldBe(WorktreeBaselineState.Registered);
        var reopened = WorkspaceManifestFile.TryRead(lane).ShouldNotBeNull();
        reopened.PassCompleted.ShouldBeFalse(
            "a pass that never read the second tree cannot be the proof that covers it");
        reopened.Code.ShouldBe(ReconcileMarker.BaselineWidenedCode,
            "the reason is machine data, so every surface can say which gap this is");
        reopened.Filled.ShouldBeTrue("and the fill's own answer survives the record being reopened");
        StoreDirListing().ShouldBe(before.StoreDirs, "reopening a record creates no store");

        var repeated = WorktreeBaseline.Register(lane, main, WorktreeFacts.Resolve(lane));

        repeated.State.ShouldBe(WorktreeBaselineState.AlreadyRegistered);
        WorkspaceManifestFile.TryRead(lane)!.Code.ShouldBe(ReconcileMarker.BaselineWidenedCode,
            "the second call registered nothing, so it reopens nothing — the gap it inherits is the first one's");
        repeated.CostNotice.ShouldNotBeNull().Contains("this verb runs now").ShouldBeFalse(
            "and it may not promise a doubling it is not paying for");
    }

    [Fact]
    public void Baseline_TheLaneAsItsOwnSecondTree_RefusesAsOneStoreTwoNamesForOneTree()
    {
        var (_, lane) = FilledLane();

        var refusal = Should.Throw<WorkspaceException>(
            () => WorktreeBaseline.Register(lane, lane, WorktreeFacts.Resolve(lane)));

        refusal.Code.ShouldBe(WorkspaceException.WorktreeSameStoreCode);
        refusal.Message.ShouldContain("leaving --baseline out");
        WorkspaceManifestFile.Load(lane).Repos
            .ShouldNotContain(repo => repo.Name == WorktreeBaseline.RepoName,
                "a refusal registers nothing");
    }

    [Fact]
    public void Baseline_AValueThatIsNeitherNameNorDirectory_RefusesAndNamesTheParentGitKnows()
    {
        var (main, lane) = FilledLane();

        var refusal = Should.Throw<WorkspaceException>(
            () => WorktreeBaseline.Register(lane, "no-such-checkout", WorktreeFacts.Resolve(lane)));

        refusal.Code.ShouldBe(WorkspaceException.InvalidQueryCode);
        // The remedy carries the value the caller actually needs: the main root this verb already read off git.
        refusal.Message.ShouldContain(main);
        WorkspaceManifestFile.Load(lane).Repos
            .ShouldNotContain(repo => repo.Name == WorktreeBaseline.RepoName);
    }

    [Fact]
    public void Baseline_WhenTheBranchTreeAlreadyOwnsTheName_RefusesRatherThanPointOneNameAtTwoTrees()
    {
        // The collision is reachable for real: the copied rows keep the PARENT's repo name, so a workspace whose
        // repo is literally called 'main' hands the lane a branch repo under that name. Renaming silently would
        // put one name on two trees, which is the exact lie this feature exists to close.
        var (main, lane) = FilledLane(parentRepoName: WorktreeBaseline.RepoName);

        var refusal = Should.Throw<WorkspaceException>(
            () => WorktreeBaseline.Register(lane, main, WorktreeFacts.Resolve(lane)));

        refusal.Code.ShouldBe(WorkspaceException.WorktreeNameClashCode);
        refusal.Message.ShouldContain(WorktreeBaseline.RepoName);
        refusal.Message.ShouldContain(RemedyText.DropCommand);
        WorkspaceManifestFile.Load(lane).Repos.ShouldHaveSingleItem().Name.ShouldBe(WorktreeBaseline.RepoName,
            "the branch tree keeps the name it was copied with; the refusal did not steal it");
    }

    [Fact]
    public void Baseline_WithNoStoreHere_RefusesRatherThanWritingAManifestNowhere()
    {
        var main = ParentRepo();
        var lane = AddLane(main, LaneBranch);

        var refusal = Should.Throw<WorkspaceException>(
            () => WorktreeBaseline.Register(lane, main, WorktreeFacts.Resolve(lane)));

        refusal.Code.ShouldBe(WorkspaceException.NotInitializedCode);
        refusal.Message.ShouldContain(RemedyText.WorktreeInitCommand);
        File.Exists(SqueezrPaths.ManifestPath(lane)).ShouldBeFalse(
            "a refusal may not leave a manifest behind for a store that was never minted");
    }

    [Fact]
    public void Baseline_ThePassThatFollowsIndexesBothTrees_WhichIsWhatTheNoticeWentFirstAbout()
    {
        var (main, lane) = FilledLane();
        // A file only the parent tree holds, so the baseline repo has something the lane cannot answer with.
        File.WriteAllText(Path.Combine(main, "ParentOnly.cs"), "public sealed class ParentOnly {\n}\n");
        TestGitRepo.Add(main, "ParentOnly.cs");
        TestGitRepo.Commit(main, "parent only file");
        var oneTree = TotalFiles(lane);
        WorktreeBaseline.Register(lane, main, WorktreeFacts.Resolve(lane));
        FileCount(lane, WorktreeBaseline.RepoName).ShouldBe(0);

        new WorkspaceIndexer().Index(lane, repoName: null, full: false, noSemantic: true);

        var baselineRows = FileCount(lane, WorktreeBaseline.RepoName);
        var branchRows = FileCount(lane, StoreFixture.RepoName);
        baselineRows.ShouldBeGreaterThan(0,
            "the one standard pass indexes what the manifest owns, which is why the row goes in first");
        branchRows.ShouldBeGreaterThan(0, "and the branch tree keeps answering for itself");
        // The price counted rather than promised: before the flag this store answered for one tree, after the
        // pass it answers for two, and the row total grew past what one tree can hold.
        TotalFiles(lane).ShouldBeGreaterThan(oneTree,
            "two trees in one store is what the notice said it was about to pay for");
        TotalFiles(lane).ShouldBe(baselineRows + branchRows,
            "the rows belong to the two repos and to nobody else — a third copy of one tree is the fork this "
            + "feature exists to close");
    }

    [Fact]
    public void Baseline_TheTwoRepoNamesAnswerFromTwoDifferentTrees()
    {
        var (main, lane) = FilledLane();
        // One relative path, two contents, and one path each tree alone holds: the answer a reader gets has to
        // say which tree it came from, and a selector must not be able to answer with the other checkout.
        File.WriteAllText(Path.Combine(main, "Shared.cs"), "public sealed class Shared {\n    // parent body\n}\n");
        File.WriteAllText(Path.Combine(main, "ParentOnly.cs"), "public sealed class ParentOnly {\n}\n");
        File.WriteAllText(Path.Combine(lane, "Shared.cs"), "public sealed class Shared {\n    // branch body\n}\n");
        File.WriteAllText(Path.Combine(lane, "BranchOnly.cs"), "public sealed class BranchOnly {\n}\n");
        WorktreeBaseline.Register(lane, main, WorktreeFacts.Resolve(lane));
        new WorkspaceIndexer().Index(lane, repoName: null, full: false, noSemantic: true);

        var parentRows = RowsOfRepo(lane, WorktreeBaseline.RepoName);
        var branchRows = RowsOfRepo(lane, StoreFixture.RepoName);

        parentRows.ShouldContain("Shared.cs");
        branchRows.ShouldContain("Shared.cs");
        parentRows.ShouldContain("ParentOnly.cs");
        // The two selectors cannot be answering the same tree twice over: that is the whole point of the row.
        branchRows.ShouldNotContain("ParentOnly.cs");
        parentRows.ShouldNotContain("BranchOnly.cs");
    }

    private (string Main, string Lane) FilledLane(string? parentRepoName = null)
    {
        var main = ParentRepo(parentRepoName);
        var lane = AddLane(main, LaneBranch);
        var registered = WorktreeRegistration.Register(lane, null);
        WorktreeStoreFill.Fill(registered, copy: true, () => StoreFixture.ModelKey)
            .State.ShouldBe(WorktreeFillState.Filled, "the premise: a lane with rows to grow on");
        return (main, lane);
    }

    private string ParentRepo(string? repoName = null)
    {
        var name = repoName ?? StoreFixture.RepoName;
        var main = Tracked(TestDirs.NewTempDir());
        TestGitRepo.Init(main);
        for (var index = 1; index <= 3; index++)
        {
            var relative = $"src/File{index}.cs";
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(main, relative))!);
            File.WriteAllText(Path.Combine(main, relative), $"public sealed class File{index} {{\n}}\n");
            TestGitRepo.Add(main, relative);
        }

        TestGitRepo.Commit(main, "seed");
        new WorkspaceManager(main).Init(ParentWorkspace);
        new WorkspaceManager(main).AddRepo(".", name);
        // The seeded rows carry the parent's repo name, because that is the name the lane manifest inherits.
        StoreFixture.Seed(
            WorkspaceStorePaths.RequireStoreDir(
                WorkspaceRegistry.FindByRootPath(WorkspaceCanonicalPath.Canonicalize(main))!.StoreKey),
            repoName: name);
        return main;
    }

    private string AddLane(string main, string branch)
    {
        var holder = Tracked(TestDirs.NewTempDir());
        var lane = Path.Combine(holder, "lane-a");
        TestGitRepo.AddWorktree(main, lane, branch);
        Tracked(lane);
        // One file only the branch holds, so the two trees have contents of their own to be told apart by.
        File.WriteAllText(Path.Combine(lane, "BranchOnly.cs"), "public sealed class BranchOnly {\n}\n");
        return lane;
    }

    // Rows counted where the answer is read from: the repo's own file list in meta.db, joined by the repo name
    // every read leg uses. A status tally would hide a repo whose index state row never landed.
    private static int FileCount(string root, string repoName)
    {
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(root));
        var repo = store.FindRepo(repoName);
        return repo is null ? 0 : store.CountFiles(repo.Id);
    }

    private static string[] RowsOfRepo(string root, string repoName)
    {
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(root));
        var repo = store.FindRepo(repoName);
        repo.ShouldNotBeNull($"the store must know repo '{repoName}' to answer for it");
        return [.. store.ListFiles(repo!.Id).Select(file => file.Path)];
    }

    // The whole store's file count, independent of how many repos claim it: the number a reader compares when
    // something says 'this lane now answers for two trees'.
    private static int TotalFiles(string root)
    {
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(root));
        return store.CountAllFiles();
    }

    private record Snapshot(string Registry, string[] StoreDirs);

    private Snapshot Take() => new(File.ReadAllText(WorkspaceRegistry.FilePath), StoreDirListing());

    private string[] StoreDirListing() =>
        Directory.Exists(SqueezrHome.WorkspacesDir)
            ? [.. Directory.GetDirectories(SqueezrHome.WorkspacesDir)
                .Select(dir => new DirectoryInfo(dir).Name)
                .OrderBy(name => name, StringComparer.Ordinal)]
            : [];

    private string Tracked(string path)
    {
        _createdDirs.Add(path);
        return path;
    }
}
