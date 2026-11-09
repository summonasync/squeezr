using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Mcp;

public class McpMapExpandToolsTests
{
    [Fact]
    public async Task RepoMap_ReturnsSignaturesWithoutBodies()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "repo_map",
            new Dictionary<string, object?> { ["depth"] = 3 },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        var lines = envelope.GetProperty("data").EnumerateArray().ToList();
        lines.ShouldContain(l =>
            l.GetProperty("depth").GetInt32() == 3 &&
            l.GetProperty("text").GetString()!.Contains("Add(int a, int b)"));
        lines.ShouldAllBe(l => !l.GetProperty("text").GetString()!.Contains("return a + b"));
        envelope.GetProperty("tokensUsed").GetInt32().ShouldBeGreaterThan(0);
        envelope.GetProperty("truncated").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task RepoMap_WithTinyBudget_TruncatesWithHintInWarnings()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "repo_map",
            new Dictionary<string, object?> { ["maxTokens"] = 10 },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        envelope.GetProperty("truncated").GetBoolean().ShouldBeTrue();
        envelope.GetProperty("tokensUsed").GetInt32().ShouldBeLessThanOrEqualTo(10);
        envelope.GetRawText().ShouldNotContain("--");
        envelope.GetProperty("warnings").EnumerateArray()
            .Select(w => w.GetString()!)
            .ShouldContain(w => w.StartsWith("truncated: ")
                && w.EndsWith("lower depth or raise maxTokens"));
    }

    [Fact]
    public async Task ExpandSpan_ReturnsSpanWithDefaultContext()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "expand_span",
            new Dictionary<string, object?> { ["file"] = "src/Calc.cs", ["startLine"] = 6, ["endLine"] = 9 },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        var data = envelope.GetProperty("data");
        data.GetProperty("repo").GetString().ShouldBe("demo");
        data.GetProperty("file").GetString().ShouldBe("src/Calc.cs");
        data.GetProperty("startLine").GetInt32().ShouldBe(6);
        data.GetProperty("endLine").GetInt32().ShouldBe(9);
        data.GetProperty("before").EnumerateArray().Count().ShouldBe(3);
        data.GetProperty("span").EnumerateArray().ToList()
            .Select(l => l.GetString()).ShouldContain(l => l!.Contains("return a + b"));
        data.GetProperty("after").EnumerateArray().Count().ShouldBe(3);
    }

    [Fact]
    public async Task ExpandSpan_AfterEdit_ReturnsLiveContent()
    {
        var workspace = NewIndexedWorkspace();
        WriteFile(workspace, "repo/src/Calc.cs", """
            namespace Demo;

            public class Calc
            {
                /// <summary>Adds numbers.</summary>
                public int Add(int a, int b)
                {
                    return a + b + 100;
                }

                public string Add(string a, string b)
                {
                    return a + b;
                }
            }
            """);
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "expand_span",
            new Dictionary<string, object?> { ["file"] = "src/Calc.cs", ["startLine"] = 8, ["endLine"] = 8 },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        envelope.GetProperty("data").GetProperty("span").EnumerateArray()
            .Select(l => l.GetString()).ShouldContain(l => l!.Contains("+ 100"));
    }

    [Fact]
    public async Task ExpandSpan_StartLineBelowOne_ErrorNamesSchemaParameterWithoutFlags()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "expand_span",
            new Dictionary<string, object?> { ["file"] = "src/Calc.cs", ["startLine"] = 0 },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldBe(true);
        var text = result.Content.ShouldHaveSingleItem().ShouldBeOfType<TextContentBlock>().Text;
        text.ShouldBe("startLine must be >= 1.");
        text.ShouldNotContain("--");
    }

    [Fact]
    public async Task ExpandSpan_AmbiguousPath_ErrorAdvisesRepoPrefixWithoutFlags()
    {
        var workspace = NewTwoRepoWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "expand_span",
            new Dictionary<string, object?> { ["file"] = "src/Dup.cs", ["startLine"] = 1 },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldBe(true);
        var text = result.Content.ShouldHaveSingleItem().ShouldBeOfType<TextContentBlock>().Text;
        text.ShouldContain("alpha:src/Dup.cs");
        text.ShouldContain("beta:src/Dup.cs");
        text.ShouldContain("Qualify the path with the repo prefix, e.g. 'alpha/src/Dup.cs'.");
        text.ShouldNotContain("--");
    }

    [Fact]
    public async Task RepoMap_OnChangedFile_RendersTheIndexAndNamesTheLag()
    {
        var workspace = NewIndexedWorkspace();
        WriteFile(workspace, "repo/src/Calc.cs", """
            namespace Demo;

            public class Calc
            {
                /// <summary>Adds numbers.</summary>
                public int Sum(int a, int b)
                {
                    return a + b;
                }

                public string Add(string a, string b)
                {
                    return a + b;
                }
            }
            """);
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "repo_map",
            new Dictionary<string, object?> { ["depth"] = 3 },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        var texts = envelope.GetProperty("data").EnumerateArray()
            .Select(l => l.GetProperty("text").GetString()!).ToList();
        texts.ShouldNotContain("int Calc.Sum(int a, int b)");
        texts.ShouldContain("int Calc.Add(int a, int b)");
        envelope.GetProperty("staleFiles").EnumerateArray()
            .Select(s => s.GetString()!).ShouldBe(["demo/src/Calc.cs"]);

        // the map is a read surface now: an inline refresh would have rewritten the row between two calls
        var second = ParseEnvelope(await harness.Client.CallToolAsync(
            "repo_map",
            new Dictionary<string, object?> { ["depth"] = 3 },
            cancellationToken: CancellationToken.None));
        second.GetProperty("staleFiles").EnumerateArray()
            .Select(s => s.GetString()!).ShouldBe(["demo/src/Calc.cs"]);
    }

    [Fact]
    public async Task RepoMap_WithHeldLock_ReportsStaleFiles()
    {
        var workspace = NewIndexedWorkspace();
        WriteFile(workspace, "repo/src/Calc.cs", """
            namespace Demo;

            public class Calc
            {
                /// <summary>Adds numbers.</summary>
                public int Sum(int a, int b)
                {
                    return a + b;
                }

                public string Add(string a, string b)
                {
                    return a + b + string.Empty;
                }
            }
            """);
        using (IndexLock.Acquire(SqueezrPaths.StoreDir(workspace)))
        {
            await using var harness = await McpTestHarness.StartAsync(workspace);

            var result = await harness.Client.CallToolAsync(
                "repo_map",
                new Dictionary<string, object?> { ["depth"] = 3 },
                cancellationToken: CancellationToken.None);

            result.IsError.ShouldNotBe(true);
            var envelope = ParseEnvelope(result);
            envelope.GetProperty("staleFiles").EnumerateArray()
                .Select(s => s.GetString()!).ShouldBe(["demo/src/Calc.cs"]);
            envelope.GetProperty("data").EnumerateArray()
                .Select(l => l.GetProperty("text").GetString()!)
                .ShouldContain("int Calc.Add(int a, int b)");
        }
    }

    [Fact]
    public async Task AllTools_EnvelopeHasIdenticalKeySet()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var calls = new Dictionary<string, Dictionary<string, object?>>
        {
            ["code_search"] = new() { ["query"] = "Add" },
            ["symbol_lookup"] = new() { ["name"] = "Add" },
            ["find_references"] = new() { ["symbol"] = "Add" },
            ["repo_map"] = [],
            ["expand_span"] = new() { ["file"] = "src/Calc.cs", ["startLine"] = 6, ["endLine"] = 9 },
            ["index_status"] = [],
            ["code_pack"] = new() { ["name"] = "Add" }
        };

        string[]? expectedKeys = null;
        foreach (var (tool, args) in calls)
        {
            var result = await harness.Client.CallToolAsync(
                tool, args, cancellationToken: CancellationToken.None);

            result.IsError.ShouldNotBe(true);
            var envelope = ParseEnvelope(result);
            var keys = envelope.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            var withoutOptional = keys.Where(k => k != "fileFreshness").ToArray();
            expectedKeys ??= withoutOptional;
            withoutOptional.ShouldBe(expectedKeys);

            envelope.GetProperty("tokensUsed").GetInt32().ShouldBeGreaterThanOrEqualTo(0);
            envelope.GetProperty("truncated").GetBoolean().ShouldBeFalse();
        }

        expectedKeys!.ShouldBe(["cost", "data", "indexAge", "staleFiles", "tokensUsed", "truncated", "warnings"]);
    }

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
        WriteFile(workspace, "repo/src/Other.cs", """
            namespace Demo;

            public class Other
            {
                public string Combine(Calc calc)
                {
                    return calc.Add("x", "y");
                }
            }
            """);
        manager.AddRepo("repo", "demo");
        new WorkspaceIndexer().Index(workspace);
        return workspace;
    }

    private static string NewTwoRepoWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        WriteFile(workspace, "repo-a/src/Dup.cs", "public class Alpha { }\n");
        WriteFile(workspace, "repo-b/src/Dup.cs", "public class Beta { }\n");
        manager.AddRepo("repo-a", "alpha");
        manager.AddRepo("repo-b", "beta");
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
