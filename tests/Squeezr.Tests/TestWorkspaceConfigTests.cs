using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests;

public sealed class TestWorkspaceConfigTests
{
    [Fact]
    public void PointQdrantAtDeadEndpoint_AfterInit_ConfigPointsAtUnreachableEndpoint()
    {
        var workspace = TestDirs.NewTempDir();
        new WorkspaceManager(workspace).Init("demo");

        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);

        ConfigFile.Load(workspace).Qdrant.Endpoint.ShouldBe(TestWorkspaceConfig.DeadQdrantEndpoint);
    }
}
