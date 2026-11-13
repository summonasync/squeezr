using Shouldly;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Tests.Indexing.Ignore;
using Squeezr.Tests.Storage;

namespace Squeezr.Tests.Workspace;

// The read door is measured where the decision is made: which rows one path folds, what a store's own record
// says through this door, what the two 'nothing here' answers say differently, and that a path whose directory is
// gone answers with the SAME fields `worktree drop` refuses on rather than a paraphrase of them.
//
// Most tests compare the registry file's bytes and the store-dir listing around the call. 'Writes nothing' is the
// property that makes this door safe to call from a hook, and a witness that only reads the answer would stay
// green if the door started writing while it answered.
[Collection("ProcessGlobals")]
public sealed class WorktreeResolveTests : IDisposable
{
    private const string HomeEnvironmentVariable = "SQUEEZR_HOME";
    private const string LaneBranch = "feat/116-resolve-lane";
    private const string SharedGoneHead =
        "does not exist, so no git facts can be read from it, while 1 registry row(s) still name it";

    private readonly string? _previousEnv;
    private readonly string _home;
    private readonly List<string> _createdDirs = [];

    public WorktreeResolveTests()
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
    public void Resolve_LiveWorktreeOwningNoRow_AnswersUnregisteredAndCreatesNothing()
    {
        var main = ParentWithRows();
        var child = LaneBesideParent(main, LaneBranch);
        var before = Take();

        var answer = WorktreeResolve.Resolve(child);

        answer.State.ShouldBe(WorktreeResolveState.Unregistered);
        answer.RootPath.ShouldBe(WorkspaceCanonicalPath.ResolveThroughLinks(child));
        answer.Branch.ShouldBe(LaneBranch);
        answer.IsLinked.ShouldBeTrue();
        answer.ParentRootPath.ShouldBe(WorkspaceCanonicalPath.Canonicalize(main));
        answer.ParentRegistered.ShouldBeTrue();
        answer.Rows.ShouldBeEmpty();
        // A read answer may not mint the store it just said was missing.
        answer.Reason.ShouldNotBeNull().ShouldContain(RemedyText.WorktreeInitCommand);
        AssertUnchanged(before, "a read answer may not mint the store it just said was missing");
        Directory.Exists(Path.Combine(child, SqueezrPaths.StoreDirName)).ShouldBeFalse();
    }

    [Fact]
    public void Resolve_DirectoryWithNoGitMarker_AnswersNotAWorktreeAndNamesPlainInit()
    {
        var plain = Tracked(TestDirs.NewTempDir());
        var before = Take();

        var answer = WorktreeResolve.Resolve(plain);

        answer.State.ShouldBe(WorktreeResolveState.NotAWorktree);
        answer.RootPath.ShouldBeNull("git names no checkout here, so there is no root to report");
        answer.Reason.ShouldNotBeNull().ShouldContain("no readable .git marker");
        answer.Reason.ShouldNotBeNull().ShouldContain(RemedyText.InitCommand);
        // A plain directory's remedy is plain init: naming the worktree verb would send the reader to a door
        // that refuses them.
        answer.Reason.ShouldNotBeNull().ShouldNotContain(RemedyText.WorktreeInitCommand);
        AssertUnchanged(before);
        Directory.Exists(Path.Combine(plain, SqueezrPaths.StoreDirName)).ShouldBeFalse();
    }

    [Fact]
    public void Resolve_AFileNotADirectory_AnswersNotAWorktreeRatherThanRefusingAsGone()
    {
        var main = ParentWithRows();
        var file = Path.Combine(main, "seed.txt");
        File.Exists(file).ShouldBeTrue("the premise: this path is a file, not a missing directory");
        var before = Take();

        var answer = WorktreeResolve.Resolve(file);

        answer.State.ShouldBe(WorktreeResolveState.NotAWorktree);
        answer.Reason.ShouldNotBeNull().ShouldContain("is a file");
        answer.Rows.ShouldBeEmpty();
        AssertUnchanged(before);
    }

