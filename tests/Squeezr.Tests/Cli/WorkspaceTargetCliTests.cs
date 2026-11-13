using System.Text.Json;
using Shouldly;
using Squeezr.Cli.Mcp;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Tests.Indexing.Ignore;

namespace Squeezr.Tests.Cli;

// The store axis measured at the door an agent actually calls: which tree answered, what the answer said about
// itself, and what the run left (never) behind. The in-process classes measure the decision; this one measures
// the contract, because "this answer cannot be mistaken for the tree I am standing in" is a statement about a
// process that ran, not about a function that returned.
//
// The class never installs its own SQUEEZR_HOME: CliTestProcess captures its run home once, and a class that
// pins a private home before the first spawn poisons every later child in the run.
[Collection("ProcessGlobals")]
public sealed class WorkspaceTargetCliTests
{
    private const string MainRepoName = "demo";
    private const string BaselineRepoName = "main";
    private const string ParentToken = "PARENTONLYTOKEN";
    private const string BranchToken = "BRANCHONLYTOKEN";

    // AC1, first half: the sugar answers from the parent store, and the answer says so in three places — the
    // bytes it read, the root and branch it names, and the warning that the named tree is not the caller's.
    [Fact]
    public async Task AtParent_ReadsTheParentTree_AndNamesRootBranchAndWarnsTheCross()
    {
        var (main, lane) = await IndexedParentAndFilledLane("target-parent");

        var run = await Run(lane, "expand", "notes.md", "2", "2", "--at", "parent", "--json");

        run.ExitCode.ShouldBe(0, run.StdErr);
        using var doc = JsonDocument.Parse(run.StdOut.Trim());
        doc.RootElement.GetProperty("repo").GetString().ShouldBe(MainRepoName);
        doc.RootElement.GetProperty("span")[0].GetString()!.ShouldContain(ParentToken);

        Says(run, $"subject: {SessionLocation.Display(main)}", "the answer names the root it came from");
        Says(run, $"branch {HeadBranch(main)}", "and the branch of the ANSWERING store, not the caller's");
        Says(run, "cross-tree", "and says the ask crossed a tree");
        Says(run, $", not '{SessionLocation.Display(lane)}'",
            "naming the checkout the query was asked in, so the reader can tell which side it stands on");
        TryDelete(main);
    }

    // AC1, second half: with no flag at all the lane's own store answers, and the answer names the CHILD — a
    // lane's coordinates are branch coordinates, and that is a fact no row in the answer carries by itself.
    [Fact]
    public async Task NoFlag_InAFilledLane_AnswersTheBranch_AndNamesTheChild()
    {
        var (main, lane) = await IndexedParentAndFilledLane("target-child");
        var laneKey = StoreKeyOf(lane);

        var run = await Run(lane, "expand", "notes.md", "2", "2", "--json");

        run.ExitCode.ShouldBe(0, run.StdErr);
        using var doc = JsonDocument.Parse(run.StdOut.Trim());
        doc.RootElement.GetProperty("span")[0].GetString()!.ShouldContain(BranchToken);
        Says(run, $"subject: {SessionLocation.Display(lane)}", "the child is named by its own root");
        Says(run, "feat/116-target-child", "and by the branch the lane is checked out on");
        Says(run, laneKey, "and by the key a follow-up query would quote back");
        DoesNotSay(run, "cross-tree", "and nothing crossed: the lane answered for itself");
        TryDelete(main);
    }

    // AC3's CLI twin: one tree in play and the caller stands inside it, so the answer owes no naming at all.
    [Fact]
    public async Task SingleRoot_MainWorktreeAnswer_AddsNoSubjectLine()
    {
        var (main, _) = await IndexedParentAndFilledLane("target-plain");

        var run = await Run(main, "search", ParentToken, "--json");

        run.ExitCode.ShouldBe(0, run.StdErr);
        Matches(run.StdOut).ShouldNotBeEmpty();
        DoesNotSay(run, "subject:", "a single-root answer adds nothing an old reader has to learn to skip");
        DoesNotSay(run, "cross-tree");
        TryDelete(main);
    }

