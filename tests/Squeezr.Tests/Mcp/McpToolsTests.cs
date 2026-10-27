using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Mcp;

[Collection("ProcessGlobals")]
public class McpToolsTests
{
    [Fact]
    public async Task ListTools_ExposesAllToolsWithParameterSchemas()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var tools = await harness.Client.ListToolsAsync(cancellationToken: CancellationToken.None);

        tools.Count.ShouldBe(7);
        var search = tools.Single(t => t.Name == "code_search");
        search.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ShouldContain("query");
        search.JsonSchema.GetProperty("required").EnumerateArray().Select(r => r.GetString()).ShouldContain("query");

        var symbol = tools.Single(t => t.Name == "symbol_lookup");
        symbol.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ShouldContain("name");
        symbol.JsonSchema.GetProperty("required").EnumerateArray().Select(r => r.GetString()).ShouldContain("name");

        var refs = tools.Single(t => t.Name == "find_references");
        refs.JsonSchema.GetProperty("required").EnumerateArray().Select(r => r.GetString()).ShouldContain("symbol");

        var map = tools.Single(t => t.Name == "repo_map");
        map.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ShouldContain("depth");

        var expand = tools.Single(t => t.Name == "expand_span");
        expand.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ShouldContain("startLine");
        expand.JsonSchema.GetProperty("required").EnumerateArray().Select(r => r.GetString()).ShouldBe(["file", "startLine"]);