    [Fact]
    public void Resolve_APathInsideTheTree_ResolvesTheCheckoutItSitsInNotTheDirectoryItStoodOn()
    {
        var main = ParentWithRows();
        var child = LaneBesideParent(main, LaneBranch);
        var src = Directory.CreateDirectory(Path.Combine(child, "src")).FullName;
        File.WriteAllText(Path.Combine(src, "Branch.cs"), "public sealed class BranchFile {\n}\n");

        var answer = WorktreeResolve.Resolve(src);

        answer.RootPath.ShouldBe(WorkspaceCanonicalPath.ResolveThroughLinks(child),
            "the subject is the checkout, so a directory under it resolves the same store");
        answer.State.ShouldBe(WorktreeResolveState.Unregistered);
    }

    [Fact]
    public void Resolve_FilledLaneWithAnOpenRecord_SurfacesTheRowColumnsAndTheStoresOwnProof()
    {
        var main = ParentWithRows();
        var child = LaneBesideParent(main, LaneBranch);
        var storeKey = FillLane(child, main);
        var before = Take();

        var answer = WorktreeResolve.Resolve(child);

        answer.State.ShouldBe(WorktreeResolveState.Resolved);
        var row = answer.Rows.ShouldHaveSingleItem();
        row.StoreKey.ShouldBe(storeKey);
        row.Kind.ShouldBe(WorkspaceKind.Worktree);
        row.Branch.ShouldBe(LaneBranch);
        row.ParentRootPath.ShouldBe(WorkspaceCanonicalPath.Canonicalize(main));
        row.TreePresent.ShouldBeTrue();
        row.StoreDirPresent.ShouldBeTrue();
        // What the store says about itself reaches this door unchanged. The fill landed rows and no pass closed
        // them, and those are separate answers rather than one word: a reader that gets only 'pending' cannot
        // tell a killed copy from an unproven tree.
        var record = row.Record.ShouldNotBeNull();
        record.Filled.ShouldBeTrue();
        record.PassCompleted.ShouldBeFalse();
        record.Pending.ShouldBeTrue();
        answer.Records.ShouldHaveSingleItem().ShouldBe(record);
        AssertUnchanged(before, "reading a lane's record must not close it, mint a dir or rewrite a row");
    }

    [Fact]
    public void Resolve_LaneRegisteredButNeverFilled_AnswersNoRecordRatherThanAFalseNotPending()
    {
        var main = ParentWithRows();
        var child = LaneBesideParent(main, LaneBranch);
        WorktreeRegistration.Register(child, null);

        var row = WorktreeResolve.Resolve(child).Rows.ShouldHaveSingleItem();

        row.Record.ShouldBeNull(
            "a store nobody filled has no reconcile record; an absent field is the honest answer, not a false");
        row.StoreDirPresent.ShouldBeTrue("the store exists, it just carries no proof yet");
    }

    [Fact]
    public void Resolve_TwoSpellingsOfOneTree_ListsEveryRowOfThatDirectoryAndNamesNoFavourite()
    {
        var main = ParentWithRows();
        var child = LaneBesideParent(main, LaneBranch);
        var physical = WorkspaceCanonicalPath.ResolveThroughLinks(child);
        var link = LinkTo(child);
        var mainRoot = WorkspaceCanonicalPath.Canonicalize(main);
        var physicalKey = WriteRow("lane-a", physical, mainRoot);
        var linkKey = WriteRow("lane-a-spelled", link, mainRoot);
        var before = Take();

        var answer = WorktreeResolve.Resolve(link);

        answer.RootPath.ShouldBe(physical);
        answer.Rows.Select(row => row.StoreKey).ShouldBe([physicalKey, linkKey],
            "two spellings of one directory are two rows of one tree; showing one hides the other's store");
        AssertUnchanged(before, "folding spellings is a read, not a re-registration");
    }

