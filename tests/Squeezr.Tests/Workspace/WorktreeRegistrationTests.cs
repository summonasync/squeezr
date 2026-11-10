using Shouldly;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Tests.Indexing.Ignore;

namespace Squeezr.Tests.Workspace;

[Collection("ProcessGlobals")]
public sealed class WorktreeRegistrationTests : IDisposable
{
    private const string HomeEnvironmentVariable = "SQUEEZR_HOME";
    private const string LaneBranch = "feat/116-lane-a";

    private readonly string? _previousEnv;
    private readonly string _home;
    private readonly List<string> _createdDirs = [];

    public WorktreeRegistrationTests()
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
    public void Register_LinkedWorktreeOfIndexedMain_WritesRowWithParentKindAndBranch()
    {
        var main = SeededRepo();
        IndexMainWorktree(main);
        var child = AddWorktree(main, Path.Combine(main, ".lanes", "lane-a"), LaneBranch);

        var outcome = WorktreeRegistration.Register(child, null);

        outcome.State.ShouldBe(WorktreeRegistrationState.Registered);
        outcome.RootPath.ShouldBe(WorkspaceCanonicalPath.Canonicalize(child));
        outcome.ParentRootPath.ShouldBe(WorkspaceCanonicalPath.Canonicalize(main));
        outcome.Kind.ShouldBe(WorkspaceKind.Worktree);
        outcome.Branch.ShouldBe(LaneBranch);
        outcome.IsLinked.ShouldBeTrue();
        outcome.ReconcilePending.ShouldBe(true);
        outcome.StoreKey.ShouldStartWith("116-lane-a-");
        outcome.WorkspaceName.ShouldBe("116-lane-a",
            "a linked tree is named after the branch leaf it holds, not the folder it sits in");

        var row = RowsFor(child).ShouldHaveSingleItem();
        row.RootPath.ShouldBe(WorkspaceCanonicalPath.Canonicalize(child));
        row.ParentRootPath.ShouldBe(WorkspaceCanonicalPath.Canonicalize(main));
        row.Kind.ShouldBe(WorkspaceKind.Worktree);
        row.Branch.ShouldBe(LaneBranch);
        File.Exists(SqueezrPaths.MetaDbPath(child)).ShouldBeTrue();
    }

    [Fact]
    public void Register_SecondPassOnTheSamePath_KeepsOneRowOneStoreAndReportsAlreadyRegistered()
    {
        var main = SeededRepo();
        IndexMainWorktree(main);
        var child = AddWorktree(main, Path.Combine(main, ".lanes", "lane-a"), LaneBranch);
        var first = WorktreeRegistration.Register(child, null);
        var storeDirNames = StoreDirNames();

        var second = WorktreeRegistration.Register(child, null);

        second.State.ShouldBe(WorktreeRegistrationState.AlreadyRegistered);
        second.StoreKey.ShouldBe(first.StoreKey);
        RowsFor(child).Count.ShouldBe(1);
        StoreDirNames().ShouldBe(storeDirNames, "an idempotent pass must not mint a second store");
    }

    // One tree gets one store, whichever spelling of its path the caller walks in through. The row lives at
    // the real root, the call arrives through a linked ancestor, and a spelling-sensitive child lookup
    // would hand this checkout a second store dir AND a second collection, silently (review-116-04 #1).
    [Fact]
    public void Register_ChildAlreadyRegisteredAtItsRealRoot_CalledWithALinkSpelledPath_AdoptsThatRowAndMintsNoSecondStore()
    {
        var baseDir = Tracked(TestDirs.NewTempDir());
        var real = Path.Combine(baseDir, "real");
        Directory.CreateDirectory(real);
        var main = Path.Combine(real, "main");
        Directory.CreateDirectory(main);
        TestGitRepo.Init(main);
        Seed(main);
        IndexMainWorktree(main);
        var link = Path.Combine(baseDir, "link");
        Directory.CreateSymbolicLink(link, real);
        var child = AddWorktree(main, Path.Combine(main, ".lanes", "lane-a"), LaneBranch);
        var spelledChild = Path.Combine(link, "main", ".lanes", "lane-a");
        var first = WorktreeRegistration.Register(child, null);
        var storeDirNames = StoreDirNames();

        var second = WorktreeRegistration.Register(spelledChild, null);

        second.State.ShouldBe(WorktreeRegistrationState.AlreadyRegistered);
        second.StoreKey.ShouldBe(first.StoreKey,
            "a link spelling of a registered root adopts the row, it does not mint a second store");
        second.RootPath.ShouldBe(WorkspaceCanonicalPath.Canonicalize(child),
            "the report names the adopted row's root, not the spelling the caller arrived under");
        RowsFor(child).Count.ShouldBe(1);
        RowsForAnySpelling(spelledChild).Count.ShouldBe(1,
            "one checkout must never hold two registry rows under two spellings");
        StoreDirNames().ShouldBe(storeDirNames);
    }

