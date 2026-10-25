using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Mcp;

public class McpProtocolSmokeTests
{
    private const string Needle = "smokeneedle";

    [Fact]
    public async Task Initialize_ReturnsSqueezrServerInfo()
    {
        await using var harness = await McpTestHarness.StartAsync(NewSmokeWorkspace());

        harness.Client.ServerInfo.Name.ShouldBe("squeezr");
        harness.Client.ServerInfo.Version.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task ListTools_ReturnsExactlySixToolsWithDescriptionsAndInputSchemas()
    {
        await using var harness = await McpTestHarness.StartAsync(NewSmokeWorkspace());

        var tools = await harness.Client.ListToolsAsync(cancellationToken: CancellationToken.None);

        tools.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal)
            .ShouldBe(["code_pack", "code_search", "expand_span", "find_references", "index_status", "repo_map", "symbol_lookup"]);
        foreach (var tool in tools)
        {
            tool.Description.ShouldNotBeNullOrWhiteSpace($"{tool.Name} must expose a non-empty description");
            tool.Description.ShouldNotContain("when available");
            tool.JsonSchema.ValueKind.ShouldBe(JsonValueKind.Object);
            tool.JsonSchema.GetProperty("type").GetString().ShouldBe("object");
        }
    }

    [Fact]
    public async Task CodeSearch_HitCoordinates_ExpandSpan_ReturnsSpanText()
    {
        var workspace = NewSmokeWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var search = await harness.Client.CallToolAsync(
            "code_search",
            new Dictionary<string, object?> { ["query"] = Needle },
            cancellationToken: CancellationToken.None);

        search.IsError.ShouldNotBe(true);
        var hit = ParseEnvelope(search).GetProperty("data").EnumerateArray().ShouldHaveSingleItem();
        var file = hit.GetProperty("file").GetString()!;
        var line = hit.GetProperty("line").GetInt32();

        var expand = await harness.Client.CallToolAsync(
            "expand_span",
            new Dictionary<string, object?> { ["file"] = file, ["startLine"] = line, ["endLine"] = line },
            cancellationToken: CancellationToken.None);

        expand.IsError.ShouldNotBe(true);
        var span = ParseEnvelope(expand).GetProperty("data").GetProperty("span").EnumerateArray()
            .Select(l => l.GetString()!).ToList();
        span.ShouldContain(l => l.Contains(Needle));
    }

    [Fact]
    public async Task AllToolCalls_ReturnCompleteEnvelope()
    {
        var workspace = NewSmokeWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        string[] expectedKeys = ["cost", "data", "indexAge", "staleFiles", "tokensUsed", "truncated", "warnings"];

        var search = await CallTool(harness, "code_search", new Dictionary<string, object?> { ["query"] = Needle });
        var searchEnvelope = ParseEnvelope(search);
        var searchHit = searchEnvelope.GetProperty("data").EnumerateArray().ShouldHaveSingleItem();

        var calls = new (string Tool, Dictionary<string, object?> Args)[]
        {
            ("expand_span", new Dictionary<string, object?>
            {
                ["file"] = searchHit.GetProperty("file").GetString()!,
                ["startLine"] = searchHit.GetProperty("line").GetInt32(),
                ["endLine"] = searchHit.GetProperty("line").GetInt32()
            }),
            ("repo_map", new Dictionary<string, object?> { ["depth"] = 3 }),
            ("symbol_lookup", new Dictionary<string, object?> { ["name"] = "Store", ["maxTokens"] = 500 }),
            ("find_references", new Dictionary<string, object?> { ["symbol"] = "Add" }),
            ("index_status", [])
        };

        var envelopes = new List<JsonElement> { searchEnvelope };
        foreach (var (tool, args) in calls)
        {
            envelopes.Add(ParseEnvelope(await CallTool(harness, tool, args)));
        }

        foreach (var envelope in envelopes)
        {
            envelope.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal)
                .ShouldBe(expectedKeys);
            envelope.GetProperty("data").ValueKind.ShouldNotBe(JsonValueKind.Null);
            envelope.GetProperty("warnings").ValueKind.ShouldBe(JsonValueKind.Array);
            envelope.GetProperty("staleFiles").ValueKind.ShouldBe(JsonValueKind.Array);
        }

        searchEnvelope.GetProperty("indexAge").ValueKind.ShouldNotBe(JsonValueKind.Null);
        envelopes[1].GetProperty("data").GetProperty("span").EnumerateArray().ShouldNotBeEmpty();
        envelopes[2].GetProperty("data").EnumerateArray().ShouldNotBeEmpty();
        var store = envelopes[3].GetProperty("data").EnumerateArray().ShouldHaveSingleItem();
        store.GetProperty("file").GetString().ShouldBe("src/store.ts");
        envelopes[4].GetProperty("data").EnumerateArray().ShouldNotBeEmpty();

        var status = envelopes[5].GetProperty("data");
        var repo = status.GetProperty("repos").EnumerateArray().ShouldHaveSingleItem();
        repo.GetProperty("files").GetInt32().ShouldBe(2);
        repo.GetProperty("symbols").GetInt32().ShouldBeGreaterThan(0);
        repo.GetProperty("chunks").GetInt32().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task IndexStatus_EmbeddingsCoverage_ReflectsMetaStoreState()
    {
        var workspace = NewSmokeWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var before = ParseEnvelope(await CallTool(harness, "index_status", [])).GetProperty("data");
        var chunks = before.GetProperty("repos").EnumerateArray().Single().GetProperty("chunks").GetInt32();
        before.GetProperty("embeddings").GetProperty("pending").GetInt32().ShouldBe(chunks);
        before.GetProperty("embeddings").GetProperty("coverage").GetDouble().ShouldBe(0);

        using (var meta = new MetaStore(SqueezrPaths.MetaDbPath(workspace)))
        {
            var chunkIds = meta.ListRepos()
                .SelectMany(r => meta.ListFiles(r.Id))
                .SelectMany(f => meta.ListChunksByFile(f.Id))
                .Select(c => c.Id)
                .ToList();
            meta.MarkChunksEmbedded(chunkIds, "m1");
        }

        var after = ParseEnvelope(await CallTool(harness, "index_status", [])).GetProperty("data");
        after.GetProperty("embeddings").GetProperty("pending").GetInt32().ShouldBe(0);
        after.GetProperty("embeddings").GetProperty("coverage").GetDouble().ShouldBe(1);
    }

    private static async Task<CallToolResult> CallTool(
        McpTestHarness harness, string tool, Dictionary<string, object?> args)
    {
        var result = await harness.Client.CallToolAsync(tool, args, cancellationToken: CancellationToken.None);
        result.IsError.ShouldNotBe(true);
        return result;
    }

    private static JsonElement ParseEnvelope(CallToolResult result)
    {
        var json = result.Content.ShouldHaveSingleItem().ToString()!;
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static string NewSmokeWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init("demo");
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        WriteFile(workspace, "repo/src/Calc.cs", """
            namespace Demo;

            public class Calc
            {
                public string Term = "smokeneedle";

                public int Add(int a, int b)
                {
                    return a + b;
                }
            }

            public class Runner
            {
                public int Go() => new Calc().Add(1, 2);
            }
            """);
        WriteFile(workspace, "repo/src/store.ts", """
            export class Store {
              find(id: number): number {
                return id;
              }
            }
            """);
        manager.AddRepo("repo", "demo");
        new WorkspaceIndexer().Index(workspace);
        return workspace;
    }

    private static void WriteFile(string workspace, string relativePath, string content)
    {
        var fullPath = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
