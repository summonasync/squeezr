using System.Text.Json;
using Shouldly;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Storage;

[Collection("ProcessGlobals")]
public sealed class WorkspaceRegistryTests : IDisposable
{
    private const string HomeEnvironmentVariable = "SQUEEZR_HOME";
    private readonly string? _previousEnv;
    private readonly string _home;
    private readonly List<string> _createdDirs = [];

    public WorkspaceRegistryTests()
    {
        _previousEnv = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        _home = TestDirs.NewTempDir();
        _createdDirs.Add(_home);
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _home);
        SqueezrHome.Reset();
    }

    [Fact]
    public void Load_WhenFileMissing_ReturnsEmptyList()
    {
        WorkspaceRegistry.Load().ShouldBeEmpty();
    }

    [Fact]
    public void Load_WhenFileCorrupt_ReturnsEmptyList()
    {
        File.WriteAllText(WorkspaceRegistry.FilePath, "not json {{{");

        WorkspaceRegistry.Load().ShouldBeEmpty();
    }

    [Fact]
    public void Add_PersistsEntryWithSpecShape()
    {
        var root = NewTempDir();
        var entry = new WorkspaceRegistryEntry("demo", root, "demo-abcdef12", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        WorkspaceRegistry.Add(entry);

        File.Exists(WorkspaceRegistry.FilePath).ShouldBeTrue();
        using var doc = JsonDocument.Parse(File.ReadAllText(WorkspaceRegistry.FilePath));
        var stored = doc.RootElement[0];
        stored.GetProperty("name").GetString().ShouldBe("demo");
        stored.GetProperty("rootPath").GetString().ShouldBe(root);
        stored.GetProperty("storeKey").GetString().ShouldBe("demo-abcdef12");
        stored.TryGetProperty("createdAt", out _).ShouldBeTrue();
        stored.TryGetProperty("lastUsedAt", out _).ShouldBeTrue();
    }

    [Fact]
    public void Add_WithSameRoot_ReplacesPreviousEntry()
    {
        var root = NewTempDir();
        WorkspaceRegistry.Add(new WorkspaceRegistryEntry("first", root, "first-aaaa1111", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        WorkspaceRegistry.Add(new WorkspaceRegistryEntry("second", root, "second-bbbb2222", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        var entries = WorkspaceRegistry.Load();
        entries.Count.ShouldBe(1);
        entries[0].Name.ShouldBe("second");
        entries[0].StoreKey.ShouldBe("second-bbbb2222");
    }

    [Fact]
    public void RemoveByRoot_RemovesMatchingEntry()
    {
        var root = NewTempDir();
        var other = NewTempDir();
        WorkspaceRegistry.Add(new WorkspaceRegistryEntry("demo", root, "demo-aaaa1111", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        WorkspaceRegistry.Add(new WorkspaceRegistryEntry("other", other, "other-bbbb2222", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        WorkspaceRegistry.RemoveByRoot(root);

        WorkspaceRegistry.Load().ShouldHaveSingleItem().RootPath.ShouldBe(other);
    }

    [Fact]
    public void FindByRootPath_ReturnsRegisteredEntry()
    {
        var root = NewTempDir();
        WorkspaceRegistry.Add(new WorkspaceRegistryEntry("demo", root, "demo-aaaa1111", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        var found = WorkspaceRegistry.FindByRootPath(root);

        found.ShouldNotBeNull();
        found!.Name.ShouldBe("demo");
        found.StoreKey.ShouldBe("demo-aaaa1111");
    }

    [Fact]
    public void FindByRootPath_WithTrailingSeparator_CanonicalizesBeforeLookup()
    {
        var root = NewTempDir();
        WorkspaceRegistry.Add(new WorkspaceRegistryEntry("demo", root, "demo-aaaa1111", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        WorkspaceRegistry.FindByRootPath(root + Path.DirectorySeparatorChar).ShouldNotBeNull();
    }

    [Fact]
    public void FindByRootPath_UnknownRoot_ReturnsNull()
    {
        WorkspaceRegistry.FindByRootPath(NewTempDir()).ShouldBeNull();
    }

    // The row a command is holding is the snapshot it acted on: re-resolving the store dir through the
    // registry a second time can answer with a different row, or with no row at all, mid-run.
    [Fact]
    public void StoreDir_RowInHand_KeepsNamingItsOwnStore_AfterTheRegistryMovesOn()
    {
        var root = NewTempDir();
        var now = DateTimeOffset.UtcNow;
        WorkspaceRegistry.Add(new WorkspaceRegistryEntry("demo", root, "demo-aaaa1111", now, now));
        var snapshot = WorkspaceRegistry.Load();

        WorkspaceRegistry.RemoveByRoot(root);

        SqueezrPaths.StoreDir(snapshot.ShouldHaveSingleItem())
            .ShouldBe(Path.Combine(_home, SqueezrPaths.WorkspacesDirName, "demo-aaaa1111"));
        SqueezrPaths.StoreDir(root).ShouldBe(Path.Combine(root, SqueezrPaths.StoreDirName));
    }

    [Fact]
    public void StoreDir_RowWithKeyThatLeavesWorkspaces_KeepsTheProjectFallback()
    {
        var root = NewTempDir();
        var entry = new WorkspaceRegistryEntry(
            "evil", root, "workspaces/../../outside", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        SqueezrPaths.StoreDir(entry).ShouldBe(Path.Combine(root, SqueezrPaths.StoreDirName));
        SqueezrPaths.MetaDbPath(entry)
            .ShouldBe(Path.Combine(root, SqueezrPaths.StoreDirName, SqueezrPaths.MetaDbFileName));
    }

    [Fact]
    public void TryGetStoreDir_RegisteredRoot_ReturnsStoreDirUnderHome()
    {
        var root = NewTempDir();
        WorkspaceRegistry.Add(new WorkspaceRegistryEntry("demo", root, "demo-aaaa1111", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        var found = WorkspaceRegistry.TryGetStoreDir(root, out var storeDir);

        found.ShouldBeTrue();
        storeDir.ShouldBe(Path.Combine(_home, SqueezrPaths.WorkspacesDirName, "demo-aaaa1111"));
    }

    [Fact]
    public void TryGetStoreDir_UnregisteredRoot_ReturnsFalse()
    {
        WorkspaceRegistry.TryGetStoreDir(NewTempDir(), out _).ShouldBeFalse();
    }

    [Fact]
    public void Add_ConcurrentFromMultipleThreads_KeepsAllEntries()
    {
        const int writers = 8;
        var roots = Enumerable.Range(0, writers).Select(_ => NewTempDir()).ToArray();

        Parallel.For(
            0,
            writers,
            i => WorkspaceRegistry.Add(
                new WorkspaceRegistryEntry($"ws-{i}", roots[i], $"key-{i}", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));

        var entries = WorkspaceRegistry.Load();
        entries.Count.ShouldBe(writers);
        foreach (var root in roots)
        {
            WorkspaceRegistry.FindByRootPath(root).ShouldNotBeNull();
        }
    }

    [Fact]
    public void Add_WhileWriteLockHeldByAnotherProcess_TimesOutAndThrows()
    {
        var root = NewTempDir();
        Directory.CreateDirectory(_home);
        var lockPath = Path.Combine(_home, SqueezrPaths.RegistryLockFileName);
        using (new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
        {
            Should.Throw<IOException>(() =>
                WorkspaceRegistry.Add(
                    new WorkspaceRegistryEntry("demo", root, "demo-aaaa1111", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));
        }

        WorkspaceRegistry.Add(
            new WorkspaceRegistryEntry("demo", root, "demo-aaaa1111", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        WorkspaceRegistry.FindByRootPath(root).ShouldNotBeNull();
    }

    [Fact]
    public void Load_WhenFileLocked_ThrowsAfterRetriesInsteadOfReturningEmpty()
    {
        Directory.CreateDirectory(_home);
        using (new FileStream(WorkspaceRegistry.FilePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
        {
            Should.Throw<IOException>(() => WorkspaceRegistry.Load());
        }
    }

    [Fact]
    public void FindByRootPath_AfterHomeSwitch_ReReadsFromNewHome()
    {
        var root = NewTempDir();
        WorkspaceRegistry.Add(new WorkspaceRegistryEntry("demo", root, "demo-aaaa1111", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        WorkspaceRegistry.FindByRootPath(root).ShouldNotBeNull();

        var otherHome = NewTempDir();
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, otherHome);
        SqueezrHome.Reset();
        try
        {
            WorkspaceRegistry.FindByRootPath(root).ShouldBeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _home);
            SqueezrHome.Reset();
        }

        WorkspaceRegistry.FindByRootPath(root).ShouldNotBeNull();
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