    [Fact]
    public void Resolve_RecordedBranchStaleAgainstGit_AnswersBothColumnsAndRewritesNothing()
    {
        var main = ParentWithRows();
        var child = LaneBesideParent(main, LaneBranch);
        var root = WorkspaceCanonicalPath.ResolveThroughLinks(child);
        var storeKey = WriteRow("lane-a", root, WorkspaceCanonicalPath.Canonicalize(main),
            recordedBranch: "feat/the-branch-that-was");
        var registryBefore = File.ReadAllText(WorkspaceRegistry.FilePath);

        var answer = WorktreeResolve.Resolve(child);

        answer.Branch.ShouldBe(LaneBranch, "git's answer is measured now");
        answer.Rows.ShouldHaveSingleItem().Branch.ShouldBe("feat/the-branch-that-was",
            "the recorded column is what the registry holds; this door corrects nothing");
        File.ReadAllText(WorkspaceRegistry.FilePath).ShouldBe(registryBefore,
            "a stale column is a fact to report, not a defect to heal on a read path");
        WorkspaceRegistry.FindByStoreKey(storeKey)!.Branch.ShouldBe("feat/the-branch-that-was");
    }

    [Fact]
    public void Resolve_MainWorktreeWithLanes_CountsTheLanesAndNamesItselfNoParent()
    {
        var main = ParentWithRows();
        var mainRoot = WorkspaceCanonicalPath.ResolveThroughLinks(main);
        var first = LaneBesideParent(main, "feat/116-resolve-one");
        var second = LaneBesideParent(main, "feat/116-resolve-two");
        WriteRow("lane-one", WorkspaceCanonicalPath.ResolveThroughLinks(first), mainRoot);
        WriteRow("lane-two", WorkspaceCanonicalPath.ResolveThroughLinks(second), mainRoot);
        var before = Take();

        var answer = WorktreeResolve.Resolve(main);

        answer.IsLinked.ShouldBeFalse();
        answer.ParentRootPath.ShouldBeNull("a primary checkout is its own main root, so it hangs under nothing");
        answer.DependentLanes.ShouldBe(2);
        AssertUnchanged(before);
    }

    [Fact]
    public void Resolve_SeparateGitDirLane_FallsBackToTheParentLinkTheRowKept()
    {
        var baseDir = Tracked(TestDirs.NewTempDir());
        var main = Path.Combine(baseDir, "main");
        Directory.CreateDirectory(main);
        Tracked(main);
        TestGitRepo.Init(main, Path.Combine(baseDir, "gitdir"));
        File.WriteAllText(Path.Combine(main, "seed.txt"), "seed\n");
        TestGitRepo.Add(main, "seed.txt");
        TestGitRepo.Commit(main, "seed");
        new WorkspaceManager(main).Init("mainws");
        new WorkspaceManager(main).AddRepo(".", StoreFixture.RepoName);
        var child = Path.Combine(baseDir, "lane-sep");
        Tracked(child);
        TestGitRepo.AddWorktree(main, child, LaneBranch);
        var mainRoot = WorkspaceCanonicalPath.Canonicalize(main);
        WriteRow("lane-sep", WorkspaceCanonicalPath.ResolveThroughLinks(child), mainRoot);

        var answer = WorktreeResolve.Resolve(child);

        answer.ParentRootPath.ShouldBe(mainRoot,
            "git names no main root for a separate git dir, so the link the registry recorded is the parent on "
            + "this answer");
        answer.ParentRegistered.ShouldBeTrue();
    }