    // AC2: a key nobody registered is a refusal that names the door that would have made the store, and it
    // answers nothing — the caller's own tree is not a fallback for a target that does not exist.
    [Theory]
    [InlineData("nope-12345678")]
    [InlineData("squeezr-aaaaaaaa")]
    public async Task Workspace_WithAnUnregisteredKey_RefusesWithTheRemedy_AndAnswersNothing(string storeKey)
    {
        var (main, lane) = await IndexedParentAndFilledLane("target-key");

        var run = await Run(lane, "search", BranchToken, "--workspace", storeKey, "--json");

        run.ExitCode.ShouldBe(1, "a store that does not exist is not an empty answer");
        var error = ErrorOf(run.StdErr);
        error.GetProperty("code").GetString().ShouldBe(WorkspaceException.WorktreeNotRegisteredCode);
        error.GetProperty("message").GetString()!.ShouldContain(storeKey);
        error.GetProperty("message").GetString()!.ShouldContain(RemedyText.WorkspaceListCommand);
        run.StdOut.ShouldNotContain(BranchToken);
        TryDelete(main);
    }

    [Fact]
    public async Task Workspace_ByStoreKey_AnswersThatStore_AndNamesIt()
    {
        var (main, lane) = await IndexedParentAndFilledLane("target-bykey");
        var parentKey = StoreKeyOf(main);

        var run = await Run(lane, "search", ParentToken, "--workspace", parentKey, "--json");

        run.ExitCode.ShouldBe(0, run.StdErr);
        Matches(run.StdOut).ShouldNotBeEmpty();
        Says(run, parentKey, "the key the caller spelled comes back as the store that answered");
        Says(run, $"subject: {SessionLocation.Display(main)}");
        TryDelete(main);
    }

    // The path spelling walks through the read door T9 shipped and comes back with THAT door's vocabulary: a lane
    // git knows and the registry does not is Unregistered with the registration remedy, never a minted row.
    [Fact]
    public async Task Workspace_ByPathOfAnUnregisteredLane_GivesTheReadDoorsOwnRefusal_AndCreatesNothing()
    {
        var (main, lane) = await IndexedParentAndUnregisteredLane("target-path");
        var registryBefore = RegistryText();
        var storesBefore = StoreKeys();

        var run = await Run(main, "search", BranchToken, "--workspace", lane, "--json");

        run.ExitCode.ShouldBe(1);
        var error = ErrorOf(run.StdErr);
        error.GetProperty("code").GetString().ShouldBe(WorkspaceException.WorktreeNotRegisteredCode);
        error.GetProperty("message").GetString()!.ShouldContain(RemedyText.WorktreeInitCommand);
        RegistryText().ShouldBe(registryBefore, "a targeting query registers nothing on the way to an answer");
        StoreKeys().ShouldBe(storesBefore, "and mints no store directory");
        TryDelete(main);
    }

    [Fact]
    public async Task Workspace_APathInsideAStoreRoot_AnswersThatStore()
    {
        var (main, lane) = await IndexedParentAndFilledLane("target-subpath");

        var run = await Run(lane, "search", ParentToken, "--workspace", Path.Combine(main, "sub"), "--json");

        run.ExitCode.ShouldBe(0, run.StdErr);
        Matches(run.StdOut).ShouldNotBeEmpty();
        TryDelete(main);
    }

    // One dead tree, three spellings of it. The lane's checkout is gone while its row survives, and the state
    // used to split in two: the key door handed the dead root to the query engine, which answered with an
    // unremedied workspace_error, while the path door spoke the read verb's gone sentence with the surviving
    // key and both reap remedies. Targeting now goes through the read door whichever way it was asked, so the
    // refusal is one message and the naming never fires for a run that produced no answer.
    [Fact]
    public async Task WorkspaceOfADeletedLane_RefusesTheGoneState_TheSameWordsFromEverySpelling()
    {
        var (main, lane) = await IndexedParentAndFilledLane("target-dead");
        var laneKey = StoreKeyOf(lane);
        var laneRoot = WorkspaceRegistry.FindByStoreKey(laneKey)!.RootPath;
        var registryBefore = RegistryText();
        TryDelete(lane);

        var byKey = await Run(main, "search", ParentToken, "--workspace", laneKey, "--json");
        var byWorkspacePath = await Run(main, "search", ParentToken, "--workspace", laneRoot, "--json");
        var byAt = await Run(main, "search", ParentToken, "--at", laneRoot, "--json");

        var gone = ErrorOf(byAt.StdErr).GetProperty("message").GetString();
        foreach (var run in new[] { byKey, byWorkspacePath, byAt })
        {
            run.ExitCode.ShouldBe(1, "a tree nothing can enter is not an empty answer");
            var error = ErrorOf(run.StdErr);
            error.GetProperty("code").GetString().ShouldBe(WorkspaceException.WorktreeNotRegisteredCode);
            var message = error.GetProperty("message").GetString()!;
            message.ShouldContain("does not exist");
            message.ShouldContain(laneKey);
            message.ShouldContain(RemedyText.WorkspaceRepairCommand);
            message.ShouldContain(RemedyText.DropCommand);
            DoesNotSay(run, "subject:", "a run that answered nothing has no subject to name");
            DoesNotSay(run, "cross-tree", "and no tree it crossed");
            run.StdOut.ShouldNotContain(ParentToken);
        }

        ErrorOf(byKey.StdErr).GetProperty("message").GetString().ShouldBe(gone);
        ErrorOf(byWorkspacePath.StdErr).GetProperty("message").GetString().ShouldBe(gone);
        RegistryText().ShouldBe(registryBefore, "the targeting door reaps nothing; that is workspace repair's work");
        TryDelete(main);
    }

