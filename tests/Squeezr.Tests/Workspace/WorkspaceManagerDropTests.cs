using Shouldly;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Workspace;

public class WorkspaceManagerDropTests
{
    [Fact]
    public void Drop_RemovesStoreDirAndRegistryEntry()
    {
        var root = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(root);
        manager.Init(null);
        var storeDir = SqueezrPaths.StoreDir(root);

        var result = manager.Drop();

        result.Removed.ShouldBe([storeDir]);
        result.Hint.ShouldBe("squeezr services down");
        Directory.Exists(storeDir).ShouldBeFalse();
        WorkspaceRegistry.FindByRootPath(root).ShouldBeNull();
    }

    [Fact]
    public void Drop_LeavesProjectDirectoryUntouched()
    {
        var root = TestDirs.NewTempDir();
        File.WriteAllText(Path.Combine(root, "keep.txt"), "user file");
        var manager = new WorkspaceManager(root);
        manager.Init(null);

        manager.Drop();

        File.Exists(Path.Combine(root, "keep.txt")).ShouldBeTrue();
        File.Exists(Path.Combine(root, SqueezrPaths.ManifestFileName)).ShouldBeFalse();
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
    public void Drop_LegacyWorkspace_RemovesEverything()
    {
        var root = TestDirs.NewTempDir();
        File.WriteAllText(Path.Combine(root, SqueezrPaths.ManifestFileName), """{"name":"legacy","repos":[]}""");
        Directory.CreateDirectory(Path.Combine(root, SqueezrPaths.StoreDirName));
        File.WriteAllText(Path.Combine(root, SqueezrPaths.StoreDirName, SqueezrPaths.MetaDbFileName), string.Empty);
        File.WriteAllText(Path.Combine(root, SqueezrPaths.ComposeFileName), "volumes:\n  squeezr-qdrant:\n");
        File.WriteAllText(Path.Combine(root, "keep.txt"), "user file");
        var manager = new WorkspaceManager(root);

        var result = manager.Drop();

        result.Removed.Length.ShouldBe(3);
        Directory.Exists(Path.Combine(root, SqueezrPaths.StoreDirName)).ShouldBeFalse();
        File.Exists(Path.Combine(root, SqueezrPaths.ManifestFileName)).ShouldBeFalse();
        File.Exists(Path.Combine(root, SqueezrPaths.ComposeFileName)).ShouldBeFalse();
        File.Exists(Path.Combine(root, "keep.txt")).ShouldBeTrue();
        WorkspaceStore.Resolve(root).IsWorkspace.ShouldBeFalse();
    }

    [Fact]
    public void Drop_LegacyWorkspace_KeepsForeignComposeFile()
    {
        var root = TestDirs.NewTempDir();
        File.WriteAllText(Path.Combine(root, SqueezrPaths.ManifestFileName), """{"name":"legacy","repos":[]}""");
        Directory.CreateDirectory(Path.Combine(root, SqueezrPaths.StoreDirName));
        File.WriteAllText(Path.Combine(root, SqueezrPaths.ComposeFileName), "services:\n  app:\n    image: nginx\n");
        var manager = new WorkspaceManager(root);

        manager.Drop();

        File.Exists(Path.Combine(root, SqueezrPaths.ComposeFileName)).ShouldBeTrue();
        File.Exists(Path.Combine(root, SqueezrPaths.ManifestFileName)).ShouldBeFalse();
    }

    [Fact]
    public void Init_WorksAgain_AfterDrop()
    {
        var root = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(root);
        manager.Init(null);
        var storeKey = WorkspaceRegistry.FindByRootPath(root)!.StoreKey;
        manager.Drop();

        var result = manager.Init("again");

        result.WorkspaceName.ShouldBe("again");
        Directory.Exists(SqueezrPaths.StoreDir(root)).ShouldBeTrue();
        var newKey = WorkspaceRegistry.FindByRootPath(root)!.StoreKey;
        newKey.ShouldStartWith("again-");
        newKey.Split('-')[^1].ShouldBe(storeKey.Split('-')[^1]);
    }
}