    [Fact]
    public void Resolve_GoneTreeTheRegistryStillClaims_RefusesWithTheDropFactsAsFields_AndTouchesNothing()
    {
        var main = ParentWithRows();
        var child = LaneBesideParent(main, LaneBranch);
        var root = WorkspaceCanonicalPath.ResolveThroughLinks(child);
        var storeKey = WriteRow("lane-a", root, WorkspaceCanonicalPath.Canonicalize(main));
        var storeDir = Directory.CreateDirectory(Path.Combine(SqueezrHome.WorkspacesDir, storeKey)).FullName;
        File.WriteAllText(Path.Combine(storeDir, "meta.db"), "x");
        Directory.Delete(child, recursive: true);
        var before = Take();

        var refusal = Should.Throw<WorktreeGoneException>(() => WorktreeResolve.Resolve(child));

        refusal.Code.ShouldBe(WorkspaceException.WorktreeNotRegisteredCode);
        refusal.Facts.Path.ShouldBe(child);
        refusal.Facts.PhysicalPath.ShouldBe(root);
        // The fields are the answer: the path as asked, the directory the link walk reached, and every row that
        // still claims it — so a reader decides what to reap without parsing this verb's sentence.
        var goneRow = refusal.Facts.Rows.ShouldHaveSingleItem();
        goneRow.StoreKey.ShouldBe(storeKey);
        goneRow.Name.ShouldBe("lane-a");
        goneRow.RootPath.ShouldBe(root);
        refusal.Message.ShouldContain("reads and writes nothing");
        AssertUnchanged(before,
            "AC5: a gone tree is refused with no writes, no dirs minted and no registry row touched");
        Directory.Exists(storeDir).ShouldBeTrue("the store stays where it was until a human reaps it");
    }

    [Fact]
    public void Resolve_GonePathNobodyClaims_RefusesWithoutNamingADropItHasNoKeyFor()
    {
        var main = ParentWithRows();
        var child = LaneBesideParent(main, LaneBranch);
        Directory.Delete(child, recursive: true);
        var before = Take();

        var refusal = Should.Throw<WorktreeGoneException>(() => WorktreeResolve.Resolve(child));

        refusal.Facts.Rows.ShouldBeEmpty();
        refusal.Message.ShouldContain("no registry row names it");
        // With no row there is no store key to clear, so the sentence may not invent a drop line.
        refusal.Message.ShouldNotContain("--store-key");
        AssertUnchanged(before);
    }

