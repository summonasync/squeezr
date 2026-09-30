using System.Text.Json;
using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Workspace;

[Collection("CliProcessGlobals")]
public sealed class WorkspaceManagerInitAdoptionTests : IDisposable
{
    private const string HomeEnvironmentVariable = "SQUEEZR_HOME";
    private readonly string? _previousEnv;
    private readonly string _home;
    private readonly List<string> _createdDirs = [];

    public WorkspaceManagerInitAdoptionTests()
    {
        _previousEnv = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        _home = TestDirs.NewTempDir();
        _createdDirs.Add(_home);
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _home);
        SqueezrHome.Reset();
        Directory.CreateDirectory(_home);
        File.WriteAllText(WorkspaceRegistry.FilePath, "[]");
    }

    [Fact]
    public void Init_OverOrphanStoreDir_PreservesConfigManifestAndMetaDb()
    {
        var root = NewTempDir();
        const string name = "adoptme";
        var canonicalRoot = WorkspaceCanonicalPath.Canonicalize(root);
        var storeKey = WorkspaceStoreKeys.Create(name, canonicalRoot);
        var storeDir = Path.Combine(SqueezrHome.WorkspacesDir, storeKey);
        Directory.CreateDirectory(storeDir);

        var customConfig = SqueezrConfig.CreateDefault() with
        {
            Embeddings = SqueezrConfig.CreateDefault().Embeddings with { Provider = "ollama" }
        };
        var configJson = JsonSerializer.Serialize(customConfig, SqueezrJson.FileOptions);
        var configPath = Path.Combine(storeDir, SqueezrPaths.ConfigFileName);
        File.WriteAllText(configPath, configJson);

        var manifest = new WorkspaceManifest
        {
            Name = name,
            RootPath = null,
            Repos = [new RepoEntry("demo", "code")]
        };
        File.WriteAllText(
            Path.Combine(storeDir, SqueezrPaths.StoreManifestFileName),
            JsonSerializer.Serialize(manifest, SqueezrJson.FileOptions));

        var metaDbPath = Path.Combine(storeDir, SqueezrPaths.MetaDbFileName);
        using (new MetaStore(metaDbPath, createIfMissing: true))
        {
        }
        var metaDbBytes = File.ReadAllBytes(metaDbPath);

        var result = new WorkspaceManager(root).Init(name);

        result.WorkspaceName.ShouldBe(name);
        result.RepairHint.ShouldBeNull();

        File.ReadAllText(configPath).ShouldBe(configJson);
        ConfigFile.Load(root).Embeddings.Provider.ShouldBe("ollama");

        var adopted = WorkspaceManifestFile.Load(root);
        adopted.Name.ShouldBe(name);
        adopted.RootPath.ShouldBe(canonicalRoot);
        adopted.Repos.ShouldHaveSingleItem().Name.ShouldBe("demo");

        File.ReadAllBytes(metaDbPath).ShouldBe(metaDbBytes);

        var entry = WorkspaceRegistry.FindByRootPath(root);
        entry.ShouldNotBeNull();
        entry!.StoreKey.ShouldBe(storeKey);

        Directory.Exists(Path.Combine(root, SqueezrPaths.StoreDirName)).ShouldBeFalse();
        File.Exists(Path.Combine(root, SqueezrPaths.ManifestFileName)).ShouldBeFalse();
    }

    [Fact]
    public void Init_OverOrphanStoreDir_WithRootPathAlreadySet_DoesNotRewriteManifest()
    {
        var root = NewTempDir();
        const string name = "adoptme";
        var canonicalRoot = WorkspaceCanonicalPath.Canonicalize(root);
        var storeDir = CreateStoreDir(WorkspaceStoreKeys.Create(name, canonicalRoot));
        var manifestPath = Path.Combine(storeDir, SqueezrPaths.StoreManifestFileName);
        var manifestJson = JsonSerializer.Serialize(
            new WorkspaceManifest { Name = name, RootPath = canonicalRoot },
            SqueezrJson.FileOptions);
        File.WriteAllText(manifestPath, manifestJson);

        var result = new WorkspaceManager(root).Init(name);

        result.RepairHint.ShouldBeNull();
        File.ReadAllText(manifestPath).ShouldBe(manifestJson);
        WorkspaceRegistry.FindByRootPath(root).ShouldNotBeNull();
    }

    [Fact]
    public void Init_WithOrphanNeighborSameRoot_ReturnsRepairHint()
    {
        var root = NewTempDir();
        var canonicalRoot = WorkspaceCanonicalPath.Canonicalize(root);
        var adoptedKey = WorkspaceStoreKeys.Create("alpha", canonicalRoot);
        var neighborKey = WorkspaceStoreKeys.Create("beta", canonicalRoot);
        WriteManifest(CreateStoreDir(adoptedKey), "alpha", canonicalRoot);
        WriteManifest(CreateStoreDir(neighborKey), "beta", canonicalRoot);

        var result = new WorkspaceManager(root).Init("alpha");

        result.RepairHint.ShouldNotBeNull();
        result.RepairHint.ShouldContain(neighborKey);
        result.RepairHint.ShouldContain("squeezr workspace repair");
        WorkspaceRegistry.FindByRootPath(root).ShouldNotBeNull();
    }

    [Fact]
    public void Init_OverOrphanStoreDir_WithCorruptManifest_RollsBackAndKeepsFiles()
    {
        var root = NewTempDir();
        const string name = "adoptme";
        var storeDir = CreateStoreDir(WorkspaceStoreKeys.Create(name, WorkspaceCanonicalPath.Canonicalize(root)));
        var configPath = Path.Combine(storeDir, SqueezrPaths.ConfigFileName);
        var configJson = JsonSerializer.Serialize(
            SqueezrConfig.CreateDefault() with
            {
                Embeddings = SqueezrConfig.CreateDefault().Embeddings with { Provider = "ollama" }
            },
            SqueezrJson.FileOptions);
        File.WriteAllText(configPath, configJson);
        var manifestPath = Path.Combine(storeDir, SqueezrPaths.StoreManifestFileName);
        File.WriteAllText(manifestPath, "not json {{{");

        Should.Throw<WorkspaceException>(() => new WorkspaceManager(root).Init(name));

        WorkspaceRegistry.FindByRootPath(root).ShouldBeNull();
        File.ReadAllText(configPath).ShouldBe(configJson);
        File.ReadAllText(manifestPath).ShouldBe("not json {{{");
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

    private static void WriteManifest(string storeDir, string name, string rootPath) =>
        File.WriteAllText(
            Path.Combine(storeDir, SqueezrPaths.StoreManifestFileName),
            JsonSerializer.Serialize(
                new WorkspaceManifest { Name = name, RootPath = rootPath },
                SqueezrJson.FileOptions));
}
