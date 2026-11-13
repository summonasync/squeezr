using System.Text.Json;
using Shouldly;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Tests.Indexing.Ignore;

namespace Squeezr.Tests.Cli;

// The read verbs measured at the door a session hook actually calls: the exit code, the payload shape, the
// channel the pending word travels on, and what the run leaves (never) behind on disk. The in-process classes
// measure the decisions; this one measures the contract, because 'safe to call from a hook' is a statement about
// a process that runs, not about a function that returns.
//
// The class never touches SQUEEZR_HOME: CliTestProcess captures its run home once, and a class that installs a
// private home before the first spawn poisons every later child in the run (the incident T7's report names).
[Collection("ProcessGlobals")]
public sealed class WorktreeReadVerbsCliTests
{
    private const string MainRepoName = "demo";
    private const string SharedToken = "SHAREDTOKEN";
    private const string ParentToken = "PARENTONLYTOKEN";
    private const string BranchToken = "BRANCHONLYTOKEN";

    [Fact]
    public async Task Resolve_OnAnUnregisteredPlainDirectory_ExitsZeroSaysSoAndCreatesNothing()
    {
        var scratch = TestDirs.NewTempDir();
        var registryBefore = RegistryText();
        var storesBefore = StoreKeys();

        var run = await Run(scratch, "worktree", "resolve", "--json");

        run.ExitCode.ShouldBe(0, run.StdErr);
        using var doc = JsonDocument.Parse(run.StdOut.Trim());
        var payload = doc.RootElement;
        payload.GetProperty("state").GetString().ShouldBe("NotAWorktree");
        payload.GetProperty("rows").GetArrayLength().ShouldBe(0);
        payload.GetProperty("storeKeys").GetArrayLength().ShouldBe(0);
        payload.GetProperty("reason").GetString()!.ShouldContain(RemedyText.InitCommand);
        // The witness AC1 asks for: nothing appeared anywhere.
        RegistryText().ShouldBe(registryBefore, "resolve wrote no row and stamped no timestamp");
        StoreKeys().ShouldBe(storesBefore, "resolve minted no store directory");
        Directory.Exists(Path.Combine(scratch, SqueezrPaths.StoreDirName)).ShouldBeFalse();
        TryDelete(scratch);
    }

    [Fact]
    public async Task Resolve_OnAnUnregisteredWorktree_ExitsZeroAndNamesTheRegistrationItDidNotPerform()
    {
        var (main, lane) = await ParentAndUnregisteredLane("read-unreg");

        var run = await Run(lane, "worktree", "resolve", "--json");

        run.ExitCode.ShouldBe(0, run.StdErr);
        using var doc = JsonDocument.Parse(run.StdOut.Trim());
        var payload = doc.RootElement;
        payload.GetProperty("state").GetString().ShouldBe("Unregistered");
        payload.GetProperty("branch").GetString().ShouldBe("feat/116-read-unreg");
        payload.GetProperty("isLinked").GetBoolean().ShouldBeTrue();
        payload.GetProperty("parentRegistered").GetBoolean().ShouldBeTrue();
        payload.GetProperty("rows").GetArrayLength().ShouldBe(0);
        // The remedy chain is complete: a lane fills from an indexed parent, so the answer names which of the two
        // steps is missing instead of sending the reader to a door that would refuse.
        payload.GetProperty("reason").GetString()!.ShouldContain(RemedyText.WorktreeInitCommand);
        TryDelete(main);
    }

    [Fact]
    public async Task List_ChildAndParent_AnswerWithTheBranchAndKindTheRegistryCarries()
    {
        var (main, lane) = await IndexedParentAndLane("read-list");

        var run = await Run(main, "worktree", "list", "--json");

        run.ExitCode.ShouldBe(0, run.StdErr);
        using var doc = JsonDocument.Parse(run.StdOut.Trim());
        var rows = doc.RootElement.GetProperty("rows").EnumerateArray().ToList();
        var parent = rows.Single(row => row.GetProperty("role").GetString() == "Parent");
        parent.GetProperty("storeKey").GetString().ShouldBe(StoreKeyOf(main));
        // The parent row was written by plain init, before any branch column existed on it.
        parent.TryGetProperty("branch", out _).ShouldBeFalse(
            "an unrecorded column is absent from the wire, never a guessed value");
        var child = rows.Single(row => row.GetProperty("role").GetString() == "Child");
        child.GetProperty("branch").GetString().ShouldBe("feat/116-read-list");
        child.GetProperty("kind").GetString().ShouldBe("Worktree");
        child.GetProperty("parentRootPath").GetString().ShouldBe(WorkspaceCanonicalPath.Canonicalize(main));
        child.GetProperty("treePresent").GetBoolean().ShouldBeTrue();

        var human = await Run(main, "worktree", "list");

        human.ExitCode.ShouldBe(0, human.StdErr);
        human.StdOut.ShouldContain("feat/116-read-list");
        human.StdOut.ShouldContain("child");
        TryDelete(main);
    }

