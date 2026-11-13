using Shouldly;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Tests.Indexing.Ignore;
using Squeezr.Tests.Storage;

namespace Squeezr.Tests.Workspace;

// The naming decision measured at the door that makes it, before any verb or envelope renders it: when an answer
// owes its subject, when it owes nothing, and which tree gets named when the two differ. The CLI and MCP classes
// measure the contract; this one measures the decision, because both surfaces read the same answer and a wrong
// decision would be wrong twice.
[Collection("ProcessGlobals")]
public sealed class AnswerSubjectTests : IDisposable
{
    private const string HomeEnvironmentVariable = "SQUEEZR_HOME";
    private const string LaneBranch = "feat/116-subject-lane";

    private readonly string? _previousEnv;
    private readonly string _home;
    private readonly List<string> _createdDirs = [];

    public AnswerSubjectTests()
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
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    [Fact]
    public void Describe_CallerInsideItsOwnRegisteredMainWorktree_NamesNothing()
    {
        var main = ParentWithRow("mainws");

        var subject = AnswerSubject.Describe(main, main);

        ShouldBeNull(subject,
            "one tree in play and the caller stands in it: an added field would be a new shape for no reason (035)");
    }

    [Fact]
    public void Describe_FilledLaneAnsweringItsOwnCaller_NamesTheBranchAndTheKeyWithoutCallingItACross()
    {
        var main = ParentWithRow("laneparent");
        var lane = LaneBesideParent(main, LaneBranch);
        var storeKey = WriteRow("lane-a", WorkspaceCanonicalPath.ResolveThroughLinks(lane),
            WorkspaceCanonicalPath.Canonicalize(main), WorkspaceKind.Worktree, LaneBranch);

        var subject = AnswerSubject.Describe(lane, lane);

        subject.ShouldNotBeNull("a lane's coordinates are branch coordinates, and quoting them elsewhere needs the name");
        subject.Branch.ShouldBe(LaneBranch);
        subject.StoreKey.ShouldBe(storeKey);
        subject.CrossTree.ShouldBeFalse("the caller stands in the tree that answered");
        subject.RootPath.ShouldBe(WorkspaceCanonicalPath.ResolveThroughLinks(lane));
    }

    [Fact]
    public void Describe_ParentAnsweringForALaneCaller_NamesTheParentAndCallsItACross()
    {
        var main = ParentWithRow("crossparent");
        var lane = LaneBesideParent(main, LaneBranch);

        var subject = AnswerSubject.Describe(main, lane);

        subject.ShouldNotBeNull();
        subject.CrossTree.ShouldBeTrue();
        subject.RootPath.ShouldBe(WorkspaceCanonicalPath.ResolveThroughLinks(main));
        subject.CallerRootPath.ShouldBe(WorkspaceCanonicalPath.ResolveThroughLinks(lane),
            "the warning names both trees, or a reader cannot tell which side of the pair it is standing on");
    }

    // The conservatism pin. A caller in no checkout at all has no second tree to be mistaken for the answering
    // one, and `serve' already says that out loud as a session warning: this door stays quiet rather than
    // dressing the same fact up as an answer field.
    [Fact]
    public void Describe_CallerStandingInNoCheckout_NamesNothing()
    {
        var main = ParentWithRow("scratchcaller");
        var scratch = Tracked(TestDirs.NewTempDir());

        var subject = AnswerSubject.Describe(main, scratch);

        ShouldBeNull(subject, "no checkout at the caller end, so nothing here can be confused with it");
    }

    [Fact]
    public void Describe_LaneAnsweringForACallerInNoCheckout_StillNamesItsBranch()
    {
        var main = ParentWithRow("scratchlane");
        var lane = LaneBesideParent(main, LaneBranch);
        WriteRow("scratchrow", WorkspaceCanonicalPath.ResolveThroughLinks(lane),
            WorkspaceCanonicalPath.Canonicalize(main), WorkspaceKind.Worktree, LaneBranch);
        var scratch = Tracked(TestDirs.NewTempDir());

        var subject = AnswerSubject.Describe(lane, scratch);

        subject.ShouldNotBeNull("a linked checkout owes its branch whoever asked");
        subject.Branch.ShouldBe(LaneBranch);
        subject.CrossTree.ShouldBeFalse("and a caller with no checkout is not a second tree");
    }

    [Fact]
    public void Describe_StoreRegisteredOnlyUnderALinkedSpelling_NamesTheRowNotTheSpelling()
    {
        var main = ParentWithRow("linkedspelling");
        var lane = LaneBesideParent(main, LaneBranch);
        var alias = CreateLinkBeside(lane);
        var storeKey = WriteRow("aliased", WorkspaceCanonicalPath.ResolveThroughLinks(lane),
            WorkspaceCanonicalPath.Canonicalize(main), WorkspaceKind.Worktree, LaneBranch);

        var subject = AnswerSubject.Describe(alias, alias);

        subject.ShouldNotBeNull();
        subject.StoreKey.ShouldBe(storeKey,
            "the directory is the identity, and a subject that quoted the spelling would name nothing");
    }

    private static void ShouldBeNull(AnswerSubject? subject, string because) =>
        subject.ShouldBeNull(because);

    private string ParentWithRow(string name)
    {
        var main = Tracked(TestDirs.NewTempDir());
        TestGitRepo.Init(main);
        File.WriteAllText(Path.Combine(main, "seed.txt"), "seed\n");
        TestGitRepo.Add(main, "seed.txt");
        TestGitRepo.Commit(main, "seed");
        WriteRow(name, WorkspaceCanonicalPath.ResolveThroughLinks(main), null, null, null);
        return main;
    }

    private string LaneBesideParent(string main, string branch)
    {
        var holder = Tracked(TestDirs.NewTempDir());
        var lane = Path.Combine(holder, "lane-a");
        TestGitRepo.AddWorktree(main, lane, branch);
        return lane;
    }

    private string CreateLinkBeside(string target)
    {
        var holder = Tracked(TestDirs.NewTempDir());
        var link = Path.Combine(holder, "link-to-lane");
        Directory.CreateSymbolicLink(link, target);
        return Tracked(link);
    }

    private string WriteRow(
        string name,
        string root,
        string? parentRoot,
        WorkspaceKind? kind,
        string? branch)
    {
        var storeKey = WorkspaceStoreKeys.Create(name, root);
        WorkspaceRegistry.Add(new WorkspaceRegistryEntry(name, root, storeKey, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, parentRoot, kind, branch));
        Directory.CreateDirectory(Path.Combine(SqueezrHome.WorkspacesDir, storeKey));
        return storeKey;
    }

    private string Tracked(string path)
    {
        _createdDirs.Add(path);
        return path;
    }
}
