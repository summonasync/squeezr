using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Mcp;

public class GhostRefsToolSurfaceTests
{
    [Fact]
    public async Task FindReferences_WhenADeclarationLagsItsCaller_NamesItInWarningsAndStaleFiles()
    {
        var workspace = NewWorkspaceWithGhost();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "find_references",
            new Dictionary<string, object?> { ["symbol"] = "Add" },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        var rows = envelope.GetProperty("data").EnumerateArray()
            .Select(r => $"{r.GetProperty("file").GetString()}:{r.GetProperty("line").GetInt32()}")
            .ToList();

        // the ghost is still served — what the lag buys is the marker, and the marker is the contract
        rows.ShouldContain("src/Runner.cs:7");
        envelope.GetProperty("staleFiles").EnumerateArray()
            .Select(s => s.GetString()!).ShouldContain("demo/src/Calc.cs");
        Warnings(envelope).ShouldContain(w =>
            w.StartsWith("refs lag for 1 declaring file(s) (demo/src/Calc.cs)", StringComparison.Ordinal)
            && w.Contains("squeezr index"));
    }

    [Fact]
    public async Task FindReferences_AfterAFullIndexServesTheReconciledRowsAndStaysQuiet()
    {
        var workspace = NewWorkspaceWithGhost();
        new WorkspaceIndexer().Index(workspace);
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "find_references",
            new Dictionary<string, object?> { ["symbol"] = "Add" },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        var rows = envelope.GetProperty("data").EnumerateArray()
            .Select(r => $"{r.GetProperty("file").GetString()}:{r.GetProperty("line").GetInt32()}")
            .ToList();

        rows.ShouldNotContain("src/Runner.cs:7");
        rows.ShouldNotBeEmpty("a clean answer still has rows to speak for it");
        envelope.GetProperty("staleFiles").EnumerateArray().ShouldBeEmpty();
        Warnings(envelope).ShouldNotContain(w => w.StartsWith("refs lag", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FindReferences_DescriptionNamesTheLagAndTheWayToCheckIt()
    {
        var workspace = NewWorkspaceWithGhost();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var tools = await harness.Client.ListToolsAsync(cancellationToken: CancellationToken.None);
        var description = tools.Single(t => t.Name == "find_references").Description;

        description.ShouldContain("declaring file");
        description.ShouldContain("staleFiles");
        description.ShouldContain("squeezr index");
    }

    [Fact]
    public async Task IndexStatus_CountsTheDeclaringFilesThatOweAReparse()
    {
        var workspace = NewWorkspaceWithGhost();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "index_status",
            new Dictionary<string, object?>(),
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var warnings = Warnings(ParseEnvelope(result));
        var lagging = warnings.Where(w => w.Contains("declaring file(s) carry", StringComparison.Ordinal)).ToList();

        lagging.ShouldHaveSingleItem();
        lagging[0].ShouldContain("1 declaring file(s)");
        lagging[0].ShouldContain("rows scanned");
        lagging[0].ShouldContain("squeezr index");
    }

    [Fact]
    public async Task IndexStatus_WithEveryDeclarationRebuilt_ReportsNoLag()
    {
        var workspace = NewWorkspaceWithGhost();
        new WorkspaceIndexer().Index(workspace);
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "index_status",
            new Dictionary<string, object?>(),
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        Warnings(ParseEnvelope(result)).ShouldNotContain(w => w.Contains("declaring file(s) carry", StringComparison.Ordinal));
    }

    // Calc.cs declares Add, Runner.cs calls it; the call is then deleted and only Runner is reprocessed,
    // which is the exact asymmetry that breeds a ghost: the row lives under Calc.cs and nothing rewrites it
    // Calc.cs declares Add and two files call it; the call inside Runner.cs is then deleted and only
    // Runner is reprocessed — the exact asymmetry that breeds a ghost: the row lives under Calc.cs,
    // and nothing in a caller-only pass rewrites it. Other.cs stays a live caller either way.
    private static string NewWorkspaceWithGhost()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        WriteFile(workspace, "repo/src/Calc.cs", """
            namespace Demo;

            public class Calc
            {
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
                public int Go(Calc calc)
                {
                    return calc.Add(1, 2);
                }
            }
            """);
        WriteFile(workspace, "repo/src/Other.cs", """
            namespace Demo;

            public class Other
            {
                public int Twice(Calc calc)
                {
                    return calc.Add(3, 4);
                }
            }
            """);
        manager.AddRepo("repo", "demo");
        new WorkspaceIndexer().Index(workspace);

        WriteFile(workspace, "repo/src/Runner.cs", """
            namespace Demo;

            public class Runner
            {
                public int Go(Calc calc)
                {
                    return 0;
                }
            }
            """);
        new WorkspaceIndexer().IndexFile(workspace, "demo", "src/Runner.cs");
        return workspace;
    }

    private static JsonElement ParseEnvelope(CallToolResult result)
    {
        var json = result.Content.ShouldHaveSingleItem().ToString()!;
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static IReadOnlyList<string> Warnings(JsonElement envelope) =>
        [.. envelope.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!)];

    private static void WriteFile(string workspace, string relativePath, string content)
    {
        var fullPath = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
