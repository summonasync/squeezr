using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Mcp;

public class McpNavigationToolsTests
{
    [Fact]
    public async Task SymbolLookup_ReturnsSignatureSpanDocAndBody()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "symbol_lookup",
            new Dictionary<string, object?> { ["name"] = "Add", ["maxTokens"] = 500 },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        var hits = envelope.GetProperty("data").EnumerateArray().ToList();
        hits.Count.ShouldBe(2);

        var intOverload = hits.Single(h =>
            h.GetProperty("signature").GetString()!.Contains("Add(int a, int b)"));
        intOverload.GetProperty("repo").GetString().ShouldBe("demo");
        intOverload.GetProperty("file").GetString().ShouldBe("src/Calc.cs");
        intOverload.GetProperty("span").GetString()!.ShouldContain("-");
        intOverload.GetProperty("docComment").GetString()!.ShouldContain("Adds numbers");
        intOverload.GetProperty("body").GetString()!.ShouldContain("return a + b");

        envelope.GetProperty("tokensUsed").GetInt32().ShouldBeGreaterThan(0);
        envelope.GetProperty("truncated").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task SymbolLookup_WithTightBudget_TruncatesWithParameterHint()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "symbol_lookup",
            new Dictionary<string, object?> { ["name"] = "Add", ["maxTokens"] = 5 },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        envelope.GetProperty("truncated").GetBoolean().ShouldBeTrue();
        envelope.GetRawText().ShouldNotContain("--");
        envelope.GetProperty("warnings").EnumerateArray()
            .Select(w => w.GetString()!)
            .ShouldContain(w => w.StartsWith("truncated: ")
                && w.EndsWith("narrow the query (repo, kind) or raise maxTokens"));
    }

    [Fact]
    public async Task SymbolLookup_NotFound_ReturnsEmptyData()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "symbol_lookup",
            new Dictionary<string, object?> { ["name"] = "Nope" },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        envelope.GetProperty("data").EnumerateArray().ShouldBeEmpty();
        envelope.GetProperty("truncated").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task SymbolLookup_NotFound_RecordsTelemetryWithZeroHits()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "symbol_lookup",
            new Dictionary<string, object?> { ["name"] = "Nope" },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);

        var entry = QueryTelemetry.ReadAll(workspace).ShouldHaveSingleItem();
        entry.Tool.ShouldBe("symbol_lookup");
        entry.Hits.ShouldBe(0);
        entry.TokensUsed.ShouldBe(envelope.GetProperty("tokensUsed").GetInt32());
        entry.Truncated.ShouldBeFalse();
    }

    [Fact]
    public async Task FindReferences_ReturnsAllCallSites()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "find_references",
            new Dictionary<string, object?> { ["symbol"] = "Add" },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        var refs = envelope.GetProperty("data").EnumerateArray().ToList();
        refs.Count.ShouldBe(2);
        refs.Select(r => r.GetProperty("file").GetString()).OrderBy(f => f)
            .ShouldBe(["src/Other.cs", "src/Runner.cs"]);
        refs.ShouldAllBe(r =>
            r.GetProperty("kind").GetString() == "call" && r.GetProperty("line").GetInt32() > 0);
        envelope.GetProperty("tokensUsed").GetInt32().ShouldBeGreaterThan(0);
        envelope.GetProperty("indexAge").ValueKind.ShouldNotBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task FindReferences_NotFound_ReturnsToolError()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "find_references",
            new Dictionary<string, object?> { ["symbol"] = "Nope" },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldBe(true);
        var text = result.Content.ShouldHaveSingleItem().ShouldBeOfType<TextContentBlock>().Text;
        text.ShouldContain("No symbol named 'Nope'");

        var tools = await harness.Client.ListToolsAsync(cancellationToken: CancellationToken.None);
        tools.Count.ShouldBe(7);
    }

    [Fact]
    public async Task FindReferences_ReportsEnclosingSymbol()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "find_references",
            new Dictionary<string, object?> { ["symbol"] = "Add" },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        var refs = envelope.GetProperty("data").EnumerateArray().ToList();
        refs.Count.ShouldBe(2);
        refs.Single(r => r.GetProperty("file").GetString() == "src/Runner.cs")
            .GetProperty("enclosingSymbol").GetString().ShouldBe("Runner.Go");
        refs.Single(r => r.GetProperty("file").GetString() == "src/Other.cs")
            .GetProperty("enclosingSymbol").GetString().ShouldBe("Other.Combine");
    }

    [Fact]
    public async Task FindReferences_ClassLevelRef_UsesCapturedEnclosing()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "find_references",
            new Dictionary<string, object?> { ["symbol"] = "Calc" },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        var fieldRefs = envelope.GetProperty("data").EnumerateArray()
            .Where(r => r.GetProperty("file").GetString() == "src/FieldHolder.cs")
            .ToList();
        fieldRefs.Count.ShouldBe(2);
        fieldRefs.ShouldContain(r => r.GetProperty("enclosingSymbol").GetString() == "FieldHolder._calc");
        fieldRefs.ShouldContain(r => r.GetProperty("enclosingSymbol").GetString() == "FieldHolder");
    }

    [Fact]
    public async Task FindReferences_WithHeldLock_ReportsDistinctStaleFiles()
    {
        var workspace = NewIndexedWorkspace();
        WriteFile(workspace, "repo/src/Calc.cs", """
            namespace Demo;

            public class Calc
            {
                /// <summary>Adds numbers.</summary>
                public int Add(int a, int b)
                {
                    return a + b + 0;
                }

                public string Add(string a, string b)
                {
                    return a + b + string.Empty;
                }
            }
            """);
        WriteFile(workspace, "repo/src/Runner.cs", """
            namespace Demo;

            public class Runner
            {
                public int Go()
                {
                    return new Calc().Add(1, 2 + 0);
                }
            }
            """);
        WriteFile(workspace, "repo/src/Other.cs", """
            namespace Demo;

            public class Other
            {
                public string Combine(Calc calc)
                {
                    return calc.Add("x", "y") + string.Empty;
                }
            }
            """);
        using (IndexLock.Acquire(SqueezrPaths.StoreDir(workspace)))
        {
            await using var harness = await McpTestHarness.StartAsync(workspace);

            var result = await harness.Client.CallToolAsync(
                "find_references",
                new Dictionary<string, object?> { ["symbol"] = "Add" },
                cancellationToken: CancellationToken.None);

            result.IsError.ShouldNotBe(true);
            var envelope = ParseEnvelope(result);
            envelope.GetProperty("staleFiles").EnumerateArray()
                .Select(s => s.GetString()!).OrderBy(f => f, StringComparer.Ordinal)
                .ShouldBe(["demo/src/Calc.cs", "demo/src/Other.cs", "demo/src/Runner.cs"]);
        }
    }

    [Fact]
    public async Task SymbolLookup_WithHeldLock_ReportsStaleFiles()
    {
        var workspace = NewIndexedWorkspace();
        WriteFile(workspace, "repo/src/Calc.cs", """
            namespace Demo;

            public class Calc
            {
                /// <summary>Adds numbers.</summary>
                public int Add(int a, int b)
                {
                    return a + b + 0;
                }

                public string Add(string a, string b)
                {
                    return a + b;
                }
            }
            """);
        using (IndexLock.Acquire(SqueezrPaths.StoreDir(workspace)))
        {
            await using var harness = await McpTestHarness.StartAsync(workspace);

            var result = await harness.Client.CallToolAsync(
                "symbol_lookup",
                new Dictionary<string, object?> { ["name"] = "Add" },
                cancellationToken: CancellationToken.None);

            result.IsError.ShouldNotBe(true);
            var envelope = ParseEnvelope(result);
            envelope.GetProperty("staleFiles").EnumerateArray()
                .Select(s => s.GetString()!).Distinct().ShouldBe(["demo/src/Calc.cs"]);
        }
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
        WriteFile(workspace, "repo/src/FieldHolder.cs", """
            namespace Demo;

            public class FieldHolder
            {
                private readonly Calc _calc = new Calc();
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
