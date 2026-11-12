using System.Text.Json;
using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Tests.Indexing.Ignore;
using Squeezr.Tests.Storage;

namespace Squeezr.Tests.Cli;

// The copy half of the verb, measured through the CLI. Two pins here are the gate conditions review-116-06
// set for this task: a filled lane must ANSWER before any indexing pass ran here (bytes nobody can query
// are not an index), and a fill that cannot complete must leave the lane fillable by a later session rather
// than stranded until a manual drop. Parents seeded with pending rows need no provider anywhere in the
// room; the two cases that must name a model pin the lane's config on disk instead.
[Collection("ProcessGlobals")]
public sealed class WorktreeInitCopyCliTests
{
    private const string MainRepoName = StoreFixture.RepoName;

    [Fact]
    public async Task WorktreeInit_DefaultCopy_ReportsThreeNumbersPerFileAndTheChildAnswersWithThem()
    {
        var main = IndexedMainWithPendingContent("cli-copy-main");
        var child = AddWorktree(main, "feat/116-cli-copy");
        // The lane's config names a provider that is not in the room, so the reconcile pass this verb now runs
        // stays structural: no inference call, no collection, and the pending it reports is the embedding
        // queue the copied rows left behind.
        PreMintLaneStore(child, "feat/116-cli-copy", provider: "ollama", model: null, probeFails: true);

        var run = await Run(child, "worktree", "init", "--json");

        run.ExitCode.ShouldBe(0);
        using var doc = JsonDocument.Parse(run.StdOut.Trim());
        var payload = doc.RootElement;
        payload.GetProperty("state").GetString().ShouldBe("Registered");
        var storeKey = payload.GetProperty("storeKey").GetString()!;
        var fill = payload.GetProperty("fill");
        fill.GetProperty("state").GetString().ShouldBe("Filled");
        var copied = fill.GetProperty("copied");
        var database = copied.GetProperty("database");
        database.GetProperty("sourceFiles").GetInt32().ShouldBe(3);
        database.GetProperty("copiedFiles").GetInt32().ShouldBe(3);
        database.GetProperty("targetFiles").GetInt32().ShouldBe(3);
        database.GetProperty("sourceChunks").GetInt32().ShouldBe(6);
        database.GetProperty("copiedChunks").GetInt32().ShouldBe(6);
        database.GetProperty("targetChunks").GetInt32().ShouldBe(6);
        database.GetProperty("sourceBytes").GetInt64().ShouldBeGreaterThan(0);
        database.GetProperty("targetBytes").GetInt64()
            .ShouldBe(database.GetProperty("sourceBytes").GetInt64(),
                "the number named installed describes the file the worktree will open, not the temp it came from");
        var trigram = copied.GetProperty("trigram");
        trigram.GetProperty("copied").GetBoolean().ShouldBeTrue();
        trigram.GetProperty("sourceDocs").GetInt32().ShouldBe(3);
        trigram.GetProperty("targetDocs").GetInt32().ShouldBe(3);
        trigram.GetProperty("targetBytes").GetInt64()
            .ShouldBe(trigram.GetProperty("sourceBytes").GetInt64());
        copied.GetProperty("ownedRepos").EnumerateArray().Select(value => value.GetString())
            .ShouldBe([MainRepoName], "the copied rows are owned by the lane the moment they land");
        copied.GetProperty("unownedRepos").EnumerateArray().ShouldBeEmpty();
        fill.TryGetProperty("vectors", out _).ShouldBeFalse(
            "a parent whose rows are all pending owns no points, so no vector store was asked on its behalf");
        payload.GetProperty("reconcilePending").GetBoolean().ShouldBeTrue(
            "measured pending, not guessed: the copied rows wait for embeddings this pass could not run");

        var childStoreDir = WorkspaceStorePaths.RequireStoreDir(storeKey);
        // The reconcile block is the fill's counterpart: one standard pass over this tree, with its numbers
        // measured off the installed store rather than off the copy. The parent's rows name files this
        // checkout does not hold, so a pass that left them standing would keep the lane answering with
        // another branch's coordinates.
        var reconcile = payload.GetProperty("reconcile");
        reconcile.GetProperty("passRan").GetBoolean().ShouldBeTrue(
            "the verb runs the reconcile pass itself, it no longer points the reader at 'squeezr index'");
        reconcile.GetProperty("state").GetString().ShouldBe("Pending");
        reconcile.GetProperty("code").GetString()
            .ShouldBe(WorktreeReconcileOutcome.RowsPendingCode,
                "no row here claims a vector, so the pending is the embedding queue, not a short collection");
        reconcile.GetProperty("obligation").GetBoolean().ShouldBeFalse(
            "obligation is the store's own claim of embedded rows, and a pending-only copy makes none");
        reconcile.GetProperty("deleted").GetInt32().ShouldBe(3,
            "the three parent-only paths leave the lane's index when this pass runs");
        reconcile.GetProperty("rescannedFiles").GetInt32().ShouldBe(1,
            "the lane keeps the one file its own tree really holds");
        StoreFixture.Counts(childStoreDir).ShouldBe(
            (reconcile.GetProperty("rescannedFiles").GetInt32(), reconcile.GetProperty("storedChunks").GetInt32()),
            "the reconcile numbers are the installed store's numbers, not a wish");
        StoreFixture.TrigramDocs(childStoreDir).ShouldBe(reconcile.GetProperty("rescannedFiles").GetInt32());
        FilesIn(childStoreDir).ShouldBe(["config.json", "meta.db", "trigram.idx", "workspace.json"],
            "a finished fill leaves exactly the files a filled store is made of — no temp, no write-ahead log");
        File.Exists(Path.Combine(childStoreDir, SqueezrPaths.LockFileName)).ShouldBeFalse(
            "both locks the fill takes are released before the verb reports success");
        // the lane root sits under the main root by construction, so what a lane manifest must never carry is
        // an absolute repos path — that is a main-worktree coordinate the lane would answer with
        WorkspaceManifestFile
            .LoadAt(Path.Combine(childStoreDir, SqueezrPaths.StoreManifestFileName))
            .Repos.ShouldAllBe(entry => !Path.IsPathRooted(entry.Path));
        // The T6 constraint in one line: the reconcile pass writes through the identity the copy seeded, it
        // never minters a second repo entry for the lane tree.
        var postPassRepos = WorkspaceManifestFile
            .LoadAt(Path.Combine(childStoreDir, SqueezrPaths.StoreManifestFileName)).Repos.ToList();
        postPassRepos.ShouldHaveSingleItem(
            "one pass over the lane tree must not add a repo entry beside the copied one");
        postPassRepos[0].Name.ShouldBe(MainRepoName, "identity, not cosmetics: the names own the trigram pairs");
        postPassRepos[0].Path.ShouldBe(".");

        var second = await Run(child, "worktree", "init", "--json");

        second.ExitCode.ShouldBe(0);
        using var again = JsonDocument.Parse(second.StdOut.Trim());
        again.RootElement.GetProperty("state").GetString().ShouldBe("AlreadyRegistered");
        again.RootElement.GetProperty("fill").GetProperty("state").GetString().ShouldBe("Skipped",
            "the verb runs at every session start; a default copy that re-filled would bury the lane's own work");
        var repeat = again.RootElement.GetProperty("reconcile");
        repeat.GetProperty("passRan").GetBoolean().ShouldBeFalse(
            "the pass is owed by state, not by this verb having run: a repeat that re-scanned would double the " +
            "work at every session start");
        repeat.GetProperty("state").GetString().ShouldBe("Pending",
            "and the pending is still reported — a pass that skipped its work does not shave the word away");
        repeat.GetProperty("code").GetString().ShouldBe(WorktreeReconcileOutcome.RowsPendingCode,
            "pending is named by code, not by prose, for the pass that has to record it");
        StoreFixture.Counts(childStoreDir).ShouldBe((1, 1));
    }