        tools.Single(t => t.Name == "index_status").ShouldNotBeNull();
    }

    [Fact]
    public async Task CodeSearch_ReturnsEnvelopeWithMatches()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "code_search",
            new Dictionary<string, object?> { ["query"] = "uniqneedle" },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        var hit = envelope.GetProperty("data").EnumerateArray().ShouldHaveSingleItem();
        hit.GetProperty("repo").GetString().ShouldBe("demo");
        hit.GetProperty("file").GetString().ShouldBe("a.cs");
        hit.GetProperty("line").GetInt32().ShouldBe(1);
        hit.GetProperty("source").GetString().ShouldBe("exact");
        hit.GetProperty("snippet").GetString()!.ShouldContain("uniqneedle");
        envelope.GetProperty("tokensUsed").GetInt32().ShouldBeGreaterThan(0);
        envelope.GetProperty("truncated").GetBoolean().ShouldBeFalse();
        envelope.GetProperty("indexAge").ValueKind.ShouldNotBe(JsonValueKind.Null);
        envelope.GetProperty("staleFiles").EnumerateArray().ShouldBeEmpty();
    }

    [Fact]
    public async Task CodeSearch_WithDeadQdrant_FallsBackToExactWithWarning()
    {
        var workspace = NewIndexedWorkspace();
        try
        {
            Environment.SetEnvironmentVariable("SQUEEZR_TOOLS_TEST_KEY", "dummy");
            ConfigFile.Save(workspace, SqueezrConfig.CreateDefault() with
            {
                Embeddings = new EmbeddingsConfig
                {
                    Provider = "openai",
                    OpenAiCompatible = new OpenAiCompatibleOptions { ApiKeyEnv = "SQUEEZR_TOOLS_TEST_KEY" }
                },
                Qdrant = new QdrantConfig { Endpoint = "http://localhost:9" }
            });
            await using var harness = await McpTestHarness.StartAsync(workspace);

            var result = await harness.Client.CallToolAsync(
                "code_search",
                new Dictionary<string, object?> { ["query"] = "uniqneedle" },
                cancellationToken: CancellationToken.None);

            result.IsError.ShouldNotBe(true);
            var envelope = ParseEnvelope(result);
            envelope.GetProperty("data").EnumerateArray().Count().ShouldBe(1);
            envelope.GetProperty("warnings").EnumerateArray()
                .Select(w => w.GetString()!).ShouldContain(w => w.Contains("exact search only"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SQUEEZR_TOOLS_TEST_KEY", null);
        }
    }

    [Fact]
    public async Task CodeSearch_WithHeldLock_ReportsStaleFiles()
    {
        var workspace = NewIndexedWorkspace();
        WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"uniqneedle\"; int Extra = 1; }");
        using (IndexLock.Acquire(SqueezrPaths.StoreDir(workspace)))
        {
            await using var harness = await McpTestHarness.StartAsync(workspace);

            var result = await harness.Client.CallToolAsync(
                "code_search",
                new Dictionary<string, object?> { ["query"] = "uniqneedle" },
                cancellationToken: CancellationToken.None);

            result.IsError.ShouldNotBe(true);
            var envelope = ParseEnvelope(result);
            envelope.GetProperty("data").EnumerateArray().Count().ShouldBe(1);
            envelope.GetProperty("staleFiles").EnumerateArray()
                .Select(s => s.GetString()!).ShouldBe(["demo/a.cs"]);
        }
    }

    [Fact]
    public async Task CodeSearch_AfterInlineRefresh_ReturnsFreshContent()
    {
        var workspace = NewIndexedWorkspace();
        WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"uniqneedle\"; string Fresh = \"freshmarker\"; }");
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "code_search",
            new Dictionary<string, object?> { ["query"] = "uniqneedle" },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        var hit = envelope.GetProperty("data").EnumerateArray().ShouldHaveSingleItem();
        hit.GetProperty("snippet").GetString()!.ShouldContain("freshmarker");
        envelope.GetProperty("staleFiles").EnumerateArray().ShouldBeEmpty();
    }

    [Fact]
    public async Task CodeSearch_WithoutMaxTokens_AppliesDefaultBudget()
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init("demo");
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        manager.AddRepo("repo", "demo");
        foreach (var i in Enumerable.Range(1, 100))
        {
            WriteFile(workspace, $"repo/f{i:000}.cs", $"class C{i} {{ string Term = \"uniqneedle filler with some extra words to burn the budget\"; }}");
        }
        new WorkspaceIndexer().Index(workspace);
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "code_search",
            new Dictionary<string, object?> { ["query"] = "uniqneedle" },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        envelope.GetProperty("tokensUsed").GetInt32().ShouldBeLessThanOrEqualTo(2000);
        envelope.GetProperty("truncated").GetBoolean().ShouldBeTrue();
        envelope.GetProperty("data").EnumerateArray().ShouldNotBeEmpty();
        envelope.GetRawText().ShouldNotContain("--");
        envelope.GetProperty("warnings").EnumerateArray()
            .Select(w => w.GetString()!)
            .ShouldContain(w => w.StartsWith("truncated: ")
                && w.Contains("raise maxTokens or narrow (repo, prefix) (exact ")
                && w.EndsWith("semantic 0)"));
    }

    [Fact]
    public async Task CodeSearch_WithinBudget_WarnsNothingAboutTruncation()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "code_search",
            new Dictionary<string, object?> { ["query"] = "uniqneedle" },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        envelope.GetProperty("truncated").GetBoolean().ShouldBeFalse();
        envelope.GetProperty("warnings").EnumerateArray()
            .Select(w => w.GetString()!)
            .ShouldNotContain(w => w.Contains("narrow") || w.Contains("raise"));
    }

    [Fact]
    public async Task IndexStatus_ReturnsFreshnessBlock()
    {
        var workspace = NewIndexedWorkspace();
        ConfigFile.Save(workspace, SqueezrConfig.CreateDefault() with
        {
            Qdrant = new QdrantConfig { Endpoint = "http://localhost:9" }
        });
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "index_status",
            new Dictionary<string, object?>(),
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        var data = envelope.GetProperty("data");
        data.GetProperty("workspace").GetString().ShouldBe("demo");
        data.GetProperty("rootPath").GetString().ShouldBe(Path.GetFullPath(workspace));

        var repo = data.GetProperty("repos").EnumerateArray().ShouldHaveSingleItem();
        repo.GetProperty("name").GetString().ShouldBe("demo");
        repo.GetProperty("rootPath").GetString().ShouldBe(Path.GetFullPath(Path.Combine(workspace, "repo")));
        repo.GetProperty("files").GetInt32().ShouldBe(1);
        repo.GetProperty("symbols").GetInt32().ShouldBeGreaterThan(0);
        repo.GetProperty("chunks").GetInt32().ShouldBeGreaterThan(0);

        data.GetProperty("embeddings").GetProperty("pending").GetInt32().ShouldBeGreaterThanOrEqualTo(0);
        var qdrant = data.GetProperty("qdrant");
        qdrant.GetProperty("available").GetBoolean().ShouldBeFalse();
        if (qdrant.TryGetProperty("points", out var points))
            points.ValueKind.ShouldBe(JsonValueKind.Null);
        envelope.GetProperty("indexAge").ValueKind.ShouldNotBe(JsonValueKind.Null);
    }

    private static JsonElement ParseEnvelope(CallToolResult result)
    {
        var json = result.Content.ShouldHaveSingleItem().ToString()!;
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static string NewIndexedWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init("demo");
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        manager.AddRepo("repo", "demo");
        WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"uniqneedle\"; }");
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
