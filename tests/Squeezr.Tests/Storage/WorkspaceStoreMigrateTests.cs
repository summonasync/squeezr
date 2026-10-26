using System.Runtime.Versioning;
using Shouldly;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Storage;

[Collection("ProcessGlobals")]
public sealed class WorkspaceStoreMigrateTests : IDisposable
{
    private const string HomeEnvironmentVariable = "SQUEEZR_HOME";
    private const string ProfileEnvironmentVariable = "HOME";
    private readonly string? _previousEnv;
    private readonly string? _previousHome;
    private readonly string _home;
    private readonly List<string> _createdDirs = [];

    public WorkspaceStoreMigrateTests()
    {
        _previousEnv = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        _previousHome = Environment.GetEnvironmentVariable(ProfileEnvironmentVariable);
        _home = TestDirs.NewTempDir();
        _createdDirs.Add(_home);
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _home);
        Environment.SetEnvironmentVariable(ProfileEnvironmentVariable, NewDir());
        SqueezrHome.Reset();
    }

    [Fact]
    public void Migrate_BothPaths_StructuralStore_RefusesOnAutoAndMovesWithApproval()
    {
        var root = NewDir();
        WriteStructuralStore(root, withMetaDb: false);

        Should.Throw<WorkspaceException>(() => WorkspaceStore.Resolve(NewDir(Path.Combine(root, "projects", "fresh"))))
            .Message.ShouldContain("central squeezr store");

        var canonicalRoot = WorkspaceCanonicalPath.Canonicalize(root);
        var result = WorkspaceStore.MigrateWithApproval(root, WorkspaceMigrationApproval.Explicit);

        result.MovedFileCount.ShouldBe(4);
        result.SourceDir.ShouldBe(Path.Combine(canonicalRoot, SqueezrPaths.StoreDirName));
        result.StoreDir.ShouldBe(Path.Combine(_home, SqueezrPaths.WorkspacesDirName, result.StoreKey));
        File.Exists(Path.Combine(result.StoreDir, SqueezrPaths.StoreManifestFileName)).ShouldBeTrue();
        File.Exists(Path.Combine(result.StoreDir, SqueezrPaths.ConfigFileName)).ShouldBeTrue();
        File.Exists(Path.Combine(result.StoreDir, SqueezrPaths.WorkspacesDirName, "w1", "x.bin")).ShouldBeTrue();
        Directory.Exists(Path.Combine(root, SqueezrPaths.StoreDirName)).ShouldBeFalse();
        WorkspaceRegistry.FindByRootPath(canonicalRoot).ShouldNotBeNull();
    }

    [Fact]
    public void Migrate_MetaDbCentralShapedStore_RefusesOnAutoAndMovesWithApproval()
    {
        var root = NewDir();
        WriteStructuralStore(root, withMetaDb: true);

        Should.Throw<WorkspaceException>(() => WorkspaceStore.Resolve(NewDir(Path.Combine(root, "work"))))
            .Message.ShouldContain("central squeezr store");

        var result = WorkspaceStore.MigrateWithApproval(root, WorkspaceMigrationApproval.Explicit);

        result.MovedFileCount.ShouldBe(5);
        File.Exists(Path.Combine(result.StoreDir, SqueezrPaths.MetaDbFileName)).ShouldBeTrue();
        File.ReadAllText(Path.Combine(result.StoreDir, SqueezrPaths.MetaDbFileName)).ShouldBe("index payload");
        Directory.Exists(Path.Combine(root, SqueezrPaths.StoreDirName)).ShouldBeFalse();
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void Migrate_DefaultHomeStore_WithApproval_StillRefuses()
    {
        var temp = NewDir();
        var profile = NewDir(Path.Combine(temp, "profile"));
        var pinnedHome = NewDir(Path.Combine(temp, "pinned"));
        var defaultStore = NewDir(Path.Combine(profile, SqueezrPaths.StoreDirName));
        Directory.CreateDirectory(Path.Combine(defaultStore, SqueezrPaths.WorkspacesDirName));
        File.WriteAllText(Path.Combine(defaultStore, SqueezrPaths.RegistryFileName), "[]");
        File.WriteAllText(Path.Combine(profile, SqueezrPaths.ManifestFileName), """{"name":"home","repos":[]}""");
        Environment.SetEnvironmentVariable(ProfileEnvironmentVariable, profile);
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, pinnedHome);
        SqueezrHome.Reset();

        var message = Should.Throw<WorkspaceException>(
            () => WorkspaceStore.MigrateWithApproval(profile, WorkspaceMigrationApproval.Explicit)).Message;

        message.ShouldContain("central squeezr store");
        message.ShouldContain(SqueezrHome.DefaultHome());
        message.ShouldContain("Nothing was moved, renamed or deleted");
        Directory.Exists(defaultStore).ShouldBeTrue();
        File.Exists(Path.Combine(defaultStore, SqueezrPaths.RegistryFileName)).ShouldBeTrue();
        WorkspaceRegistry.Load().ShouldBeEmpty();
    }

    [Fact]
    public void Migrate_AlreadyRegisteredWorkspace_RefusesWithoutTouchingTheStore()
    {
        var root = NewDir();
        new WorkspaceManager(root).Init("registered");

        Should.Throw<WorkspaceException>(
            () => WorkspaceStore.MigrateWithApproval(root, WorkspaceMigrationApproval.Explicit))
            .Message.ShouldContain("already a registered workspace");

        Directory.Exists(SqueezrPaths.StoreDir(root)).ShouldBeTrue();
    }

    [Fact]
    public void Migrate_MissingStoreDir_RefusesWithThePath()
    {
        var root = NewDir();

        Should.Throw<WorkspaceException>(
            () => WorkspaceStore.MigrateWithApproval(root, WorkspaceMigrationApproval.Explicit))
            .Message.ShouldContain("Nothing to migrate");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _previousEnv);
        Environment.SetEnvironmentVariable(ProfileEnvironmentVariable, _previousHome);
        SqueezrHome.Reset();
        foreach (var dir in _createdDirs)
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private static void WriteStructuralStore(string root, bool withMetaDb)
    {
        File.WriteAllText(Path.Combine(root, SqueezrPaths.ManifestFileName), """{"name":"legacy","repos":[]}""");
        var storeDir = Path.Combine(root, SqueezrPaths.StoreDirName);
        Directory.CreateDirectory(Path.Combine(storeDir, SqueezrPaths.WorkspacesDirName, "w1"));
        File.WriteAllText(Path.Combine(storeDir, SqueezrPaths.WorkspacesDirName, "w1", "x.bin"), "chunk");
        File.WriteAllText(Path.Combine(storeDir, SqueezrPaths.RegistryFileName), "[]");
        File.WriteAllText(Path.Combine(storeDir, SqueezrPaths.ConfigFileName), "{}");
        if (withMetaDb)
        {
            File.WriteAllText(Path.Combine(storeDir, SqueezrPaths.MetaDbFileName), "index payload");
        }
    }

    private string NewDir(string? path = null)
    {
        var dir = Path.GetFullPath(path ?? TestDirs.NewTempDir());
        Directory.CreateDirectory(dir);
        _createdDirs.Add(dir);
        return dir;
    }
}