    [Fact]
    public void Resolve_AndDrop_RefuseAGoneTreeOnTheSameFieldsAndEachOnItsOwnRemedy()
    {
        var main = ParentWithRows();
        var child = LaneBesideParent(main, LaneBranch);
        var root = WorkspaceCanonicalPath.ResolveThroughLinks(child);
        var storeKey = WriteRow("lane-a", root, WorkspaceCanonicalPath.Canonicalize(main));
        Directory.Delete(child, recursive: true);

        var dropped = Should.Throw<WorktreeGoneException>(() => WorktreeDrop.Plan(child));
        var resolved = Should.Throw<WorktreeGoneException>(() => WorktreeResolve.Resolve(child));

        // One class of facts, two remedy sentences. The shared half is the same words and the same fields, which
        // is what makes this card's fifth AC a check on T8 rather than a second story about it.
        resolved.Message.ShouldContain($"'{child}' {SharedGoneHead}");
        dropped.Message.ShouldContain($"'{child}' {SharedGoneHead}");
        resolved.Facts.Rows.ShouldHaveSingleItem().StoreKey.ShouldBe(storeKey);
        dropped.Facts.Rows.ShouldHaveSingleItem().StoreKey.ShouldBe(storeKey);
        dropped.Message.ShouldContain("This verb drops a live tree");
        resolved.Message.ShouldContain("This verb reads and writes nothing");
        // The read door may not promise a removal it does not perform.
        resolved.Message.ShouldNotContain("This verb drops a live tree");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/a\0b")]
    public void Resolve_GarbagePathArgument_RefusesAsInvalidInputWithoutTouchingTheRegistry(string path)
    {
        var before = Take();

        var refusal = Should.Throw<WorkspaceException>(() => WorktreeResolve.Resolve(path));

        refusal.Code.ShouldBe(WorkspaceException.NotAWorktreeCode);
        AssertUnchanged(before);
    }

    private string ParentWithRows()
    {
        var main = Tracked(TestDirs.NewTempDir());
        TestGitRepo.Init(main);
        File.WriteAllText(Path.Combine(main, "seed.txt"), "seed\n");
        TestGitRepo.Add(main, "seed.txt");
        TestGitRepo.Commit(main, "seed");
        new WorkspaceManager(main).Init("mainws");
        new WorkspaceManager(main).AddRepo(".", StoreFixture.RepoName);
        // Rows the copy can be measured against, seeded through the fixture that counts them: a parent with a
        // database and nothing in it would let a fill that moved no bytes pass as 'filled' here too.
        StoreFixture.Seed(StoreDirOf(WorkspaceStoreKeys.Create("mainws",
            WorkspaceCanonicalPath.Canonicalize(main))));
        File.Exists(SqueezrPaths.MetaDbPath(main)).ShouldBeTrue("the premise: the parent has rows to inherit");
        return main;
    }

    // The lane lives beside the parent, not inside it: --baseline makes one pass index the PARENT tree, and a
    // lane nested under that tree would then be indexed twice, under two repos. Real repos keep their lanes out of
    // the parent's index through .gitignore; a fixture without that line has to place the directory itself.
    private string LaneBesideParent(string main, string branch)
    {
        var holder = Tracked(TestDirs.NewTempDir());
        var child = Path.Combine(holder, "lane-a");
        TestGitRepo.AddWorktree(main, child, branch);
        Tracked(child);
        return child;
    }

    private string FillLane(string child, string main)
    {
        var registered = WorktreeRegistration.Register(child, null);
        WorktreeStoreFill.Fill(registered, copy: true, () => StoreFixture.ModelKey)
            .State.ShouldBe(WorktreeFillState.Filled, "the premise: rows landed and no pass ran yet");
        WorkspaceRegistry.FindByRootPath(WorkspaceCanonicalPath.ResolveThroughLinks(child))!
            .ParentRootPath.ShouldBe(WorkspaceCanonicalPath.Canonicalize(main));
        return registered.StoreKey!;
    }

    private string LinkTo(string target)
    {
        var holder = Tracked(TestDirs.NewTempDir());
        var link = Path.Combine(holder, "lane-link");
        Directory.CreateSymbolicLink(link, target);
        return link;
    }

    private string WriteRow(
        string name,
        string root,
        string? parentRoot,
        string? recordedBranch = LaneBranch)
    {
        var storeKey = WorkspaceStoreKeys.Create(name, root);
        WorkspaceRegistry.Add(new WorkspaceRegistryEntry(
            name, root, storeKey, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, parentRoot,
            parentRoot is null ? null : WorkspaceKind.Worktree, recordedBranch));
        return storeKey;
    }

    private static string StoreDirOf(string storeKey) => WorkspaceStorePaths.RequireStoreDir(storeKey);

    private record Snapshot(string Registry, string[] StoreDirs);

    private Snapshot Take() =>
        new(File.Exists(WorkspaceRegistry.FilePath) ? File.ReadAllText(WorkspaceRegistry.FilePath) : "",
            StoreDirListing());

    private string[] StoreDirListing() =>
        Directory.Exists(SqueezrHome.WorkspacesDir)
            ? [.. Directory.GetDirectories(SqueezrHome.WorkspacesDir)
                .Select(dir => new DirectoryInfo(dir).Name)
                .OrderBy(name => name, StringComparer.Ordinal)]
            : [];

    private void AssertUnchanged(Snapshot before, string? because = null)
    {
        File.ReadAllText(WorkspaceRegistry.FilePath).ShouldBe(before.Registry,
            because ?? "resolve writes nothing: no row, no reordering, no timestamp");
        StoreDirListing().ShouldBe(before.StoreDirs,
            because ?? "resolve writes nothing: no store dir minted, none removed");
    }

    private string Tracked(string path)
    {
        _createdDirs.Add(path);
        return path;
    }
}
