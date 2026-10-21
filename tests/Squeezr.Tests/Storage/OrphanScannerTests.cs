using System.Text.Json;
using Shouldly;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Storage;

[Collection("CliProcessGlobals")]
public sealed class OrphanScannerTests : IDisposable
{
    private const string HomeEnvironmentVariable = "SQUEEZR_HOME";
    private readonly string? _previousEnv;
    private readonly string _home;
    private readonly List<string> _createdDirs = [];

    public OrphanScannerTests()
    {
        _previousEnv = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        _home = TestDirs.NewTempDir();
        _createdDirs.Add(_home);
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _home);
        SqueezrHome.Reset();
        File.WriteAllText(WorkspaceRegistry.FilePath, "[]");
    }

    [Fact]
    public void Scan_MixedLayout_ClassifiesOrphansAndSkipsGarbage()
    {
        var adoptableRoot = NewTempDir();
        var legacyRoot = NewTempDir();
        var movedRoot = NewTempDir();
        var registeredRoot = NewTempDir();

        var adoptableKey = WorkspaceStoreKeys.Create("adoptable", WorkspaceCanonicalPath.Canonicalize(adoptableRoot));
        WriteManifest(CreateStoreDir(adoptableKey), "adoptable", adoptableRoot);

        var legacyKey = WorkspaceStoreKeys.Create("legacy", WorkspaceCanonicalPath.Canonicalize(legacyRoot));
        WriteManifest(CreateStoreDir(legacyKey), "legacy", null);

        var keyMismatchDir = CreateStoreDir(WorkspaceStoreKeys.Create("moved", WorkspaceCanonicalPath.Canonicalize(movedRoot)));
        WriteManifest(keyMismatchDir, "moved", NewTempDir());

        CreateStoreDir(WorkspaceStoreKeys.Create("empty", WorkspaceCanonicalPath.Canonicalize(NewTempDir())));
        var corruptDir = CreateStoreDir(WorkspaceStoreKeys.Create("corrupt", WorkspaceCanonicalPath.Canonicalize(NewTempDir())));
        File.WriteAllText(Path.Combine(corruptDir, SqueezrPaths.StoreManifestFileName), "not json {{{");

        var registeredKey = WorkspaceStoreKeys.Create("registered", WorkspaceCanonicalPath.Canonicalize(registeredRoot));
        WriteManifest(CreateStoreDir(registeredKey), "registered", registeredRoot);
        WorkspaceRegistry.Add(new WorkspaceRegistryEntry(
            "registered",
            WorkspaceCanonicalPath.Canonicalize(registeredRoot),
            registeredKey,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow));

        var result = OrphanScanner.Scan();

        result.Orphans.Count.ShouldBe(2);
        var adoptable = result.Orphans.Single(o => o.IsAdoptable);
        adoptable.StoreKey.ShouldBe(adoptableKey);
        adoptable.Name.ShouldBe("adoptable");
        adoptable.RootPath.ShouldBe(WorkspaceCanonicalPath.Canonicalize(adoptableRoot));

        var legacy = result.Orphans.Single(o => !o.IsAdoptable);
        legacy.StoreKey.ShouldBe(legacyKey);
        legacy.Name.ShouldBe("legacy");
        legacy.RootPath.ShouldBeNull();

        result.Husks.ShouldBeEmpty();
    }

    [Fact]
    public void Scan_DeadRootOrphan_ReportsTheRecordedRoot_AsNotAdoptable()
    {
        var deadRoot = NewTempDir();
        var canonical = WorkspaceCanonicalPath.Canonicalize(deadRoot);
        var storeKey = WorkspaceStoreKeys.Create("gone-root", canonical);
        WriteManifest(CreateStoreDir(storeKey), "gone-root", deadRoot);
        Directory.Delete(deadRoot, recursive: true);

        var orphan = OrphanScanner.Scan().Orphans.ShouldHaveSingleItem();

        orphan.StoreKey.ShouldBe(storeKey);
        orphan.Name.ShouldBe("gone-root");
        orphan.RootPath.ShouldBe(canonical);
        orphan.RootExists.ShouldBeFalse();
        orphan.IsAdoptable.ShouldBeFalse();
    }

    [Fact]
    public void Scan_DetectsHuskEntriesByHomeAndStoreParentRoots()
    {
        var normalRoot = NewTempDir();
        WorkspaceRegistry.Add(new WorkspaceRegistryEntry(
            "husk-home",
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "husk-home-aaaa1111",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow));
        WorkspaceRegistry.Add(new WorkspaceRegistryEntry(
            "husk-store",
            SqueezrHome.Resolve(),
            "husk-store-bbbb2222",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow));
        WorkspaceRegistry.Add(new WorkspaceRegistryEntry(
            "normal",
            WorkspaceCanonicalPath.Canonicalize(normalRoot),
            "normal-cccc3333",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow));

        var result = OrphanScanner.Scan();

        result.Orphans.ShouldBeEmpty();
        result.Husks.Count.ShouldBe(2);
        result.Husks.ShouldContain(h => h.StoreKey == "husk-home-aaaa1111");
        result.Husks.ShouldContain(h => h.StoreKey == "husk-store-bbbb2222");
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

    private string CreateStoreDir(string storeKey)
    {
        var storeDir = Path.Combine(SqueezrHome.WorkspacesDir, storeKey);
        Directory.CreateDirectory(storeDir);
        return storeDir;
    }

    private static void WriteManifest(string storeDir, string name, string? rootPath) =>
        File.WriteAllText(
            Path.Combine(storeDir, SqueezrPaths.StoreManifestFileName),
            JsonSerializer.Serialize(
                new WorkspaceManifest { Name = name, RootPath = rootPath },
                SqueezrJson.FileOptions));
}