    // The verb runs at every session start, so a pass that changes nothing has to leave the registry file
    // alone: every Add is a full Save — rewrite workspaces.json, rotate the .bak, move the row to the end.
    [Fact]
    public void Register_SecondPassThatChangesNothing_RewritesNoRegistryByte()
    {
        var main = SeededRepo();
        IndexMainWorktree(main);
        var child = AddWorktree(main, Path.Combine(main, ".lanes", "lane-a"), LaneBranch);
        WorktreeRegistration.Register(child, null);
        var stamps = RegistryStamps();

        var second = WorktreeRegistration.Register(child, null);

        second.State.ShouldBe(WorktreeRegistrationState.AlreadyRegistered);
        RegistryStamps().ShouldBe(stamps,
            "a no-op pass must not rewrite workspaces.json, move its mtime or rotate the backup");
    }

    // The "rewrites nothing" half of a primary checkout: plain init owns this row and the worktree verb
    // only reads it back and says so (review-116-04 #11 — this branch had no pin at all).
    [Fact]
    public void Register_PrimaryCheckoutAlreadyRegistered_NamesTheMainWorktreeAndRewritesNothing()
    {
        var main = SeededRepo();
        IndexMainWorktree(main);
        var stamps = RegistryStamps();
        var storeDirNames = StoreDirNames();

        var outcome = WorktreeRegistration.Register(main, null);

        outcome.State.ShouldBe(WorktreeRegistrationState.AlreadyRegistered);
        outcome.IsLinked.ShouldBeFalse();
        outcome.Kind.ShouldBeNull("a primary checkout is not a worktree row");
        outcome.StoreKey.ShouldBe(WorkspaceStoreKeys.Create("mainws", WorkspaceCanonicalPath.Canonicalize(main)));
        outcome.ReconcilePending.ShouldBeNull("an adopted store cannot prove its reconcile state");
        outcome.Reason.ShouldNotBeNull().ShouldContain("is the main worktree");
        RowsFor(main).ShouldHaveSingleItem().Kind.ShouldBeNull();
        StoreDirNames().ShouldBe(storeDirNames);
        RegistryStamps().ShouldBe(stamps);
    }

    [Fact]
    public void Register_DetachedChildSecondPass_KeepsOneRowOneStoreAndNoBranchClaim()
    {
        var main = SeededRepo();
        IndexMainWorktree(main);
        var child = AddWorktree(main, Path.Combine(main, ".lanes", "lane-a"), LaneBranch);
        TestGitRepo.Detach(child);
        var first = WorktreeRegistration.Register(child, null);
        var stamps = RegistryStamps();
        var storeDirNames = StoreDirNames();

        var second = WorktreeRegistration.Register(child, null);

        second.State.ShouldBe(WorktreeRegistrationState.AlreadyRegistered);
        second.StoreKey.ShouldBe(first.StoreKey);
        second.IsDetached.ShouldBeTrue();
        second.Branch.ShouldBeNull();
        RowsFor(child).ShouldHaveSingleItem().Branch.ShouldBeNull();
        StoreDirNames().ShouldBe(storeDirNames);
        RegistryStamps().ShouldBe(stamps, "a detached child is idempotent too — the stamp matches, so nothing rewrites");
    }

