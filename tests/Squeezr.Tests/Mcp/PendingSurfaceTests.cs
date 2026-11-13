using System.Text.Json;
using ModelContextProtocol.Protocol;
using Shouldly;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Tests.Indexing.Ignore;

namespace Squeezr.Tests.Mcp;

// The pending word where a reader actually meets it: 'status --json', the MCP index_status, and the per-file
// verdict that rides a file-naming answer. The state under test is the one between a copy and its reconcile
// pass, reached through the product's own fill door, because the claim is about what the surfaces say about a
// store rather than about what a manifest is able to hold.
//
// Three harness facts decide the shape of this file, and each is a trap this class has to answer:
//
//   - this class never changes SQUEEZR_HOME and spawns children through the plain CliTestProcess.RunAsync.
//     That helper pins every child to the home it resolved the first time any test touched it, so a class
//     that installs a private home before those children do sends the whole run's CLI into a directory it
//     later deletes — which reads as a foreign test going red, not as this one going wrong.
//   - the parent is indexed by the product rather than seeded, so the rows a lane copies name a file this
//     checkout really holds. A verdict that would have read 'unknown' on its own is a gate that proves nothing.
[Collection("ProcessGlobals")]
public sealed class PendingSurfaceTests : IDisposable
{
    private const string RepoName = "demo";

    private readonly List<string> _createdDirs = [];

