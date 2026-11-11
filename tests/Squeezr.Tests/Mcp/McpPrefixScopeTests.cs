using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Mcp;

public class McpPrefixScopeTests
{
    private const string Marker = "sharedneedle";

    [Fact]
    public async Task RepoMap_Prefix_ScopesSubtreeWithRelativeDirs()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "repo_map",
            new Dictionary<string, object?> { ["prefix"] = "src/", ["depth"] = 2 },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        envelope.GetProperty("truncated").GetBoolean().ShouldBeFalse();
        var texts = DataTexts(envelope);
        texts.ShouldContain("demo/");
        texts.ShouldContain(t => t.StartsWith("Core/", StringComparison.Ordinal));
        texts.ShouldNotContain(t => t.Contains("util.js"));
    }

    [Fact]
    public async Task RepoMap_PrefixVariants_HitTheSameCacheScope()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var scoped = await harness.Client.CallToolAsync(
            "repo_map",
            new Dictionary<string, object?> { ["prefix"] = "src" },
            cancellationToken: CancellationToken.None);
        var unscoped = await harness.Client.CallToolAsync(
            "repo_map",
            new Dictionary<string, object?>(),
            cancellationToken: CancellationToken.None);

        scoped.IsError.ShouldNotBe(true);
        unscoped.IsError.ShouldNotBe(true);
        DataTexts(ParseEnvelope(unscoped)).ShouldContain(t => t.Contains("util.js"));
        DataTexts(ParseEnvelope(scoped)).ShouldNotContain(t => t.Contains("util.js"));
    }

    [Fact]
    public async Task RepoMap_InvalidPrefix_ToolErrorNamesNearestDirs()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "repo_map",
            new Dictionary<string, object?> { ["prefix"] = "srcx/" },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldBe(true);
        var text = result.Content.ShouldHaveSingleItem().ShouldBeOfType<TextContentBlock>().Text;
        text.ShouldContain("No indexed files under prefix 'srcx/'");
        text.ShouldContain("Nearest directories");
        text.ShouldContain("src/Cli/");
        text.ShouldNotContain("--");
    }

    [Fact]
    public async Task RepoMap_CaseMismatchedPrefix_StillScopesTheStructuralPass()
    {
        var workspace = NewIndexedWorkspace();
        WriteFile(workspace, "repo/src/Core/Alpha.cs", """
            public class Alpha
            {
                public int Sum(int a, int b)
                {
                    return a + b;
                }
            }
            """);
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "repo_map",
            new Dictionary<string, object?> { ["prefix"] = "SRC/", ["depth"] = 3 },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);

        // the prefix arrives in another case than the stored paths, and the pass reads rows, not rendered
        // labels — so the changed file is still found, and it is named in the casing the store holds
        envelope.GetProperty("staleFiles").EnumerateArray()
            .Select(s => s.GetString()!)
            .ShouldBe(["demo/src/Core/Alpha.cs"]);
        DataTexts(envelope).ShouldNotContain(t => t.Contains("Sum(int a, int b)"));
    }

    [Fact]
    public async Task CodeSearch_Prefix_ExactArmStaysInsideSubtree()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "code_search",
            new Dictionary<string, object?> { ["query"] = Marker, ["prefix"] = "src/" },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        var files = envelope.GetProperty("data").EnumerateArray()
            .Select(hit => hit.GetProperty("file").GetString()!)
            .ToList();
        files.ShouldBe(["src/Cli/Beta.cs", "src/Core/Alpha.cs"], ignoreOrder: true);

        var unscoped = await harness.Client.CallToolAsync(
            "code_search",
            new Dictionary<string, object?> { ["query"] = Marker },
            cancellationToken: CancellationToken.None);
        ParseEnvelope(unscoped).GetProperty("data").EnumerateArray()
            .Select(hit => hit.GetProperty("file").GetString()!)
            .ShouldContain("lib/util.js");
    }

    [Fact]
    public async Task CodeSearch_InvalidPrefix_ToolErrorNotSilentEmpty()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "code_search",
            new Dictionary<string, object?> { ["query"] = Marker, ["prefix"] = "nope/" },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.Content.ShouldHaveSingleItem().ShouldBeOfType<TextContentBlock>().Text
            .ShouldContain("No indexed files under prefix 'nope/'");
    }

    private static List<string> DataTexts(JsonElement envelope) =>
        envelope.GetProperty("data").EnumerateArray()
            .Select(l => l.GetProperty("text").GetString()!)
            .ToList();

    private static JsonElement ParseEnvelope(CallToolResult result)
    {
        var json = result.Content.ShouldHaveSingleItem().ToString()!;
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static string NewIndexedWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        WriteFile(workspace, "repo/src/Core/Alpha.cs", $$"""
            public class Alpha
            {
                // {{Marker}}
                public int Add(int a, int b)
                {
                    return a + b;
                }
            }
            """);
        WriteFile(workspace, "repo/src/Cli/Beta.cs", $$"""
            public class Beta
            {
                // {{Marker}}
                public int Run() => 1;
            }
            """);
        WriteFile(workspace, "repo/lib/util.js", $$"""
            function helper() {
                // {{Marker}}
                return 1;
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