    // The third state's carriers, measured through the CLI rather than in the core: the payload names the
    // state and keeps the pending word, and the assumption reaches the channel a session-start hook reads.
    // A hook that treats a copied store as proven is the reader this state exists to stop, and it reads
    // stderr, not the reconcile block.
    [Fact]
    public async Task WorktreeInit_SecondPassOverAHealthyLane_ReportsAssumedAndWarnsOnStdErr()
    {
        const string branch = "feat/116-cli-assumed";
        var main = IndexedMainWithPendingContent("cli-assumed-main");
        var child = AddWorktree(main, branch);
        PreMintLaneStore(child, branch, provider: "ollama", model: null, probeFails: true);

        var first = await Run(child, "worktree", "init", "--no-semantic", "--json");

        first.ExitCode.ShouldBe(0, first.StdErr);
        using (var doc = JsonDocument.Parse(first.StdOut.Trim()))
        {
            doc.RootElement.GetProperty("fill").GetProperty("state").GetString().ShouldBe("Filled");
            doc.RootElement.GetProperty("reconcile").GetProperty("state").GetString().ShouldBe("Reconciled",
                "the session that ran the pass over this tree is the one allowed to say reconciled");
        }

        var second = await Run(child, "worktree", "init", "--no-semantic", "--json");

        second.ExitCode.ShouldBe(0, second.StdErr);
        using var again = JsonDocument.Parse(second.StdOut.Trim());
        again.RootElement.GetProperty("fill").GetProperty("state").GetString().ShouldBe("Skipped");
        var repeat = again.RootElement.GetProperty("reconcile");
        repeat.GetProperty("state").GetString().ShouldBe("Assumed");
        repeat.GetProperty("code").GetString().ShouldBe(WorktreeReconcileOutcome.AssumedCode);
        repeat.GetProperty("passRan").GetBoolean().ShouldBeFalse();
        again.RootElement.GetProperty("reconcilePending").GetBoolean().ShouldBeTrue(
            "an assumed match is two copied numbers agreeing, so the reader keeps the pending word");
        // the warning channel is where a hook that reads no payload learns the difference
        second.StdErr.ShouldContain("no pass over this tree");
    }