    public void Dispose()
    {
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

    // AC1 and AC3 at the store door: rows copied, no pass over this tree. The record answers on all three
    // surfaces, and the per-file verdict that would otherwise certify a copied row as a match says 'unknown'.
    [Fact]
    public async Task FilledLaneWithNoPass_ReportsPendingOnStatusIndexStatusAndWithholdsFresh()
    {
        var lane = await FilledLaneWithoutAPassAsync("surface-pending");

        // The premises, measured before any verdict is claimed: this checkout holds the file, the store holds
        // its row, and the record says no pass here proved it.
        File.Exists(Path.Combine(lane, "Greeter.cs")).ShouldBeTrue();
        WorkspaceManifestFile.Load(lane).Reconcile.ShouldNotBeNull()
            .PassCompleted.ShouldBeFalse("no pass over this tree has run");
        StoredPaths(lane).ShouldBe(["Greeter.cs"],
            "the copy landed the row the verdict below is made of, under the name this lane owns");

        var status = await RunAsync(lane, "status", "--json");

        status.ExitCode.ShouldBe(0, status.StdErr);
        using (var doc = JsonDocument.Parse(status.StdOut.Trim()))
        {
            var block = doc.RootElement.GetProperty("reconcile");
            block.GetProperty("filled").GetBoolean().ShouldBeTrue();
            block.GetProperty("passCompleted").GetBoolean().ShouldBeFalse();
            block.GetProperty("pending").GetBoolean().ShouldBeTrue();
            WarningsOf(doc.RootElement).ShouldContain(line =>
                line.Contains("reconcile pending", StringComparison.Ordinal));
        }
        status.StdErr.Contains("reconcile pending").ShouldBeTrue(
            "the warning channel is where a session-start hook that reads no payload learns it");

        await using var harness = await McpTestHarness.StartAsync(lane);

        var indexStatus = await CallAsync(harness, "index_status");
        indexStatus.GetProperty("data").TryGetProperty("reconcile", out var mcpBlock).ShouldBeTrue();
        mcpBlock.GetProperty("passCompleted").GetBoolean().ShouldBeFalse();
        WarningsOf(indexStatus).ShouldContain(line =>
            line.Contains("reconcile pending", StringComparison.Ordinal));

        var expanded = await CallAsync(harness, "expand_span", ExpandArgs());

        expanded.TryGetProperty("fileFreshness", out var freshnessBlock).ShouldBeTrue(expanded.ToString());
        var verdicts = freshnessBlock.EnumerateArray().ToList();
        verdicts.ShouldNotBeEmpty("the answer names the file, so it owes the reader a verdict on it");
        VerdictOf(verdicts[0]).ShouldBe("unknown",
            "a copied row cannot certify this file as fresh before a pass over this tree proved it");
        WarningsOf(expanded).ShouldContain(line =>
            line.Contains("reconcile pending", StringComparison.Ordinal),
            "and the withholding is said, not performed in silence");
    }

    // AC2 and the crash-honest return path: the pass the store was waiting for lands and both answers read
    // fresh again; taking the record back to 'open' — the state a killed pass leaves — puts the pending word
    // on the surfaces again with nothing else touched. That direction is what proves the surfaces read the
    // record rather than remembering the session that filled it.
    [Fact]
    public async Task ClosedPass_ClearsThePendingWord_AnOpenRecordBringsItBack()
    {
        var lane = await FilledLaneWithoutAPassAsync("surface-closed");

        var reconciled = await RunAsync(lane, "worktree", "init", "--no-semantic", "--json");

        reconciled.ExitCode.ShouldBe(0, reconciled.StdErr);
        using (var report = JsonDocument.Parse(reconciled.StdOut.Trim()))
        {
            var reconcile = report.RootElement.GetProperty("reconcile");
            reconcile.GetProperty("state").GetString().ShouldBe("Reconciled");
            reconcile.GetProperty("passRan").GetBoolean().ShouldBeTrue(
                "the record this lane carried said no pass had run, so this start owes exactly that pass");
            reconcile.GetProperty("basis").GetString().ShouldBe("pass");
        }

        var cleared = await RunAsync(lane, "status", "--json");

        cleared.ExitCode.ShouldBe(0, cleared.StdErr);
        using (var doc = JsonDocument.Parse(cleared.StdOut.Trim()))
        {
            doc.RootElement.GetProperty("reconcile").GetProperty("pending").GetBoolean().ShouldBeFalse();
            doc.RootElement.GetProperty("reconcile").GetProperty("passCompleted").GetBoolean().ShouldBeTrue();
            WarningsOf(doc.RootElement).ShouldNotContain(line =>
                line.Contains("reconcile pending", StringComparison.Ordinal));
            doc.RootElement.GetProperty("files").GetInt32().ShouldBe(1,
                "and the numbers the reader trusts still count this tree's one file");
        }

        await using var harness = await McpTestHarness.StartAsync(lane);
        var afterPass = await CallAsync(harness, "expand_span", ExpandArgs());

        afterPass.TryGetProperty("fileFreshness", out var closedRows).ShouldBeTrue(afterPass.ToString());
        VerdictOf(closedRows.EnumerateArray().First()).ShouldBe("fresh",
            "with the pass closed, the verdict the store can support is the one it ships");

        // the record a killed pass leaves, written back over the closed one
        WorkspaceManifestFile.WriteReconcileAt(
            SqueezrPaths.ManifestPath(lane), lane, ReconcileMarker.Opened().FilledFrom(true, null));

        var reopened = await RunAsync(lane, "status", "--json");

        reopened.ExitCode.ShouldBe(0, reopened.StdErr);
        using (var doc = JsonDocument.Parse(reopened.StdOut.Trim()))
        {
            doc.RootElement.GetProperty("reconcile").GetProperty("pending").GetBoolean().ShouldBeTrue();
            WarningsOf(doc.RootElement).ShouldContain(line =>
                line.Contains("reconcile pending", StringComparison.Ordinal));
        }

        var reopenedAnswer = await CallAsync(harness, "expand_span", ExpandArgs());

        reopenedAnswer.TryGetProperty("fileFreshness", out var reopenedRows).ShouldBeTrue(reopenedAnswer.ToString());
        VerdictOf(reopenedRows.EnumerateArray().First())
            .ShouldNotBe("fresh", "an open record withholds the verdict even where a closed one was measured");
    }

    // AC4: the store this rule never touched — a workspace with no block in its manifest — keeps answering
    // exactly as it did, with no pending word anywhere and its verdicts served as measured.
    [Fact]
    public async Task OldManifestWithoutTheField_ServesFreshVerdictsAndSaysNothingAboutPending()
    {
        var workspace = Tracked(TestDirs.NewTempDir());
        TestGitRepo.Init(workspace);
        WriteGreeter(workspace);
        TestGitRepo.Add(workspace, "Greeter.cs");
        TestGitRepo.Commit(workspace, "seed");
        (await RunAsync(workspace, "init", "--name", "surface-old", "--json")).ExitCode.ShouldBe(0);
        (await RunAsync(workspace, "add", ".", "--name", RepoName, "--json")).ExitCode.ShouldBe(0);
        (await RunAsync(workspace, "index", "--no-semantic", "--json")).ExitCode.ShouldBe(0);
        StoredPaths(workspace).ShouldBe(["Greeter.cs"],
            "the premise, measured: this store holds the row the verdict below claims about it");

        // An ordinary workspace is the pre-rule shape on its own: no door on this path ever writes the field,
        // so 'the field is absent' needs no forgery here.
        WorkspaceManifestFile.Load(workspace).Reconcile.ShouldBeNull(
            "the premise is measured, not assumed: nothing this test opened writes the record");

        var status = await RunAsync(workspace, "status", "--json");

        status.ExitCode.ShouldBe(0, status.StdErr);
        using var doc = JsonDocument.Parse(status.StdOut.Trim());
        doc.RootElement.TryGetProperty("reconcile", out _).ShouldBeFalse(
            "an additive field that is absent writes no block rather than one saying false");
        WarningsOf(doc.RootElement).ShouldNotContain(line =>
            line.Contains("reconcile pending", StringComparison.Ordinal));
        status.StdErr.Contains("reconcile pending").ShouldBeFalse();

        await using var harness = await McpTestHarness.StartAsync(workspace);
        var expanded = await CallAsync(harness, "expand_span",
            ExpandArgs());

        expanded.TryGetProperty("fileFreshness", out var oldRows).ShouldBeTrue(expanded.ToString());
        VerdictOf(oldRows.EnumerateArray().First()).ShouldBe("fresh",
            "a workspace that indexed its own tree is not held to a rule it never needed");
    }

    // The two halves of the record are kept apart on purpose. A lane whose pass closed and whose vectors were
    // never accounted for owes the pending word — and still ships its per-file verdicts, because a pass over
    // this root really did prove those coordinates. Folding the two together would either hide a silent
    // vector arm or withhold a verdict the store can support.
    [Fact]
    public async Task ClosedPassWithAnUnansweredVectorArm_WarnsAndStillServesTheCoordinateVerdict()
    {
        var lane = await FilledLaneWithoutAPassAsync("surface-semantic");
        (await RunAsync(lane, "worktree", "init", "--no-semantic", "--json")).ExitCode.ShouldBe(0, "");
        WorkspaceManifestFile.WriteReconcileAt(SqueezrPaths.ManifestPath(lane), lane,
            ReconcileMarker.Opened().FilledFrom(exactArmLanded: true, code: null).ClosedPass(
                passCompleted: true, semanticArmLanded: false, obligationChunks: 12, coveredChunks: null,
                code: WorktreeReconcileOutcome.StoreSilentCode));

        var status = await RunAsync(lane, "status", "--json");

        status.ExitCode.ShouldBe(0, status.StdErr);
        using (var doc = JsonDocument.Parse(status.StdOut.Trim()))
        {
            var block = doc.RootElement.GetProperty("reconcile");
            block.GetProperty("passCompleted").GetBoolean().ShouldBeTrue();
            block.GetProperty("semanticArmLanded").GetBoolean().ShouldBeFalse();
            block.GetProperty("pending").GetBoolean().ShouldBeTrue(
                "an unaccounted vector claim keeps the pending word even with the pass closed");
            WarningsOf(doc.RootElement).ShouldContain(line =>
                line.Contains("never answered", StringComparison.Ordinal),
                "and it says the measurement was never taken, not that it came back empty");
        }

        await using var harness = await McpTestHarness.StartAsync(lane);
        var expanded = await CallAsync(harness, "expand_span", ExpandArgs());

        VerdictOf(expanded.GetProperty("fileFreshness").EnumerateArray().First()).ShouldBe("fresh",
            "the coordinates were proved by a pass over this tree, so the verdict stands on its own evidence");
    }

    // The fill without the pass, through the product's own doors and stopped where a session that never
    // reached the reconcile would stop: registration, copy, manifest seed — no pass over this tree.
    private async Task<string> FilledLaneWithoutAPassAsync(string name)
    {
        var main = Tracked(TestDirs.NewTempDir());
        TestGitRepo.Init(main);
        WriteGreeter(main);
        TestGitRepo.Add(main, "Greeter.cs");
        TestGitRepo.Commit(main, "seed");
        var lane = Path.Combine(Tracked(TestDirs.NewTempDir()), "lane");
        TestGitRepo.AddWorktree(main, lane, $"feat/116-{name}");

        (await RunAsync(main, "init", "--name", name, "--json")).ExitCode.ShouldBe(0);
        (await RunAsync(main, "add", ".", "--name", RepoName, "--json")).ExitCode.ShouldBe(0);
        var indexed = await RunAsync(main, "index", "--no-semantic", "--json");
        indexed.ExitCode.ShouldBe(0, indexed.StdErr);
        StoredPaths(main).ShouldBe(["Greeter.cs"],
            "one tracked file and no second copy of it under the lane: every verdict below is about that row");

        var registered = WorktreeRegistration.Register(lane, null);
        registered.State.ShouldBe(WorktreeRegistrationState.Registered, registered.Reason);
        var fill = WorktreeStoreFill.Fill(registered, true, () => "ollama/nomic-embed-text/768");
        fill.State.ShouldBe(WorktreeFillState.Filled, fill.Reason);
        return lane;
    }

    private static Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string workingDir, params string[] args) => CliTestProcess.RunAsync(workingDir, args);

