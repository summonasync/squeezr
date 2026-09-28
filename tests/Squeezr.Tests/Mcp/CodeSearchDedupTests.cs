using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Mcp;

public class CodeSearchDedupTests
{
    private const string Needle = "dedupneedle";

    [Fact]
    public async Task ToolDescription_DocumentsTopKAsDistinctFiles()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var tools = await harness.Client.ListToolsAsync(cancellationToken: CancellationToken.None);
        var search = tools.Single(t => t.Name == "code_search");

        search.Description.ShouldContain("One hit per file");
        search.Description.ShouldContain("alsoMatched");
        search.Description.ShouldContain("topK counts distinct files");
    }

    [Fact]
    public async Task Hit_WithoutCollapsedSpans_OmitsAlsoMatched()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "code_search",
            new Dictionary<string, object?> { ["query"] = Needle },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var hit = ParseData(result);
        hit.GetProperty("file").GetString().ShouldBe("src/Alpha.cs");
        hit.TryGetProperty("alsoMatched", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Envelope_KeepsItsKeySet_AfterTheHitShapeChange()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "code_search",
            new Dictionary<string, object?> { ["query"] = Needle },
            cancellationToken: CancellationToken.None);

        using var doc = JsonDocument.Parse(result.Content.ShouldHaveSingleItem().ToString()!);
        doc.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal)
            .ShouldBe(["data", "indexAge", "staleFiles", "tokensUsed", "truncated", "warnings"]);
    }

    private static JsonElement ParseData(CallToolResult result)
    {
        using var doc = JsonDocument.Parse(result.Content.ShouldHaveSingleItem().ToString()!);
        var data = doc.RootElement.GetProperty("data").EnumerateArray().ToList();
        return data.ShouldHaveSingleItem().Clone();
    }

    private static string NewIndexedWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init("demo");
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        WriteFile(workspace, "repo/src/Alpha.cs", $"public class Alpha {{ /* {Needle} */ }}\n");
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
