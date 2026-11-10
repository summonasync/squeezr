using System.Text.Json;
using System.Text.Json.Nodes;
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
    public void FindContainingPath_InsideRegisteredChild_ReturnsChildNotParent()
    {
        var parent = RegisteredRoot(NewTempDir());
        var child = RegisteredRoot(Directory.CreateDirectory(Path.Combine(parent, ".lanes", "lane-x")).FullName);
        WorkspaceRegistry.Add(NewEntry("parent", parent, "parent-aaaa1111"));
        WorkspaceRegistry.Add(NewEntry("lane", child, "lane-bbbb2222"));

        var inChild = WorkspaceRegistry.FindContainingPath(Path.Combine(child, "src", "a.cs"));
        inChild.ShouldNotBeNull();
        inChild!.StoreKey.ShouldBe("lane-bbbb2222");

        var inParent = WorkspaceRegistry.FindContainingPath(Path.Combine(parent, "src", "a.cs"));
        inParent.ShouldNotBeNull();
        inParent!.StoreKey.ShouldBe("parent-aaaa1111");
    }

    [Fact]
    public void FindContainingPath_ChildRowLoadedFirst_SameVerdictsAsParentRowFirst()
    {
        var parent = RegisteredRoot(NewTempDir());
        var child = RegisteredRoot(Directory.CreateDirectory(Path.Combine(parent, ".lanes", "lane-x")).FullName);
        WorkspaceRegistry.Add(NewEntry("lane", child, "lane-bbbb2222"));
        WorkspaceRegistry.Add(NewEntry("parent", parent, "parent-aaaa1111"));

        WorkspaceRegistry.FindContainingPath(Path.Combine(child, "src", "a.cs"))!.StoreKey.ShouldBe("lane-bbbb2222");
        WorkspaceRegistry.FindContainingPath(Path.Combine(parent, "src", "a.cs"))!.StoreKey.ShouldBe("parent-aaaa1111");
    }

    [Fact]
    public void FindContainingPath_RegisteredChildWithDeeperGrandchild_ReturnsDeepest()
    {
        var parent = RegisteredRoot(NewTempDir());
        var child = RegisteredRoot(Directory.CreateDirectory(Path.Combine(parent, ".lanes", "lane-x")).FullName);
        var grandchild = RegisteredRoot(Directory.CreateDirectory(Path.Combine(child, "src", "pkg")).FullName);
        WorkspaceRegistry.Add(NewEntry("parent", parent, "parent-aaaa1111"));
        WorkspaceRegistry.Add(NewEntry("pkg", grandchild, "pkg-cccc3333"));
        WorkspaceRegistry.Add(NewEntry("lane", child, "lane-bbbb2222"));

        WorkspaceRegistry.FindContainingPath(Path.Combine(grandchild, "a.cs"))!.StoreKey.ShouldBe("pkg-cccc3333");
        WorkspaceRegistry.FindContainingPath(Path.Combine(child, "other.cs"))!.StoreKey.ShouldBe("lane-bbbb2222");
    }

    [Fact]
    public void Load_LegacyFiveFieldRow_NewMembersDefaultAndResolutionUnchanged()
    {
        var root = RegisteredRoot(NewTempDir());
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(
            WorkspaceRegistry.FilePath,
            new JsonArray(
                new JsonObject
                {
                    ["name"] = "legacy",
                    ["rootPath"] = root,
                    ["storeKey"] = "legacy-dddd4444",
                    ["createdAt"] = "2026-09-01T10:00:00+00:00",
                    ["lastUsedAt"] = "2026-09-02T11:00:00+00:00",
                }).ToJsonString());

        var stored = WorkspaceRegistry.Load().ShouldHaveSingleItem();
        stored.Name.ShouldBe("legacy");
        stored.RootPath.ShouldBe(root);
        stored.StoreKey.ShouldBe("legacy-dddd4444");
        stored.ParentRootPath.ShouldBeNull();
        stored.Kind.ShouldBeNull();
        stored.Branch.ShouldBeNull();
        WorkspaceRegistry.FindByRootPath(root).ShouldNotBeNull();
        WorkspaceRegistry.FindContainingPath(Path.Combine(root, "src", "a.cs"))!.StoreKey.ShouldBe("legacy-dddd4444");
    }

    [Fact]
    public void Add_WithWorktreeFields_RoundTripsThroughDisk()
    {
        var parent = RegisteredRoot(NewTempDir());
        var child = RegisteredRoot(Directory.CreateDirectory(Path.Combine(parent, ".lanes", "lane-x")).FullName);
        WorkspaceRegistry.Add(new WorkspaceRegistryEntry(
            "lane",
            child,
            "lane-eeee5555",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            parent,
            WorkspaceKind.Worktree,
            "feat/116-worktree-aware-workspaces"));

        using (var doc = JsonDocument.Parse(File.ReadAllText(WorkspaceRegistry.FilePath)))
        {
            var stored = doc.RootElement[0];
            stored.GetProperty("parentRootPath").GetString().ShouldBe(parent);
            stored.GetProperty("kind").GetString().ShouldBe("Worktree");
            stored.GetProperty("branch").GetString().ShouldBe("feat/116-worktree-aware-workspaces");
        }

        var reloaded = WorkspaceRegistry.Load().ShouldHaveSingleItem();
        reloaded.ParentRootPath.ShouldBe(parent);
        reloaded.Kind.ShouldBe(WorkspaceKind.Worktree);
        reloaded.Branch.ShouldBe("feat/116-worktree-aware-workspaces");
        WorkspaceRegistry.FindContainingPath(Path.Combine(child, "src", "a.cs"))!.StoreKey.ShouldBe("lane-eeee5555");
    }

    [Fact]
    public void Add_WithoutWorktreeFields_WritesTheFiveFieldShapeAnOldBuildReads()
    {
        var root = RegisteredRoot(NewTempDir());

        WorkspaceRegistry.Add(NewEntry("demo", root, "demo-ffff6666"));

        using var doc = JsonDocument.Parse(File.ReadAllText(WorkspaceRegistry.FilePath));
        var stored = doc.RootElement[0];
        stored.EnumerateObject().Count().ShouldBe(5);
        stored.TryGetProperty("parentRootPath", out _).ShouldBeFalse();
        stored.TryGetProperty("kind", out _).ShouldBeFalse();
        stored.TryGetProperty("branch", out _).ShouldBeFalse();
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

    // Roots go into the registry the way the product writes them: canonical, so the containment
    // comparison sees the same spelling FindContainingPath resolves a queried path to.
    private string RegisteredRoot(string dir)
    {
        var canonical = WorkspaceCanonicalPath.Canonicalize(dir);
        Directory.CreateDirectory(canonical);
        return canonical;
    }

    private static WorkspaceRegistryEntry NewEntry(string name, string root, string storeKey) =>
        new(name, root, storeKey, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}