    // An option with no value selects no store. Before this refusal was pinned both spellings fell through to
    // the nearest store and exited 0, which makes an unfilled flag mean the opposite of a missing one.
    [Theory]
    [InlineData("--workspace", "")]
    [InlineData("--at", "")]
    [InlineData("--workspace", "   ")]
    [InlineData("--at", " ")]
    public async Task SelectorOptionWithNoValue_RefusesAsInvalidQuery_AndAnswersNothing(
        string option, string value)
    {
        var (main, lane) = await IndexedParentAndFilledLane("target-empty");

        var run = await Run(lane, "search", BranchToken, option, CommandLineValue(value), "--json");

        run.ExitCode.ShouldBe(1, "an unfilled option must not quietly mean the nearest store");
        var error = ErrorOf(run.StdErr);
        error.GetProperty("code").GetString().ShouldBe(WorkspaceException.InvalidQueryCode);
        var message = error.GetProperty("message").GetString()!;
        message.ShouldContain(option);
        // The remedy half of the sentence has to point at a door the option actually has: `--at' reads a path or
        // the parent sugar and takes no store key, so offering one here is the impossible-remedy defect again.
        message.ShouldContain(option == "--at" ? "'parent'" : "store key");
        message.ShouldNotContain(option == "--at" ? "registered store key" : "'parent' for this checkout");
        run.StdOut.ShouldNotContain(BranchToken);
        DoesNotSay(run, "subject:");
        TryDelete(main);
    }

    // A targeted query reads and reads: the witness is that the registry bytes and the store listing are what
    // they were, the same property that makes `worktree resolve' safe to call twice a day from a hook. BOTH
    // spellings are witnessed, because each reaches the registry through a different door — the key looks its row
    // up directly, the sugar walks git first — and a write hidden in either one would otherwise stay unmeasured.
    [Fact]
    public async Task TargetedQuery_ReadsOnly_AndLeavesTheRegistryAlone()
    {
        var (main, lane) = await IndexedParentAndFilledLane("target-readonly");
        var parentKey = StoreKeyOf(main);
        var registryBefore = RegistryText();
        var storesBefore = StoreKeys();

        var throughGit = await Run(lane, "symbol", "notes.md", "--at", "parent", "--json");
        var throughKey = await Run(lane, "symbol", "notes.md", "--workspace", parentKey, "--json");

        throughGit.ExitCode.ShouldBe(0, throughGit.StdErr);
        throughKey.ExitCode.ShouldBe(0, throughKey.StdErr);
        RegistryText().ShouldBe(registryBefore,
            "neither spelling stamped LastUsedAt, rewrote a row or touched the registry file");
        StoreKeys().ShouldBe(storesBefore);
        TryDelete(main);
    }