    private static void WriteGreeter(string root) => File.WriteAllText(
        Path.Combine(root, "Greeter.cs"),
        "public sealed class Greeter\n{\n    public string Greet() => \"hello\";\n}\n");

    private static IReadOnlyList<string> StoredPaths(string root)
    {
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(root));
        var repo = store.FindRepo(RepoName);
        return repo is null
            ? []
            : [.. store.ListFiles(repo.Id).Select(file => file.Path)
                .OrderBy(path => path, StringComparer.Ordinal)];
    }

    // The tool speaks repo-relative paths; an absolute one is not a coordinate it indexes, and a test that
    // asked with one would be measuring the error path rather than the verdict gate.
    private static Dictionary<string, object?> ExpandArgs() => new()
    {
        ["file"] = "Greeter.cs",
        ["startLine"] = 3,
    };

    private static IReadOnlyList<string> WarningsOf(JsonElement envelope) =>
        [.. envelope.GetProperty("warnings").EnumerateArray().Select(value => value.GetString()!)];

    private static string VerdictOf(JsonElement row) => row.GetProperty("verdict").GetString()!;

    private static async Task<JsonElement> CallAsync(
        McpTestHarness harness, string tool, Dictionary<string, object?>? args = null)
    {
        var result = await harness.Client.CallToolAsync(
            tool, args ?? new Dictionary<string, object?>(), cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true, $"{tool} failed: {string.Join(" ", result.Content.Select(b => b.ToString()))}");
        return JsonDocument.Parse(result.Content.ShouldHaveSingleItem().ToString()!).RootElement.Clone();
    }

    private string Tracked(string dir)
    {
        _createdDirs.Add(dir);
        return dir;
    }
}