    // The query pins here run against the lane right after the verb: since T6 the verb itself lands one
    // incremental pass over this tree, so 'copied and silent' is no longer a state a reader can reach by
    // skipping 'squeezr index'. What these pins still prove is that a filled lane answers — status reports
    // files, search names the repo and the path, symbol names the repo — without anyone indexing by hand.
    [Fact]
    public async Task WorktreeInit_FilledLaneAnswersQueriesRightAfterTheVerb()
    {
        var main = IndexedMainOfWorktree("cli-query-main");
        var child = AddWorktree(main, "feat/116-cli-query");
        // A provider that is not in the room keeps the verb's own pass structural: no inference call and no
        // collection are opened in the developer's store by a test run.
        PreMintLaneStore(child, "feat/116-cli-query", provider: "ollama", model: null, probeFails: true);

        var run = await Run(child, "worktree", "init", "--json");

        run.ExitCode.ShouldBe(0);
        string storeKey;
        using (var doc = JsonDocument.Parse(run.StdOut.Trim()))
        {
            doc.RootElement.GetProperty("fill").GetProperty("state").GetString().ShouldBe("Filled");
            storeKey = doc.RootElement.GetProperty("storeKey").GetString()!;
        }

        var status = await Run(child, "status", "--json");

        status.ExitCode.ShouldBe(0);
        using var statusDoc = JsonDocument.Parse(status.StdOut.Trim());
        statusDoc.RootElement.GetProperty("files").GetInt32().ShouldBeGreaterThan(0,
            "a filled lane reports its files, not the empty manifest a copy without seeding leaves behind");
        var current = statusDoc.RootElement.GetProperty("workspaces").EnumerateArray()
            .Single(entry => entry.GetProperty("current").GetBoolean());
        current.GetProperty("rootPath").GetString().ShouldBe(WorkspaceCanonicalPath.ResolveThroughLinks(child));
        current.GetProperty("storeKey").GetString().ShouldBe(storeKey);
        var repoRows = statusDoc.RootElement.GetProperty("repos").EnumerateArray().ToList();
        repoRows.ShouldNotBeEmpty("a filled lane reports the repo its manifest owns");
        var repo = repoRows.Single(entry => entry.GetProperty("name").GetString() == MainRepoName);
        repo.GetProperty("path").GetString().ShouldBe(".");
        repo.GetProperty("files").GetInt32().ShouldBeGreaterThan(0);

        var search = await Run(child, "search", "Greeter", "--json");

        search.ExitCode.ShouldBe(0);
        var hits = SearchMatches(search.StdOut);
        hits.ShouldNotBeEmpty(
            "a copied trigram index plus a lane-owned manifest entry is what makes a marker findable here");
        hits[0].GetProperty("repo").GetString().ShouldBe(MainRepoName);
        hits[0].GetProperty("path").GetString().ShouldBe("Greeter.cs");
        File.Exists(Path.Combine(child, "Greeter.cs")).ShouldBeTrue("the answer points at a file this tree holds");

        var symbol = await Run(child, "symbol", "Greeter", "--json");

        symbol.ExitCode.ShouldBe(0);
        using var symbolDoc = JsonDocument.Parse(symbol.StdOut.Trim());
        var matches = symbolDoc.RootElement.GetProperty("matches").EnumerateArray().ToList();
        matches.ShouldNotBeEmpty("a lane that cannot name its own repo is the symptom this pin refuses");
        matches[0].GetProperty("repo").GetString().ShouldBe(MainRepoName);

        // review-116-06 #3: the verb prints 'run squeezr index here', so the pass has to run in a filled
        // lane — a remedy sentence that errors is worse than no remedy at all.
        var index = await Run(child, "index", "--no-semantic", "--json");

        index.ExitCode.ShouldBe(0);
        // the pass runs against the repo this lane's manifest now owns
        index.StdOut.ShouldContain(MainRepoName);
    }

