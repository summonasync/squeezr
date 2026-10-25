using System.Runtime.Versioning;
using Shouldly;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Storage;

[Collection("ProcessGlobals")]
public sealed class CentralStoreGuardTests : IDisposable
{
    private const string HomeEnvironmentVariable = "SQUEEZR_HOME";
    private const string ProfileEnvironmentVariable = "HOME";
    private readonly string? _previousEnv;
    private readonly string? _previousHome;
    private readonly string _home;
    private readonly string _storeDir;
    private readonly List<string> _createdDirs = [];

    public CentralStoreGuardTests()
    {
        _previousEnv = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        _previousHome = Environment.GetEnvironmentVariable(ProfileEnvironmentVariable);
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
    public void Drop_StoreParentWithUntrustedRow_ThrowsAndKeepsStore()
    {
        var projectRoot = NewProject("projA");
        new WorkspaceManager(projectRoot).Init("projA");
        var projectStoreDir = SqueezrPaths.StoreDir(projectRoot);
        WorkspaceRegistry.Add(new WorkspaceRegistryEntry(
            "home",
            WorkspaceCanonicalPath.Canonicalize(_home),
            "..",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow));
        File.WriteAllText(Path.Combine(_storeDir, "home-payload.txt"), "home payload");

        Should.Throw<WorkspaceException>(() => new WorkspaceManager(_home).Drop())
            .Message.ShouldContain("central squeezr store");

        Directory.Exists(_storeDir).ShouldBeTrue();
        File.ReadAllText(Path.Combine(_storeDir, "home-payload.txt")).ShouldBe("home payload");
        StoreFilesExist(projectStoreDir).ShouldBeTrue();
        OnlyExpectedStoreDirs([Path.GetFileName(projectStoreDir)]);
        File.Exists(WorkspaceRegistry.FilePath).ShouldBeTrue();
        WorkspaceRegistry.Load().Count.ShouldBe(2);
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

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void Drop_CentralLookingStoreWithNoEquality_RefusesAndKeepsTheStore()
    {
        // The W1 hole: a root whose .squeezr is central-shaped but equals no home passed the
        // path-identity guard, and drop deleted the whole foreign store.
        var temp = NewDir(TestDirs.NewTempDir());
        var root = NewDir(Path.Combine(temp, "proj"));
        var foreignStore = NewDir(Path.Combine(root, SqueezrPaths.StoreDirName));
        Directory.CreateDirectory(Path.Combine(foreignStore, SqueezrPaths.WorkspacesDirName));
        File.WriteAllText(Path.Combine(foreignStore, SqueezrPaths.RegistryFileName), "[]");
        File.WriteAllText(Path.Combine(foreignStore, "canary.txt"), "keep me");
        var pinnedHome = NewDir(Path.Combine(temp, "pinned"));
        Environment.SetEnvironmentVariable(ProfileEnvironmentVariable, NewDir(Path.Combine(temp, "home-profile")));
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, pinnedHome);
        SqueezrHome.Reset();
        WorkspaceCanonicalPath.ResolveThroughLinks(SqueezrHome.DefaultHome())
            .ShouldStartWith(WorkspaceCanonicalPath.Canonicalize(temp));

        var canonicalStore = WorkspaceCanonicalPath.ResolveThroughLinks(foreignStore);
        canonicalStore.ShouldNotBe(WorkspaceCanonicalPath.ResolveThroughLinks(SqueezrHome.Resolve()));
        canonicalStore.ShouldNotBe(WorkspaceCanonicalPath.ResolveThroughLinks(SqueezrHome.DefaultHome()));

        var message = Should.Throw<WorkspaceException>(() => new WorkspaceManager(root).Drop()).Message;

        message.ShouldContain("central squeezr store");
        message.ShouldContain("Nothing was deleted");
        message.ShouldContain(SqueezrHome.Resolve());
        message.ShouldContain(SqueezrHome.DefaultHome());
        File.ReadAllText(Path.Combine(foreignStore, "canary.txt")).ShouldBe("keep me");
        File.Exists(Path.Combine(foreignStore, SqueezrPaths.RegistryFileName)).ShouldBeTrue();
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void Init_CentralLookingStoreWithNoEquality_RefusesAndWritesNoMetaDb()
    {
        var temp = NewDir(TestDirs.NewTempDir());
        var root = NewDir(Path.Combine(temp, "proj"));
        var foreignStore = NewDir(Path.Combine(root, SqueezrPaths.StoreDirName));
        Directory.CreateDirectory(Path.Combine(foreignStore, SqueezrPaths.WorkspacesDirName));
        File.WriteAllText(Path.Combine(foreignStore, SqueezrPaths.RegistryFileName), "[]");
        var pinnedHome = NewDir(Path.Combine(temp, "pinned"));
        Environment.SetEnvironmentVariable(ProfileEnvironmentVariable, NewDir(Path.Combine(temp, "home-profile")));
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, pinnedHome);
        SqueezrHome.Reset();
        WorkspaceCanonicalPath.ResolveThroughLinks(SqueezrHome.DefaultHome())
            .ShouldStartWith(WorkspaceCanonicalPath.Canonicalize(temp));

        var canonicalStore = WorkspaceCanonicalPath.ResolveThroughLinks(foreignStore);
        canonicalStore.ShouldNotBe(WorkspaceCanonicalPath.ResolveThroughLinks(SqueezrHome.Resolve()));
        canonicalStore.ShouldNotBe(WorkspaceCanonicalPath.ResolveThroughLinks(SqueezrHome.DefaultHome()));

        var message = Should.Throw<WorkspaceException>(() => new WorkspaceManager(root).Init("probe")).Message;

        message.ShouldContain("central squeezr store");
        message.ShouldContain("Nothing was created or changed");
        message.ShouldContain(SqueezrHome.Resolve());
        message.ShouldContain(SqueezrHome.DefaultHome());
        File.Exists(Path.Combine(foreignStore, SqueezrPaths.MetaDbFileName)).ShouldBeFalse();
        File.Exists(Path.Combine(foreignStore, SqueezrPaths.RegistryFileName)).ShouldBeTrue();
        WorkspaceRegistry.Load().ShouldBeEmpty();
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void Init_DefaultHomeUnderOverride_RefusesAndNamesBothHomes()
    {
        var pinnedHome = NewDir(Path.Combine(TestDirs.NewTempDir(), "pinned"));
        Environment.SetEnvironmentVariable(ProfileEnvironmentVariable, _home);
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, pinnedHome);
        SqueezrHome.Reset();

        var message = Should.Throw<WorkspaceException>(() => new WorkspaceManager(_home).Init("home")).Message;

        message.ShouldContain("central squeezr store");
        message.ShouldContain(SqueezrHome.DefaultHome());
        message.ShouldContain(SqueezrHome.Resolve());
        message.ShouldContain($"{SqueezrHome.HomeEnvironmentVariable}='{pinnedHome}' is in effect.");
        message.ShouldContain("Nothing was created or changed.");
        WorkspaceRegistry.Load().ShouldBeEmpty();
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void Resolve_WalkIntoManifestRootAtDefaultHome_RefusesOnThePathSignalAlone()
    {
        var pinnedHome = NewDir(Path.Combine(TestDirs.NewTempDir(), "pinned"));
        Environment.SetEnvironmentVariable(ProfileEnvironmentVariable, _home);
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, pinnedHome);
        SqueezrHome.Reset();
        File.WriteAllText(Path.Combine(_home, SqueezrPaths.ManifestFileName), """{"name":"home","repos":[]}""");
        Directory.CreateDirectory(_storeDir);

        var message = Should.Throw<WorkspaceException>(
            () => WorkspaceStore.Resolve(NewDir(Path.Combine(_home, "projects", "fresh")))).Message;

        message.ShouldContain("central squeezr store");
        message.ShouldContain(_storeDir);
        message.ShouldContain(SqueezrHome.DefaultHome());
        message.ShouldContain(SqueezrHome.Resolve());
        message.ShouldContain($"{SqueezrHome.HomeEnvironmentVariable}='{pinnedHome}' is in effect.");
        message.ShouldContain("Nothing was moved, renamed or deleted.");
        SqueezrHome.LooksLikeCentralStore(_storeDir).ShouldBeFalse();
        Directory.Exists(_storeDir).ShouldBeTrue();
        File.Exists(Path.Combine(_home, SqueezrPaths.ManifestFileName)).ShouldBeTrue();
        WorkspaceRegistry.Load().ShouldBeEmpty();
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void Resolve_WalkIntoManifestRootWithForeignCentralShapedStore_RefusesOnTheStructuralSignalAlone()
    {
        var temp = TestDirs.NewTempDir();
        var homeProfile = NewDir(Path.Combine(temp, "home"));
        var pinnedHome = NewDir(Path.Combine(temp, "pinned"));
        var foreignProfile = NewDir(Path.Combine(temp, "foreign"));
        var foreignStore = NewDir(Path.Combine(foreignProfile, SqueezrPaths.StoreDirName));
        Directory.CreateDirectory(Path.Combine(foreignStore, SqueezrPaths.WorkspacesDirName));
        File.WriteAllText(Path.Combine(foreignStore, SqueezrPaths.RegistryFileName), "[]");
        File.WriteAllText(
            Path.Combine(foreignProfile, SqueezrPaths.ManifestFileName), """{"name":"foreign","repos":[]}""");
        Environment.SetEnvironmentVariable(ProfileEnvironmentVariable, homeProfile);
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, pinnedHome);
        SqueezrHome.Reset();

        var canonicalStore = WorkspaceCanonicalPath.ResolveThroughLinks(foreignStore);
        canonicalStore.ShouldNotBe(WorkspaceCanonicalPath.ResolveThroughLinks(SqueezrHome.Resolve()));
        canonicalStore.ShouldNotBe(WorkspaceCanonicalPath.ResolveThroughLinks(SqueezrHome.DefaultHome()));

        var message = Should.Throw<WorkspaceException>(
            () => WorkspaceStore.Resolve(NewDir(Path.Combine(foreignProfile, "work")))).Message;

        message.ShouldContain("central squeezr store");
        message.ShouldContain("Nothing was moved, renamed or deleted.");
        message.ShouldContain($"{SqueezrHome.HomeEnvironmentVariable}='{pinnedHome}' is in effect.");
        Directory.Exists(foreignStore).ShouldBeTrue();
        File.Exists(Path.Combine(foreignStore, SqueezrPaths.RegistryFileName)).ShouldBeTrue();
        File.Exists(Path.Combine(foreignProfile, SqueezrPaths.ManifestFileName)).ShouldBeTrue();
        WorkspaceRegistry.Load().ShouldBeEmpty();
    }

    [Fact]
    public void Resolve_RegisteredRowWithCentralShapedStore_RefusesOnStructureBeforeTheMove()
    {
        var root = NewDir(TestDirs.NewTempDir());
        var decoyStore = NewDir(Path.Combine(root, SqueezrPaths.StoreDirName));
        Directory.CreateDirectory(Path.Combine(decoyStore, SqueezrPaths.WorkspacesDirName));
        File.WriteAllText(Path.Combine(decoyStore, SqueezrPaths.RegistryFileName), "[]");
        File.WriteAllText(Path.Combine(root, SqueezrPaths.ManifestFileName), """{"name":"registered","repos":[]}""");
        var canonicalRoot = WorkspaceCanonicalPath.Canonicalize(root);
        var storeKey = WorkspaceStoreKeys.Create("registered", canonicalRoot);
        WorkspaceRegistry.Add(new WorkspaceRegistryEntry(
            "registered", canonicalRoot, storeKey, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        var canonicalDecoy = WorkspaceCanonicalPath.ResolveThroughLinks(decoyStore);
        canonicalDecoy.ShouldNotBe(WorkspaceCanonicalPath.ResolveThroughLinks(SqueezrHome.Resolve()));
        canonicalDecoy.ShouldNotBe(WorkspaceCanonicalPath.ResolveThroughLinks(SqueezrHome.DefaultHome()));

        var message = Should.Throw<WorkspaceException>(() => WorkspaceStore.Resolve(root)).Message;

        message.ShouldContain("central squeezr store");
        message.ShouldContain("Nothing was moved, renamed or deleted.");
        message.ShouldContain(SqueezrHome.DefaultHome());
        message.ShouldContain(SqueezrHome.Resolve());
        Directory.Exists(decoyStore).ShouldBeTrue();
        File.Exists(Path.Combine(decoyStore, SqueezrPaths.RegistryFileName)).ShouldBeTrue();
        File.Exists(Path.Combine(root, SqueezrPaths.ManifestFileName)).ShouldBeTrue();
        WorkspaceRegistry.FindByRootPath(canonicalRoot).ShouldNotBeNull();
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