    // The axis pin the constraint demands. The store selector decides WHICH store answers, and `--repo` filters
    // the repos INSIDE that store — it never points the query back at the caller's tree. `main' is the baseline
    // row of the LANE store, so under --at parent it is a repo the answering store does not own: a named
    // refusal, not a silent fall back.
    [Fact]
    public async Task AtParent_AndRepoFilter_TheRepoAxisRunsInsideTheSelectedStore()
    {
        var (main, lane) = await IndexedParentAndFilledLane("target-axis");
        var joined = await Run(lane, "worktree", "init", "--no-semantic", "--baseline", main, "--json");
        joined.ExitCode.ShouldBe(0, joined.StdErr);

        var insideLaneStore = await Run(lane, "search", ParentToken, "--repo", BaselineRepoName, "--json");
        var parentOwnRepo = await Run(lane, "search", ParentToken, "--at", "parent", "--repo", MainRepoName, "--json");
        var foreignRepoUnderParent = await Run(lane, "expand", "notes.md", "2", "2",
            "--at", "parent", "--repo", BaselineRepoName, "--json");
        var foreignRepoUnderParentSearch = await Run(lane, "search", ParentToken,
            "--at", "parent", "--repo", BaselineRepoName, "--json");

        Matches(insideLaneStore.StdOut).ShouldNotBeEmpty();
        Matches(parentOwnRepo.StdOut).ShouldNotBeEmpty();
        foreignRepoUnderParent.ExitCode.ShouldBe(1,
            "'main' lives in the lane store, and the parent store the query was pointed at does not own it");
        ErrorOf(foreignRepoUnderParent.StdErr).GetProperty("message").GetString()!
            .ShouldContain($"'{BaselineRepoName}'");
        Says(foreignRepoUnderParent, "subject:",
            "and the refusal still names the store that was asked, so the reader is not sent hunting");
        Matches(foreignRepoUnderParentSearch.StdOut).ShouldBeEmpty(
            "the same filter on the search door answers with zero rows: the filter ran inside the selected store " +
            "and that store holds no such repo — it never reached back into the caller's own tree");
        TryDelete(main);
    }

    [Fact]
    public async Task AtParent_OnAMainWorktree_RefusesNamingNoParent()
    {
        var (main, _) = await IndexedParentAndFilledLane("target-noparent");

        var run = await Run(main, "map", "--at", "parent", "--json");

        run.ExitCode.ShouldBe(1);
        var error = ErrorOf(run.StdErr);
        error.GetProperty("code").GetString().ShouldBe(WorkspaceException.NotAWorktreeCode);
        error.GetProperty("message").GetString()!.ShouldContain("--workspace");
        TryDelete(main);
    }

    [Fact]
    public async Task AtParent_WhenTheParentOwnsNoStore_RefusesNamingTheStepMissing()
    {
        var main = TestDirs.NewTempDir();
        TestGitRepo.Init(main);
        File.WriteAllText(Path.Combine(main, "seed.txt"), "seed\n");
        TestGitRepo.Add(main, "seed.txt");
        TestGitRepo.Commit(main, "seed");
        var holder = TestDirs.NewTempDir();
        var lane = Path.Combine(holder, "lane-a");
        TestGitRepo.AddWorktree(main, lane, "feat/116-target-unindexed");

        var run = await Run(lane, "refs", "Program", "--at", "parent", "--json");

        run.ExitCode.ShouldBe(1);
        var error = ErrorOf(run.StdErr);
        error.GetProperty("code").GetString().ShouldBe(WorkspaceException.WorktreeNotRegisteredCode);
        error.GetProperty("message").GetString()!.ShouldContain(RemedyText.InitCommand);
        TryDelete(main);
        TryDelete(holder);
    }

    // Two spellings of one axis is a contradiction, and the verb says so rather than picking a favourite.
    [Fact]
    public async Task BothSelectorsSpelled_RefusesAsOneAxis()
    {
        var (main, lane) = await IndexedParentAndFilledLane("target-both");

        var run = await Run(lane, "search", BranchToken,
            "--workspace", StoreKeyOf(main), "--at", "parent", "--json");

        run.ExitCode.ShouldBe(1);
        ErrorOf(run.StdErr).GetProperty("code").GetString().ShouldBe(WorkspaceException.InvalidQueryCode);
        TryDelete(main);
    }

    // The sugar and the explicit key must not become two answers for one store: the naming is one door, so the
    // same tree reports the same subject line whichever way the query reached it.
    [Fact]
    public async Task AtParent_AndWorkspaceKey_NameOneStore_ThroughEitherSpelling()
    {
        var (main, lane) = await IndexedParentAndFilledLane("target-parity");
        var parentKey = StoreKeyOf(main);

        var sugar = await Run(lane, "search", ParentToken, "--at", "parent", "--json");
        var explicitKey = await Run(lane, "search", ParentToken, "--workspace", parentKey, "--json");

        sugar.ExitCode.ShouldBe(0, sugar.StdErr);
        explicitKey.ExitCode.ShouldBe(0, explicitKey.StdErr);
        SubjectOf(sugar.StdErr).ShouldBe(SubjectOf(explicitKey.StdErr));
        Says(sugar, "cross-tree");
        Says(explicitKey, "cross-tree");
        TryDelete(main);
    }