    [Fact]
    public void Register_MainWorktreeNotIndexed_MintsNothing()
    {
        var main = SeededRepo();
        var child = AddWorktree(main, Path.Combine(main, ".lanes", "lane-a"), LaneBranch);
        var storeDirNames = StoreDirNames();

        var outcome = WorktreeRegistration.Register(child, null);

        outcome.State.ShouldBe(WorktreeRegistrationState.ParentNotIndexed);
        outcome.StoreKey.ShouldBeNull();
        outcome.WorkspaceName.ShouldBeNull();
        outcome.RootPath.ShouldBe(WorkspaceCanonicalPath.Canonicalize(child),
            "a pass that minted nothing still reports one rootPath shape");
        outcome.ReconcilePending.ShouldBeNull(
            "a pass that created no store has no reconcile state to assert — unknown, never false");
        outcome.Reason.ShouldNotBeNull().ShouldContain("is not an indexed workspace");
        outcome.Reason.ShouldContain(main);
        WorkspaceRegistry.FindByRootPath(child).ShouldBeNull();
        StoreDirNames().ShouldBe(storeDirNames);
        Directory.Exists(Path.Combine(child, SqueezrPaths.StoreDirName)).ShouldBeFalse();
        File.Exists(Path.Combine(child, SqueezrPaths.ManifestFileName)).ShouldBeFalse();
        File.Exists(WorkspaceRegistry.FilePath).ShouldBeTrue();
        WorkspaceRegistry.Load().ShouldBeEmpty("a refused pass must not register the main worktree either");
    }

    [Fact]
    public void Register_SeparateGitDirHasNoParentRootToWrite_MintsNothing()
    {
        var (_, child) = SeparateGitDirRepo();
        var facts = WorktreeFacts.Resolve(child).ShouldNotBeNull();
        facts.IsLinked.ShouldBeTrue("a linked checkout is still a worktree of its own");
        facts.MainWorktreeRoot.ShouldBeNull("git records no main root when the common dir is not '<root>/.git'");
        var storeDirNames = StoreDirNames();

        var outcome = WorktreeRegistration.Register(child, null);

        outcome.State.ShouldBe(WorktreeRegistrationState.NoParentRootToName);
        outcome.Reason.ShouldNotBeNull().ShouldContain("not spelled '<main root>/.git'");
        WorkspaceRegistry.FindByRootPath(child).ShouldBeNull();
        StoreDirNames().ShouldBe(storeDirNames);
    }

    [Fact]
    public void Register_SeparateGitDirSecondPass_StillMintsNothing()
    {
        var (_, child) = SeparateGitDirRepo();
        var first = WorktreeRegistration.Register(child, null);
        var stamps = RegistryStamps();
        var storeDirNames = StoreDirNames();

        var second = WorktreeRegistration.Register(child, null);

        first.State.ShouldBe(WorktreeRegistrationState.NoParentRootToName);
        second.State.ShouldBe(WorktreeRegistrationState.NoParentRootToName);
        second.StoreKey.ShouldBeNull();
        second.ReconcilePending.ShouldBeNull();
        WorkspaceRegistry.Load().ShouldBeEmpty();
        StoreDirNames().ShouldBe(storeDirNames);
        RegistryStamps().ShouldBe(stamps);
    }

    // git cannot name a main root for the --separate-git-dir shape. That is an unknown, not a proof that
    // the pass which measured the link was wrong, and this column is what drop, repair and resolve hang a
    // worktree's death on. The row goes in by hand because no pass of this verb can produce the state
    // "link already carried, git now silent" — the verb mints a link only where git can name one.
    [Fact]
    public void Register_MainRootUnknowableNow_KeepsTheParentLinkTheRowAlreadyCarried()
    {
        var (main, child) = SeparateGitDirRepo();
        var facts = WorktreeFacts.Resolve(child).ShouldNotBeNull();
        facts.IsLinked.ShouldBeTrue();
        facts.MainWorktreeRoot.ShouldBeNull();
        var canonicalChild = WorkspaceCanonicalPath.Canonicalize(child);
        var canonicalMain = WorkspaceCanonicalPath.Canonicalize(main);
        WorkspaceRegistry.Add(new WorkspaceRegistryEntry(
            "116-sep",
            canonicalChild,
            WorkspaceStoreKeys.Create("116-sep", canonicalChild),
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch)
        {
            ParentRootPath = canonicalMain,
            Kind = WorkspaceKind.Worktree,
            Branch = "feat/116-sep",
        });
        var storeDirNames = StoreDirNames();

        var outcome = WorktreeRegistration.Register(child, null);

        outcome.State.ShouldBe(WorktreeRegistrationState.AlreadyRegistered);
        var row = RowsFor(child).ShouldHaveSingleItem();
        row.ParentRootPath.ShouldBe(canonicalMain,
            "an unknown now is not a proven absent — the measured parent link survives the pass");
        row.Kind.ShouldBe(WorkspaceKind.Worktree);
        row.Branch.ShouldBe("feat/116-sep");
        outcome.ParentRootPath.ShouldBe(canonicalMain);
        StoreDirNames().ShouldBe(storeDirNames);
    }