    // BLOCKER #2's pin at the CLI level: a provider that cannot be named is a session that reports a skip
    // and leaves the work finishable, not an exit 1 over a store nothing will fill again without a drop.
    [Fact]
    public async Task WorktreeInit_ModelUnresolvableOnFirstPass_SkipsAtExitZeroAndALaterPassFills()
    {
        const string branch = "feat/116-cli-strand";
        const string modelKey = "ollama/cli-strand/768";
        var main = IndexedMainWithEmbeddedRows("cli-strand-main", modelKey);
        var child = AddWorktree(main, branch);
        var storeDir = PreMintLaneStore(child, branch, provider: "auto", model: null, probeFails: true);

        var first = await Run(child, "worktree", "init", "--json");

        first.ExitCode.ShouldBe(0,
            "the failure is on the ordinary session-start path — a hook that reruns this must not see an error");
        using (var doc = JsonDocument.Parse(first.StdOut.Trim()))
        {
            var fill = doc.RootElement.GetProperty("fill");
            fill.GetProperty("state").GetString().ShouldBe("Skipped");
            fill.GetProperty("reason").GetString()!.ShouldContain("could not be named");
            fill.TryGetProperty("copied", out _).ShouldBeFalse();
            doc.RootElement.GetProperty("storeKey").GetString()
                .ShouldBe(StoreKeyOfBranch(child, branch), "the mint stands — the fill is what was not done");
        }

        StoreFixture.Counts(storeDir).ShouldBe((0, 0), "a skipped fill moves no bytes");
        File.Exists(Path.Combine(storeDir, SqueezrPaths.TrigramIndexFileName)).ShouldBeFalse();

        // the next session: the store's own config names a provider, so no probe is needed and the same verb
        // completes what the first pass could not — no drop, no manual repair
        WriteLaneConfig(storeDir, provider: "ollama", model: "cli-strand");

        var second = await Run(child, "worktree", "init", "--no-semantic", "--json");

        second.ExitCode.ShouldBe(0);
        using var filledDoc = JsonDocument.Parse(second.StdOut.Trim());
        filledDoc.RootElement.GetProperty("state").GetString().ShouldBe("AlreadyRegistered");
        filledDoc.RootElement.GetProperty("fill").GetProperty("state").GetString().ShouldBe("Filled");

        // The reconcile numbers are the installed store's numbers: the three paths this checkout does not hold
        // leave the index, the file it really holds lands in it, and --no-semantic keeps the pass structural.
        var reconcile = filledDoc.RootElement.GetProperty("reconcile");
        reconcile.GetProperty("passRan").GetBoolean().ShouldBeTrue();
        reconcile.GetProperty("obligation").GetBoolean().ShouldBeFalse(
            "the copied rows that claimed vectors named files this tree does not hold, so the pass retired " +
            "the claim with the rows");
        reconcile.GetProperty("rescannedFiles").GetInt32().ShouldBe(1);
        StoreFixture.Counts(storeDir).ShouldBe((1, 1),
            "the absolute anchor: one file this tree holds and the one chunk it lands, not a number the "
            + "reconcile block wrote for itself");
        StoreFixture.Counts(storeDir).ShouldBe(
            (reconcile.GetProperty("rescannedFiles").GetInt32(), reconcile.GetProperty("storedChunks").GetInt32()),
            "and the report carries those same installed numbers");
        FilesIn(storeDir).ShouldBe(["config.json", "meta.db", "trigram.idx", "workspace.json"]);
    }

    // The human surface carries the same numbers: the person at the terminal has to see what moved, in
    // bytes, that the copied store is not reconciled yet, and which rows the lane now owns. One label for
    // one object — the surface says 'fill', the JSON says 'fill', 'copy' names only the mode.
    [Fact]
    public async Task WorktreeInit_HumanModeOnAFilledLane_PrintsTheTriplesThePendingReconcileAndTheOwnedRow()
    {
        var main = IndexedMainWithPendingContent("cli-human-main");
        var child = AddWorktree(main, "feat/116-cli-human");
        PreMintLaneStore(child, "feat/116-cli-human", provider: "ollama", model: null, probeFails: true);

        var run = await Run(child, "worktree", "init");

        run.ExitCode.ShouldBe(0);
        run.StdOut.ShouldContain("fill: three numbers per file");
        run.StdOut.ShouldContain("meta.db");
        run.StdOut.ShouldContain("bytes");
        run.StdOut.ShouldContain("files 3→3→3");
        run.StdOut.ShouldContain("chunks 6→6→6");
        run.StdOut.ShouldContain("trigram.idx");
        run.StdOut.ShouldContain("docs 3→3→3");
        run.StdOut.ShouldContain($"manifest owns {MainRepoName}");
        run.StdOut.ShouldContain("reconcile pending");
        // the numbers the contract asks the verb to print: what was rescanned here and what it changed
        run.StdOut.ShouldContain("pass over this tree");
        run.StdOut.ShouldContain("rescanned");
        run.StdOut.ShouldContain("re-embedded 0 chunk(s)");
        run.StdOut.ShouldContain("chunk(s) here have never been embedded");
        // the printed unit is the unit the number is counted in, in the branch that reports no obligation too
        run.StdOut.ShouldContain("chunk hash(es) this store calls embedded");
        run.StdOut.ShouldContain("fill: vectors");
        // the human surface has to say the vector arm was never asked, not leave it out silently
        run.StdOut.ShouldContain("no embedded chunks");
        run.StdOut.ShouldNotContain("copy:");
    }

