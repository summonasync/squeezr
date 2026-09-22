using System.Text.Json;
using Shouldly;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Storage;

[Collection("SqueezrHomeScope")]
public sealed class WorkspaceRegistryBackupTests : IDisposable
{
    private const string HomeEnvironmentVariable = "SQUEEZR_HOME";
    private readonly string? _previousEnv;
    private readonly string _home;
    private readonly List<string> _createdDirs = [];

    public WorkspaceRegistryBackupTests()
    {
        _previousEnv = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        _home = TestDirs.NewTempDir();
        _createdDirs.Add(_home);
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _home);
        SqueezrHome.Reset();
    }

    [Fact]
    public void Add_RotatesPreviousGenerationIntoBackup()
    {
        var root = NewTempDir();
        WorkspaceRegistry.Add(new WorkspaceRegistryEntry("first", root, "first-aaaa1111", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        WorkspaceRegistry.Add(new WorkspaceRegistryEntry("second", root, "second-bbbb2222", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        File.Exists(WorkspaceRegistry.BackupFilePath).ShouldBeTrue();
        var backup = ReadEntries(WorkspaceRegistry.BackupFilePath);
        backup.ShouldHaveSingleItem().Name.ShouldBe("first");

        var primary = ReadEntries(WorkspaceRegistry.FilePath);
        primary.ShouldHaveSingleItem().Name.ShouldBe("second");
    }

    [Fact]
    public void Load_PrimaryMissing_RestoresFromBackupAndRewritesPrimary()
    {
        WorkspaceRegistry.Add(NewEntry("first"));
        WorkspaceRegistry.Add(NewEntry("second"));
        File.Delete(WorkspaceRegistry.FilePath);

        var entries = WorkspaceRegistry.Load();

        entries.ShouldHaveSingleItem().Name.ShouldBe("first");
        File.Exists(WorkspaceRegistry.FilePath).ShouldBeTrue();
        ReadEntries(WorkspaceRegistry.FilePath).ShouldHaveSingleItem().Name.ShouldBe("first");
    }

    [Fact]
    public void Load_CorruptPrimaryWithBackup_UsesBackupAndRewritesPrimary()
    {
        WorkspaceRegistry.Add(NewEntry("first"));
        WorkspaceRegistry.Add(NewEntry("second"));
        File.WriteAllText(WorkspaceRegistry.FilePath, "not json {{{");

        var entries = WorkspaceRegistry.Load();

        entries.ShouldHaveSingleItem().Name.ShouldBe("first");
        ReadEntries(WorkspaceRegistry.FilePath).ShouldHaveSingleItem().Name.ShouldBe("first");
        ReadEntries(WorkspaceRegistry.BackupFilePath).ShouldHaveSingleItem().Name.ShouldBe("first");
    }

    [Fact]
    public void Load_PrimaryAndBackupMissing_RebuildsFromStoreManifests()
    {
        var root = NewTempDir();
        new WorkspaceManager(root).Init("rebuild-me");
        var storeKey = WorkspaceStore.Resolve(root).StoreKey!;
        File.Delete(WorkspaceRegistry.FilePath);
        File.Delete(WorkspaceRegistry.BackupFilePath);

        var entries = WorkspaceRegistry.Load();

        var entry = entries.ShouldHaveSingleItem();
        entry.Name.ShouldBe("rebuild-me");
        entry.RootPath.ShouldBe(WorkspaceCanonicalPath.Canonicalize(root));
        entry.StoreKey.ShouldBe(storeKey);
        File.Exists(WorkspaceRegistry.FilePath).ShouldBeTrue();
        WorkspaceRegistry.FindByRootPath(root).ShouldNotBeNull();
    }

    [Fact]
    public void Load_RebuildSkipsManifestWithMismatchedStoreKey()
    {
        PlantStoreDir("foreign-aaaa1111", """{"name":"foreign","repos":[]}""");
        File.Delete(WorkspaceRegistry.FilePath);

        WorkspaceRegistry.Load().ShouldBeEmpty();
        File.Exists(WorkspaceRegistry.FilePath).ShouldBeFalse();
    }

    [Fact]
    public void Load_RebuildUsesRecordedRootPathOnlyWhenStoreKeyMatches()
    {
        var root = NewTempDir();
        new WorkspaceManager(root).Init("roundtrip");
        var foreignRoot = NewTempDir();
        var manifestPath = Path.Combine(
            _home,
            SqueezrPaths.WorkspacesDirName,
            WorkspaceStore.Resolve(root).StoreKey!,
            SqueezrPaths.StoreManifestFileName);
        File.WriteAllText(
            manifestPath,
            $$"""{"name":"roundtrip","rootPath":"{{foreignRoot}}","repos":[]}""");
        File.Delete(WorkspaceRegistry.FilePath);
        File.Delete(WorkspaceRegistry.BackupFilePath);

        WorkspaceRegistry.Load().ShouldBeEmpty();
    }

    [Fact]
    public void Load_RebuildSkipsCorruptStoreManifestAndKeepsValidEntries()
    {
        var root = NewTempDir();
        new WorkspaceManager(root).Init("valid");
        var validStoreKey = WorkspaceStore.Resolve(root).StoreKey!;
        PlantStoreDir("broken-bbbb2222", "not json {{{");
        File.Delete(WorkspaceRegistry.FilePath);
        File.Delete(WorkspaceRegistry.BackupFilePath);

        var entries = WorkspaceRegistry.Load();

        var entry = entries.ShouldHaveSingleItem();
        entry.StoreKey.ShouldBe(validStoreKey);
    }

    [Fact]
    public void Load_WhenWorkspacesDirUnreadable_ReturnsEmptyWithoutThrowing()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var workspacesDir = Path.Combine(_home, SqueezrPaths.WorkspacesDirName);
        Directory.CreateDirectory(workspacesDir);
        File.SetUnixFileMode(workspacesDir, UnixFileMode.None);
        try
        {
            WorkspaceRegistry.Load().ShouldBeEmpty();
        }
        finally
        {
            File.SetUnixFileMode(workspacesDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void Add_LeavesNoTempFilesInStoreRoot()
    {
        WorkspaceRegistry.Add(NewEntry("first"));
        WorkspaceRegistry.Add(NewEntry("second"));
        WorkspaceRegistry.RemoveByRoot("whatever");

        Directory.EnumerateFiles(_home, "*.tmp").ShouldBeEmpty();
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

    private void PlantStoreDir(string storeKey, string manifestJson)
    {
        var storeDir = Path.Combine(_home, SqueezrPaths.WorkspacesDirName, storeKey);
        Directory.CreateDirectory(storeDir);
        File.WriteAllText(Path.Combine(storeDir, SqueezrPaths.StoreManifestFileName), manifestJson);
    }

    private static IReadOnlyList<WorkspaceRegistryEntry> ReadEntries(string path) =>
        JsonSerializer.Deserialize<List<WorkspaceRegistryEntry>>(File.ReadAllText(path), SqueezrJson.FileOptions) ?? [];

    private static WorkspaceRegistryEntry NewEntry(string name) =>
        new(name, Path.Combine(Path.GetTempPath(), name), $"{name}-aaaa1111", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private string NewTempDir()
    {
        var dir = TestDirs.NewTempDir();
        _createdDirs.Add(dir);
        return dir;
    }
}
