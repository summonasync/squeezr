using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Mcp;

public class TruncationRemedyTests
{
    [Fact]
    public async Task CodeSearch_Truncated_NamesCeilingAndRemedy()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var warnings = (await CallWarnings(harness, "code_search", new Dictionary<string, object?>
        {
            ["query"] = "Add", ["maxTokens"] = 1,
        })).Warnings;

        warnings.Count(w => w.StartsWith("truncated: ")).ShouldBe(1);
        warnings.ShouldContain(w => w.Contains(" of 1 token ceiling — raise maxTokens"));
    }

    [Fact]
    public async Task CodeSearch_Complete_NoTruncationLine()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var envelope = await CallWarnings(harness, "code_search", new Dictionary<string, object?>
        {
            ["query"] = "Add", ["maxTokens"] = 2000,
        });

        envelope.Warnings.Count(w => w.StartsWith("truncated: ")).ShouldBe(0);
    }

    [Fact]
    public async Task SymbolLookup_Truncated_NamesCeilingAndQueryNarrowing()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var warnings = (await CallWarnings(harness, "symbol_lookup", new Dictionary<string, object?>
        {
            ["name"] = "Add", ["maxTokens"] = 1,
        })).Warnings;

        warnings.Count(w => w.StartsWith("truncated: ")).ShouldBe(1);
        warnings.ShouldContain(w => w.Contains(" of 1 token ceiling — narrow the query"));
    }

    [Fact]
    public async Task RepoMap_Truncated_NamesCeilingAndDepthLowering()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var warnings = (await CallWarnings(harness, "repo_map", new Dictionary<string, object?>
        {
            ["depth"] = 3, ["maxTokens"] = 1,
        })).Warnings;

        warnings.Count(w => w.StartsWith("truncated: ")).ShouldBe(1);
        warnings.ShouldContain(w => w.Contains(" of 1 token ceiling — lower depth"));
    }

    [Fact]
    public async Task ExpandSpan_Truncated_NamesCeilingAndSpanNarrowing()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var warnings = (await CallWarnings(harness, "expand_span", new Dictionary<string, object?>
        {
            ["file"] = "src/Calc.cs", ["startLine"] = 1, ["endLine"] = 15, ["maxTokens"] = 1,
        })).Warnings;

        warnings.Count(w => w.StartsWith("truncated: ")).ShouldBe(1);
        warnings.ShouldContain(w => w.Contains(" of 1 token ceiling — raise maxTokens, or narrow the span"));
    }

    private static async Task<(JsonElement Envelope, IReadOnlyList<string> Warnings)> CallWarnings(
        McpTestHarness harness, string tool, Dictionary<string, object?> args)
    {
        var result = await harness.Client.CallToolAsync(tool, args, cancellationToken: CancellationToken.None);
        result.IsError.ShouldNotBe(true);
        var json = result.Content.ShouldHaveSingleItem().ToString()!;
        var envelope = JsonDocument.Parse(json).RootElement.Clone();
        var warnings = envelope.GetProperty("warnings").EnumerateArray()
            .Select(w => w.GetString()!).ToList();
        return (envelope, warnings);
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
