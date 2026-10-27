using System.Text.Json;
using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Mcp;

public class SymbolParamAliasToolTests
{
    [Fact]
    public async Task SymbolLookup_AliasSymbol_MatchesCanonicalResult()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var canonical = await CallRaw(harness, "symbol_lookup", new Dictionary<string, object?> { ["name"] = "Add" });
        var alias = await CallRaw(harness, "symbol_lookup", new Dictionary<string, object?> { ["symbol"] = "Add" });

        canonical.IsError.ShouldNotBe(true);
        alias.IsError.ShouldNotBe(true);
        DataOf(alias).ShouldBe(DataOf(canonical));
    }

    [Fact]
    public async Task FindReferences_AliasName_MatchesCanonicalResult()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var canonical = await CallRaw(harness, "find_references", new Dictionary<string, object?> { ["symbol"] = "Add" });
        var alias = await CallRaw(harness, "find_references", new Dictionary<string, object?> { ["name"] = "Add" });

        canonical.IsError.ShouldNotBe(true);
        alias.IsError.ShouldNotBe(true);
        DataOf(alias).ShouldBe(DataOf(canonical));
    }

    [Fact]
    public async Task SymbolLookup_NoSymbol_ErrorNamesCanonicalAndAlias()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var message = await ErrorMessage(harness, "symbol_lookup", []);

        message.ShouldContain("accepts 'name' or its alias 'symbol'");
    }

    [Fact]
    public async Task FindReferences_NoSymbol_ErrorNamesCanonicalAndAlias()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var message = await ErrorMessage(harness, "find_references", []);

        message.ShouldContain("accepts 'symbol' or its alias 'name'");
    }

    [Fact]
    public async Task SymbolLookup_ConflictingAlias_ErrorNamesBothValues()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var message = await ErrorMessage(harness, "symbol_lookup",
            new Dictionary<string, object?> { ["name"] = "Add", ["symbol"] = "Calc" });

        message.ShouldContain("different values ('Add' vs 'Calc')");
    }

    private static string DataOf(ModelContextProtocol.Protocol.CallToolResult result)
    {
        var json = result.Content.Single().ToString()!;
        return JsonDocument.Parse(json).RootElement.GetProperty("data").GetRawText();
    }

    private static async Task<ModelContextProtocol.Protocol.CallToolResult> CallRaw(
        McpTestHarness harness, string tool, Dictionary<string, object?> args) =>
        await harness.Client.CallToolAsync(tool, args, cancellationToken: CancellationToken.None);

    private static async Task<string> ErrorMessage(
        McpTestHarness harness, string tool, Dictionary<string, object?> args)
    {
        var result = await CallRaw(harness, tool, args);
        result.IsError.ShouldBe(true);
        return result.Content.Single().ToString()!;
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
                /// <summary>Adds numbers.</summary>
                public int Add(int a, int b)
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
        return workspace;
    }

    private static void WriteFile(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
