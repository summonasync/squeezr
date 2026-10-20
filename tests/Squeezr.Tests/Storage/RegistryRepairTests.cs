using System.Runtime.Versioning;
using System.Text.Json;
using Shouldly;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Storage;

[Collection("CliProcessGlobals")]
public sealed class RegistryRepairTests : IDisposable
{
    private const string HomeEnvironmentVariable = "SQUEEZR_HOME";
    private const string HuskKey = "husk-home-aaaa1111";
    private static readonly UnixFileMode HomeWritable =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
    private readonly string? _previousEnv;
    private readonly string _home;
    private readonly List<string> _createdDirs = [];

    public RegistryRepairTests()
    {
        _previousEnv = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        _home = TestDirs.NewTempDir();
        _createdDirs.Add(_home);
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _home);
        SqueezrHome.Reset();
        File.WriteAllText(WorkspaceRegistry.FilePath, "[]");
    }

    [Fact]
    public void AdoptAll_AdoptsAdoptable_ReportsLegacyAndHusk_KeepsStoreFiles()
    {
        var adoptableRoot = NewTempDir();
        var legacyRoot = NewTempDir();
        var adoptableKey = WorkspaceStoreKeys.Create("adoptable", WorkspaceCanonicalPath.Canonicalize(adoptableRoot));
        var legacyKey = WorkspaceStoreKeys.Create("legacy", WorkspaceCanonicalPath.Canonicalize(legacyRoot));
        var adoptableManifestPath = Path.Combine(CreateStoreDir(adoptableKey), SqueezrPaths.StoreManifestFileName);
        WriteManifest(adoptableManifestPath, "adoptable", adoptableRoot);
        var adoptableManifestBytes = File.ReadAllBytes(adoptableManifestPath);
        var metaDbPath = Path.Combine(SqueezrHome.WorkspacesDir, adoptableKey, SqueezrPaths.MetaDbFileName);
        using (new MetaStore(metaDbPath, createIfMissing: true))
        {
        }
        var metaDbBytes = File.ReadAllBytes(metaDbPath);

        var legacyManifestPath = Path.Combine(CreateStoreDir(legacyKey), SqueezrPaths.StoreManifestFileName);
        WriteManifest(legacyManifestPath, "legacy", null);
        var legacyManifestBytes = File.ReadAllBytes(legacyManifestPath);

        WorkspaceRegistry.Add(new WorkspaceRegistryEntry(
            "husk-home",
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            HuskKey,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow));

        var outcome = RegistryRepair.AdoptAll();

        outcome.Adopted.ShouldHaveSingleItem().StoreKey.ShouldBe(adoptableKey);
        outcome.NeedsRoot.ShouldHaveSingleItem().StoreKey.ShouldBe(legacyKey);
        outcome.Husks.ShouldHaveSingleItem().StoreKey.ShouldBe(HuskKey);

        var entry = WorkspaceRegistry.FindByStoreKey(adoptableKey);
        entry.ShouldNotBeNull();
        entry!.RootPath.ShouldBe(WorkspaceCanonicalPath.Canonicalize(adoptableRoot));
        entry.Name.ShouldBe("adoptable");

        File.ReadAllBytes(adoptableManifestPath).ShouldBe(adoptableManifestBytes);
        File.ReadAllBytes(metaDbPath).ShouldBe(metaDbBytes);
        File.ReadAllBytes(legacyManifestPath).ShouldBe(legacyManifestBytes);
        WorkspaceRegistry.FindByStoreKey(HuskKey).ShouldNotBeNull();

        var second = RegistryRepair.AdoptAll();

        second.Adopted.ShouldBeEmpty();
        WorkspaceRegistry.Load().Count.ShouldBe(2);
        File.ReadAllBytes(adoptableManifestPath).ShouldBe(adoptableManifestBytes);
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void AdoptAll_UnwritableHome_ReportsEveryAdoptableOrphanAsFailedAndAdoptsNothing()
    {
        var root = NewTempDir();
        var storeKey = WorkspaceStoreKeys.Create("blocked", WorkspaceCanonicalPath.Canonicalize(root));
        var storeDir = CreateStoreDir(storeKey);
        WriteManifest(Path.Combine(storeDir, SqueezrPaths.StoreManifestFileName), "blocked", root);
        File.SetUnixFileMode(_home, HomeWritable & ~UnixFileMode.UserWrite);
        try
        {
            var outcome = RegistryRepair.AdoptAll();

            outcome.Adopted.ShouldBeEmpty();
            outcome.NeedsRoot.ShouldBeEmpty();
            var failure = outcome.Failed.ShouldHaveSingleItem();
            failure.Orphan.StoreKey.ShouldBe(storeKey);
            failure.Orphan.Name.ShouldBe("blocked");
            failure.Cause.ShouldContain("UnauthorizedAccessException");
            WorkspaceRegistry.Load().ShouldBeEmpty();
        }
        finally
        {
            File.SetUnixFileMode(_home, HomeWritable);
        }

        Directory.Exists(storeDir).ShouldBeTrue();
    }

    [Fact]
    public void AdoptWithRoot_ValidRoot_AdoptsLegacyOrphan()
    {
        var root = NewTempDir();
        var storeKey = WorkspaceStoreKeys.Create("legacy", WorkspaceCanonicalPath.Canonicalize(root));
        var manifestPath = Path.Combine(CreateStoreDir(storeKey), SqueezrPaths.StoreManifestFileName);
        WriteManifest(manifestPath, "legacy", null);

        var entry = RegistryRepair.AdoptWithRoot(storeKey, root);

        var canonicalRoot = WorkspaceCanonicalPath.Canonicalize(root);
        entry.RootPath.ShouldBe(canonicalRoot);
        entry.StoreKey.ShouldBe(storeKey);
        WorkspaceRegistry.FindByStoreKey(storeKey).ShouldNotBeNull();
        File.Exists(Path.Combine(root, SqueezrPaths.ManifestFileName)).ShouldBeFalse();
        Directory.Exists(Path.Combine(root, SqueezrPaths.StoreDirName)).ShouldBeFalse();

        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        doc.RootElement.GetProperty("rootPath").GetString().ShouldBe(canonicalRoot);
        doc.RootElement.GetProperty("name").GetString().ShouldBe("legacy");
    }

    [Fact]
    public void AdoptWithRoot_WrongRoot_ThrowsAndKeepsRegistryAndManifest()
    {
        var root = NewTempDir();
        var otherRoot = NewTempDir();
        var storeKey = WorkspaceStoreKeys.Create("legacy", WorkspaceCanonicalPath.Canonicalize(root));
        var manifestPath = Path.Combine(CreateStoreDir(storeKey), SqueezrPaths.StoreManifestFileName);
        WriteManifest(manifestPath, "legacy", null);
        var manifestBytes = File.ReadAllBytes(manifestPath);

        Should.Throw<WorkspaceException>(() => RegistryRepair.AdoptWithRoot(storeKey, otherRoot));

        WorkspaceRegistry.FindByStoreKey(storeKey).ShouldBeNull();
        File.ReadAllBytes(manifestPath).ShouldBe(manifestBytes);
    }

    [Fact]
    public void AdoptWithRoot_AlreadyHasRoot_Throws()
    {
        var root = NewTempDir();
        var storeKey = WorkspaceStoreKeys.Create("adoptable", WorkspaceCanonicalPath.Canonicalize(root));
        var manifestPath = Path.Combine(CreateStoreDir(storeKey), SqueezrPaths.StoreManifestFileName);
        WriteManifest(manifestPath, "adoptable", root);

        Should.Throw<WorkspaceException>(() => RegistryRepair.AdoptWithRoot(storeKey, root));

        WorkspaceRegistry.FindByStoreKey(storeKey).ShouldBeNull();
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

    private static void WriteManifest(string manifestPath, string name, string? rootPath) =>
        File.WriteAllText(
            manifestPath,
            JsonSerializer.Serialize(
                new WorkspaceManifest { Name = name, RootPath = rootPath },
                SqueezrJson.FileOptions));
}
