using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Lite;

public class LiteServerSmokeTests
{
    [Fact]
    public async Task Initialize_ReturnsSqzrliteServerInfo()
    {
        await using var harness = await LiteMcpTestHarness.StartAsync(TestDirs.NewTempDir());

        harness.Client.ServerInfo.Name.ShouldBe("sqzrlite");
        harness.Client.ServerInfo.Version.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task ListTools_ReturnsTheSevenLiteTools()
    {
        await using var harness = await LiteMcpTestHarness.StartAsync(TestDirs.NewTempDir());

        var tools = await harness.Client.ListToolsAsync(cancellationToken: CancellationToken.None);

        tools.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal)
            .ShouldBe(["code_pack", "code_search", "expand_span", "find_references", "index_status", "repo_map", "symbol_lookup"]);
    }

    [Fact]
    public async Task ToolCallUninitializedWorkspace_ReturnsStructuredError_ServerStaysAlive()
    {
        await using var harness = await LiteMcpTestHarness.StartAsync(TestDirs.NewTempDir());

        var result = await harness.Client.CallToolAsync(
            "index_status",
            new Dictionary<string, object?>(),
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldBe(true);
        var payload = result.StructuredContent.ShouldNotBeNull();
        payload.GetProperty("code").GetString().ShouldBe("workspace_not_initialized");
        payload.GetProperty("nextSteps").EnumerateArray()
            .Select(step => step.GetString()!)
            .ShouldContain(step => step.Contains("sqzrlite init", StringComparison.Ordinal));
        payload.ToString().ShouldNotContain("squeezr init");

        var tools = await harness.Client.ListToolsAsync(cancellationToken: CancellationToken.None);
        tools.Count.ShouldBe(7);
    }

    [Fact]
    public async Task IndexStatus_ReportsSemanticUnsupported_NeverFakeSemanticZeroes()
    {
        var workspace = NewIndexedWorkspace(withFullProfileConfig: false);
        await using var harness = await LiteMcpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "index_status",
            new Dictionary<string, object?>(),
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        var data = envelope.GetProperty("data");
        DataKeys(data).ShouldContain("semantic");
        DataKeys(data).ShouldNotContain("embeddings");
        DataKeys(data).ShouldNotContain("qdrant");
        data.GetProperty("semantic").GetString().ShouldBe("unsupported");
        data.GetProperty("repos").EnumerateArray().ShouldHaveSingleItem();
        data.GetProperty("kindChunks").EnumerateObject().ShouldNotBeEmpty();
    }

    [Fact]
    public async Task CodeSearch_AnswersOnTheExactArm_AfterPlainIndex()
    {
        var workspace = NewIndexedWorkspace(withFullProfileConfig: false);
        await using var harness = await LiteMcpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "code_search",
            new Dictionary<string, object?> { ["query"] = "Add(int a, int b)" },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        var hits = envelope.GetProperty("data").EnumerateArray().ToList();
        hits.ShouldNotBeEmpty();
        hits.Select(hit => hit.GetProperty("source").GetString())
            .ShouldAllBe(source => source == "exact");
        envelope.GetProperty("cost").GetProperty("baselineTokens").GetInt64().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task RepoMap_SymbolLookup_ExpandSpan_FindReferences_CodePack_AllAnswer()
    {
        var workspace = NewIndexedWorkspace(withFullProfileConfig: false);
        await using var harness = await LiteMcpTestHarness.StartAsync(workspace);

        var map = ParseEnvelope(await harness.Client.CallToolAsync(
            "repo_map",
            new Dictionary<string, object?>(),
            cancellationToken: CancellationToken.None));
        map.GetProperty("data").EnumerateArray().ShouldNotBeEmpty();

        var symbol = ParseEnvelope(await harness.Client.CallToolAsync(
            "symbol_lookup",
            new Dictionary<string, object?> { ["name"] = "Add" },
            cancellationToken: CancellationToken.None));
        symbol.GetProperty("data").EnumerateArray().ShouldNotBeEmpty();

        var span = ParseEnvelope(await harness.Client.CallToolAsync(
            "expand_span",
            new Dictionary<string, object?> { ["file"] = "src/Calc.cs", ["startLine"] = 4 },
            cancellationToken: CancellationToken.None));
        span.GetProperty("data").GetProperty("span").EnumerateArray().ShouldNotBeEmpty();

        var refs = ParseEnvelope(await harness.Client.CallToolAsync(
            "find_references",
            new Dictionary<string, object?> { ["symbol"] = "Add" },
            cancellationToken: CancellationToken.None));
        refs.GetProperty("data").EnumerateArray().ShouldNotBeEmpty();

        var pack = ParseEnvelope(await harness.Client.CallToolAsync(
            "code_pack",
            new Dictionary<string, object?> { ["name"] = "Calc" },
            cancellationToken: CancellationToken.None));
        pack.GetProperty("data").GetProperty("callers").EnumerateArray().ShouldNotBeEmpty();
        pack.GetProperty("data").GetProperty("complete").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task FullProfileConfig_ServeStartsAndToolsAnswer_WithToleranceWarnings()
    {
        var workspace = NewIndexedWorkspace(withFullProfileConfig: true);
        var diagnostics = new StringWriter();
        await using var harness = await LiteMcpTestHarness.StartAsync(workspace, stderr: diagnostics);

        var result = await harness.Client.CallToolAsync(
            "code_search",
            new Dictionary<string, object?> { ["query"] = "Calc" },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        ParseEnvelope(result).GetProperty("data").EnumerateArray().ShouldNotBeEmpty();
        diagnostics.ToString().ShouldContain("config section 'embeddings'");
        diagnostics.ToString().ShouldContain("config section 'qdrant'");
    }

    private static JsonElement ParseEnvelope(CallToolResult result)
    {
        var json = result.Content.ShouldHaveSingleItem().ShouldBeOfType<TextContentBlock>().Text;
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static List<string> DataKeys(JsonElement data) =>
        [.. data.EnumerateObject().Select(property => property.Name)];

    private static string NewIndexedWorkspace(bool withFullProfileConfig)
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init("demo");
        WriteFile(workspace, "repo/src/Calc.cs", """
            namespace Demo;

            public class Calc
            {
                /// <summary>Adds numbers.</summary>
                public int Add(int a, int b)
                {
                    return a + b;
                }

                public string Add(string a, string b)
                {
                    return a + b;
                }
            }
            """);
        WriteFile(workspace, "repo/src/Runner.cs", """
            namespace Demo;

            public class Runner
            {
                public int Go()
                {
                    return new Calc().Add(1, 2);
                }
            }
            """);
        manager.AddRepo("repo", "demo");
        new WorkspaceIndexer().Index(workspace);

        if (withFullProfileConfig)
        {
            WriteFullProfileConfig(workspace);
        }

        return workspace;
    }

    private static void WriteFullProfileConfig(string workspace)
    {
        var configPath = Squeezr.Core.Storage.SqueezrPaths.ConfigPath(workspace);
        var json = """
            {
              "chunking": { "maxTokensPerChunk": 512, "fallbackWindowLines": 60, "fallbackOverlapLines": 15 },
              "indexing": { "ignore": ["**/node_modules/**"], "maxFileSizeKb": 512, "noSemantic": false, "rankExclude": [] },
              "watch": { "enabled": false },
              "embeddings": {
                "provider": "ollama",
                "ollama": { "model": "nomic-embed-text", "endpoint": "http://localhost:11434" }
              },
              "qdrant": { "endpoint": "http://localhost:6334" }
            }
            """;
        File.WriteAllText(configPath, json);
    }

    private static void WriteFile(string root, string relativePath, string content)
    {
        var fullPath = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
