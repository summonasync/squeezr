using Shouldly;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Tests.Indexing.Ignore;
using Squeezr.Tests.Storage;

namespace Squeezr.Tests.Workspace;

// The family listing is the shape the registry already holds: which row the family hangs on, which rows name it,
// and what branch and kind each was recorded with. Two things are pinned hard here. First, the columns come off
// T1's fields — a listing that re-derived them from git would answer a different question than the one the board
// asked, and it would cost a git process on a read path. Second, the scope is one family: a lane of another
// repository must not turn up, because the fleet view is this card's declared anti-scope and `workspace list`
// already owns the machine-wide sweep.
[Collection("ProcessGlobals")]
public sealed class WorktreeListingTests : IDisposable
{
    private const string HomeEnvironmentVariable = "SQUEEZR_HOME";

    private readonly string? _previousEnv;
    private readonly string _home;
    private readonly List<string> _createdDirs = [];

    public WorktreeListingTests()
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
    public void List_FromALane_ReturnsTheParentRowAndItsOwnWithBranchAndKindFromTheColumns()
    {
        var (main, lane, mainKey, laneKey) = FamilyWithOneLane("feat/116-list-lane");
        var registryBefore = File.ReadAllText(WorkspaceRegistry.FilePath);

        var answer = WorktreeListing.List(lane);

        answer.FamilyRootPath.ShouldBe(WorkspaceCanonicalPath.Canonicalize(main));
        answer.QueriedBranch.ShouldBe("feat/116-list-lane",
            "git's answer for the checkout the path stands in, measured off the same files git's facts came from");
        var parent = answer.Rows.Single(row => row.Role == WorktreeFamilyRole.Parent);
        parent.StoreKey.ShouldBe(mainKey);
        parent.IsCurrent.ShouldBeFalse("the path stands in the lane, not the parent");
        var child = answer.Rows.Single(row => row.Role == WorktreeFamilyRole.Child);
        child.StoreKey.ShouldBe(laneKey);
        child.Branch.ShouldBe("feat/116-list-lane",
            "the branch column T1 wrote is what this listing prints, not a re-derivation");
        child.Kind.ShouldBe(WorkspaceKind.Worktree);
        child.IsCurrent.ShouldBeTrue();
        answer.Rows.Count.ShouldBe(2);
        File.ReadAllText(WorkspaceRegistry.FilePath).ShouldBe(registryBefore,
            "a listing is a read: no row is rewritten, reordered or timestamped");
    }

    [Fact]
    public void List_FromTheParent_ReturnsItFirstAndEveryLaneNamingItInRootOrder()
    {
        var (main, first, _, _) = FamilyWithOneLane("feat/116-list-one");
        var second = AddLane(main, "feat/116-list-two");
        var third = AddLane(main, "feat/116-list-three");
        var mainRoot = WorkspaceCanonicalPath.Canonicalize(main);
        WriteLaneRow("lane-two", second, mainRoot);
        WriteLaneRow("lane-three", third, mainRoot);

        var answer = WorktreeListing.List(main);

        answer.Rows.Select(row => row.Role).ShouldBe(
            [WorktreeFamilyRole.Parent, WorktreeFamilyRole.Child, WorktreeFamilyRole.Child,
                WorktreeFamilyRole.Child]);
        string[] expectedLanes = [Physical(first), Physical(second), Physical(third)];
        Array.Sort(expectedLanes, StringComparer.Ordinal);
        answer.Rows.Skip(1).Select(row => row.RootPath).ShouldBe(expectedLanes,
            "lanes are ordered by root, so one registry answers the same table twice");
        answer.Rows.ShouldContain(row => row.IsCurrent && row.Role == WorktreeFamilyRole.Parent,
            "a primary checkout is its own anchor, so 'current' lands on the parent side");
        answer.RowsOnDisk.ShouldBe(4);
    }

    [Fact]
    public void List_LaneRowWrittenBeforeTheColumns_ReportsNoBranchRatherThanAGuess()
    {
        var main = ParentRepo();
        var lane = AddLane(main, "feat/116-list-legacy");
        var mainRoot = WorkspaceCanonicalPath.Canonicalize(main);
        var storeKey = WriteRow("legacy-lane", Physical(lane), mainRoot, kind: null, branch: null);

        var row = WorktreeListing.List(lane).Rows.Single(entry => entry.StoreKey == storeKey);

        row.Branch.ShouldBeNull(
            "a row written before the branch column existed does not know its branch, and the listing does not "
            + "fill the gap from git");
        row.Kind.ShouldBeNull();
        row.Role.ShouldBe(WorktreeFamilyRole.Child, "the parent link makes it a child, not the kind column");
    }

