using System.Text.Json;
using LiteDB;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Mcp;

public class FindReferencesEnclosingFallbackTests
{
    [Fact]
    public async Task FindReferences_NullEnclosingInStore_FallsBackToSpanInference()
    {
        var workspace = NewIndexedWorkspace();
        StripEnclosingSymbols(workspace);
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
    public async Task FindReferences_NestedClass_CapturedMatchesInferenceFormat()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init("demo");
        WriteFile(workspace, "repo/src/Outer.cs", """
            namespace Demo;

            public class Outer
            {
                public class Inner
                {
                    public void M()
                    {
                        Target();
                    }
                }

                public void Target()
                {
                }
            }
            """);
        manager.AddRepo("repo", "demo");
        new WorkspaceIndexer().Index(workspace);

        await using var harness = await McpTestHarness.StartAsync(workspace);

        var captured = await CallRefsAsync(harness, workspace);
        captured.ShouldBe("Inner.M");

        StripEnclosingSymbols(workspace);
        var inferred = await CallRefsAsync(harness, workspace);
        inferred.ShouldBe("Inner.M");
    }

    private static async Task<string?> CallRefsAsync(McpTestHarness harness, string workspace)
    {
        var result = await harness.Client.CallToolAsync(
            "find_references",
            new Dictionary<string, object?> { ["symbol"] = "Target" },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        return envelope.GetProperty("data").EnumerateArray()
            .ShouldHaveSingleItem()
            .GetProperty("enclosingSymbol")
            .GetString();
    }

    private static void StripEnclosingSymbols(string workspace)
    {
        using var db = new LiteDatabase(SqueezrPaths.MetaDbPath(workspace));
        var refs = db.GetCollection(MetaStore.RefsCollection);
        foreach (var doc in refs.FindAll().ToList())
        {
            if (doc.Remove("enclosingSymbol"))
            {
                refs.Update(doc);
            }
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
        WriteFile(workspace, "repo/src/Calc.cs", """
            namespace Demo;

            public class Calc
            {
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

    private static void WriteFile(string workspace, string relativePath, string content)
    {
        var fullPath = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