    // The contract's script test: the main worktree is indexed at one revision, this checkout holds another,
    // and after the verb the lane answers with ITS content and ITS line numbers. Every assertion here compares
    // the answer against the file on disk in this tree, never against what the parent's index says.
    [Fact]
    public async Task WorktreeInit_CopyThenReconcile_LaneAnswersWithItsOwnBranchContentAndLineNumbers()
    {
        const string marker = "GreetOnTheLane";
        var main = IndexedMainOfWorktree("cli-ab-main");
        var child = AddWorktree(main, "feat/116-cli-ab");
        PreMintLaneStore(child, "feat/116-cli-ab", provider: "ollama", model: null, probeFails: true);

        // Three lines of prose go above the method, so the number this tree has to answer with is not the
        // number the main worktree holds, and the method's own name exists on this branch only.
        var laneFile = Path.Combine(child, "Greeter.cs");
        File.WriteAllText(laneFile,
            "public sealed class Greeter\n{\n    // lane note\n    // lane note\n    // lane note\n"
            + $"    public string {marker}() => \"hi\";\n}}\n");
        var diskLine = Array.IndexOf(File.ReadAllLines(laneFile), $"    public string {marker}() => \"hi\";") + 1;
        diskLine.ShouldBe(6, "the fixture itself has to state the number the lane is expected to answer with");

        var run = await Run(child, "worktree", "init", "--json");

        run.ExitCode.ShouldBe(0);
        using (var doc = JsonDocument.Parse(run.StdOut.Trim()))
        {
            var reconcile = doc.RootElement.GetProperty("reconcile");
            reconcile.GetProperty("passRan").GetBoolean().ShouldBeTrue();
            reconcile.GetProperty("modified").GetInt32().ShouldBe(1,
                "one file differs between this branch and the revision the copied rows describe");
            reconcile.GetProperty("rescannedFiles").GetInt32().ShouldBe(1,
                "the .git marker a linked worktree keeps is not a source file");
        }

        var search = await Run(child, "search", marker, "--json");

        search.ExitCode.ShouldBe(0, search.StdErr);
        var hits = SearchMatches(search.StdOut);
        hits.ShouldNotBeEmpty("the reconcile pass moved this row onto the branch's own file");
        hits[0].GetProperty("path").GetString().ShouldBe("Greeter.cs");
        hits[0].GetProperty("line").GetInt32().ShouldBe(diskLine,
            "the lane answers with the line number its own checkout holds, not the one it copied");
        hits[0].GetProperty("snippet").GetString()!.ShouldContain(marker);

        // The main worktree never saw this name: an answer there would mean the lane was reading the parent's
        // store rather than its own.
        var parentSearch = await Run(main, "search", marker, "--json");

        parentSearch.ExitCode.ShouldBe(0, parentSearch.StdErr);
        SearchMatches(parentSearch.StdOut).ShouldBeEmpty(
            "one tree one store: the branch's own edit is not in the main worktree's index");

        // The marker file a linked worktree keeps is not a source file. Measured before this exclusion, the
        // lane indexed one file more than the parent and 'search gitdir' answered with the main worktree's
        // absolute path out of this store.
        var gitdir = await Run(child, "search", "gitdir", "--json");

        gitdir.ExitCode.ShouldBe(0, gitdir.StdErr);
        SearchMatches(gitdir.StdOut).ShouldBeEmpty();
        File.Exists(Path.Combine(child, ".git")).ShouldBeTrue("this lane really is a linked worktree");
        var status = await Run(child, "status", "--json");

        status.ExitCode.ShouldBe(0, status.StdErr);
        using var statusDoc = JsonDocument.Parse(status.StdOut.Trim());
        statusDoc.RootElement.GetProperty("files").GetInt32().ShouldBe(
            1, "the counters a reader trusts count this tree's one source file, marker excluded");
    }