    [Fact]
    public async Task Baseline_TheCostGoesOutOnTheWarningChannel_AndTheReportCarriesTheBlock()
    {
        var (main, lane) = await IndexedParentAndLane("read-baseline");

        var init = await Run(lane, "worktree", "init", "--no-semantic", "--baseline", main, "--json");

        init.ExitCode.ShouldBe(0, init.StdErr);
        init.StdErr.Contains("roughly doubles").ShouldBeTrue(
            "the price is named where every other cost of this verb already travels");
        using var doc = JsonDocument.Parse(init.StdOut.Trim());
        var baseline = doc.RootElement.GetProperty("baseline");
        baseline.GetProperty("state").GetString().ShouldBe("Registered");
        baseline.GetProperty("repoName").GetString().ShouldBe("main");
        baseline.GetProperty("baselineRootPath").GetString()
            .ShouldBe(WorkspaceCanonicalPath.ResolveThroughLinks(main));
        baseline.GetProperty("costNotice").GetString()!.ShouldContain("roughly doubles");
        // One store, two repos: the lane's own and the baseline's, and the manifest is where a reader can check it.
        WorkspaceManifestFile.Load(lane).Repos.Select(repo => repo.Name)
            .ShouldBe([MainRepoName, "main"]);
        TryDelete(main);
    }

    [Fact]
    public async Task Baseline_RepoMainAnswersFromTheParentTree_WhileTheLanesRepoAnswersFromTheBranch()
    {
        var (main, lane) = await IndexedParentAndLane("read-route");
        var joined = await Run(lane, "worktree", "init", "--no-semantic", "--baseline", main, "--json");
        joined.ExitCode.ShouldBe(0, joined.StdErr);

        var parentOnly = await Run(lane, "search", ParentToken, "--repo", "main", "--json");
        var branchOnlyUnderParent = await Run(lane, "search", BranchToken, "--repo", "main", "--json");
        var branchOnly = await Run(lane, "search", BranchToken, "--json");
        var parentUnderLanesRepo = await Run(lane, "search", ParentToken, "--repo", MainRepoName, "--json");

        parentOnly.ExitCode.ShouldBe(0, parentOnly.StdErr);
        var parentRows = Matches(parentOnly.StdOut);
        parentRows.ShouldNotBeEmpty("--repo main reaches the parent tree");
        parentRows.ShouldAllBe(row => row.GetProperty("repo").GetString() == "main",
            "a baseline answer never mixes in the lane's rows");
        SummaryCount(branchOnlyUnderParent.StdOut).ShouldBe(0,
            "the parent tree holds no branch-only content, and the selector does not invent one");
        var branchRows = Matches(branchOnly.StdOut);
        branchRows.ShouldNotBeEmpty("the branch tree answers for itself by default");
        branchRows.ShouldAllBe(row => row.GetProperty("repo").GetString() == MainRepoName);
        SummaryCount(parentUnderLanesRepo.StdOut).ShouldBe(0,
            "the lane's own repo cannot answer with a file its checkout does not hold");
        TryDelete(main);
    }

