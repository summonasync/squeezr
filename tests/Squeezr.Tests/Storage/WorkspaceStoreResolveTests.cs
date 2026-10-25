using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Storage;

[Collection("ProcessGlobals")]
public sealed class WorkspaceStoreResolveTests : IDisposable
{
    private const string HomeEnvironmentVariable = "SQUEEZR_HOME";
    private const string ProfileEnvironmentVariable = "HOME";
    private readonly string? _previousEnv;
    private readonly string? _previousHome;
    private readonly string _home;
    private readonly List<string> _createdDirs = [];

    public WorkspaceStoreResolveTests()
    {
        _previousEnv = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        _previousHome = Environment.GetEnvironmentVariable(ProfileEnvironmentVariable);
        _home = TestDirs.NewTempDir();
        _createdDirs.Add(_home);
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _home);
        SqueezrHome.Reset();
    }

    [Fact]
    public void Resolve_UnregisteredStartWithoutLegacy_ReturnsNotFound()
    {
        var start = NewDir();

        var result = WorkspaceStore.Resolve(start);

        result.IsWorkspace.ShouldBeFalse();
        result.MigratedFromLegacy.ShouldBeFalse();
        result.StoreKey.ShouldBeNull();
        result.StoreDir.ShouldBeNull();
        result.RootPath.ShouldBe(Path.GetFullPath(start));
    }

    [Fact]
    public void Resolve_StartAtFilesystemRoot_ReturnsNotFoundInsteadOfThrowing()
    {
        // The upward walk canonicalizes every level, so it always reaches the
        // filesystem root; on Windows that used to throw before the loop could exit.
        var root = Path.GetPathRoot(Path.GetTempPath());
        root.ShouldNotBeNullOrEmpty();

        var result = WorkspaceStore.Resolve(root);

        result.IsWorkspace.ShouldBeFalse();
        result.RootPath.ShouldBe(root);
    }

    [Fact]
    public void Resolve_StartUnderTheStoreHome_DoesNotMigrateTheCentralStoreAsLegacy()
    {
        // The central store lives at <home>/.squeezr, which is shaped exactly like a
        // legacy in-project store dir. Walking up from any directory beneath the home
        // used to "migrate" it: contents moved away, directory deleted.
        var profile = NewDir();
        var store = Path.Combine(profile, SqueezrPaths.StoreDirName);
        Directory.CreateDirectory(Path.Combine(store, SqueezrPaths.WorkspacesDirName));
        File.WriteAllText(Path.Combine(store, SqueezrPaths.RegistryFileName), "[]");
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, store);
        SqueezrHome.Reset();

        var result = WorkspaceStore.Resolve(NewDir(Path.Combine(profile, "projects", "fresh")));

        result.IsWorkspace.ShouldBeFalse();
        result.MigratedFromLegacy.ShouldBeFalse();
        Directory.Exists(store).ShouldBeTrue();
        File.Exists(Path.Combine(store, SqueezrPaths.RegistryFileName)).ShouldBeTrue();
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void Resolve_OverridePinnedAway_StartUnderFakedHome_DoesNotMigrateTheDefaultHomeStore()
    {
        // The 2026-10-19 shape: SQUEEZR_HOME pinned elsewhere makes the profile's own store
        // "not central" for a resolve-relative predicate, and the upward walk used to carry
        // the default home away as a legacy in-project store. Centrality is absolute.
        var profile = NewDir();
        var defaultStore = NewDir(Path.Combine(profile, SqueezrPaths.StoreDirName));
        Directory.CreateDirectory(Path.Combine(defaultStore, SqueezrPaths.WorkspacesDirName));
        File.WriteAllText(Path.Combine(defaultStore, SqueezrPaths.RegistryFileName), "[]");
        var pinnedHome = NewDir();
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, pinnedHome);
        Environment.SetEnvironmentVariable(ProfileEnvironmentVariable, profile);
        SqueezrHome.Reset();

        var result = WorkspaceStore.Resolve(NewDir(Path.Combine(profile, "projects", "fresh")));

        result.IsWorkspace.ShouldBeFalse();
        result.MigratedFromLegacy.ShouldBeFalse();
        Directory.Exists(defaultStore).ShouldBeTrue();
        File.Exists(Path.Combine(defaultStore, SqueezrPaths.RegistryFileName)).ShouldBeTrue();
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void Resolve_OverridePinnedAway_BareDefaultHomeStore_IsNotLegacyOnTheWalk()
    {
        // A fresh or never-initialized default home holds a bare .squeezr, so the structural
        // signal is silent; only the absolute default-home arm keeps the walk from treating
        // it as a legacy in-project store under a pinned override.
        var profile = NewDir();
        var defaultStore = NewDir(Path.Combine(profile, SqueezrPaths.StoreDirName));
        var pinnedHome = NewDir();
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, pinnedHome);
        Environment.SetEnvironmentVariable(ProfileEnvironmentVariable, profile);
        SqueezrHome.Reset();

        var result = WorkspaceStore.Resolve(NewDir(Path.Combine(profile, "projects", "fresh")));

        result.IsWorkspace.ShouldBeFalse();
        result.MigratedFromLegacy.ShouldBeFalse();
        Directory.Exists(defaultStore).ShouldBeTrue();
    }

    [Fact]
    public void Resolve_StartUnderAForeignCentralStore_DoesNotMigrateItEither()
    {
        // Same shape, but not the store this process resolves - an old store left behind
        // after SQUEEZR_HOME was repointed. Still a central store, still not legacy.
        var profile = NewDir();
        var foreignStore = Path.Combine(profile, SqueezrPaths.StoreDirName);
        Directory.CreateDirectory(Path.Combine(foreignStore, SqueezrPaths.WorkspacesDirName));
        File.WriteAllText(Path.Combine(foreignStore, SqueezrPaths.RegistryFileName), "[]");

        var result = WorkspaceStore.Resolve(NewDir(Path.Combine(profile, "work")));

        result.IsWorkspace.ShouldBeFalse();
        result.MigratedFromLegacy.ShouldBeFalse();
        File.Exists(Path.Combine(foreignStore, SqueezrPaths.RegistryFileName)).ShouldBeTrue();
    }

    [Fact]
    public void Resolve_NestedStart_FindsRegisteredWorkspace()
    {
        var root = NewDir();
        new WorkspaceManager(root).Init("My System");
        var nested = NewDir(Path.Combine(root, "src", "lib", "deep"));

        var result = WorkspaceStore.Resolve(nested);

        result.IsWorkspace.ShouldBeTrue();
        result.MigratedFromLegacy.ShouldBeFalse();
        result.RootPath.ShouldBe(Path.GetFullPath(root));
        result.StoreDir.ShouldBe(Path.Combine(_home, SqueezrPaths.WorkspacesDirName, result.StoreKey!));
        Directory.Exists(result.StoreDir).ShouldBeTrue();
    }

    [Fact]
    public void Resolve_RepeatedResolveOfSameRoot_ReturnsSameStoreKey()
    {
        var root = NewDir();
        new WorkspaceManager(root).Init("demo");

        var first = WorkspaceStore.Resolve(root);
        var second = WorkspaceStore.Resolve(root);

        first.StoreKey.ShouldNotBeNull();
        second.StoreKey.ShouldBe(first.StoreKey);
        second.RootPath.ShouldBe(first.RootPath);
    }

    [Fact]
    public void StoreKey_IsReadableSlugWithEightHexHashSuffix()
    {
        var root = NewDir();
        new WorkspaceManager(root).Init("My System!");

        var key = WorkspaceStore.Resolve(root).StoreKey!;

        Regex.IsMatch(key, "^my-system-[0-9a-f]{8}$").ShouldBeTrue();
    }

    [Fact]
    public void StoreKey_DiffersAcrossRoots()
    {
        var firstRoot = NewDir();
        var secondRoot = NewDir();
        new WorkspaceManager(firstRoot).Init("demo");
        new WorkspaceManager(secondRoot).Init("demo");

        WorkspaceStore.Resolve(firstRoot).StoreKey.ShouldNotBe(WorkspaceStore.Resolve(secondRoot).StoreKey);
    }

    [Fact]
    public void Resolve_LegacyWorkspace_MigratesContentToStore()
    {
        var root = NewDir();
        var nested = NewDir(Path.Combine(root, "src", "deep"));
        CreateLegacyLayout(root, withComposeMarker: true);
        File.WriteAllText(Path.Combine(root, "keep.txt"), "user file");

        var result = WorkspaceStore.Resolve(nested);

        result.IsWorkspace.ShouldBeTrue();
        result.MigratedFromLegacy.ShouldBeTrue();
        result.RootPath.ShouldBe(Path.GetFullPath(root));

        File.Exists(Path.Combine(result.StoreDir!, SqueezrPaths.StoreManifestFileName)).ShouldBeTrue();
        File.Exists(Path.Combine(result.StoreDir!, SqueezrPaths.ConfigFileName)).ShouldBeTrue();
        File.Exists(Path.Combine(result.StoreDir!, SqueezrPaths.MetaDbFileName)).ShouldBeTrue();
        File.Exists(Path.Combine(result.StoreDir!, SqueezrPaths.TrigramIndexFileName)).ShouldBeTrue();
        File.Exists(Path.Combine(result.StoreDir!, SqueezrPaths.LockFileName)).ShouldBeTrue();
        File.Exists(Path.Combine(result.StoreDir!, SqueezrPaths.StateFileName)).ShouldBeFalse();
        WorkspaceManifestFile.Load(root).Name.ShouldBe("legacy");

        File.Exists(Path.Combine(root, SqueezrPaths.ManifestFileName)).ShouldBeFalse();
        Directory.Exists(Path.Combine(root, SqueezrPaths.StoreDirName)).ShouldBeFalse();
        File.Exists(Path.Combine(root, SqueezrPaths.ComposeFileName)).ShouldBeFalse();
        File.Exists(Path.Combine(root, "keep.txt")).ShouldBeTrue();

        var entry = WorkspaceRegistry.FindByRootPath(root);
        entry.ShouldNotBeNull();
        entry!.StoreKey.ShouldBe(result.StoreKey);
    }

    [Fact]
    public void Resolve_AfterFullMigration_ReturnsNoMigrationFlag()
    {
        var root = NewDir();
        CreateLegacyLayout(root, withComposeMarker: false);

        var first = WorkspaceStore.Resolve(root);
        first.MigratedFromLegacy.ShouldBeTrue();

        var second = WorkspaceStore.Resolve(root);

        second.MigratedFromLegacy.ShouldBeFalse();
        second.IsWorkspace.ShouldBeTrue();
        second.StoreKey.ShouldBe(first.StoreKey);
    }

    [Fact]
    public void Resolve_LegacyComposeWithoutMarker_KeepsComposeFile()
    {
        var root = NewDir();
        CreateLegacyLayout(root, withComposeMarker: false, composeContent: "services:\n  app:\n    image: nginx\n");

        var result = WorkspaceStore.Resolve(root);

        result.MigratedFromLegacy.ShouldBeTrue();
        File.Exists(Path.Combine(root, SqueezrPaths.ComposeFileName)).ShouldBeTrue();
    }

    [Fact]
    public void Resolve_LegacyManifestOnly_MigratesWithManifestName()
    {
        var root = NewDir();
        File.WriteAllText(Path.Combine(root, SqueezrPaths.ManifestFileName), """{"name":"from-manifest","repos":[]}""");

        var result = WorkspaceStore.Resolve(root);

        result.IsWorkspace.ShouldBeTrue();
        result.MigratedFromLegacy.ShouldBeTrue();
        WorkspaceRegistry.FindByRootPath(root)!.Name.ShouldBe("from-manifest");
        File.Exists(Path.Combine(result.StoreDir!, SqueezrPaths.StoreManifestFileName)).ShouldBeTrue();
    }

    [Fact]
    public void Resolve_LegacyStoreDirOnly_MigratesWithDirectoryName()
    {
        var root = NewDir();
        Directory.CreateDirectory(Path.Combine(root, SqueezrPaths.StoreDirName));
        File.WriteAllText(Path.Combine(root, SqueezrPaths.StoreDirName, SqueezrPaths.ConfigFileName), "{}");

        var result = WorkspaceStore.Resolve(root);

        result.IsWorkspace.ShouldBeTrue();
        result.MigratedFromLegacy.ShouldBeTrue();
        WorkspaceRegistry.FindByRootPath(root)!.Name.ShouldBe(new DirectoryInfo(root).Name);
        File.Exists(Path.Combine(result.StoreDir!, SqueezrPaths.ConfigFileName)).ShouldBeTrue();
        Directory.Exists(Path.Combine(root, SqueezrPaths.StoreDirName)).ShouldBeFalse();
    }

    [Fact]
    public void Resolve_LegacyWorkspaceWithNestedStoreDir_MovesSubdirectories()
    {
        var root = NewDir();
        CreateLegacyLayout(root, withComposeMarker: false);
        var nestedDir = Path.Combine(root, SqueezrPaths.StoreDirName, "snapshots", "v1");
        Directory.CreateDirectory(nestedDir);
        File.WriteAllText(Path.Combine(nestedDir, "data.bin"), "payload");

        var result = WorkspaceStore.Resolve(root);

        result.MigratedFromLegacy.ShouldBeTrue();
        File.Exists(Path.Combine(result.StoreDir!, "snapshots", "v1", "data.bin")).ShouldBeTrue();
        File.ReadAllText(Path.Combine(result.StoreDir!, "snapshots", "v1", "data.bin")).ShouldBe("payload");
        Directory.Exists(Path.Combine(root, SqueezrPaths.StoreDirName)).ShouldBeFalse();
    }

    [Fact]
    public void Resolve_PartialMigration_ResumesAndCompletes()
    {
        var root = NewDir();
        CreateLegacyLayout(root, withComposeMarker: false);
        var storeKey = WorkspaceStoreKeys.Create("legacy", Path.GetFullPath(root));
        var storeDir = Path.Combine(_home, SqueezrPaths.WorkspacesDirName, storeKey);
        Directory.CreateDirectory(storeDir);
        File.Move(
            Path.Combine(root, SqueezrPaths.StoreDirName, SqueezrPaths.MetaDbFileName),
            Path.Combine(storeDir, SqueezrPaths.MetaDbFileName));

        var result = WorkspaceStore.Resolve(root);

        result.IsWorkspace.ShouldBeTrue();
        result.MigratedFromLegacy.ShouldBeTrue();
        result.StoreKey.ShouldBe(storeKey);
        File.Exists(Path.Combine(storeDir, SqueezrPaths.MetaDbFileName)).ShouldBeTrue();
        File.Exists(Path.Combine(storeDir, SqueezrPaths.ConfigFileName)).ShouldBeTrue();
        File.Exists(Path.Combine(storeDir, SqueezrPaths.StateFileName)).ShouldBeFalse();
        File.Exists(Path.Combine(storeDir, SqueezrPaths.StoreManifestFileName)).ShouldBeTrue();
        File.Exists(Path.Combine(root, SqueezrPaths.ManifestFileName)).ShouldBeFalse();
        Directory.Exists(Path.Combine(root, SqueezrPaths.StoreDirName)).ShouldBeFalse();
    }

    [Fact]
    public void Resolve_RegisteredWorkspaceWithLeftoverLegacyFiles_CompletesMigration()
    {
        var root = NewDir();
        CreateLegacyLayout(root, withComposeMarker: false);
        var first = WorkspaceStore.Resolve(root);
        first.MigratedFromLegacy.ShouldBeTrue();
        File.WriteAllText(
            Path.Combine(root, SqueezrPaths.ManifestFileName),
            """{"name":"legacy","repos":[]}""");

        var second = WorkspaceStore.Resolve(root);

        second.MigratedFromLegacy.ShouldBeTrue();
        second.StoreKey.ShouldBe(first.StoreKey);
        File.Exists(Path.Combine(root, SqueezrPaths.ManifestFileName)).ShouldBeFalse();
    }

    [Fact]
    public void Resolve_SymlinkedStart_ResolvesThroughLinkTarget()
    {
        var realRoot = NewDir();
        new WorkspaceManager(realRoot).Init("realws");
        var linkPath = Path.Combine(NewDir(), "link");
        var linkCreated = TryCreateSymbolicLink(linkPath, realRoot);
        if (!linkCreated && !OperatingSystem.IsWindows())
        {
            linkCreated.ShouldBeTrue();
            return;
        }

        if (!linkCreated)
        {
            return;
        }

        var nested = NewDir(Path.Combine(linkPath, "sub"));
        var result = WorkspaceStore.Resolve(nested);

        result.IsWorkspace.ShouldBeTrue();
        result.RootPath.ShouldBe(Path.GetFullPath(realRoot));
        result.StoreKey.ShouldBe(WorkspaceStore.Resolve(realRoot).StoreKey);
    }

    [Fact]
    public void Resolve_LegacyShortIgnoreList_UnionsCurrentDefaults()
    {
        var root = NewDir();
        CreateLegacyLayoutWithConfig(root, LegacyConfig(ignore: """["**/node_modules/**", "**/custom-build/**"]"""));

        var result = WorkspaceStore.Resolve(root);

        result.MigratedFromLegacy.ShouldBeTrue();
        var defaults = SqueezrConfig.CreateDefault().Indexing.Ignore;
        ConfigFile.Load(root).Indexing.Ignore.ShouldBe(
            ["**/node_modules/**", "**/custom-build/**", .. defaults.Where(d => d != "**/node_modules/**")]);
    }

    [Fact]
    public void Resolve_LegacyGitAwareKey_NotWrittenBackAndExplicitProviderKept()
    {
        var root = NewDir();
        CreateLegacyLayoutWithConfig(root, """
            {
              "chunking": { "maxTokensPerChunk": 512, "fallbackWindowLines": 60, "fallbackOverlapLines": 15 },
              "indexing": { "ignore": [], "maxFileSizeKb": 512, "gitAware": true },
              "qdrant": { "endpoint": "http://localhost:6334" },
              "embeddings": { "provider": "onnx" }
            }
            """);

        var result = WorkspaceStore.Resolve(root);

        var raw = File.ReadAllText(Path.Combine(result.StoreDir!, SqueezrPaths.ConfigFileName));
        raw.ShouldNotContain("gitAware");
        ConfigFile.Load(root).Embeddings.Provider.ShouldBe("onnx");
    }

    [Fact]
    public void Resolve_ReMigration_DoesNotDuplicateDefaults()
    {
        var root = NewDir();
        CreateLegacyLayoutWithConfig(root, LegacyConfig(ignore: """["**/node_modules/**"]"""));

        var first = WorkspaceStore.Resolve(root);
        var afterFirst = ConfigFile.Load(root).Indexing.Ignore;

        File.WriteAllText(Path.Combine(root, SqueezrPaths.ManifestFileName), """{"name":"legacy","repos":[]}""");
        var second = WorkspaceStore.Resolve(root);

        second.StoreKey.ShouldBe(first.StoreKey);
        ConfigFile.Load(root).Indexing.Ignore.ShouldBe(afterFirst);
    }

    private static string LegacyConfig(string ignore) =>
        """
        {
          "chunking": { "maxTokensPerChunk": 512, "fallbackWindowLines": 60, "fallbackOverlapLines": 15 },
          "indexing": { "ignore": ##IGNORE##, "maxFileSizeKb": 512 },
          "qdrant": { "endpoint": "http://localhost:6334" }
        }
        """.Replace("##IGNORE##", ignore);

    private static void CreateLegacyLayoutWithConfig(string root, string configJson)
    {
        File.WriteAllText(Path.Combine(root, SqueezrPaths.ManifestFileName), """{"name":"legacy","repos":[]}""");
        var storeDir = Path.Combine(root, SqueezrPaths.StoreDirName);
        Directory.CreateDirectory(storeDir);
        File.WriteAllText(Path.Combine(storeDir, SqueezrPaths.ConfigFileName), configJson);
    }

    private static bool TryCreateSymbolicLink(string linkPath, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, target);
            return Directory.Exists(linkPath);
        }
        catch (Exception)
        {
            return false;
        }
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

    private static void CreateLegacyLayout(string root, bool withComposeMarker, string? composeContent = null)
    {
        File.WriteAllText(Path.Combine(root, SqueezrPaths.ManifestFileName), """{"name":"legacy","repos":[]}""");
        var storeDir = Path.Combine(root, SqueezrPaths.StoreDirName);
        Directory.CreateDirectory(storeDir);
        File.WriteAllText(Path.Combine(storeDir, SqueezrPaths.ConfigFileName), "{}");
        File.WriteAllText(Path.Combine(storeDir, SqueezrPaths.MetaDbFileName), string.Empty);
        File.WriteAllText(Path.Combine(storeDir, SqueezrPaths.TrigramIndexFileName), string.Empty);
        File.WriteAllText(Path.Combine(storeDir, SqueezrPaths.LockFileName), string.Empty);
        File.WriteAllText(Path.Combine(storeDir, SqueezrPaths.StateFileName), """{"version":1,"repos":{}}""");
        var compose = composeContent ?? (withComposeMarker ? "volumes:\n  squeezr-qdrant:\n" : string.Empty);
        File.WriteAllText(Path.Combine(root, SqueezrPaths.ComposeFileName), compose);
    }

    private string NewDir(string? path = null)
    {
        var dir = Path.GetFullPath(path ?? TestDirs.NewTempDir());
        Directory.CreateDirectory(dir);
        _createdDirs.Add(dir);
        return dir;
    }
}
