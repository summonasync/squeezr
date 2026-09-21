using System.Text.RegularExpressions;
using Shouldly;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Storage;

[Collection("SqueezrHomeScope")]
public sealed class WorkspaceStoreResolveTests : IDisposable
{
    private const string HomeEnvironmentVariable = "SQUEEZR_HOME";
    private readonly string? _previousEnv;
    private readonly string _home;
    private readonly List<string> _createdDirs = [];

    public WorkspaceStoreResolveTests()
    {
        _previousEnv = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
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