    [Fact]
    public async Task Baseline_OneRelativePathInBothTrees_YieldsTwoRowsDistinguishedByRepo()
    {
        var (main, lane) = await IndexedParentAndLane("read-dual");
        var joined = await Run(lane, "worktree", "init", "--no-semantic", "--baseline", main, "--json");
        joined.ExitCode.ShouldBe(0, joined.StdErr);

        var run = await Run(lane, "search", SharedToken, "--json");

        run.ExitCode.ShouldBe(0, run.StdErr);
        var rows = Matches(run.StdOut);
        rows.Count(row => row.GetProperty("path").GetString() == "notes.md").ShouldBe(2,
            "one relative path, two trees, two rows in one answer");
        rows.ShouldAllBe(row => row.GetProperty("path").GetString() == "notes.md");
        // The two rows are distinguishable because each names the repo it came from, which is what makes a
        // baseline answer safe to read at all: the envelope says which tree a coordinate belongs to.
        rows.Select(row => row.GetProperty("repo").GetString()).Distinct().Count().ShouldBe(2);
        rows.Select(row => row.GetProperty("repo").GetString()).ShouldContain("main");
        rows.Select(row => row.GetProperty("repo").GetString()).ShouldContain(MainRepoName);
        TryDelete(main);
    }

    // The navigation half of the same claim, measured where the answer carries the pair the spec names: an
    // envelope that reads a span says WHICH repo's tree the lines came from, and a bare path that both trees
    // hold is refused rather than guessed at. This is also the leg that makes the second store's row honest:
    // --repo main resolves to the parent's directory, not to a second session over the lane's.
    [Fact]
    public async Task Baseline_TheNavigationDoorsNameTheTreeTheyRead_AndABareSharedPathIsRefusedAsAmbiguous()
    {
        var (main, lane) = await IndexedParentAndLane("read-nav");
        var joined = await Run(lane, "worktree", "init", "--no-semantic", "--baseline", main, "--json");
        joined.ExitCode.ShouldBe(0, joined.StdErr);
        var storesAfterBaseline = StoreKeys();

        var fromParent = await Run(lane, "expand", "notes.md", "2", "2", "--repo", "main", "--json");
        var fromBranch = await Run(lane, "expand", "notes.md", "2", "2", "--repo", MainRepoName, "--json");
        var ambiguous = await Run(lane, "expand", "notes.md", "2", "2", "--json");

        fromParent.ExitCode.ShouldBe(0, fromParent.StdErr);
        using (var parentDoc = JsonDocument.Parse(fromParent.StdOut.Trim()))
        {
            parentDoc.RootElement.GetProperty("repo").GetString().ShouldBe("main");
            parentDoc.RootElement.GetProperty("span")[0].GetString()!.ShouldContain(ParentToken);
        }

        fromBranch.ExitCode.ShouldBe(0, fromBranch.StdErr);
        using (var branchDoc = JsonDocument.Parse(fromBranch.StdOut.Trim()))
        {
            branchDoc.RootElement.GetProperty("repo").GetString().ShouldBe(MainRepoName);
            branchDoc.RootElement.GetProperty("span")[0].GetString()!.ShouldContain(BranchToken);
        }

        ambiguous.ExitCode.ShouldBe(1, "one relative path in two trees is not a door's coin flip");
        ambiguous.StdErr.ShouldContain($"{MainRepoName}:notes.md");
        ambiguous.StdErr.ShouldContain("main:notes.md");
        // One store, two trees: the baseline added a repo, never a second workspace, session or store dir.
        StoreKeys().ShouldBe(storesAfterBaseline, "--baseline registers a repo, it does not mint a store");
        TryDelete(main);
    }

    [Fact]
    public async Task Resolve_OnAGoneWorktreeTree_ExitsOneWithTheFactsTheDropReports_AndTouchesNothing()
    {
        var (main, lane) = await IndexedParentAndLane("read-gone");
        var storeKey = StoreKeyOf(lane);
        Directory.Delete(lane, recursive: true);
        var registryBefore = RegistryText();
        var storesBefore = StoreKeys();

        var run = await Run(main, "worktree", "resolve", "--json", lane);

        run.ExitCode.ShouldBe(1, "a gone tree is a refusal, not a report");
        using var doc = JsonDocument.Parse(run.StdOut.Trim());
        var payload = doc.RootElement;
        payload.GetProperty("state").GetString().ShouldBe("Gone");
        payload.GetProperty("storeKeys").EnumerateArray().Select(key => key.GetString())
            .ShouldContain(storeKey, "the rows that outlived the tree are named as data");
        var gone = payload.GetProperty("gone");
        gone.GetProperty("physicalPath").GetString().ShouldBe(WorkspaceCanonicalPath.ResolveThroughLinks(lane));
        var goneRow = gone.GetProperty("rows").EnumerateArray().Single();
        goneRow.GetProperty("storeKey").GetString().ShouldBe(storeKey);
        goneRow.GetProperty("rootPath").GetString().ShouldBe(WorkspaceCanonicalPath.ResolveThroughLinks(lane));
        var error = ErrorOf(run.StdErr);
        error.GetProperty("code").GetString().ShouldBe(WorkspaceException.WorktreeNotRegisteredCode);
        error.GetProperty("message").GetString()!.ShouldContain("reads and writes nothing");
        RegistryText().ShouldBe(registryBefore, "AC5: no registry row touched");
        StoreKeys().ShouldBe(storesBefore, "AC5: no dir minted and none removed");
        TryDelete(main);
    }

