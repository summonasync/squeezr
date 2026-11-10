using System.Text;
using System.Text.Json;
using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Tests.Mcp;

namespace Squeezr.Tests.Cli;

// review-115-04 F3: the MCP envelope, the CLI document and the ledger row are three serializers of one answer,
// and nothing used to cross-check them. One workspace, one symbol, all three read out on the same call.
[Collection("ProcessGlobals")]
public sealed class TruncationStateCrossingTests
{
    // 120 tokens refuse the body and still carry the whole member map: mapped, nothing lost
    private const int MappedBudget = 120;

    // 40 tokens refuse the body and lose member rows too: that one really is cut
    private const int ClippedBudget = 40;

    // the pack pays for the same map plus caller rows, so it needs a little more room to stay mapped
    private const int PackBudget = 160;

    // 40 tokens leave the map half-rendered and no room for the caller row: a pack that really lost something
    private const int PackClippedBudget = 40;

    private const int MethodCount = 12;

    [Fact]
    public async Task MappedAnswer_EnvelopeCliDocumentAndLedgerRows_AgreeOnTheState()
    {
        var workspace = NewFixtureWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var envelope = await CallToolAsync(harness, "symbol_lookup", Args("Huge", MappedBudget));
        var cli = await RunCliAsync(workspace, "symbol", "Huge", "--max-tokens", MappedBudget.ToString(), "--json");
        cli.ExitCode.ShouldBe(0);

        using var cliDoc = JsonDocument.Parse(cli.StdOut.Trim());
        var rows = QueryTelemetry.ReadAll(workspace);
        var mcpRow = rows.Single(row => row.Tool == "symbol_lookup");
        var cliRow = rows.Single(row => row.Tool == "symbol");

        envelope.GetProperty("truncated").GetBoolean().ShouldBeFalse();
        envelope.GetProperty("cost").GetProperty("complete").GetBoolean().ShouldBeTrue();
        EnvelopeHit(envelope).GetProperty("bodyOmitted").GetBoolean().ShouldBeTrue();

        cliDoc.RootElement.GetProperty("state").GetString().ShouldBe("mapped");
        cliDoc.RootElement.GetProperty("truncated").GetBoolean().ShouldBeFalse();
        CliHit(cliDoc.RootElement).GetProperty("bodyOmitted").GetBoolean().ShouldBeTrue();

        foreach (var row in new[] { mcpRow, cliRow })
        {
            row.Truncated.ShouldBeFalse();
            row.Mapped.ShouldBe(true);
        }

        var billed = envelope.GetProperty("tokensUsed").GetInt32();
        billed.ShouldBeGreaterThan(0);
        cliDoc.RootElement.GetProperty("tokensUsed").GetInt32().ShouldBe(billed);
        mcpRow.TokensUsed.ShouldBe(billed);
        cliRow.TokensUsed.ShouldBe(billed);
    }

    [Fact]
    public async Task ClippedAnswer_EnvelopeCliDocumentAndLedgerRows_AgreeOnTheState()
    {
        // a workspace and a connection of its own: BUGS/024 keeps maxTokens out of the symbol_lookup cache key,
        // so a second ceiling on one connection would replay the mapped answer instead of measuring this one
        var workspace = NewFixtureWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var envelope = await CallToolAsync(harness, "symbol_lookup", Args("Huge", ClippedBudget));
        var cli = await RunCliAsync(workspace, "symbol", "Huge", "--max-tokens", ClippedBudget.ToString(), "--json");
        cli.ExitCode.ShouldBe(0);

        using var cliDoc = JsonDocument.Parse(cli.StdOut.Trim());
        var rows = QueryTelemetry.ReadAll(workspace);
        var mcpRow = rows.Single(row => row.Tool == "symbol_lookup");
        var cliRow = rows.Single(row => row.Tool == "symbol");

        envelope.GetProperty("truncated").GetBoolean().ShouldBeTrue();
        envelope.GetProperty("cost").GetProperty("complete").GetBoolean().ShouldBeFalse();
        // the leg has to be a clipped answer WITH a refused body: a header refusal would leave zero hits and the
        // mapped formula would come out false whatever the guard says, so the pin would measure nothing
        EnvelopeHit(envelope).GetProperty("bodyOmitted").GetBoolean().ShouldBeTrue();

        cliDoc.RootElement.GetProperty("state").GetString().ShouldBe("clipped");
        cliDoc.RootElement.GetProperty("truncated").GetBoolean().ShouldBeTrue();

        foreach (var row in new[] { mcpRow, cliRow })
        {
            row.Truncated.ShouldBeTrue();
            row.Mapped.ShouldBe(false);
        }

        var billed = envelope.GetProperty("tokensUsed").GetInt32();
        cliDoc.RootElement.GetProperty("tokensUsed").GetInt32().ShouldBe(billed);
        mcpRow.TokensUsed.ShouldBe(billed);
        cliRow.TokensUsed.ShouldBe(billed);
    }

