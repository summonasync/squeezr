using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Mcp;

// symbol_lookup shaped its answer by maxTokens but keyed the cached envelope without the budget, so the
// remedy feature 115 exists to make honest — "raise maxTokens" — could not be followed inside the session that
// heard it: the raised call replayed the small answer and read as proof that ceilings do nothing. These pins hold
// the executable form of the remedy, and the price it must not cost: an identical ceiling still replays.
public class SymbolLookupBudgetCacheKeyTests
{
    // 40 tokens cut the member map mid-list: a clipped answer whose own warning names the remedy
    private const int CutBudget = 40;

    // 600 tokens fit the whole class body — the answer the raised ceiling is supposed to deliver
    private const int WholeBodyBudget = 600;

    private const int MethodCount = 12;

    [Fact]
    public async Task SymbolLookup_RaisedCeilingInOneSession_ReRunsAndDeliversTheBiggerAnswer()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var cut = await LookupAsync(harness, CutBudget);
        var raised = await LookupAsync(harness, WholeBodyBudget);

        // the first leg has to be an answer that actually recommends raising the ceiling, otherwise the second
        // call proves nothing about the remedy this feature ships
        cut.GetProperty("truncated").GetBoolean().ShouldBeTrue();
        Warnings(cut).ShouldContain(w => w.Contains("raise maxTokens", StringComparison.Ordinal));
        HitOf(cut).TryGetProperty("body", out _).ShouldBeFalse();

        var raisedHit = HitOf(raised);
        raised.GetProperty("truncated").GetBoolean().ShouldBeFalse();
        raisedHit.GetProperty("body").GetString()!.ShouldContain($"return a + {MethodCount};");
        Warnings(raised).ShouldNotContain(w => w.StartsWith("truncated: ", StringComparison.Ordinal));

        var cutTokens = cut.GetProperty("tokensUsed").GetInt32();
        var raisedTokens = raised.GetProperty("tokensUsed").GetInt32();
        raisedTokens.ShouldBeGreaterThan(cutTokens);

        var rows = LedgerRows(workspace);
        rows.Count.ShouldBe(2);
        rows.ShouldAllBe(row => !row.CacheHit);
        rows[0].TokensUsed.ShouldBe(cutTokens);
        rows[1].TokensUsed.ShouldBe(raisedTokens);
        ParamsOf(rows[0]).GetProperty("maxTokens").GetInt32().ShouldBe(CutBudget);
        ParamsOf(rows[1]).GetProperty("maxTokens").GetInt32().ShouldBe(WholeBodyBudget);
    }

    [Fact]
    public async Task SymbolLookup_SameCeilingTwice_StillReplaysTheStoredEnvelope()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var first = await LookupAsync(harness, WholeBodyBudget);
        var second = await LookupAsync(harness, WholeBodyBudget);

        // a replayed hit reproduces the answer byte for byte; the whole wire cannot be compared because the
        // once-per-session observer notice rides only the first envelope of the connection
        HitOf(second).GetRawText().ShouldBe(HitOf(first).GetRawText());
        second.GetProperty("truncated").GetBoolean().ShouldBe(first.GetProperty("truncated").GetBoolean());

        var rows = LedgerRows(workspace);
        rows.Count.ShouldBe(2);
        rows[0].CacheHit.ShouldBeFalse();
        rows[1].CacheHit.ShouldBeTrue();
        rows[1].TokensUsed.ShouldBe(rows[0].TokensUsed);
        rows[1].Truncated.ShouldBe(rows[0].Truncated);
        // the hit replays the stored envelope by reference, so its cost block cannot drift either; asserting it
        // costs nothing and closes the one field the earlier pair of legs left unmeasured
        second.GetProperty("cost").GetRawText().ShouldBe(first.GetProperty("cost").GetRawText());
    }

    [Fact]
    public async Task SymbolLookup_CeilingOmitted_RecordsTheResolvedDefaultInTheKey()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        // the default is only observable through the product's own resolution — hand-building the params object
        // would assert a literal instead of the tool's behaviour
        var result = await harness.Client.CallToolAsync(
            "symbol_lookup",
            new Dictionary<string, object?> { ["name"] = "Huge" },
            cancellationToken: CancellationToken.None);
        result.IsError.ShouldNotBe(true);

        var row = LedgerRows(workspace).ShouldHaveSingleItem();
        ParamsOf(row).GetProperty("maxTokens").GetInt32().ShouldBe(SymbolLookup.DefaultMaxTokens);
        row.TokensUsed.ShouldBe(
            JsonDocument.Parse(result.Content.ShouldHaveSingleItem().ShouldBeOfType<TextContentBlock>().Text)
                .RootElement.GetProperty("tokensUsed").GetInt32());
    }

    [Fact]
    public async Task SymbolLookup_LedgerLine_NamesTheBudgetInsideParams()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        await LookupAsync(harness, CutBudget);

        var raw = File.ReadLines(SqueezrPaths.QueriesLogPath(workspace))
            .Single(line => line.Contains("\"tool\":\"symbol_lookup\"", StringComparison.Ordinal));
        raw.ShouldContain("\"maxTokens\":40");

        var parameters = JsonDocument.Parse(raw).RootElement.GetProperty("params");
        ParameterKeys(parameters).ShouldBe("name,kind,repo,maxTokens,secretGuardExtra");
        parameters.GetProperty("maxTokens").GetInt32().ShouldBe(CutBudget);
        parameters.GetRawText().ShouldBe(
            """{"name":"Huge","kind":null,"repo":null,"maxTokens":40,"secretGuardExtra":[]}""");
    }

    private static string ParameterKeys(JsonElement parameters) =>
        string.Join(',', parameters.EnumerateObject().Select(p => p.Name));

    private static async Task<JsonElement> LookupAsync(McpTestHarness harness, int maxTokens)
    {
        var result = await harness.Client.CallToolAsync(
            "symbol_lookup",
            new Dictionary<string, object?> { ["name"] = "Huge", ["maxTokens"] = maxTokens },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        return JsonDocument.Parse(
            result.Content.ShouldHaveSingleItem().ShouldBeOfType<TextContentBlock>().Text).RootElement.Clone();
    }

    private static List<QueryTelemetryEntry> LedgerRows(string workspace) =>
        QueryTelemetry.ReadAll(workspace).Where(row => row.Tool == "symbol_lookup").ToList();

    private static JsonElement ParamsOf(QueryTelemetryEntry row) => (JsonElement)row.Params!;

    private static JsonElement HitOf(JsonElement envelope) =>
        envelope.GetProperty("data").EnumerateArray().ShouldHaveSingleItem();

    private static List<string> Warnings(JsonElement envelope) =>
        envelope.GetProperty("warnings").EnumerateArray()
            .Select(w => w.GetString() ?? string.Empty)
            .ToList();

    private static string NewIndexedWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init("demo");
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        WriteFile(workspace, "repo/src/Huge.cs", HugeClass());
        manager.AddRepo("repo", "demo");
        new WorkspaceIndexer().Index(workspace);
        return workspace;
    }

    private static string HugeClass()
    {
        var text = new StringBuilder("namespace Demo;\n\npublic class Huge\n{\n");
        foreach (var index in Enumerable.Range(1, MethodCount))
        {
            text.Append($"    public int Method{index:00}(int a)\n");
            text.Append("    {\n");
            text.Append($"        return a + {index};\n");
            text.Append("    }\n\n");
        }

        return text.Append("}\n").ToString();
    }

    private static void WriteFile(string workspace, string relativePath, string content)
    {
        var fullPath = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
