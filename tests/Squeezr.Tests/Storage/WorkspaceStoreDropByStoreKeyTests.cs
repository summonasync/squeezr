using Shouldly;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Storage;

[Collection("CliProcessGlobals")]
public sealed class WorkspaceStoreDropByStoreKeyTests : IDisposable
{
    private const string HomeEnvironmentVariable = "SQUEEZR_HOME";
    private readonly string? _previousEnv;
    private readonly string _home;
    private readonly List<string> _createdDirs = [];

    public WorkspaceStoreDropByStoreKeyTests()
    {
        _previousEnv = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        _home = TestDirs.NewTempDir();
        _createdDirs.Add(_home);
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _home);
        SqueezrHome.Reset();
    }

    [Fact]
    public void DropByStoreKey_RemovesStoreDirAndRegistryEntry()
    {
        var root = NewTempDir();
        new WorkspaceManager(root).Init("dropped");
        var entry = WorkspaceRegistry.FindByRootPath(root);
        entry.ShouldNotBeNull();

        var removed = WorkspaceStore.DropByStoreKey(entry!.StoreKey);

        removed.ShouldBe([Path.Combine(_home, "workspaces", entry.StoreKey)]);
        Directory.Exists(Path.Combine(_home, "workspaces", entry.StoreKey)).ShouldBeFalse();
        WorkspaceRegistry.FindByRootPath(root).ShouldBeNull();
        WorkspaceRegistry.FindByStoreKey(entry.StoreKey).ShouldBeNull();
    }

    [Fact]
    public void DropByStoreKey_UnknownKey_ThrowsWorkspaceException()
    {
        var error = Should.Throw<WorkspaceException>(() => WorkspaceStore.DropByStoreKey("no-such-key"));

        error.Message.ShouldContain("no-such-key");
    }

    [Fact]
    public void DropByStoreKey_StaleEntryWithoutStoreDir_RemovesRegistryEntry()
    {
        var root = NewTempDir();
        new WorkspaceManager(root).Init("stale");
        var entry = WorkspaceRegistry.FindByRootPath(root)!;
        Directory.Delete(Path.Combine(_home, "workspaces", entry.StoreKey), recursive: true);
        Directory.Delete(root, recursive: true);

        var removed = WorkspaceStore.DropByStoreKey(entry.StoreKey);

        removed.ShouldBeEmpty();
        WorkspaceRegistry.FindByStoreKey(entry.StoreKey).ShouldBeNull();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _previousEnv);
        SqueezrHome.Reset();
        foreach (var dir in _createdDirs)
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private string NewTempDir()
    {
        var dir = TestDirs.NewTempDir();
        _createdDirs.Add(dir);
        return dir;
    }
}