    [Fact]
    public void Register_DetachedHead_WritesNoBranchGuess()
    {
        var main = SeededRepo();
        IndexMainWorktree(main);
        var child = AddWorktree(main, Path.Combine(main, ".lanes", "lane-a"), LaneBranch);
        TestGitRepo.Detach(child);

        var outcome = WorktreeRegistration.Register(child, null);

        outcome.IsDetached.ShouldBeTrue();
        outcome.Branch.ShouldBeNull("the row carries what git names, never what a caller would expect");
        outcome.Kind.ShouldBe(WorkspaceKind.Worktree);
        outcome.ParentRootPath.ShouldBe(WorkspaceCanonicalPath.Canonicalize(main));
        RowsFor(child).ShouldHaveSingleItem().Branch.ShouldBeNull();
    }

    [Fact]
    public void Register_PathAlreadyRegisteredUnderItsFolderName_DefaultNamingIsNotAClash()
    {
        var main = SeededRepo();
        IndexMainWorktree(main);
        var child = AddWorktree(main, Path.Combine(Tracked(TestDirs.NewTempDir()), "lane-a"), LaneBranch);
        new WorkspaceManager(child).Init("lane-a");
        var storeDirNames = StoreDirNames();

        var outcome = WorktreeRegistration.Register(child, null);

        outcome.State.ShouldBe(WorktreeRegistrationState.AlreadyRegistered);
        outcome.StoreKey.ShouldBe(WorkspaceStoreKeys.Create("lane-a", WorkspaceCanonicalPath.Canonicalize(child)));
        outcome.StoreKey.ShouldNotBe(WorkspaceStoreKeys.Create("116-lane-a", WorkspaceCanonicalPath.Canonicalize(child)),
            "the branch spelling would mint a second key for a root that is already registered");
        outcome.StoreKey.ShouldStartWith("lane-a-");
        StoreDirNames().ShouldBe(storeDirNames);
        var row = RowsFor(child).ShouldHaveSingleItem();
        row.Kind.ShouldBe(WorkspaceKind.Worktree);
        row.ParentRootPath.ShouldBe(WorkspaceCanonicalPath.Canonicalize(main));
    }

    [Fact]
    public void Register_NameThatSlugsToTheRegisteredKey_IsNotAClash()
    {
        var main = SeededRepo();
        IndexMainWorktree(main);
        var child = AddWorktree(main, Path.Combine(main, ".lanes", "lane-a"), LaneBranch);
        var first = WorktreeRegistration.Register(child, "My Lane");

        var second = WorktreeRegistration.Register(child, "my lane");

        first.StoreKey.ShouldBe(WorkspaceStoreKeys.Create("My Lane", WorkspaceCanonicalPath.Canonicalize(child)));
        second.State.ShouldBe(WorktreeRegistrationState.AlreadyRegistered);
        second.StoreKey.ShouldBe(first.StoreKey);
        second.WorkspaceName.ShouldBe("My Lane", "the stored name is not rewritten by an equivalent spelling");
    }

