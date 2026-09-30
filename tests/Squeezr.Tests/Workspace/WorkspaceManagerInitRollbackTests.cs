using Shouldly;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Workspace;

[Collection("CliProcessGlobals")]
public sealed class WorkspaceManagerInitRollbackTests : IDisposable
{
    private const string HomeEnvironmentVariable = "SQUEEZR_HOME";
    private readonly string? _previousHome;
    private readonly string _home;
    private readonly List<string> _createdDirs = [];

    public WorkspaceManagerInitRollbackTests()
    {
        _previousHome = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        _home = TestDirs.NewTempDir();
        _createdDirs.Add(_home);
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _home);
        SqueezrHome.Reset();
    }

    [Fact]
    public void Init_WhenStoreCreationFails_RollsBackRegistryEntry()
    {
        var root = TestDirs.NewTempDir();
        _createdDirs.Add(root);
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, SqueezrPaths.WorkspacesDirName), "blocks store directory creation");

        var manager = new WorkspaceManager(root);
        Should.Throw<IOException>(() => manager.Init("ghost"));

        WorkspaceRegistry.FindByRootPath(root).ShouldBeNull();
        var entries = WorkspaceRegistry.Load();
        entries.ShouldAllBe(entry => entry.RootPath != Path.GetFullPath(root));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _previousHome);
        SqueezrHome.Reset();
        foreach (var dir in _createdDirs)
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