    [Fact]
    public async Task SymbolLookup_MappedAnswer_FreshAndCacheHit_RecordTheSamePair()
    {
        var workspace = NewFixtureWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        await CallToolAsync(harness, "symbol_lookup", Args("Huge", MappedBudget));
        await CallToolAsync(harness, "symbol_lookup", Args("Huge", MappedBudget));

        var rows = LedgerRowsFor(workspace, "symbol_lookup");
        rows.Count.ShouldBe(2);
        rows[0].CacheHit.ShouldBeFalse();
        rows[1].CacheHit.ShouldBeTrue();
        rows.ShouldAllBe(row => !row.Truncated && row.Mapped == true);
        rows[1].Truncated.ShouldBe(rows[0].Truncated);
        rows[1].Mapped.ShouldBe(rows[0].Mapped);
        rows[1].TokensUsed.ShouldBe(rows[0].TokensUsed);
        rows[0].Hits.ShouldBeGreaterThan(0);
        rows[1].Hits.ShouldBe(rows[0].Hits);
    }

    [Fact]
    public async Task CodePack_MappedPack_FreshAndCacheHit_RecordTheSamePair()
    {
        var workspace = NewFixtureWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var args = Args("Huge", PackBudget);
        var envelope = await CallToolAsync(harness, "code_pack", args);
        await CallToolAsync(harness, "code_pack", args);

        // the fixture has to be the mapped pack the pin is about, not some other state that happens to be stable
        var pack = envelope.GetProperty("data");
        pack.GetProperty("bodyOmitted").GetBoolean().ShouldBeTrue();
        envelope.GetProperty("truncated").GetBoolean().ShouldBeFalse();
        pack.GetProperty("withheldCallers").GetInt32().ShouldBe(0);

        var rows = LedgerRowsFor(workspace, "code_pack");
        rows.Count.ShouldBe(2);
        rows[0].CacheHit.ShouldBeFalse();
        rows[1].CacheHit.ShouldBeTrue();
        rows.ShouldAllBe(row => !row.Truncated && row.Mapped == true);
        rows[1].TokensUsed.ShouldBe(rows[0].TokensUsed);
        rows[0].Hits.ShouldBeGreaterThan(0);
        rows[1].Hits.ShouldBe(rows[0].Hits);

        // and the pair is anchored to the billed answer, not just to itself: two rows agreeing on zero would be
        // a stable lie, the way the symbol legs above are anchored to envelope.tokensUsed
        var billed = envelope.GetProperty("tokensUsed").GetInt32();
        billed.ShouldBeGreaterThan(0);
        rows[0].TokensUsed.ShouldBe(billed);
    }

    [Fact]
    public async Task CodePack_ClippedPack_FreshAndCacheHit_RecordTheSamePair()
    {
        var workspace = NewFixtureWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var args = Args("Huge", PackClippedBudget);
        var envelope = await CallToolAsync(harness, "code_pack", args);
        await CallToolAsync(harness, "code_pack", args);

        // the leg only bites when the pack lost rows AND still says the body stayed out: drop the clipped guard on
        // CodePackTool's mapped formula and this answer records truncated:true with mapped:true — the conflation
        // the whole feature exists to remove, so the guard below is what the assertion protects
        var pack = envelope.GetProperty("data");
        pack.GetProperty("bodyOmitted").GetBoolean().ShouldBeTrue();
        envelope.GetProperty("truncated").GetBoolean().ShouldBeTrue();
        (pack.GetProperty("withheldCallers").GetInt32() > 0 || pack.GetProperty("membersOmitted").GetInt32() > 0)
            .ShouldBeTrue("a clipped pack must lose something on its own account: callers or member rows");

        var rows = LedgerRowsFor(workspace, "code_pack");
        rows.Count.ShouldBe(2);
        rows[0].CacheHit.ShouldBeFalse();
        rows[1].CacheHit.ShouldBeTrue();
        rows.ShouldAllBe(row => row.Truncated && row.Mapped == false);
        rows[1].Truncated.ShouldBe(rows[0].Truncated);
        rows[1].Mapped.ShouldBe(rows[0].Mapped);
        rows[1].TokensUsed.ShouldBe(rows[0].TokensUsed);

        var billed = envelope.GetProperty("tokensUsed").GetInt32();
        billed.ShouldBeGreaterThan(0);
        rows[0].TokensUsed.ShouldBe(billed);
    }

    private static Dictionary<string, object?> Args(string name, int maxTokens) =>
        new() { ["name"] = name, ["maxTokens"] = maxTokens };

    private static async Task<JsonElement> CallToolAsync(
        McpTestHarness harness, string tool, Dictionary<string, object?> arguments)
    {
        var result = await harness.Client.CallToolAsync(
            tool, arguments, cancellationToken: CancellationToken.None);
        result.IsError.ShouldNotBe(true);
        return JsonDocument.Parse(result.Content.ShouldHaveSingleItem().ToString()!).RootElement.Clone();
    }

    private static JsonElement EnvelopeHit(JsonElement envelope) =>
        envelope.GetProperty("data").EnumerateArray().ShouldHaveSingleItem();

    private static JsonElement CliHit(JsonElement document) =>
        document.GetProperty("matches").EnumerateArray().ShouldHaveSingleItem();

    private static List<QueryTelemetryEntry> LedgerRowsFor(string workspace, string tool) =>
        QueryTelemetry.ReadAll(workspace).Where(row => row.Tool == tool).ToList();

    private static Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(
        string workingDir, params string[] args) => CliTestProcess.RunAsync(workingDir, args);

    private static string NewFixtureWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init("demo");
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        WriteFile(workspace, "repo/src/Huge.cs", HugeClass());
        WriteFile(workspace, "repo/src/Uses.cs", """
            namespace Demo;

            public sealed class Uses
            {
                public int Run() => new Huge().Method01(1);
            }
            """);
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
