using System.Text.Json;
using Shouldly;
using ModelContextProtocol.Protocol;
using Squeezr.Cli.Mcp;
using Squeezr.Core.Indexing;
using Squeezr.Core.Serialization;
using Squeezr.Tests.Indexing.Ignore;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Mcp;

// The envelope half of the subject: what a tool call's JSON looks like when the answer owes its tree, and — the
// pin that keeps every old consumer whole — that it looks EXACTLY like what it looked like before when it does
// not. The additive rule (035) is measured as a key list, not argued.
[Collection("ProcessGlobals")]
public sealed class ToolEnvelopeSubjectTests
{
    private const string LaneBranch = "feat/116-envelope-lane";

    [Fact]
    public void Serialize_NoSubjectToName_KeyListIsTheOneOldConsumersParse()
    {
        var envelope = ToolEnvelope.Wrap(
            new { hit = 1 }, 7, false, ["w1"], staleFiles: ["demo/a.cs"],
            freshness: AnswerFreshness.ForContent(
                checkedCandidates: 1, namedFiles: 1, staleTotal: 1, listed: 1, laggingFiles: 0,
                stalePaths: ["demo/a.cs"], payloadLines: [null]));

        var json = JsonSerializer.Serialize(envelope);
        using var document = JsonDocument.Parse(json);

        document.RootElement.EnumerateObject().Select(p => p.Name).ShouldBe(
            ["data", "tokensUsed", "truncated", "warnings", "indexAge", "staleFiles", "cost", "freshness"]);
        document.RootElement.TryGetProperty("subject", out _).ShouldBeFalse(
            "the key is absent when there is nothing to name, not present and empty");
        json.ShouldNotContain("cross-tree");
    }

    [Fact]
    public void Wrap_LaneSession_NamesRootBranchAndStoreKey_BesideTheFieldsThatAlreadyRode()
    {
        var subject = new AnswerSubject("/lane/root", LaneBranch, "lane-9c176b2e", "/lane/root", CrossTree: false);

        var envelope = ToolEnvelope.Wrap(new { hit = 1 }, 7, false, ["w1"])
            .WithSubject(
                Squeezr.Cli.Output.AnswerSubjectNotice.BodyOf(subject),
                Squeezr.Cli.Output.AnswerSubjectNotice.CrossTreeWarningOf(subject));

        var json = JsonSerializer.Serialize(envelope);
        using var document = JsonDocument.Parse(json);
        var named = document.RootElement.GetProperty("subject");

        named.GetProperty("rootPath").GetString().ShouldBe("/lane/root");
        named.GetProperty("branch").GetString().ShouldBe(LaneBranch);
        named.GetProperty("storeKey").GetString().ShouldBe("lane-9c176b2e");
        document.RootElement.GetProperty("warnings").EnumerateArray().Select(w => w.GetString())
            .ShouldBe(["w1"], "a lane answering its own caller crosses nothing, so it warns nothing");
        document.RootElement.EnumerateObject().Select(p => p.Name).Last().ShouldBe("subject",
            "the block lands after freshness, where every added sibling of this envelope has always landed");
    }

    [Fact]
    public void Wrap_CrossTreeSession_CarriesTheWarningInTheExistingWarningsArray()
    {
        var subject = new AnswerSubject("/repo/main", "master", "main-1a2b3c4d", "/repo/.lanes/lane", CrossTree: true);

        var envelope = ToolEnvelope.Wrap(new { hit = 1 }, 7, false, ["reconcile pending: rows copied"])
            .WithSubject(
                Squeezr.Cli.Output.AnswerSubjectNotice.BodyOf(subject),
                Squeezr.Cli.Output.AnswerSubjectNotice.CrossTreeWarningOf(subject));

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(envelope));
        var warnings = document.RootElement.GetProperty("warnings").EnumerateArray()
            .Select(w => w.GetString()!).ToList();