    [Fact]
    public void Register_NameAskingForAnotherStoreOnARegisteredPath_RefusesWithTheDropRemedy()
    {
        var main = SeededRepo();
        IndexMainWorktree(main);
        var child = AddWorktree(main, Path.Combine(main, ".lanes", "lane-a"), LaneBranch);
        var registered = WorktreeRegistration.Register(child, null);
        var storeDirNames = StoreDirNames();

        var clash = Should.Throw<WorkspaceException>(() => WorktreeRegistration.Register(child, "other-lane"));

        clash.Code.ShouldBe(WorkspaceException.WorktreeNameClashCode);
        clash.Message.ShouldContain(registered.StoreKey!);
        clash.Message.ShouldContain("other-lane-");
        clash.Message.ShouldContain($"squeezr drop --store-key '{registered.StoreKey}'");
        RowsFor(child).ShouldHaveSingleItem().StoreKey.ShouldBe(registered.StoreKey);
        StoreDirNames().ShouldBe(storeDirNames);
    }

    // The parent lookup is by canonical root, and Canonicalize resolves only the final component: a main
    // worktree registered through a linked ancestor keeps a link-shaped row while git names the main root
    // in its real shape. This verb then sees no indexed parent and mints nothing — the safe direction of a
    // gap that belongs to the identity layer, pinned so the day someone closes it this test says so loudly.
    [Fact]
    public void Register_MainRegisteredThroughAnAncestorLink_SeesNoIndexedMainWorktreeAndMintsNothing()
    {
        var (real, spelledMain, child) = MainRegisteredThroughAncestorLink();
        var storeDirNames = StoreDirNames();

        var outcome = WorktreeRegistration.Register(child, null);

        WorkspaceRegistry.FindByRootPath(spelledMain)!.RootPath.ShouldBe(spelledMain,
            "the row keeps the link spelling it was registered under");
        outcome.State.ShouldBe(WorktreeRegistrationState.ParentNotIndexed);
        outcome.Reason.ShouldNotBeNull().ShouldContain(Path.Combine(real, "main"));
        WorkspaceRegistry.FindByRootPath(child).ShouldBeNull();
        StoreDirNames().ShouldBe(storeDirNames);
    }

    [Fact]
    public void Register_LinkShapedMainWorktreeSecondPass_StillMintsNothing()
    {
        var (_, _, child) = MainRegisteredThroughAncestorLink();
        var first = WorktreeRegistration.Register(child, null);
        var stamps = RegistryStamps();
        var storeDirNames = StoreDirNames();

        var second = WorktreeRegistration.Register(child, null);

        first.State.ShouldBe(WorktreeRegistrationState.ParentNotIndexed);
        second.State.ShouldBe(WorktreeRegistrationState.ParentNotIndexed);
        second.StoreKey.ShouldBeNull();
        second.ReconcilePending.ShouldBeNull();
        WorkspaceRegistry.FindByRootPath(child).ShouldBeNull();
        StoreDirNames().ShouldBe(storeDirNames);
        RegistryStamps().ShouldBe(stamps);
    }

    [Fact]
    public void Register_PathWithNoGitMarker_RefusesAndPointsAtPlainInit()
    {
        var outside = Tracked(TestDirs.NewTempDir());

        var refusal = Should.Throw<WorkspaceException>(() => WorktreeRegistration.Register(outside, null));

        refusal.Code.ShouldBe(WorkspaceException.NotAWorktreeCode);
        refusal.Message.ShouldContain("not a git worktree");
        refusal.Message.ShouldContain(RemedyText.InitCommand);
    }

    // WorktreeFacts refuses blank and NUL-bearing paths on purpose, because the path API throws on them.
    // The refusal has to quote what the caller passed, not re-canonicalize it: an ArgumentException here
    // escapes the command's catch filter and lands on the error channel outside the --json contract.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/has\0nul")]
    public void Register_PathTheFileSystemRefusesToRead_FailsWithTheWorktreeCodeNotAnArgumentException(string path)
    {
        var refusal = Should.Throw<WorkspaceException>(() => WorktreeRegistration.Register(path, null));

        refusal.Code.ShouldBe(WorkspaceException.NotAWorktreeCode);
        refusal.Message.ShouldContain("is not a git worktree");
    }

