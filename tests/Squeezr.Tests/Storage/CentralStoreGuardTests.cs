using Shouldly;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Storage;

[Collection("ProcessGlobals")]
public sealed class CentralStoreGuardTests : IDisposable
{
    private const string HomeEnvironmentVariable = "SQUEEZR_HOME";
    private readonly string? _previousEnv;
    private readonly string _home;
    private readonly string _storeDir;
    private readonly List<string> _createdDirs = [];

    public CentralStoreGuardTests()
    {
        _previousEnv = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        _home = TestDirs.NewTempDir();
        _createdDirs.Add(_home);
        _storeDir = Path.Combine(_home, SqueezrPaths.StoreDirName);
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _storeDir);
        SqueezrHome.Reset();
    }

    [Fact]
    public void Resolve_UnderStoreParentWithoutCloserWorkspace_DoesNotRegisterOrDestroyStore()
    {
        var projectRoot = NewProject("projA");
        new WorkspaceManager(projectRoot).Init("projA");
        var projectStoreDir = SqueezrPaths.StoreDir(projectRoot);
        var triggerDir = NewDir(Path.Combine(_home, "orchestrator-tmp", "live-check"));

        var result = WorkspaceStore.Resolve(triggerDir);

        result.IsWorkspace.ShouldBeFalse();
        result.MigratedFromLegacy.ShouldBeFalse();
        result.RootPath.ShouldBe(Path.GetFullPath(triggerDir));

        File.Exists(WorkspaceRegistry.FilePath).ShouldBeTrue();
        var entries = WorkspaceRegistry.Load();
        entries.ShouldHaveSingleItem().RootPath.ShouldBe(WorkspaceCanonicalPath.Canonicalize(projectRoot));
        WorkspaceRegistry.FindByRootPath(_home).ShouldBeNull();
        StoreFilesExist(projectStoreDir).ShouldBeTrue();
        OnlyExpectedStoreDirs([Path.GetFileName(projectStoreDir)]);
    }

    [Fact]
    public void Resolve_StoreParentRegistered_DoesNotTouchCentralStore()
    {
        var projectRoot = NewProject("projA");
        new WorkspaceManager(projectRoot).Init("projA");
        var projectStoreDir = SqueezrPaths.StoreDir(projectRoot);
        var canonicalHome = WorkspaceCanonicalPath.Canonicalize(_home);
        WorkspaceRegistry.Add(new WorkspaceRegistryEntry("home", canonicalHome, "home-deadbeef", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        var triggerDir = NewDir(Path.Combine(_home, "scratch"));

        var result = WorkspaceStore.Resolve(triggerDir);

        result.IsWorkspace.ShouldBeTrue();
        result.RootPath.ShouldBe(canonicalHome);
        result.MigratedFromLegacy.ShouldBeFalse();

        File.Exists(WorkspaceRegistry.FilePath).ShouldBeTrue();
        WorkspaceRegistry.Load().Count.ShouldBe(2);
        StoreFilesExist(projectStoreDir).ShouldBeTrue();
        OnlyExpectedStoreDirs([Path.GetFileName(projectStoreDir)]);
    }

    [Fact]
    public void Resolve_StoreParentWithLegacyManifest_RefusesMigrationAndKeepsStore()
    {
        var projectRoot = NewProject("projA");
        new WorkspaceManager(projectRoot).Init("projA");
        var projectStoreDir = SqueezrPaths.StoreDir(projectRoot);
        var canonicalHome = WorkspaceCanonicalPath.Canonicalize(_home);
        WorkspaceRegistry.Add(new WorkspaceRegistryEntry("home", canonicalHome, "home-deadbeef", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        File.WriteAllText(Path.Combine(_home, SqueezrPaths.ManifestFileName), """{"name":"home","repos":[]}""");
        var triggerDir = NewDir(Path.Combine(_home, "scratch"));

        Should.Throw<WorkspaceException>(() => WorkspaceStore.Resolve(triggerDir));

        File.Exists(WorkspaceRegistry.FilePath).ShouldBeTrue();
        WorkspaceRegistry.Load().Count.ShouldBe(2);
        StoreFilesExist(projectStoreDir).ShouldBeTrue();
        OnlyExpectedStoreDirs([Path.GetFileName(projectStoreDir)]);
        File.Exists(Path.Combine(_home, SqueezrPaths.ManifestFileName)).ShouldBeTrue();
    }

    [Fact]
    public void Init_StoreParent_ThrowsAndWritesNothing()
    {
        Should.Throw<WorkspaceException>(() => new WorkspaceManager(_home).Init("home"))
            .Message.ShouldContain("central squeezr store");

        File.Exists(WorkspaceRegistry.FilePath).ShouldBeFalse();
        WorkspaceRegistry.Load().ShouldBeEmpty();
        Directory.Exists(Path.Combine(_storeDir, SqueezrPaths.WorkspacesDirName)).ShouldBeFalse();
    }

    [Fact]
    public void Resolve_StoreParentWithLegacyManifestWithoutEntry_RefusesBeforeRegistering()
    {
        File.WriteAllText(Path.Combine(_home, SqueezrPaths.ManifestFileName), """{"name":"home","repos":[]}""");
        var triggerDir = NewDir(Path.Combine(_home, "scratch"));

        Should.Throw<WorkspaceException>(() => WorkspaceStore.Resolve(triggerDir))
            .Message.ShouldContain("central squeezr store");

        WorkspaceRegistry.Load().ShouldBeEmpty();
        File.Exists(WorkspaceRegistry.FilePath).ShouldBeFalse();
        File.Exists(Path.Combine(_home, SqueezrPaths.ManifestFileName)).ShouldBeTrue();
        Directory.Exists(Path.Combine(_storeDir, SqueezrPaths.WorkspacesDirName)).ShouldBeFalse();
    }

    [Fact]
    public void Init_UnderStoreParentNotAWorkspace_SucceedsAsOwnWorkspace()
    {
        var projectRoot = NewProject("projA");
        new WorkspaceManager(projectRoot).Init("projA");
        var projectStoreDir = SqueezrPaths.StoreDir(projectRoot);
        var liveCheckDir = NewDir(Path.Combine(_home, "orchestrator-tmp", "live-check"));

        var result = new WorkspaceManager(liveCheckDir).Init("live-check");

        result.WorkspaceName.ShouldBe("live-check");
        result.RootPath.ShouldBe(Path.GetFullPath(liveCheckDir));
        StoreFilesExist(projectStoreDir).ShouldBeTrue();
        StoreFilesExist(SqueezrPaths.StoreDir(liveCheckDir)).ShouldBeTrue();
        WorkspaceRegistry.Load().Count.ShouldBe(2);
        OnlyExpectedStoreDirs(
        [
            Path.GetFileName(projectStoreDir),
            Path.GetFileName(SqueezrPaths.StoreDir(liveCheckDir)),
        ]);
    }

    [Fact]
    public void InitAndAdd_SecondWorkspaceNeverRemovesFirstWorkspaceFiles()
    {
        var firstRoot = NewProject("projA");
        var secondRoot = NewProject("projB");

        new WorkspaceManager(firstRoot).Init("projA");
        var firstStoreDir = SqueezrPaths.StoreDir(firstRoot);
        StoreFilesExist(firstStoreDir).ShouldBeTrue();

        var second = new WorkspaceManager(secondRoot);
        second.Init("projB");
        StoreFilesExist(firstStoreDir).ShouldBeTrue();

        second.AddRepo(".", "repoB");
        StoreFilesExist(firstStoreDir).ShouldBeTrue();

        File.ReadAllText(Path.Combine(firstStoreDir, SqueezrPaths.StoreManifestFileName))
            .ShouldContain("projA");
        var entries = WorkspaceRegistry.Load();
        entries.Count.ShouldBe(2);
        WorkspaceRegistry.FindByRootPath(firstRoot).ShouldNotBeNull();
        WorkspaceRegistry.FindByRootPath(secondRoot).ShouldNotBeNull();
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

    private static bool StoreFilesExist(string storeDir) =>
        File.Exists(Path.Combine(storeDir, SqueezrPaths.StoreManifestFileName))
        && File.Exists(Path.Combine(storeDir, SqueezrPaths.ConfigFileName))
        && File.Exists(Path.Combine(storeDir, SqueezrPaths.MetaDbFileName));

    private void OnlyExpectedStoreDirs(IReadOnlyList<string> expectedStoreDirNames)
    {
        var workspacesDir = Path.Combine(_storeDir, SqueezrPaths.WorkspacesDirName);
        var actual = Directory.Exists(workspacesDir)
            ? Directory.EnumerateDirectories(workspacesDir).Select(dir => Path.GetFileName(dir)!).OrderBy(name => name, StringComparer.Ordinal).ToList()
            : [];
        actual.ShouldBe(expectedStoreDirNames.OrderBy(name => name, StringComparer.Ordinal).ToList());
    }

    private string NewProject(string name)
    {
        var root = NewDir(Path.Combine(_home, name));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        return root;
    }

    private string NewDir(string path)
    {
        var dir = Path.GetFullPath(path);
        Directory.CreateDirectory(dir);
        _createdDirs.Add(dir);
        return dir;
    }
}
