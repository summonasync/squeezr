using System.Text.Json;
using Shouldly;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Storage;

[Collection("CliProcessGlobals")]
public sealed class RegistryAdoptContainmentTests : IDisposable
{
    private const string HomeEnvironmentVariable = "SQUEEZR_HOME";
    private const string LeakedMarker = "leaked";
    private readonly string? _previousEnv;
    private readonly string _scratch;
    private readonly string _home;
    private readonly string _workspaces;
    private readonly string _outsideDir;
    private readonly byte[] _homeVictimBytes;
    private readonly byte[] _outsideVictimBytes;

    public RegistryAdoptContainmentTests()
    {
        _previousEnv = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        _scratch = TestDirs.NewTempDir();
        _home = Path.Combine(_scratch, "home");
        _workspaces = Path.Combine(_home, SqueezrPaths.WorkspacesDirName);
        _outsideDir = Path.Combine(_scratch, "outside");
        Directory.CreateDirectory(_workspaces);
        Directory.CreateDirectory(_outsideDir);
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _home);
        SqueezrHome.Reset();
        File.WriteAllText(WorkspaceRegistry.FilePath, "[]");

        // Readable stores a traversal key would reach: one level above workspaces/
        // is the home itself (key ".."), one level above the home is the scratch dir.
        WriteStoreManifest(_home, "home-victim", Canonical(Path.Combine(_scratch, LeakedMarker + "-home-root")));
        WriteStoreManifest(_outsideDir, "outside-victim", Canonical(Path.Combine(_scratch, LeakedMarker + "-outside-root")));
        _homeVictimBytes = File.ReadAllBytes(Path.Combine(_home, SqueezrPaths.StoreManifestFileName));
        _outsideVictimBytes = File.ReadAllBytes(Path.Combine(_outsideDir, SqueezrPaths.StoreManifestFileName));
        File.WriteAllText(Path.Combine(_home, "canary.txt"), "keep me");
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../outside")]
    [InlineData("workspaces/../../outside")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("abc/")]
    [InlineData("/tmp/squeezr-adopt-probe")]
    [InlineData("a:b")]
    [InlineData("C:evil")]
    [InlineData("..:stream")]
    public void AdoptWithRoot_KeyThatLeavesWorkspaces_RefusesBeforeReadingAnyStore(string storeKey)
    {
        var error = Should.Throw<WorkspaceException>(
            () => RegistryRepair.AdoptWithRoot(storeKey, Path.Combine(_scratch, "any-root")));

        error.Message.ShouldContain("not a single path segment");
        error.Code.ShouldBe(WorkspaceException.DefaultCode);
        error.Message.ShouldNotContain(LeakedMarker);
        error.Message.ShouldNotContain("already records root path");
        error.Message.ShouldNotContain("victim");
        error.Message.ShouldNotContain(_home);
        HasNothingBeenReadOrWritten();
    }

    [Theory]
    [InlineData("café")]
    [InlineData("проект")]
    [InlineData("דגם")]
    public void AdoptWithRoot_NonAsciiSingleSegmentKey_AdoptsAsBefore(string name)
    {
        var root = NewDir(name);
        var storeKey = WorkspaceStoreKeys.Create(name, Canonical(root));
        storeKey.ShouldStartWith(name);
        var manifestPath = Path.Combine(CreateStoreDir(storeKey), SqueezrPaths.StoreManifestFileName);
        WriteStoreManifestFile(manifestPath, name, null);

        var entry = RegistryRepair.AdoptWithRoot(storeKey, root);

        var canonicalRoot = Canonical(root);
        entry.Name.ShouldBe(name);
        entry.StoreKey.ShouldBe(storeKey);
        entry.RootPath.ShouldBe(canonicalRoot);
        WorkspaceRegistry.FindByStoreKey(storeKey).ShouldNotBeNull();
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        document.RootElement.GetProperty("rootPath").GetString().ShouldBe(canonicalRoot);
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

    private void HasNothingBeenReadOrWritten()
    {
        File.ReadAllBytes(Path.Combine(_home, SqueezrPaths.StoreManifestFileName)).ShouldBe(_homeVictimBytes);
        File.ReadAllBytes(Path.Combine(_outsideDir, SqueezrPaths.StoreManifestFileName)).ShouldBe(_outsideVictimBytes);
        WorkspaceRegistry.Load().ShouldBeEmpty();
        Directory.Exists(_home).ShouldBeTrue();
        File.ReadAllText(Path.Combine(_home, "canary.txt")).ShouldBe("keep me");
        File.Exists(Path.Combine(_home, SqueezrPaths.MetaDbFileName)).ShouldBeFalse();
        Directory.GetFileSystemEntries(_workspaces).ShouldBeEmpty();
    }

    private string NewDir(string leaf)
    {
        var dir = Path.Combine(_scratch, $"root-{leaf}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private string CreateStoreDir(string storeKey)
    {
        var storeDir = Path.Combine(_workspaces, storeKey);
        Directory.CreateDirectory(storeDir);
        return storeDir;
    }

    private void WriteStoreManifest(string directory, string name, string? rootPath) =>
        WriteStoreManifestFile(Path.Combine(directory, SqueezrPaths.StoreManifestFileName), name, rootPath);

    private static void WriteStoreManifestFile(string manifestPath, string name, string? rootPath) =>
        File.WriteAllText(
            manifestPath,
            JsonSerializer.Serialize(
                new WorkspaceManifest { Name = name, RootPath = rootPath },
                SqueezrJson.FileOptions));

    private static string Canonical(string path) => WorkspaceCanonicalPath.Canonicalize(path);
}