    [Fact]
    public void Register_FromAFileInsideTheWorktree_RegistersTheToplevelNotTheFile()
    {
        var main = SeededRepo();
        IndexMainWorktree(main);
        var child = AddWorktree(main, Path.Combine(main, ".lanes", "lane-a"), LaneBranch);
        var file = Path.Combine(child, "notes.md");
        File.WriteAllText(file, "notes\n");

        var outcome = WorktreeRegistration.Register(file, null);

        outcome.RootPath.ShouldBe(WorkspaceCanonicalPath.Canonicalize(child));
        WorkspaceRegistry.FindByRootPath(file).ShouldBeNull();
    }

    private string SeededRepo()
    {
        var main = Tracked(TestDirs.NewTempDir());
        TestGitRepo.Init(main);
        Seed(main);
        return main;
    }

    private static void Seed(string main)
    {
        File.WriteAllText(Path.Combine(main, "seed.txt"), "seed\n");
        TestGitRepo.Add(main, "seed.txt");
        TestGitRepo.Commit(main, "seed");
    }

    private static void IndexMainWorktree(string main) => new WorkspaceManager(main).Init("mainws");

    private static string AddWorktree(string main, string child, string branch)
    {
        TestGitRepo.AddWorktree(main, child, branch);
        return child;
    }

    // git's --separate-git-dir layout: the shared dir lives outside both worktrees, so the common dir is
    // never spelled '<main root>/.git' and no file names the main worktree root any more.
    private (string Main, string Child) SeparateGitDirRepo()
    {
        var baseDir = Tracked(TestDirs.NewTempDir());
        var commonDir = Path.Combine(baseDir, "gitdir");
        var main = Path.Combine(baseDir, "main");
        Directory.CreateDirectory(main);
        TestGitRepo.Init(main, commonDir);
        Seed(main);
        var child = Path.Combine(baseDir, "lane-sep");
        TestGitRepo.AddWorktree(main, child, "feat/116-sep");
        return (main, child);
    }

    // A main worktree registered through a linked ancestor keeps the link spelling in its row, while git
    // names the main root in its real shape — the two spellings never meet in a root comparison.
    private (string Real, string SpelledMain, string Child) MainRegisteredThroughAncestorLink()
    {
        var baseDir = Tracked(TestDirs.NewTempDir());
        var real = Path.Combine(baseDir, "real");
        Directory.CreateDirectory(real);
        var main = Path.Combine(real, "main");
        Directory.CreateDirectory(main);
        TestGitRepo.Init(main);
        Seed(main);
        var link = Path.Combine(baseDir, "link");
        Directory.CreateSymbolicLink(link, real);
        var spelledMain = Path.Combine(link, "main");
        new WorkspaceManager(spelledMain).Init("linked-main");
        var child = Path.Combine(spelledMain, ".lanes", "lane-a");
        TestGitRepo.AddWorktree(main, child, LaneBranch);
        return (real, spelledMain, child);
    }

    private List<WorkspaceRegistryEntry> RowsFor(string path)
    {
        var canonical = WorkspaceCanonicalPath.Canonicalize(path);
        return WorkspaceRegistry.Load().Where(row => row.RootPath == canonical).ToList();
    }

    private List<WorkspaceRegistryEntry> RowsForAnySpelling(string path)
    {
        var physical = WorkspaceCanonicalPath.ResolveThroughLinks(path);
        return WorkspaceRegistry.Load()
            .Where(row => WorkspaceCanonicalPath.ResolveThroughLinks(row.RootPath) == physical)
            .ToList();
    }

    // The write signature of the registry: every Save rewrites the file and rotates the backup, so two
    // mtimes say whether a pass touched it, byte-identical content or not.
    private static (long Registry, long Backup) RegistryStamps() =>
        (WriteStamp(WorkspaceRegistry.FilePath), WriteStamp(WorkspaceRegistry.BackupFilePath));

    private static long WriteStamp(string path) =>
        File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : -1;

    private string StoreDirNames() =>
        Directory.Exists(SqueezrHome.WorkspacesDir)
            ? string.Join(',', Directory.GetDirectories(SqueezrHome.WorkspacesDir)
                .Select(dir => new DirectoryInfo(dir).Name)
                .Order(StringComparer.Ordinal))
            : string.Empty;

    private string Tracked(string dir)
    {
        _createdDirs.Add(dir);
        return dir;
    }
}
