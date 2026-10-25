using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Mcp;

public class CodeSearchKindFilterTests
{
    private const string Needle = "kindneedlewatcher";

    [Fact]
    public async Task ToolDescription_DocumentsTheKindFilter()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var tools = await harness.Client.ListToolsAsync(cancellationToken: CancellationToken.None);
        var search = tools.Single(t => t.Name == "code_search");

        search.Description.ShouldContain("\"code\"");
        search.Description.ShouldContain("\"docs\"");
        search.Description.ShouldContain("both arms");
        search.Description.ShouldContain("match only kind=all");
    }

    [Fact]
    public async Task Kind_Code_HidesDocsFiles()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var files = await SearchFilesAsync(harness, kind: "code");

        files.ShouldBe(["src/Watcher.cs"]);
    }

    [Fact]
    public async Task Kind_Docs_HidesCodeFiles()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var files = await SearchFilesAsync(harness, kind: "docs");

        files.ShouldBe(["docs/Notes.md"]);
    }

    [Fact]
    public async Task Kind_Omitted_KeepsBothKinds()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var files = await SearchFilesAsync(harness, kind: null);

        files.OrderBy(f => f, StringComparer.Ordinal).ShouldBe(["docs/Notes.md", "src/Watcher.cs"]);
    }

    [Fact]
    public async Task Kind_UnknownValue_FailsWithTheAllowedList()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "code_search",
            new Dictionary<string, object?> { ["query"] = Needle, ["kind"] = "sql" },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldBe(true);
        var text = result.Content.ShouldHaveSingleItem().ToString()!;
        text.ShouldContain("allowed values: all, code, docs");
    }

    private static async Task<List<string>> SearchFilesAsync(McpTestHarness harness, string? kind)
    {
        var parameters = new Dictionary<string, object?> { ["query"] = Needle };
        if (kind is not null)
        {
            parameters["kind"] = kind;
        }

        var result = await harness.Client.CallToolAsync(
            "code_search", parameters, cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        using var doc = JsonDocument.Parse(result.Content.ShouldHaveSingleItem().ToString()!);
        return doc.RootElement.GetProperty("data").EnumerateArray()
            .Select(hit => hit.GetProperty("file").GetString()!)
            .ToList();
    }

    private static string NewIndexedWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init("demo");
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        WriteFile(workspace, "repo/src/Watcher.cs", $"class Watcher\n{{\n    // {Needle}\n}}\n");
        WriteFile(workspace, "repo/docs/Notes.md", $"# Notes\n\n{Needle}.\n");
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
