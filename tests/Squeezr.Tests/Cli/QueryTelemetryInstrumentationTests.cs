using System.Text.Json;
using Shouldly;
using Squeezr.Cli;
using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Tests.Mcp;

namespace Squeezr.Tests.Cli;

[Collection("ProcessGlobals")]
public sealed class QueryTelemetryInstrumentationTests
{
    [Fact]
    public async Task Search_Json_AppendsSingleRecordWithActualValues()
    {
        var root = await NewIndexedCliWorkspaceAsync();

        var search = await RunCliAsync(root, "search", "foo", "--json");
        search.ExitCode.ShouldBe(0);

        var entries = QueryTelemetry.ReadAll(root);
        entries.Count.ShouldBe(1);
        var entry = entries.Single();
        entry.Tool.ShouldBe("search");
        entry.SessionId.ShouldNotBe(Guid.Empty);
        entry.TokensUsed.ShouldBeGreaterThan(0);
        entry.Truncated.ShouldBeFalse();
        entry.Mapped.ShouldBe(false);
        entry.Hits.ShouldBe(1);
        var parameters = (JsonElement)entry.Params!;
        parameters.GetProperty("query").GetString().ShouldBe("foo");
        parameters.GetProperty("regex").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Expand_AppendsRecordWithFileParam()
    {
        var root = await NewIndexedCliWorkspaceAsync();

        var expand = await RunCliAsync(root, "expand", "src/Foo.cs", "1");
        expand.ExitCode.ShouldBe(0);

        var entries = QueryTelemetry.ReadAll(root);
        entries.Count.ShouldBe(1);
        var entry = entries.Single();
        entry.Tool.ShouldBe("expand");
        entry.SessionId.ShouldNotBe(Guid.Empty);
        entry.TokensUsed.ShouldBeGreaterThan(0);
        entry.Mapped.ShouldBe(false);
        entry.Hits.ShouldBe(1);
        var parameters = (JsonElement)entry.Params!;
        parameters.GetProperty("file").GetString().ShouldBe("src/Foo.cs");
        parameters.GetProperty("startLine").GetInt32().ShouldBe(1);
        parameters.GetProperty("endLine").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Search_Failure_DoesNotAppendTelemetry()
    {
        var root = await NewIndexedCliWorkspaceAsync();

        (await RunCliAsync(root, "search", "foo", "--json")).ExitCode.ShouldBe(0);
        QueryTelemetry.Count(root).ShouldBe(1);

        File.WriteAllBytes(
            SqueezrPaths.TrigramIndexPath(root),
            Enumerable.Repeat((byte)0xAA, 40).ToArray());
        var failed = await RunCliAsync(root, "search", "foo", "--json");
        failed.ExitCode.ShouldBe(1);

        QueryTelemetry.Count(root).ShouldBe(1);
    }

    [Fact]
    public async Task SymbolLookup_ClippedAnswer_FreshAndCacheHit_RecordTheSamePair()
    {
        var root = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(root);

        // 14 tokens: the header lands, the body is refused, the one member row does not fit — so the answer carries
        // a hit that reports its body as left out and ONLY the clipped guard keeps the row honest. A ceiling that
        // refuses the header would leave zero hits and Any(bodyOmitted) would come out false for every formula.
        var (cut, rows) = await LookupTwiceAsync(harness, root, "Calc", maxTokens: 14);

        HitOf(cut).GetProperty("bodyOmitted").GetBoolean().ShouldBeTrue();
        rows[0].CacheHit.ShouldBeFalse();
        rows[1].CacheHit.ShouldBeTrue();
        rows[1].Truncated.ShouldBe(rows[0].Truncated);
        rows[1].Mapped.ShouldBe(rows[0].Mapped);
        rows[1].TokensUsed.ShouldBe(rows[0].TokensUsed);
        rows[0].Hits.ShouldBeGreaterThan(0);
        rows.ShouldAllBe(row => row.Truncated && row.Mapped == false);
    }

    [Fact]
    public async Task SymbolLookup_CompleteAnswer_FreshAndCacheHit_RecordTheSamePair()
    {
        var root = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(root);

        var (full, rows) = await LookupTwiceAsync(harness, root, "Calc", maxTokens: 600);

        // this leg pins complete-vs-clipped agreement on the two roads, and says so on the answer itself: the body
        // arrived, so the hit carries no mapped marker at all. The mapped distinction is carried by
        // TruncationStateCrossingTests, where the same fixture is answered with a member map instead of a body
        var hit = HitOf(full);
        hit.GetProperty("body").GetString()!.ShouldContain("a + b");
        hit.TryGetProperty("bodyOmitted", out _).ShouldBeFalse();

        rows[0].CacheHit.ShouldBeFalse();
        rows[1].CacheHit.ShouldBeTrue();
        rows[1].Truncated.ShouldBe(rows[0].Truncated);
        rows[1].Mapped.ShouldBe(rows[0].Mapped);
        rows[1].TokensUsed.ShouldBe(rows[0].TokensUsed);
        rows.ShouldAllBe(row => !row.Truncated && row.Mapped == false);
    }

    [Fact]
    public async Task BudgetedTools_WithoutMappedState_NameTheStateOnEveryRow()
    {
        var root = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(root);

        await harness.Client.CallToolAsync(
            "code_search", new Dictionary<string, object?> { ["query"] = "Add" }, cancellationToken: CancellationToken.None);
        await harness.Client.CallToolAsync(
            "repo_map", new Dictionary<string, object?>(), cancellationToken: CancellationToken.None);
        await harness.Client.CallToolAsync(
            "expand_span",
            new Dictionary<string, object?> { ["file"] = "src/Calc.cs", ["startLine"] = 3 },
            cancellationToken: CancellationToken.None);
        await harness.Client.CallToolAsync(
            "find_references", new Dictionary<string, object?> { ["symbol"] = "Add" }, cancellationToken: CancellationToken.None);
        await harness.Client.CallToolAsync(
            "index_status", new Dictionary<string, object?>(), cancellationToken: CancellationToken.None);

        var rows = QueryTelemetry.ReadAll(root);
        rows.Select(row => row.Tool).OrderBy(tool => tool, StringComparer.Ordinal)
            .ShouldBe(["code_search", "expand_span", "find_references", "index_status", "repo_map"]);
        rows.ShouldAllBe(row => row.Mapped == false);
        File.ReadAllLines(SqueezrPaths.QueriesLogPath(root))
            .ShouldAllBe(line => line.Contains("\"mapped\":false", StringComparison.Ordinal));
    }

    [Fact]
    public async Task McpToolCalls_ShareSessionIdWithinConnection()
    {
        var root = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(root);

        var search = await harness.Client.CallToolAsync(
            "code_search",
            new Dictionary<string, object?> { ["query"] = "Add" },
            cancellationToken: CancellationToken.None);
        search.IsError.ShouldNotBe(true);

        var expand = await harness.Client.CallToolAsync(
            "expand_span",
            new Dictionary<string, object?> { ["file"] = "src/Calc.cs", ["startLine"] = 3 },
            cancellationToken: CancellationToken.None);
        expand.IsError.ShouldNotBe(true);

        var entries = QueryTelemetry.ReadAll(root);
        entries.Count.ShouldBe(2);
        entries.ShouldAllBe(e => e.SessionId != Guid.Empty);
        entries.Select(e => e.SessionId).Distinct().Count().ShouldBe(1);
        entries.Select(e => e.Tool).OrderBy(t => t, StringComparer.Ordinal)
            .ShouldBe(["code_search", "expand_span"]);
        var parameters = (JsonElement)entries.Single(e => e.Tool == "expand_span").Params!;
        parameters.GetProperty("file").GetString().ShouldBe("src/Calc.cs");
    }

    [Fact]
    public void QuerySession_Id_StaysConstantWithinProcess()
    {
        QuerySession.Id.ShouldNotBe(Guid.Empty);
        QuerySession.Id.ShouldBe(QuerySession.Id);
    }

    // returns the fresh answer beside the rows: a leg whose precondition is only a comment goes vacuum the moment
    // the token accounting drifts, so the state the ledger is compared against has to be asserted on the answer too
    private static async Task<(JsonElement Fresh, List<QueryTelemetryEntry> Rows)> LookupTwiceAsync(
        McpTestHarness harness, string root, string name, int maxTokens)
    {
        var arguments = new Dictionary<string, object?> { ["name"] = name, ["maxTokens"] = maxTokens };

        var fresh = await CallAsync(harness, arguments);
        await CallAsync(harness, arguments);

        // both calls carry one ceiling on purpose: what this pair pins is the fresh answer and the hit that
        // replays it, and a hit only exists under an identical key. The ceiling-varying legs — the ones that must
        // NOT hit since 115/T7 put the budget into the symbol_lookup key — are SymbolLookupBudgetCacheKeyTests
        return (fresh, QueryTelemetry.ReadAll(root).Where(row => row.Tool == "symbol_lookup").ToList());
    }

    private static async Task<JsonElement> CallAsync(
        McpTestHarness harness, Dictionary<string, object?> arguments)
    {
        var result = await harness.Client.CallToolAsync(
            "symbol_lookup", arguments, cancellationToken: CancellationToken.None);
        result.IsError.ShouldNotBe(true);
        return JsonDocument.Parse(result.Content.ShouldHaveSingleItem().ToString()!).RootElement.Clone();
    }

    private static JsonElement HitOf(JsonElement answer) =>
        answer.GetProperty("data").EnumerateArray().ShouldHaveSingleItem();

    private static async Task<string> NewIndexedCliWorkspaceAsync()
    {
        var root = TestDirs.NewTempDir();
        (await RunCliAsync(root, "init", "--json")).ExitCode.ShouldBe(0);
        WriteFile(root, "src/Foo.cs", "public class Foo { string foo = \"foobody\"; }");
        (await RunCliAsync(root, "add", ".", "--name", "dot", "--json")).ExitCode.ShouldBe(0);
        (await RunCliAsync(root, "index", "--json")).ExitCode.ShouldBe(0);
        return root;
    }

    private static string NewIndexedWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init("demo");
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        WriteFile(workspace, "repo/src/Calc.cs", """
            namespace Demo;

            public class Calc
            {
                public int Add(int a, int b) => a + b;
            }
            """);
        manager.AddRepo("repo", "demo");
        new WorkspaceIndexer().Index(workspace);
        return workspace;
    }

    private static void WriteFile(string root, string relativePath, string content)
    {
        var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private static Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(
        string workingDir, params string[] args) => CliTestProcess.RunAsync(workingDir, args);
}