    [Fact]
    public async Task WorktreeInit_ReindexFlag_RegistersWithoutMovingAnyParentRow()
    {
        var main = IndexedMainWithPendingContent("cli-reindex-main");
        var child = AddWorktree(main, "feat/116-cli-reindex");

        var run = await Run(child, "worktree", "init", "--reindex", "--json");

        run.ExitCode.ShouldBe(0);
        using var doc = JsonDocument.Parse(run.StdOut.Trim());
        var fill = doc.RootElement.GetProperty("fill");
        fill.GetProperty("state").GetString().ShouldBe("Skipped");
        fill.GetProperty("reason").GetString()!.ShouldContain("--reindex");
        fill.TryGetProperty("copied", out _).ShouldBeFalse("a skipped fill has no numbers to report");

        var storeDir = WorkspaceStorePaths.RequireStoreDir(doc.RootElement.GetProperty("storeKey").GetString()!);
        var reindexReconcile = doc.RootElement.GetProperty("reconcile");
        reindexReconcile.GetProperty("state").GetString().ShouldBe("Deferred",
            "--reindex stays the explicit from-scratch option: this verb does not index the tree behind it");
        reindexReconcile.GetProperty("passRan").GetBoolean().ShouldBeFalse();
        StoreFixture.Counts(storeDir).ShouldBe((0, 0));
        File.Exists(Path.Combine(storeDir, SqueezrPaths.TrigramIndexFileName)).ShouldBeFalse();
    }

    // review-116-06 #3 and ruling D: text may only name a command that runs today, and the command that
    // fills this lane today is the index of its own tree — a promise about a reconcile pass that never
    // lands is the door that does not open.
    [Fact]
    public async Task WorktreeInit_HumanModeOnReindex_SaysNoPassRanAndNamesTheDoorThatDoes()
    {
        var main = IndexedMainWithPendingContent("cli-reindex-human-main");
        var child = AddWorktree(main, "feat/116-cli-reindex-human");

        var run = await Run(child, "worktree", "init", "--reindex");

        run.ExitCode.ShouldBe(0);
        run.StdOut.ShouldContain("--reindex");
        run.StdOut.ShouldContain("reconcile pass");
        run.StdOut.ShouldContain("squeezr index");
    }

    [Fact]
    public async Task WorktreeInit_CopyAndReindexTogether_RefuseAsContradictoryFlags()
    {
        var main = IndexedMainWithPendingContent("cli-both-main");
        var child = AddWorktree(main, "feat/116-cli-both");

        var run = await Run(child, "worktree", "init", "--copy", "--reindex", "--json");

        run.ExitCode.ShouldBe(1);
        run.StdOut.ShouldBeEmpty();
        var error = ParseStdErrError(run.StdErr);
        error.GetProperty("code").GetString().ShouldBe(WorkspaceException.InvalidQueryCode);
        error.GetProperty("message").GetString()!.ShouldContain("opposite fills");
        RegistryRowsUnder(child).ShouldBeEmpty("a contradictory flag set is refused before anything is minted");
    }

    // The parent holds embedded rows under another model and the lane's own config names a provider
    // explicitly (so the check resolves with no network probe) and points Qdrant at a dead endpoint: a
    // refusal that needs no vector store in the room is the refusal the contract asks for.
    [Fact]
    public async Task WorktreeInit_SourceEmbeddedWithAnotherModel_RefusesBeforeAnyByteLands()
    {
        const string branch = "feat/116-cli-clash";
        var main = IndexedMainWithEmbeddedRows("cli-clash-main", "ollama/cli-parent/768");
        var child = AddWorktree(main, branch);
        var storeDir = PreMintLaneStore(child, branch, provider: "ollama", model: "cli-other");

        var run = await Run(child, "worktree", "init", "--copy", "--json");

        run.ExitCode.ShouldBe(1);
        run.StdOut.ShouldBeEmpty();
        var error = ParseStdErrError(run.StdErr);
        error.GetProperty("code").GetString().ShouldBe(WorkspaceException.WorktreeModelMismatchCode);
        var message = error.GetProperty("message").GetString()!;
        message.ShouldContain("6 embedded chunk(s)");
        message.ShouldContain("ollama/cli-other/768");
        message.ShouldContain("--reindex");

        StoreFixture.Counts(storeDir).ShouldBe((0, 0),
            "the refusal is before the copy, so the lane still holds the empty store init minted");
        File.Exists(Path.Combine(storeDir, SqueezrPaths.TrigramIndexFileName)).ShouldBeFalse();
        FilesIn(storeDir).ShouldBe(["config.json", "meta.db", "workspace.json"],
            "a refusal keeps its temp out of the store it refused to write");
    }

