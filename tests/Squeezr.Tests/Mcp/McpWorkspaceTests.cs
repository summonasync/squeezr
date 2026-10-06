using Shouldly;
using Squeezr.Cli.Mcp;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Mcp;

public class McpWorkspaceTests
{
    [Fact]
    public void RequireWorkspace_MissingManifest_ThrowsNotInitializedCode()
    {
        var workspace = new McpWorkspace(TestDirs.NewTempDir());

        var ex = Should.Throw<WorkspaceException>(workspace.RequireWorkspace);

        ex.Code.ShouldBe(WorkspaceException.NotInitializedCode);
        ex.Code.ShouldNotBe(WorkspaceException.DefaultCode);
    }

    [Fact]
    public void RequireWorkspace_MissingManifest_KeepsTheHumanSentenceVerbatim()
    {
        var workspace = new McpWorkspace(TestDirs.NewTempDir());

        var ex = Should.Throw<WorkspaceException>(workspace.RequireWorkspace);

        ex.Message.ShouldBe("Workspace is not initialized. Run 'squeezr init' first.");
    }

    [Fact]
    public void RequireWorkspace_ManifestPresent_DoesNotThrow()
    {
        var root = TestDirs.NewTempDir();
        File.WriteAllText(SqueezrPaths.ManifestPath(root), "{}");

        Should.NotThrow(() => new McpWorkspace(root).RequireWorkspace());
    }
}
