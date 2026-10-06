using System.Text.Json;
using Shouldly;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Storage;

// Registry rows are disk input. Until a row's store key is proven to name one directory under
// workspaces/, nothing may be resolved, migrated or deleted through it.
[Collection("CliProcessGlobals")]
public sealed class StoreRowContainmentTests : IDisposable
{
    private const string HomeEnvironmentVariable = "SQUEEZR_HOME";
    private readonly string? _previousEnv;
    private readonly string _scratch;
    private readonly string _home;
    private readonly string _workspaces;

    public StoreRowContainmentTests()
    {
        _previousEnv = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        _scratch = TestDirs.NewTempDir();
        _home = Path.Combine(_scratch, "home");
        _workspaces = Path.Combine(_home, SqueezrPaths.WorkspacesDirName);
        Directory.CreateDirectory(_workspaces);
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _home);
        SqueezrHome.Reset();
        File.WriteAllText(Path.Combine(_home, "canary.txt"), "keep me");
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../outside")]
    [InlineData("workspaces/../../outside")]
    [InlineData("a/b")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("a:b")]
    public void TryGetStoreDir_RowWithKeyThatLeavesWorkspaces_ResolvesNoStoreDir(string storeKey)
    {
        var root = NewProject("plain");
        WriteRegistry(Row("evil", root, storeKey));

        WorkspaceRegistry.TryGetStoreDir(root, out var storeDir).ShouldBeFalse();
        storeDir.ShouldBeEmpty();
        SqueezrPaths.StoreDir(root).ShouldBe(Path.Combine(root, SqueezrPaths.StoreDirName));
        SqueezrPaths.ManifestPath(root).ShouldBe(Path.Combine(root, SqueezrPaths.ManifestFileName));
        SqueezrPaths.MetaDbPath(root).ShouldBe(Path.Combine(root, SqueezrPaths.StoreDirName, SqueezrPaths.MetaDbFileName));
        WorkspaceRegistry.Load().ShouldHaveSingleItem().StoreKey.ShouldBe(storeKey);
        StoreUntouched();
    }

    [Theory]
    [InlineData("проект-проект")]
    [InlineData("café-work")]
    [InlineData("דגם-1a2b3c4d")]
    public void TryGetStoreDir_RowWithNonAsciiSingleSegmentKey_ResolvesUnderWorkspaces(string storeKey)
    {
        var root = NewProject("unicode");
        WriteRegistry(Row("unicode", root, storeKey));

        WorkspaceRegistry.TryGetStoreDir(root, out var storeDir).ShouldBeTrue();
        storeDir.ShouldBe(Path.Combine(_workspaces, storeKey));
    }

    [Fact]
    public void Resolve_RowWithKeyThatLeavesWorkspaces_ReportsWorkspaceWithoutStoreDir()
    {
        const string storeKey = "..";
        var root = NewProject("resolve");
        WriteRegistry(Row("evil", root, storeKey));

        var resolution = WorkspaceStore.Resolve(root);

        resolution.IsWorkspace.ShouldBeTrue();
        resolution.StoreKey.ShouldBe(storeKey);
        resolution.StoreDir.ShouldBeNull();
        resolution.MigratedFromLegacy.ShouldBeFalse();
        WorkspaceRegistry.Load().ShouldHaveSingleItem().StoreKey.ShouldBe(storeKey);
        StoreUntouched();
    }

    [Fact]
    public void Resolve_RowWithKeyThatLeavesWorkspaces_LeavesLegacyContentInTheProject()
    {
        const string storeKey = "..";
        var root = NewProject("legacy");
        WriteLegacyStore(root);
        WriteRegistry(Row("evil", root, storeKey));

        var resolution = WorkspaceStore.Resolve(root);

        resolution.StoreDir.ShouldBeNull();
        resolution.MigratedFromLegacy.ShouldBeFalse();
        File.Exists(Path.Combine(root, SqueezrPaths.ManifestFileName)).ShouldBeTrue();
        File.Exists(Path.Combine(root, SqueezrPaths.StoreDirName, SqueezrPaths.MetaDbFileName)).ShouldBeTrue();
        File.Exists(Path.Combine(_home, SqueezrPaths.StoreManifestFileName)).ShouldBeFalse();
        File.Exists(Path.Combine(_home, SqueezrPaths.MetaDbFileName)).ShouldBeFalse();
        StoreUntouched();
    }