    [Fact]
    public void List_DeadLane_IsReportedGoneAndSplitOutOfTheOnDiskCount_AndTouchesNothing()
    {
        var (main, _, _, _) = FamilyWithOneLane("feat/116-list-live");
        var doomed = AddLane(main, "feat/116-list-doomed");
        var doomedKey = WriteLaneRow("doomed", doomed, WorkspaceCanonicalPath.Canonicalize(main));
        Directory.Delete(doomed, recursive: true);
        var before = Take();

        var answer = WorktreeListing.List(main);

        answer.Rows.Single(row => row.StoreKey == doomedKey).TreePresent.ShouldBeFalse();
        answer.Rows.Count.ShouldBe(3);
        answer.RowsOnDisk.ShouldBe(2, "one lane is a row without a checkout");
        AssertUnchanged(before, "a dead row is reported, not reaped: the listing writes nothing");
    }

    [Fact]
    public void List_AnotherReposLane_NeverJoinsTheFamily()
    {
        var (_, lane, mainKey, laneKey) = FamilyWithOneLane("feat/116-list-mine");
        var other = ParentRepo("otherws");
        var otherLane = AddLane(other, "feat/116-list-theirs");
        var otherKey = WriteLaneRow("other-lane", otherLane, WorkspaceCanonicalPath.Canonicalize(other));

        var answer = WorktreeListing.List(lane);

        // The family is bounded by the queried checkout: the machine-wide sweep is another door's job.
        answer.Rows.Select(row => row.StoreKey).ShouldNotContain(otherKey);
        string[] familyKeys = [mainKey, laneKey];
        Array.Sort(familyKeys, StringComparer.Ordinal);
        var listedKeys = answer.Rows.Select(row => row.StoreKey)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        listedKeys.ShouldBe(familyKeys);
        answer.Rows.Count.ShouldBe(2);
    }

    [Fact]
    public void List_PlainDirectoryNobodyRegistered_AnswersAnEmptyFamilyWithAReason()
    {
        var plain = Tracked(TestDirs.NewTempDir());
        var before = Take();

        var answer = WorktreeListing.List(plain);

        answer.FamilyRootPath.ShouldBeNull();
        answer.Rows.ShouldBeEmpty();
        answer.RowsOnDisk.ShouldBe(0);
        answer.Reason.ShouldNotBeNull().ShouldContain(RemedyText.InitCommand);
        answer.Reason.ShouldNotBeNull().ShouldContain(RemedyText.WorktreeInitCommand);
        AssertUnchanged(before);
    }

    [Fact]
    public void List_AFileNotADirectory_RefusesAsNotAWorktreeRatherThanRefusingAsGone()
    {
        var main = ParentRepo();
        var file = Path.Combine(main, "src", "Seed.cs");
        File.Exists(file).ShouldBeTrue("the premise: a real file inside the checkout");

        var refusal = Should.Throw<WorkspaceException>(() => WorktreeListing.List(file));

        refusal.Code.ShouldBe(WorkspaceException.NotAWorktreeCode);
        refusal.Message.ShouldContain("is a file");
    }

    [Fact]
    public void List_APathInsideARegisteredWorkspace_AnswersThatWorkspacesFamily()
    {
        var (main, _, mainKey, laneKey) = FamilyWithOneLane("feat/116-list-src");

        var answer = WorktreeListing.List(Path.Combine(main, "src"));

        answer.FamilyRootPath.ShouldBe(WorkspaceCanonicalPath.Canonicalize(main));
        answer.QueriedRootPath.ShouldBe(Physical(main),
            "the subject of a listing is the checkout the path sits in, not the directory it stood on");
        answer.QueriedBranch.ShouldBe(WorktreeFacts.Resolve(main)!.Branch,
            "and the branch named is that checkout's, measured from the same files");
        answer.Rows.Select(row => row.StoreKey).ShouldBe([mainKey, laneKey]);
        answer.Rows.Single(row => row.StoreKey == mainKey).IsCurrent.ShouldBeTrue(
            "the queried directory sits in the parent checkout, so that is the row it resolves to");
        var lane = answer.Rows.Single(row => row.StoreKey == laneKey);
        lane.Role.ShouldBe(WorktreeFamilyRole.Child);
        lane.IsCurrent.ShouldBeFalse("a lane of the family is not the row this path resolves to");
    }

