using System.Runtime.Versioning;
using System.Text.Json;
using Shouldly;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Storage;

[Collection("ProcessGlobals")]
public sealed class WorkspaceStoreDropKeyGuardTests : IDisposable
{
    private const string HomeEnvironmentVariable = "SQUEEZR_HOME";
    private readonly string? _previousEnv;
    private readonly string _home;
    private readonly string _workspaces;
    private readonly List<string> _extraDirs = [];

    public WorkspaceStoreDropKeyGuardTests()
    {
        _previousEnv = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        _home = TestDirs.NewTempDir();
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _home);
        SqueezrHome.Reset();
        _workspaces = Path.Combine(_home, SqueezrPaths.WorkspacesDirName);
        Directory.CreateDirectory(_workspaces);
        File.WriteAllText(Path.Combine(_home, "canary.txt"), "keep me");
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("../..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("abc/")]
    [InlineData("/tmp/squeezr-traversal-victim")]
    [InlineData("workspaces/../../outside")]
    [InlineData("a:b")]
    [InlineData("C:evil")]
    [InlineData("..:stream")]
    public void DropByStoreKey_KeyThatLeavesWorkspaces_RefusesBeforeAnyFileSystemOperation(string storeKey)
    {
        WriteRegistry(Row("evil", storeKey), Row("victim", "victim-key"));
        var victimDir = WriteStoreDir("victim-key");

        var error = Should.Throw<WorkspaceException>(() => WorkspaceStore.DropByStoreKey(storeKey));

        error.Message.ShouldContain("not a single path segment");
        error.Code.ShouldBe(WorkspaceException.DefaultCode);
        error.Message.ShouldNotContain(_home);
        WorkspaceRegistry.FindByStoreKey(storeKey).ShouldNotBeNull();
        WorkspaceRegistry.FindByStoreKey("victim-key").ShouldNotBeNull();
        Directory.Exists(victimDir).ShouldBeTrue();
        Directory.Exists(_home).ShouldBeTrue();
        File.ReadAllText(Path.Combine(_home, "canary.txt")).ShouldBe("keep me");
    }

    [Theory]
    [InlineData("a..b")]
    [InlineData("...")]
    [InlineData("café-work-1a2b3c4d")]
    [InlineData("דגם-1a2b3c4d")]
    [InlineData("проект-проект-272ddca8")]
    [InlineData("~")]
    [InlineData("a-b_c.d")]
    [InlineData("workspaces")]
    public void DropByStoreKey_SingleSegmentKey_RemovesRowAndStore(string storeKey)
    {
        WriteRegistry(Row("target", storeKey));
        var storeDir = WriteStoreDir(storeKey);

        var removed = WorkspaceStore.DropByStoreKey(storeKey);

        removed.ShouldHaveSingleItem().ShouldBe(Path.Combine(_workspaces, storeKey));
        Directory.Exists(storeDir).ShouldBeFalse();
        WorkspaceRegistry.FindByStoreKey(storeKey).ShouldBeNull();
        Directory.Exists(_home).ShouldBeTrue();
        Directory.Exists(_workspaces).ShouldBeTrue();
        File.Exists(Path.Combine(_home, "canary.txt")).ShouldBeTrue();
    }

    [Fact]
    public void DropByStoreKey_KeyWithNulByte_RefusesBeforeAnyFileSystemOperation()
    {
        var storeKey = "a\0b";
        WriteRegistry(Row("nul", storeKey), Row("victim", "victim-key"));
        var victimDir = WriteStoreDir("victim-key");

        WorkspaceStorePaths.IsSingleSegmentKey(storeKey).ShouldBeFalse();
        var error = Should.Throw<WorkspaceException>(() => WorkspaceStore.DropByStoreKey(storeKey));

        error.Message.ShouldContain("not a single path segment");
        WorkspaceRegistry.FindByStoreKey(storeKey).ShouldNotBeNull();
        Directory.Exists(victimDir).ShouldBeTrue();
        Directory.Exists(_home).ShouldBeTrue();
        File.ReadAllText(Path.Combine(_home, "canary.txt")).ShouldBe("keep me");
    }

    // The trailing-dot/space family is a Win32 second-parser claim, not testable from this host. On
    // POSIX these keys are literal sibling names: containment holds by normalization, and the delete
    // must stop at the named directory instead of reaching workspaces/ itself.
    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void DropByStoreKey_TrailingDotOrSpaceKey_RemovesOnlyTheLiteralDirectory()
    {
        WriteRegistry(Row("tail-dot", ".. "), Row("tail-space", ".. ."), Row("sibling", "sibling-key"));
        var dotDir = WriteStoreDir(".. ");
        var spaceDir = WriteStoreDir(".. .");
        var siblingDir = WriteStoreDir("sibling-key");

        WorkspaceStore.DropByStoreKey(".. ").ShouldHaveSingleItem().ShouldBe(Path.Combine(_workspaces, ".. "));
        WorkspaceStore.DropByStoreKey(".. .").ShouldHaveSingleItem().ShouldBe(Path.Combine(_workspaces, ".. ."));

        Directory.Exists(dotDir).ShouldBeFalse();
        Directory.Exists(spaceDir).ShouldBeFalse();
        Directory.Exists(siblingDir).ShouldBeTrue();
        Directory.Exists(_workspaces).ShouldBeTrue();
        Directory.Exists(_home).ShouldBeTrue();
        File.ReadAllText(Path.Combine(_home, "canary.txt")).ShouldBe("keep me");
        WorkspaceRegistry.FindByStoreKey("sibling-key").ShouldNotBeNull();
    }

    [Fact]
    public void RequireStoreDir_SingleSegmentKey_CombinesUnderTheWorkspacesRoot()
    {
        WorkspaceStorePaths.RequireStoreDir("demo-1a2b3c4d")
            .ShouldBe(Path.Combine(_workspaces, "demo-1a2b3c4d"));
        WorkspaceStorePaths.IsSingleSegmentKey("demo-1a2b3c4d").ShouldBeTrue();
    }

    [Fact]
    public void DropByStoreKey_HomeConfiguredWithTraversalSegments_AcceptsLegalSingleSegmentKeys()
    {
        const string storeKey = "alias-1a2b3c4d";
        var aliasHome = _home + "-alias";
        var configuredHome = Path.Combine(_home, "..", Path.GetFileName(aliasHome));
        _extraDirs.Add(aliasHome);
        try
        {
            Directory.CreateDirectory(Path.Combine(aliasHome, SqueezrPaths.WorkspacesDirName, storeKey));
            File.WriteAllText(
                Path.Combine(aliasHome, SqueezrPaths.WorkspacesDirName, storeKey, SqueezrPaths.MetaDbFileName), "x");
            WorkspaceRegistryEntry[] rows = [Row("alias", storeKey)];
            File.WriteAllText(
                Path.Combine(aliasHome, SqueezrPaths.RegistryFileName),
                JsonSerializer.Serialize(rows, SqueezrJson.FileOptions));
            Environment.SetEnvironmentVariable(HomeEnvironmentVariable, configuredHome);
            SqueezrHome.Reset();

            var removed = WorkspaceStore.DropByStoreKey(storeKey);

            removed.ShouldHaveSingleItem().ShouldBe(
                Path.Combine(configuredHome, SqueezrPaths.WorkspacesDirName, storeKey));
            Directory.Exists(Path.Combine(aliasHome, SqueezrPaths.WorkspacesDirName, storeKey)).ShouldBeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _home);
            SqueezrHome.Reset();
        }
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void DropByStoreKey_StoreDirUndeletable_ReportsStoreRemovalVerdictAndKeepsTheRow()
    {
        const string storeKey = "blocked-1a2b3c4d";
        WriteRegistry(Row("blocked", storeKey));
        var storeDir = WriteStoreDir(storeKey);
        File.SetUnixFileMode(storeDir, UnixFileMode.None);
        try
        {
            var error = Should.Throw<WorkspaceStoreRemovalException>(
                () => WorkspaceStore.DropByStoreKey(storeKey));

            error.StoreKey.ShouldBe(storeKey);
            error.Message.ShouldNotContain(storeDir);
            error.InnerException.ShouldNotBeNull();
            Directory.Exists(storeDir).ShouldBeTrue();
            WorkspaceRegistry.FindByStoreKey(storeKey).ShouldNotBeNull();
        }
        finally
        {
            File.SetUnixFileMode(
                storeDir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _previousEnv);
        SqueezrHome.Reset();
        foreach (var dir in _extraDirs.Where(Directory.Exists))
        {
            Directory.Delete(dir, recursive: true);
        }

        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, recursive: true);
        }
    }

    private string WriteStoreDir(string storeKey)
    {
        var storeDir = Path.Combine(_workspaces, storeKey);
        Directory.CreateDirectory(storeDir);
        File.WriteAllText(
            Path.Combine(storeDir, SqueezrPaths.MetaDbFileName),
            new string('x', 32));
        return storeDir;
    }

    private void WriteRegistry(params WorkspaceRegistryEntry[] entries) =>
        File.WriteAllText(
            Path.Combine(_home, SqueezrPaths.RegistryFileName),
            JsonSerializer.Serialize(entries, SqueezrJson.FileOptions));

    private WorkspaceRegistryEntry Row(string name, string storeKey)
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkspaceRegistryEntry(name, Canonical(Path.Combine(_home, "gone", name)), storeKey, now, now);
    }

    private static string Canonical(string path) => WorkspaceCanonicalPath.Canonicalize(path);
}