    // The verb wires the semantic arm, and a vector store that is not in the room is a reported session-start
    // fact rather than a failed one: the files land, the fill says Filled, and the vector block says it did
    // not — on stdout for the reader walking the payload, on stderr for the hook that only reads warnings.
    // The lane's own config names a dead endpoint, so this is also the pin that a fill cannot mint a
    // collection in the developer's Qdrant to discover that.
    [Fact]
    public async Task WorktreeInit_EmbeddedParentWithNoAnsweringStore_FillsFilesAndReportsVectorsNotCopied()
    {
        const string branch = "feat/116-cli-vectors";
        var main = IndexedMainWithEmbeddedRows("cli-vectors-main", StoreFixture.ModelKey);
        var child = AddWorktree(main, branch);
        var storeDir = PreMintLaneStore(child, branch, provider: "ollama", model: "nomic-embed-text");

        var run = await Run(child, "worktree", "init", "--copy", "--no-semantic", "--json");

        run.ExitCode.ShouldBe(0);
        using var doc = JsonDocument.Parse(run.StdOut.Trim());
        var fill = doc.RootElement.GetProperty("fill");
        fill.GetProperty("state").GetString().ShouldBe("Filled");
        var vectors = fill.GetProperty("vectors");
        vectors.GetProperty("copied").GetBoolean().ShouldBeFalse();
        vectors.GetProperty("sourcePoints").GetInt64().ShouldBe(0);
        vectors.GetProperty("copiedPoints").GetInt64().ShouldBe(0);
        vectors.GetProperty("targetPoints").GetInt64().ShouldBe(0);
        vectors.GetProperty("reason").GetString()!.ShouldContain("did not answer");
        run.StdErr.ShouldContain("did not answer");
        // --no-semantic keeps the verb's own pass structural: it reaps the copied rows whose paths this tree
        // does not hold, and the claim they carried goes with them, so nothing here reads as a store whose
        // vectors are missing. The warning line above is the honest half: the parent's own points did not land.
        var reconcile = doc.RootElement.GetProperty("reconcile");
        reconcile.GetProperty("passRan").GetBoolean().ShouldBeTrue();
        reconcile.GetProperty("obligation").GetBoolean().ShouldBeFalse(
            "no row left in this store calls itself embedded");
        reconcile.GetProperty("state").GetString().ShouldBe("Reconciled");
        reconcile.GetProperty("rescannedFiles").GetInt32().ShouldBe(1);
        StoreFixture.Counts(storeDir).ShouldBe((1, 1),
            "the absolute anchor, before any number this same payload reports");
        StoreFixture.Counts(storeDir).ShouldBe(
            (reconcile.GetProperty("rescannedFiles").GetInt32(), reconcile.GetProperty("storedChunks").GetInt32()),
            "the reconcile numbers are the installed store's numbers");
        FilesIn(storeDir).ShouldBe(["config.json", "meta.db", "trigram.idx", "workspace.json"]);
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> Run(
        string workingDir, params string[] args) => await CliTestProcess.RunAsync(workingDir, args);

    // A parent the verb can be filled from: registry row, one repo in the manifest, rows in the database
    // under that same name. 'squeezr add' writes the pair together, which is the point — a parent whose
    // manifest does not name its own rows is the broken state, not a starting shape.
    private string IndexedMainWithPendingContent(string name)
    {
        var main = InitMainRepo(name);
        StoreFixture.Seed(StoreDirOfMain(main), modelKey: null);
        return main;
    }

    private string IndexedMainWithEmbeddedRows(string name, string modelKey)
    {
        var main = InitMainRepo(name);
        StoreFixture.Seed(StoreDirOfMain(main), modelKey);
        return main;
    }

    // The query pin needs rows describing files the lane's checkout really holds, so the parent is indexed
    // by the product rather than seeded. --no-semantic keeps the whole run independent of a live ollama:
    // the copy's model question is only asked when the source holds vectors.
    private string IndexedMainOfWorktree(string name)
    {
        var main = TestDirs.NewTempDir();
        TestGitRepo.Init(main);
        File.WriteAllText(
            Path.Combine(main, "Greeter.cs"),
            "public sealed class Greeter\n{\n    public string Greet() => \"hello from the branch\";\n}\n");
        TestGitRepo.Add(main, "Greeter.cs");
        TestGitRepo.Commit(main, "seed");
        (Run(main, "init", "--name", name, "--json").GetAwaiter().GetResult()).ExitCode.ShouldBe(0);
        (Run(main, "add", ".", "--name", MainRepoName, "--json").GetAwaiter().GetResult()).ExitCode.ShouldBe(0);
        var index = Run(main, "index", "--no-semantic", "--json").GetAwaiter().GetResult();
        index.ExitCode.ShouldBe(0);
        return main;
    }

    private string InitMainRepo(string name)
    {
        var main = TestDirs.NewTempDir();
        TestGitRepo.Init(main);
        File.WriteAllText(Path.Combine(main, "seed.txt"), "seed\n");
        TestGitRepo.Add(main, "seed.txt");
        TestGitRepo.Commit(main, "seed");
        (Run(main, "init", "--name", name, "--json").GetAwaiter().GetResult()).ExitCode.ShouldBe(0);
        (Run(main, "add", ".", "--name", MainRepoName, "--json").GetAwaiter().GetResult()).ExitCode.ShouldBe(0);
        return main;
    }

    private string StoreDirOfMain(string main) => WorkspaceStorePaths.RequireStoreDir(StoreKeyOfRoot(main));

    // The lane store a session-start hook would find half-done: the model question reads this config, so
    // writing it before the verb runs is how a probe is forced or bypassed without touching the network.
    // The manifest is written with it because init only keeps a config it finds next to a manifest it
    // adopts — a bare config in an unknown store dir would be overwritten by the mint.
    private string PreMintLaneStore(string child, string branch, string provider, string? model, bool probeFails = false)
    {
        var storeDir = Directory.CreateDirectory(LaneStoreDir(child, branch)).FullName;
        var childRoot = WorkspaceCanonicalPath.Canonicalize(WorkspaceCanonicalPath.ResolveThroughLinks(child));
        File.WriteAllText(
            Path.Combine(storeDir, SqueezrPaths.StoreManifestFileName),
            JsonSerializer.Serialize(
                new WorkspaceManifest { Name = branch.Split('/')[^1], RootPath = childRoot },
                SqueezrJson.FileOptions));
        WriteLaneConfig(storeDir, provider, model, probeFails);
        return storeDir;
    }

    private static void WriteLaneConfig(string storeDir, string provider, string? model, bool probeFails = false)
    {
        var defaults = SqueezrConfig.CreateDefault();
        var ollama = new OllamaOptions
        {
            Model = model ?? defaults.Embeddings.Ollama.Model,
            ModelProfile = null,
        };
        if (probeFails)
        {
            ollama = ollama with { Endpoint = TestWorkspaceConfig.DeadOllamaEndpoint };
        }

        File.WriteAllText(
            Path.Combine(storeDir, SqueezrPaths.ConfigFileName),
            JsonSerializer.Serialize(
                defaults with
                {
                    Embeddings = new EmbeddingsConfig { Provider = provider, Ollama = ollama },
                    Qdrant = new QdrantConfig { Endpoint = TestWorkspaceConfig.DeadQdrantEndpoint },
                },
                SqueezrJson.FileOptions));
    }

    private static string LaneStoreDir(string child, string branch) =>
        WorkspaceStorePaths.RequireStoreDir(StoreKeyOfBranch(child, branch));

    private static string StoreKeyOfBranch(string child, string branch)
    {
        var childRoot = WorkspaceCanonicalPath.Canonicalize(WorkspaceCanonicalPath.ResolveThroughLinks(child));
        return WorkspaceStoreKeys.Create(branch.Split('/')[^1], childRoot);
    }

    private static string AddWorktree(string main, string branch)
    {
        var child = Path.Combine(main, ".lanes", branch.Split('/')[^1]);
        TestGitRepo.AddWorktree(main, child, branch);
        return child;
    }

    private string StoreKeyOfRoot(string root)
    {
        var canonical = WorkspaceCanonicalPath.Canonicalize(root);
        using var doc = JsonDocument.Parse(File.ReadAllText(WorkspaceRegistry.FilePath));
        return doc.RootElement.EnumerateArray()
            .Single(row => row.GetProperty("rootPath").GetString() == canonical)
            .GetProperty("storeKey").GetString()!;
    }

    private static IReadOnlyList<JsonElement> SearchMatches(string stdOut)
    {
        var matches = new List<JsonElement>();
        foreach (var line in stdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var parsed = JsonDocument.Parse(line);
            if (parsed.RootElement.GetProperty("type").GetString() == "match")
            {
                matches.Add(parsed.RootElement.Clone());
            }
        }

        return matches;
    }

    private static IReadOnlyList<string> FilesIn(string storeDir) =>
        Directory.Exists(storeDir)
            ? [.. Directory.GetFiles(storeDir).Select(Path.GetFileName).Select(name => name!).Order(StringComparer.Ordinal)]
            : [];

    private static JsonElement[] RegistryRowsUnder(string path)
    {
        if (!File.Exists(WorkspaceRegistry.FilePath))
        {
            return [];
        }

        var canonical = WorkspaceCanonicalPath.Canonicalize(path);
        using var doc = JsonDocument.Parse(File.ReadAllText(WorkspaceRegistry.FilePath));
        return doc.RootElement.EnumerateArray()
            .Where(row => row.GetProperty("rootPath").GetString() == canonical)
            .Select(row => row.Clone())
            .ToArray();
    }

    private static JsonElement ParseStdErrError(string stdErr)
    {
        var lines = stdErr.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using var doc = JsonDocument.Parse(lines[^1]);
        return doc.RootElement.GetProperty("error").Clone();
    }
}