    private static async Task<(string Main, string Lane)> IndexedParentAndFilledLane(string name)
    {
        var (main, lane) = await IndexedParentAndUnregisteredLane(name);
        var registered = await Run(lane, "worktree", "init", "--no-semantic", "--json");
        registered.ExitCode.ShouldBe(0, registered.StdErr);
        return (main, lane);
    }

    private static async Task<(string Main, string Lane)> IndexedParentAndUnregisteredLane(string name)
    {
        var main = TestDirs.NewTempDir();
        TestGitRepo.Init(main);
        File.WriteAllText(Path.Combine(main, "notes.md"), $"shared line\n{ParentToken}\n");
        Directory.CreateDirectory(Path.Combine(main, "sub"));
        File.WriteAllText(Path.Combine(main, "sub", "keep.md"), "keep\n");
        TestGitRepo.Add(main, "notes.md", "sub/keep.md");
        TestGitRepo.Commit(main, "seed");
        var holder = TestDirs.NewTempDir();
        var lane = Path.Combine(holder, "lane-a");
        TestGitRepo.AddWorktree(main, lane, $"feat/116-{name}");
        File.WriteAllText(Path.Combine(lane, "notes.md"), $"shared line\n{BranchToken}\n");
        (await Run(main, "init", "--name", name, "--json")).ExitCode.ShouldBe(0);
        (await Run(main, "add", ".", "--name", MainRepoName, "--json")).ExitCode.ShouldBe(0);
        var index = await Run(main, "index", "--no-semantic", "--json");
        index.ExitCode.ShouldBe(0, index.StdErr);
        return (main, lane);
    }

    private static Task<(int ExitCode, string StdOut, string StdErr)> Run(
        string workingDir, params string[] args) => CliTestProcess.RunAsync(workingDir, args);

    // CliTestProcess joins its args into one command line, so a value that must reach the child EMPTY cannot
    // be passed as an empty string — it would vanish and leave the child reading `--workspace --json', which is
    // a different run than the one being pinned. The quoted token is what the shell-equivalent parser turns
    // back into an empty argument.
    private static string CommandLineValue(string value) => $"\"{value}\"";

    // The naming travels on the warning channel, so every claim about it is a claim about stderr — and a failure
    // has to say what went missing with the run's own text next to it.
    private static void Says(
        (int ExitCode, string StdOut, string StdErr) run, string needle, string because = "")
    {
        if (!run.StdErr.Contains(needle, StringComparison.Ordinal))
        {
            throw new ShouldAssertException(
                $"stderr should contain \"{needle}\"{(because.Length > 0 ? $" — {because}" : string.Empty)}." +
                $" Actual stderr:\n{run.StdErr}");
        }
    }

    private static void DoesNotSay(
        (int ExitCode, string StdOut, string StdErr) run, string needle, string because = "")
    {
        if (run.StdErr.Contains(needle, StringComparison.Ordinal))
        {
            throw new ShouldAssertException(
                $"stderr should not contain \"{needle}\"{(because.Length > 0 ? $" — {because}" : string.Empty)}." +
                $" Actual stderr:\n{run.StdErr}");
        }
    }

    private static List<JsonElement> Matches(string stdOut) =>
        stdOut.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line))
            .Where(doc => doc.RootElement.GetProperty("type").GetString() == "match")
            .Select(doc => doc.RootElement.Clone())
            .ToList();

    private static JsonElement ErrorOf(string stdErr)
    {
        var lines = stdErr.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using var doc = JsonDocument.Parse(lines[^1]);
        return doc.RootElement.GetProperty("error").Clone();
    }

    private static string SubjectOf(string stdErr) =>
        stdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.StartsWith("subject:", StringComparison.Ordinal)).Trim();

    // The parent's branch is whatever `git init' chose on this box. The claim is that the answer names the
    // ANSWERING store's branch, so the test reads the file git reads instead of hard-coding a name.
    private static string HeadBranch(string root) =>
        File.ReadAllText(Path.Combine(root, ".git", "HEAD")).Trim()
            .Replace("ref: refs/heads/", string.Empty);

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
