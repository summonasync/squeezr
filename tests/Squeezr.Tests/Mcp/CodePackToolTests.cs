using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Mcp;

public class CodePackToolTests
{
    [Fact]
    public async Task CodePack_KnownSymbol_CompletePackWithoutRemedyNoise()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "code_pack",
            new Dictionary<string, object?> { ["name"] = "Add" },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        envelope.GetProperty("truncated").GetBoolean().ShouldBeFalse();

        var data = envelope.GetProperty("data");
        data.GetProperty("name").GetString().ShouldBe("Add");
        data.GetProperty("signature").GetString().ShouldNotBeNull();
        data.GetProperty("bodyLines").EnumerateArray().ShouldNotBeEmpty();
        data.GetProperty("callers").EnumerateArray().ShouldNotBeEmpty();
        data.GetProperty("withheldCallers").GetInt32().ShouldBe(0);
        data.GetProperty("complete").GetBoolean().ShouldBeTrue();

        envelope.GetProperty("cost").GetProperty("complete").GetBoolean().ShouldBeTrue();

        envelope.GetProperty("warnings").EnumerateArray()
            .Select(w => w.GetString()!)
            .Count(w => w.Contains("withheld") || w.StartsWith("truncated: "))
            .ShouldBe(0);
    }

    [Fact]
    public async Task CodePack_ClippedPack_CompleteFalseWithRemedyLine()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);
        var full = FullLookupTokens(workspace, "Add");

        var result = await harness.Client.CallToolAsync(
            "code_pack",
            new Dictionary<string, object?> { ["name"] = "Add", ["maxTokens"] = full },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        envelope.GetProperty("truncated").GetBoolean().ShouldBeTrue();

        var data = envelope.GetProperty("data");
        data.GetProperty("complete").GetBoolean().ShouldBeFalse();
        data.GetProperty("withheldCallers").GetInt32().ShouldBeGreaterThan(0);
        data.GetProperty("bodyLines").EnumerateArray().ShouldNotBeEmpty();

        envelope.GetProperty("cost").GetProperty("complete").GetBoolean().ShouldBeFalse();
        envelope.GetProperty("warnings").EnumerateArray()
            .Select(w => w.GetString()!)
            .ShouldContain(w => w.Contains("caller(s) withheld to fit the budget"));
    }

    [Fact]
    public async Task CodePack_TelemetryRow_CarriesToolNameAndBaseline()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        await harness.Client.CallToolAsync(
            "code_pack",
            new Dictionary<string, object?> { ["name"] = "Add" },
            cancellationToken: CancellationToken.None);

        var line = File.ReadAllLines(SqueezrPaths.QueriesLogPath(workspace))
            .Select(l => JsonDocument.Parse(l).RootElement)
            .Last(e => e.GetProperty("tool").GetString() == "code_pack");
        line.GetProperty("tokensUsed").GetInt32().ShouldBeGreaterThan(0);
        line.GetProperty("baselineTokens").GetInt64().ShouldBeGreaterThan(0);
    }

    private static int FullLookupTokens(string workspace, string name) =>
        new Squeezr.Core.Indexing.Navigation.SymbolPackComposer(workspace)
            .Compose(name, null, null, 2000).LookupTokens;

    private static JsonElement ParseEnvelope(CallToolResult result)
    {
        var json = result.Content.ShouldHaveSingleItem().ToString()!;
        return JsonDocument.Parse(json).RootElement.Clone();
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
        return workspace;
    }

    private static void WriteFile(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
