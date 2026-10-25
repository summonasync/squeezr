using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Serialization;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Mcp;

public class FreshnessPresenceTests
{
    [Fact]
    public async Task IndexStatus_DeclaresWhyItHasNoPerFileVerdicts()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "index_status",
            new Dictionary<string, object?>(),
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        envelope.GetProperty("warnings").EnumerateArray()
            .Select(w => w.GetString()!)
            .ShouldContain(w => w.Contains("per-file freshness is not part of this aggregate report"));
    }

    [Fact]
    public async Task RepoMap_DeclaresWhyItHasNoPerFileVerdicts()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "repo_map",
            new Dictionary<string, object?> { ["depth"] = 2 },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        envelope.GetProperty("warnings").EnumerateArray()
            .Select(w => w.GetString()!)
            .ShouldContain(w => w.Contains("per-file freshness is not attached to map lines"));
    }

    [Fact]
    public async Task CodeSearch_LiveWriterOnStore_NamesPidAndBeatAge()
    {
        var workspace = NewIndexedWorkspace();
        WriteLiveClaim(workspace);
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "code_search",
            new Dictionary<string, object?> { ["query"] = "Add" },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        var writerLine = envelope.GetProperty("warnings").EnumerateArray()
            .Select(w => w.GetString()!)
            .Single(w => w.StartsWith("active writer on this store:"));
        writerLine.ShouldContain($"pid {Environment.ProcessId}");
        writerLine.ShouldContain("watch beat 0s old");
    }

    [Fact]
    public async Task CodeSearch_NoClaim_NoWriterLine()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "code_search",
            new Dictionary<string, object?> { ["query"] = "Add" },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        envelope.GetProperty("warnings").EnumerateArray()
            .Select(w => w.GetString()!)
            .Count(w => w.StartsWith("active writer on this store:"))
            .ShouldBe(0);
    }

    [Fact]
    public async Task CodeSearch_StaleBeat_NoWriterLine()
    {
        var workspace = NewIndexedWorkspace();
        WriteLiveClaim(workspace, ageMinutes: 60);
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "code_search",
            new Dictionary<string, object?> { ["query"] = "Add" },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        envelope.GetProperty("warnings").EnumerateArray()
            .Select(w => w.GetString()!)
            .Count(w => w.StartsWith("active writer on this store:"))
            .ShouldBe(0);
    }

    private static void WriteLiveClaim(string workspace, double ageMinutes = 0)
    {
        var storeDir = SqueezrPaths.StoreDir(workspace);
        Directory.CreateDirectory(storeDir);
        var now = DateTime.UtcNow;
        var marker = new WatchOwnerMarker(
            Environment.ProcessId,
            Environment.MachineName,
            DaemonHeartbeat.DefaultKind,
            now.AddMinutes(-ageMinutes),
            now.AddMinutes(-ageMinutes));
        var line = JsonSerializer.Serialize(marker, SqueezrJson.FileOptions);
        File.WriteAllText(Path.Combine(storeDir, WatchOwnerMarker.MarkerFileName), line);
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