    [Fact]
    public void List_GonePath_RefusesOnTheSameFieldsTheResolveDoorReports()
    {
        var (main, _, _, _) = FamilyWithOneLane("feat/116-list-ok");
        var doomed = AddLane(main, "feat/116-list-doomed");
        var doomedKey = WriteLaneRow("doomed", doomed, WorkspaceCanonicalPath.Canonicalize(main));
        Directory.Delete(doomed, recursive: true);

        var refusal = Should.Throw<WorktreeGoneException>(() => WorktreeListing.List(doomed));

        // One class of refusal for both read doors: the same code and the same measured fields. A listing that
        // came back empty here would read as 'this family has no lanes', which is a different fact entirely.
        refusal.Code.ShouldBe(WorkspaceException.WorktreeNotRegisteredCode);
        refusal.Facts.Rows.ShouldHaveSingleItem().StoreKey.ShouldBe(doomedKey);
        refusal.Message.ShouldContain("reads and writes nothing");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/a\0b")]
    public void List_GarbagePathArgument_RefusesAsInvalidInput(string path)
    {
        Should.Throw<WorkspaceException>(() => WorktreeListing.List(path))
            .Code.ShouldBe(WorkspaceException.NotAWorktreeCode);
    }

    private (string Main, string Lane, string MainStoreKey, string LaneStoreKey) FamilyWithOneLane(string branch)
    {
        var main = ParentRepo();
        var mainRoot = WorkspaceCanonicalPath.Canonicalize(main);
        var lane = AddLane(main, branch);
        return (main, lane, WorkspaceRegistry.FindByRootPath(mainRoot)!.StoreKey,
            WriteLaneRow("lane-" + branch.Split('/')[^1], lane, mainRoot));
    }

    private string ParentRepo(string name = "mainws")
    {
        var main = Tracked(TestDirs.NewTempDir());
        TestGitRepo.Init(main);
        Directory.CreateDirectory(Path.Combine(main, "src"));
        File.WriteAllText(Path.Combine(main, "src", "Seed.cs"), "public sealed class Seed {\n}\n");
        TestGitRepo.Add(main, "src/Seed.cs");
        TestGitRepo.Commit(main, "seed");
        new WorkspaceManager(main).Init(name);
        new WorkspaceManager(main).AddRepo(".", StoreFixture.RepoName);
        // Rows the family's parent really owns: a listing that reported a store with no database behind it would
        // let a green row read as an indexed workspace.
        StoreFixture.Seed(WorkspaceStorePaths.RequireStoreDir(
            WorkspaceRegistry.FindByRootPath(WorkspaceCanonicalPath.Canonicalize(main))!.StoreKey));
        return main;
    }

    // Lanes live beside their parent, not inside it: the parent's index is what a baseline pass reads, and a
    // nested checkout would be counted twice by anything that walks the parent tree.
    private string AddLane(string main, string branch)
    {
        var holder = Tracked(TestDirs.NewTempDir());
        var lane = Path.Combine(holder, "lane-a");
        TestGitRepo.AddWorktree(main, lane, branch);
        Tracked(lane);
        return lane;
    }

    private string WriteLaneRow(string name, string lane, string mainRoot) =>
        WriteRow(name, Physical(lane), mainRoot, WorkspaceKind.Worktree, WorktreeFacts.Resolve(lane)?.Branch);

    private string WriteRow(
        string name,
        string root,
        string? parentRoot,
        WorkspaceKind? kind,
        string? branch)
    {
        var storeKey = WorkspaceStoreKeys.Create(name, root);
        WorkspaceRegistry.Add(new WorkspaceRegistryEntry(
            name, root, storeKey, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, parentRoot, kind, branch));
        return storeKey;
    }

    private static string Physical(string path) => WorkspaceCanonicalPath.ResolveThroughLinks(path);

    private record Snapshot(string Registry, string[] StoreDirs);

    private Snapshot Take() =>
        new(File.ReadAllText(WorkspaceRegistry.FilePath), StoreDirListing());

    private string[] StoreDirListing() =>
        Directory.Exists(SqueezrHome.WorkspacesDir)
            ? [.. Directory.GetDirectories(SqueezrHome.WorkspacesDir)
                .Select(dir => new DirectoryInfo(dir).Name)
                .OrderBy(name => name, StringComparer.Ordinal)]
            : [];

    private void AssertUnchanged(Snapshot before, string? because = null)
    {
        File.ReadAllText(WorkspaceRegistry.FilePath).ShouldBe(before.Registry,
            because ?? "the listing writes nothing");
        StoreDirListing().ShouldBe(before.StoreDirs, because ?? "the listing mints no store dir");
    }

    private string Tracked(string path)
    {
        _createdDirs.Add(path);
        return path;
    }
}