    [Fact]
    public async Task Resolve_OnAFilledPendingLane_SaysPendingFromTheSharedDoor_AndNeverNamesRepairAsIt()
    {
        var (main, lane) = await IndexedParentAndLane("read-pending");
        // The state a killed pass leaves: rows copied, proof never closed. Reached through the store's own door
        // and stopped there, so this answer is read from a record a real fill wrote.
        var manifest = WorkspaceManifestFile.Load(lane);
        manifest.Reconcile.ShouldNotBeNull().PassCompleted.ShouldBeTrue("the premise: init closed it");
        WorkspaceManifestFile.WriteReconcileAt(SqueezrPaths.ManifestPath(lane), lane,
            manifest.Reconcile.ClosedPass(
                passCompleted: false, semanticArmLanded: null, obligationChunks: 0, coveredChunks: null,
                code: null));
        WorkspaceManifestFile.Load(lane).Reconcile!.Pending.ShouldBeTrue("the premise: an open record");

        var run = await Run(lane, "worktree", "resolve", "--json");

        run.ExitCode.ShouldBe(0, run.StdErr);
        using var doc = JsonDocument.Parse(run.StdOut.Trim());
        var row = doc.RootElement.GetProperty("rows").EnumerateArray().Single();
        row.GetProperty("record").GetProperty("passCompleted").GetBoolean().ShouldBeFalse();
        doc.RootElement.GetProperty("reconcilePending").GetBoolean().ShouldBeTrue();
        var notices = doc.RootElement.GetProperty("notices").EnumerateArray()
            .Select(line => line.GetString()!).ToList();
        notices.ShouldNotBeEmpty("a lane whose coordinates are copied says so");
        notices.ShouldAllBe(line => line.Contains("reconcile pending", StringComparison.Ordinal),
            "the wording is T7's door's, not a second vocabulary invented for this verb");
        run.StdErr.Contains("reconcile pending").ShouldBeTrue(
            "and the hook that reads only stderr still hears it");
        notices.ShouldAllBe(line => !line.Contains(RemedyText.WorkspaceRepairCommand, StringComparison.Ordinal),
            "D8: a registry sweep never opens a meta.db, so repair may not be pitched as this door");
        TryDelete(main);
    }

    [Fact]
    public async Task Baseline_HumanRun_PrintsTheCostLineBeforeTheReconcileLineThatPaysIt()
    {
        var (main, lane) = await IndexedParentAndLane("read-order");

        var run = await Run(lane, "worktree", "init", "--no-semantic", "--baseline", main);

        run.ExitCode.ShouldBe(0, run.StdErr);
        var cost = run.StdOut.IndexOf("baseline:", StringComparison.Ordinal);
        var pass = run.StdOut.IndexOf("reconcile", StringComparison.Ordinal);
        cost.ShouldBeGreaterThanOrEqualTo(0, "the notice went to stdout in human mode");
        pass.ShouldBeGreaterThanOrEqualTo(0, "the pass reported its own verdict on the same stream");
        (cost < pass).ShouldBeTrue(
            "the constraint is an ordering: the price is named before the pass that pays it runs");
        TryDelete(main);
    }

