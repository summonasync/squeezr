using Shouldly;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Workspace;

public class WorkspaceManagerDropTests
{
    [Fact]
    public void Drop_RemovesStoreDir()
    {
        var root = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(root);
        manager.Init(null);

        var result = manager.Drop();

        result.Removed.ShouldBe([".squeezr", "squeezr.workspace.json", "docker-compose.yml"]);
        result.Hint.ShouldBe("docker compose down -v");
        Directory.Exists(SqueezrPaths.StoreDir(root)).ShouldBeFalse();
    }

    [Fact]
    public void Drop_RemovesManifestAndComposeFile()
    {
        var root = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(root);
        manager.Init(null);

        manager.Drop();

        File.Exists(SqueezrPaths.ManifestPath(root)).ShouldBeFalse();
        File.Exists(SqueezrPaths.ComposePath(root)).ShouldBeFalse();
    }

    [Fact]
    public void Drop_Throws_WhenNothingToDrop()
    {
        var root = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(root);

        Should.Throw<WorkspaceException>(() => manager.Drop());
    }

    [Fact]
    public void Init_WorksAgain_AfterDrop()
    {
        var root = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(root);
        manager.Init(null);
        manager.Drop();

        var result = manager.Init("again");

        result.WorkspaceName.ShouldBe("again");
        Directory.Exists(SqueezrPaths.StoreDir(root)).ShouldBeTrue();
    }
}