    [Fact]
    public void Drop_RowWithKeyThatLeavesWorkspaces_KeepsTheCentralStore()
    {
        const string storeKey = "..";
        var root = NewProject("drop");
        WriteLegacyStore(root);
        WriteRegistry(Row("evil", root, storeKey));
        File.WriteAllText(Path.Combine(_home, SqueezrPaths.MetaDbFileName), "home payload");

        var result = new WorkspaceManager(root).Drop();

        result.Removed.ShouldNotContain(_home);
        Directory.Exists(_home).ShouldBeTrue();
        File.ReadAllText(Path.Combine(_home, SqueezrPaths.MetaDbFileName)).ShouldBe("home payload");
        StoreUntouched();
    }

    [Fact]
    public void Drop_RowWithKeyThatLeavesWorkspaces_WhenHomeIsTheFallbackStore_ThrowsAndKeepsHome()
    {
        const string storeKey = "..";
        var root = NewProject("overlap");
        WriteLegacyStore(root);
        var home = Path.Combine(root, SqueezrPaths.StoreDirName);
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, home);
        SqueezrHome.Reset();
        WriteRegistry(Row("evil", root, storeKey));
        File.WriteAllText(Path.Combine(home, "canary.txt"), "keep me");

        Should.Throw<WorkspaceException>(() => new WorkspaceManager(root).Drop())
            .Message.ShouldContain("central squeezr store");

        Directory.Exists(home).ShouldBeTrue();
        File.ReadAllText(Path.Combine(home, "canary.txt")).ShouldBe("keep me");
        File.ReadAllText(Path.Combine(home, SqueezrPaths.MetaDbFileName)).ShouldBe("payload");
        File.Exists(Path.Combine(root, SqueezrPaths.ManifestFileName)).ShouldBeTrue();
        WorkspaceRegistry.Load().ShouldHaveSingleItem().StoreKey.ShouldBe(storeKey);
    }

    [Fact]
    public void Drop_RowWithLegalSingleSegmentKey_RemovesStoreUnderWorkspacesAndKeepsHome()
    {
        var root = NewProject("legal-drop");
        Directory.CreateDirectory(root);
        new WorkspaceManager(root).Init(null);
        var storeDir = SqueezrPaths.StoreDir(root);
        Directory.Exists(storeDir).ShouldBeTrue();

        var result = new WorkspaceManager(root).Drop();

        result.Removed.ShouldHaveSingleItem().ShouldBe(storeDir);
        Directory.Exists(storeDir).ShouldBeFalse();
        Directory.Exists(_home).ShouldBeTrue();
        StoreUntouched();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _previousEnv);
        SqueezrHome.Reset();
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    private void StoreUntouched()
    {
        Directory.Exists(_home).ShouldBeTrue();
        File.ReadAllText(Path.Combine(_home, "canary.txt")).ShouldBe("keep me");
        Directory.GetFileSystemEntries(_workspaces).ShouldBeEmpty();
    }

    private string NewProject(string leaf)
    {
        var dir = Path.Combine(_scratch, $"project-{leaf}");
        Directory.CreateDirectory(dir);
        return WorkspaceCanonicalPath.Canonicalize(dir);
    }

    private static void WriteLegacyStore(string root)
    {
        File.WriteAllText(
            Path.Combine(root, SqueezrPaths.ManifestFileName),
            JsonSerializer.Serialize(new WorkspaceManifest { Name = "legacy", Repos = [] }, SqueezrJson.FileOptions));
        Directory.CreateDirectory(Path.Combine(root, SqueezrPaths.StoreDirName));
        File.WriteAllText(Path.Combine(root, SqueezrPaths.StoreDirName, SqueezrPaths.MetaDbFileName), "payload");
    }

    private void WriteRegistry(params WorkspaceRegistryEntry[] entries) =>
        File.WriteAllText(
            WorkspaceRegistry.FilePath,
            JsonSerializer.Serialize(entries, SqueezrJson.FileOptions));

    private static WorkspaceRegistryEntry Row(string name, string canonicalRoot, string storeKey)
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkspaceRegistryEntry(name, canonicalRoot, storeKey, now, now);
    }
}