    [Fact]
    public async Task Baseline_WithNoIndexedParent_ReportsSkippedAndRegistersNoRepoRow()
    {
        var main = TestDirs.NewTempDir();
        TestGitRepo.Init(main);
        File.WriteAllText(Path.Combine(main, "seed.txt"), "seed\n");
        TestGitRepo.Add(main, "seed.txt");
        TestGitRepo.Commit(main, "seed");
        var lane = AddLane(main, "feat/116-read-skip");

        var run = await Run(lane, "worktree", "init", "--no-semantic", "--baseline", main, "--json");

        run.ExitCode.ShouldBe(0, run.StdErr);
        run.StdErr.Contains("created no store").ShouldBeTrue(
            "the flag was asked and could not be honoured, which is said out loud");
        using var doc = JsonDocument.Parse(run.StdOut.Trim());
        doc.RootElement.GetProperty("baseline").GetProperty("state").GetString().ShouldBe("Skipped");
        doc.RootElement.GetProperty("state").GetString().ShouldBe("ParentNotIndexed");
        TryDelete(main);
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> Run(
        string workingDir, params string[] args) => await CliTestProcess.RunAsync(workingDir, args);

    // A registered, indexed parent with one lane of its own, and the two trees made to differ by content the
    // other cannot answer with. The lane is checked out BESIDE the parent, not under it: a nested lane would be
    // indexed by the parent's repo too, and then a baseline row would not be measurable as a second tree.
    private static async Task<(string Main, string Lane)> IndexedParentAndLane(string name)
    {
        var (main, lane) = await ParentAndUnregisteredLane(name);
        var registered = await Run(lane, "worktree", "init", "--no-semantic", "--json");
        registered.ExitCode.ShouldBe(0, registered.StdErr);
        return (main, lane);
    }

    private static async Task<(string Main, string Lane)> ParentAndUnregisteredLane(string name)
    {
        var main = TestDirs.NewTempDir();
        TestGitRepo.Init(main);
        File.WriteAllText(Path.Combine(main, "notes.md"), $"shared {SharedToken} line\n");
        TestGitRepo.Add(main, "notes.md");
        TestGitRepo.Commit(main, "seed");
        var lane = AddLane(main, $"feat/116-{name}");
        // The branch changes its own copy and adds its own path; the parent gets a file the branch will never
        // hold. Neither tree can answer for the other's content, which is what every selector claim below needs.
        File.WriteAllText(Path.Combine(lane, "notes.md"), $"shared {SharedToken} line\n{BranchToken}\n");
        File.WriteAllText(Path.Combine(lane, "BranchNote.md"), $"{BranchToken} here\n");
        File.WriteAllText(Path.Combine(main, "notes.md"), $"shared {SharedToken} line\n{ParentToken}\n");
        File.WriteAllText(Path.Combine(main, "ParentNote.md"), $"{ParentToken} here\n");
        (await Run(main, "init", "--name", name, "--json")).ExitCode.ShouldBe(0);
        (await Run(main, "add", ".", "--name", MainRepoName, "--json")).ExitCode.ShouldBe(0);
        var index = await Run(main, "index", "--no-semantic", "--json");
        index.ExitCode.ShouldBe(0, index.StdErr);
        return (main, lane);
    }

    private static string AddLane(string main, string branch)
    {
        var holder = TestDirs.NewTempDir();
        var lane = Path.Combine(holder, "lane-a");
        TestGitRepo.AddWorktree(main, lane, branch);
        return lane;
    }

    private static List<JsonElement> Matches(string stdOut) =>
        stdOut.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line))
            .Where(doc => doc.RootElement.GetProperty("type").GetString() == "match")
            .Select(doc => doc.RootElement.Clone())
            .ToList();

    private static int SummaryCount(string stdOut) =>
        stdOut.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line))
            .Single(doc => doc.RootElement.GetProperty("type").GetString() == "summary")
            .RootElement.GetProperty("matches").GetInt32();

    private static JsonElement ErrorOf(string stdErr)
    {
        var lines = stdErr.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using var doc = JsonDocument.Parse(lines[^1]);
        return doc.RootElement.GetProperty("error").Clone();
    }

    private static string StoreKeyOf(string root) =>
        WorkspaceRegistry.FindByRootPath(WorkspaceCanonicalPath.ResolveThroughLinks(root))!.StoreKey;

    private static string RegistryText() =>
        File.Exists(WorkspaceRegistry.FilePath) ? File.ReadAllText(WorkspaceRegistry.FilePath) : "";

    private static string[] StoreKeys() =>
        Directory.Exists(SqueezrHome.WorkspacesDir)
            ? [.. Directory.GetDirectories(SqueezrHome.WorkspacesDir)
                .Select(dir => new DirectoryInfo(dir).Name)
                .OrderBy(name => name, StringComparer.Ordinal)]
            : [];

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