        warnings.ShouldContain(w => w!.StartsWith("reconcile pending:", StringComparison.Ordinal),
            "T7's block is a sibling, never a reshaped field: the pending line keeps its own wording and place");
        warnings.ShouldContain(w => w!.StartsWith("cross-tree:", StringComparison.Ordinal));
        warnings.Single(w => w!.Contains("/repo/main", StringComparison.Ordinal)
                             && w.Contains("/repo/.lanes/lane", StringComparison.Ordinal))
            .ShouldNotBeNull("the warning names BOTH trees, or a reader cannot tell which side it stands on");
    }

    // This line is emitted when the target is chosen, which is before anyone knows whether the run answers: a
    // refusal after the naming — the repo filter rejecting a repo the selected store does not own — carries the
    // warning and has no rows to describe. So the sentence claims the two trees, and keeps its coordinate half
    // modal instead of asserting an answer that never existed.
    [Fact]
    public void CrossTreeWarning_ClaimsTheTreesItMeasured_AndNoRowsTheRunNeverReturned()
    {
        var warning = Squeezr.Cli.Output.AnswerSubjectNotice.CrossTreeWarningOf(
            new AnswerSubject("/repo/main", "master", "main-1a2b3c4d", "/repo/.lanes/lane", CrossTree: true))!;

        warning.ShouldStartWith("cross-tree:");
        warning.ShouldContain("/repo/main");
        warning.ShouldContain("/repo/.lanes/lane");
        warning.ShouldNotContain("in this answer");
    }

    // The gate is the seam the subject rides: it stamps every envelope the session answers with, so no tool has
    // to remember the naming and a forgotten one is structurally impossible.
    [Fact]
    public async Task Gate_StampsEveryAnswerOfALaneSession_IncludingTheCachedReplay()
    {
        var subject = new AnswerSubject("/lane/root", LaneBranch, "lane-9c176b2e", "/lane/root", CrossTree: false);
        var gate = new McpToolGate(subject: subject);

        var first = await gate.RunAsync(_ => Task.FromResult(ToolEnvelope.Wrap(new { hit = 1 }, 3, false)), CancellationToken.None);
        var second = await gate.RunAsync(_ => Task.FromResult(ToolEnvelope.Wrap(new { hit = 2 }, 4, false)), CancellationToken.None);

        first.Subject.ShouldNotBeNull();
        second.Subject!.Branch.ShouldBe(LaneBranch);
        first.Subject!.StoreKey.ShouldBe("lane-9c176b2e");
    }

    [Fact]
    public async Task Gate_NoSubjectSession_AnswersExactlyAsItDidBeforeTheBlock()
    {
        var gate = new McpToolGate();

        var envelope = await gate.RunAsync(_ => Task.FromResult(ToolEnvelope.Wrap(new { hit = 1 }, 3, false, ["w1"])), CancellationToken.None);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(envelope));
        document.RootElement.TryGetProperty("subject", out _).ShouldBeFalse();
        document.RootElement.GetProperty("warnings").GetArrayLength().ShouldBe(1);
    }

    // The wire pin: the block is measured on a real session over the transport, not only on the record, because
    // what an agent reads is the JSON that came back. A session standing in a lane while the parent store answers
    // is the dogfood shape this feature was opened for, and it is the one shape that must say so on the wire.
    [Fact]
    public async Task Serve_LaneSessionOnAParentStore_CarriesTheSubjectBlockOnTheWire()
    {
        var main = IndexedCheckout("wire-main");
        var lane = Path.Combine(TestDirs.NewTempDir(), "lane-a");
        TestGitRepo.AddWorktree(main, lane, "feat/116-envelope-wire");

        await using var crossed = await McpTestHarness.StartAsync(main, sessionDirectory: lane);
        var crossedEnvelope = ParseEnvelope(await crossed.Client.CallToolAsync(
            "repo_map", new Dictionary<string, object?> { ["depth"] = 1 }, cancellationToken: CancellationToken.None));

        crossedEnvelope.TryGetProperty("subject", out var named).ShouldBeTrue(
            "the answer that describes a tree the session is not standing in names it");
        named.GetProperty("rootPath").GetString()!.ShouldBe(SessionLocation.Display(main));
        named.GetProperty("branch").GetString()!.ShouldBe(HeadBranch(main));
        crossedEnvelope.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!)
            .ShouldContain(w => w.StartsWith("cross-tree:", StringComparison.Ordinal));

        await using var standing = await McpTestHarness.StartAsync(main, sessionDirectory: main);
        var standingEnvelope = ParseEnvelope(await standing.Client.CallToolAsync(
            "repo_map", new Dictionary<string, object?> { ["depth"] = 1 }, cancellationToken: CancellationToken.None));

        standingEnvelope.TryGetProperty("subject", out _).ShouldBeFalse(
            "and the ordinary session's JSON is the JSON it always was — AC3 measured at the transport, not argued");
    }

    private static JsonElement ParseEnvelope(CallToolResult result)
    {
        var json = result.Content.ShouldHaveSingleItem().ToString()!;
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Fact]
    public async Task WithSubject_ReplayOfAStoredEnvelope_GetsItsSubjectFromTheSession_NotTheCache()
    {
        var subject = new AnswerSubject("/lane/root", LaneBranch, "lane-9c176b2e", null, CrossTree: false);
        var stored = ToolEnvelope.Wrap(new { hit = 1 }, 3, false, freshness: AnswerFreshness.ForContent(
            checkedCandidates: 1, namedFiles: 1, staleTotal: 0, listed: 0, laggingFiles: 0,
            stalePaths: [], payloadLines: [null]));
        var gate = new McpToolGate(subject: subject);

        var replayed = await gate.RunAsync(_ => Task.FromResult(stored.MarkReplayed()), CancellationToken.None);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(replayed));
        document.RootElement.GetProperty("subject").GetProperty("branch").GetString().ShouldBe(LaneBranch);
        document.RootElement.GetProperty("freshness").GetProperty("replayed").GetBoolean().ShouldBeTrue(
            "and the replay mark T7 shipped still rides: the two added blocks are siblings on one envelope");
    }

    private static string HeadBranch(string root) =>
        File.ReadAllText(Path.Combine(root, ".git", "HEAD")).Trim().Replace("ref: refs/heads/", string.Empty);

    private static string IndexedCheckout(string name)
    {
        var workspace = TestDirs.NewTempDir();
        TestGitRepo.Init(workspace);
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        File.WriteAllText(Path.Combine(workspace, "repo", "Alpha.cs"), "class Alpha { string Term = \"uniqneedle\"; }");
        TestGitRepo.Add(workspace, "repo/Alpha.cs");
        TestGitRepo.Commit(workspace, "seed");
        var manager = new WorkspaceManager(workspace);
        manager.Init(name);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        manager.AddRepo("repo", "demo");
        new WorkspaceIndexer().Index(workspace);
        return workspace;
    }
}
