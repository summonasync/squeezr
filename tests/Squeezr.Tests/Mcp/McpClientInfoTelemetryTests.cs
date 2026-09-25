using Shouldly;
using Squeezr.Cli.Mcp;
using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Mcp;

public class McpClientInfoTelemetryTests
{
    [Fact]
    public async Task IndexStatus_McpClient_RecordsClientInfoInQueryTelemetry()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "index_status",
            new Dictionary<string, object?>(),
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);

        var entries = QueryTelemetry.ReadAll(workspace);
        var entry = entries.ShouldHaveSingleItem();
        entry.Client.ShouldBe("squeezr-tests");
        entry.ClientVersion.ShouldBe("0.0.0");
    }

    [Fact]
    public async Task RepoMap_McpClient_RecordsClientInfoInQueryTelemetry()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "repo_map",
            new Dictionary<string, object?> { ["depth"] = 1 },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);

        var entry = QueryTelemetry.ReadAll(workspace).ShouldHaveSingleItem();
        entry.Client.ShouldBe("squeezr-tests");
        entry.ClientVersion.ShouldBe("0.0.0");
    }

    [Fact]
    public void ClientIdentity_BeforeAttach_NameAndVersionAreNull()
    {
        var identity = new ClientIdentity();

        identity.Name.ShouldBeNull();
        identity.Version.ShouldBeNull();
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
                public int Add(int a, int b) => a + b;
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
