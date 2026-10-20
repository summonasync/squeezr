using System.Runtime.Versioning;
using Shouldly;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Storage;

// The 067/T9 fix round closed the untrusted-row route into the central store, but the guard compared two
// spellings lexically: with SQUEEZR_HOME named through a linked ancestor (<d>/link/.squeezr naming
// <d>/real/.squeezr) the compare missed and Directory.Delete took the whole home. Measured on three
// binaries in audits/registry-store-parent-drop-guard-2026-10-06/residual-symlink-home.txt.
[Collection("CliProcessGlobals")]
public sealed class SymlinkedHomeStoreGuardTests : IDisposable
{
    private const string HomeEnvironmentVariable = "SQUEEZR_HOME";
    private readonly string? _previousEnv;
    private readonly string _scratch;
    private readonly string _real;
    private readonly string _homeThroughLink;
    private readonly string _homeReal;

    public SymlinkedHomeStoreGuardTests()
    {
        _previousEnv = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        _scratch = TestDirs.NewTempDir();
        _real = Path.Combine(_scratch, "real");
        Directory.CreateDirectory(_real);
        Directory.CreateSymbolicLink(Path.Combine(_scratch, "link"), _real);
        _homeThroughLink = Path.Combine(_scratch, "link", SqueezrPaths.StoreDirName);
        _homeReal = Path.Combine(_real, SqueezrPaths.StoreDirName);
        Directory.CreateDirectory(Path.Combine(_homeReal, SqueezrPaths.WorkspacesDirName));
        File.WriteAllText(Path.Combine(_homeReal, "canary.txt"), "keep me");
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _homeThroughLink);
        SqueezrHome.Reset();
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void IsCentralStore_HomeNamedThroughALinkedAncestor_IsTheSameDirectory()
    {
        SqueezrHome.IsCentralStore(_homeReal).ShouldBeTrue();
        SqueezrHome.IsCentralStore(_homeThroughLink).ShouldBeTrue();
        SqueezrHome.IsCentralStore(Path.Combine(_homeReal, SqueezrPaths.WorkspacesDirName)).ShouldBeFalse();
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void Drop_RowWithUntrustedKey_HomeNamedThroughALink_ThrowsAndKeepsHome()
    {
        var projectRoot = NewDir(_real);
        WorkspaceRegistry.Add(new WorkspaceRegistryEntry(
            "home", projectRoot, "..", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        Should.Throw<WorkspaceException>(() => new WorkspaceManager(projectRoot).Drop())
            .Message.ShouldContain("central squeezr store");

        Directory.Exists(_homeReal).ShouldBeTrue();
        File.ReadAllText(Path.Combine(_homeReal, "canary.txt")).ShouldBe("keep me");
        WorkspaceRegistry.FindByRootPath(projectRoot).ShouldNotBeNull();
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void Init_AtTheRealPath_HomeNamedThroughALink_RefusesAndWritesNothing()
    {
        Should.Throw<WorkspaceException>(() => new WorkspaceManager(_real).Init("home"))
            .Message.ShouldContain("central squeezr store");

        Directory.Exists(_homeReal).ShouldBeTrue();
        File.ReadAllText(Path.Combine(_homeReal, "canary.txt")).ShouldBe("keep me");
        WorkspaceRegistry.Load().ShouldBeEmpty();
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void Resolve_LegacyManifestAtTheLinkedHomeRoot_RefusesMigrationAndKeepsStoreContent()
    {
        var projectRoot = NewDir(_real);
        File.WriteAllText(
            Path.Combine(projectRoot, SqueezrPaths.ManifestFileName),
            """{"name":"linked-home","repos":[]}""");
        File.WriteAllText(Path.Combine(_homeReal, SqueezrPaths.MetaDbFileName), "home payload");
        WorkspaceRegistry.Add(new WorkspaceRegistryEntry(
            "linked-home", projectRoot, "linked-home-1a2b3c4d", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        Should.Throw<WorkspaceException>(() => WorkspaceStore.Resolve(projectRoot))
            .Message.ShouldContain("central squeezr store");

        File.ReadAllText(Path.Combine(_homeReal, SqueezrPaths.MetaDbFileName)).ShouldBe("home payload");
        File.Exists(Path.Combine(projectRoot, SqueezrPaths.ManifestFileName)).ShouldBeTrue();
        Directory.GetFileSystemEntries(Path.Combine(_homeReal, SqueezrPaths.WorkspacesDirName)).ShouldBeEmpty();
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void Drop_LegalStoreKey_HomeNamedThroughALink_StillRemovesTheStoreAndKeepsHome()
    {
        var projectRoot = NewDir(Path.Combine(_real, "proj"));
        new WorkspaceManager(projectRoot).Init("proj");
        var storeKey = WorkspaceRegistry.FindByRootPath(projectRoot)!.StoreKey;
        var storeDir = Path.Combine(_homeReal, SqueezrPaths.WorkspacesDirName, storeKey);
        Directory.Exists(storeDir).ShouldBeTrue();

        var result = new WorkspaceManager(projectRoot).Drop();

        result.Removed.ShouldHaveSingleItem().ShouldBe(Path.Combine(_homeThroughLink, SqueezrPaths.WorkspacesDirName, storeKey));
        Directory.Exists(storeDir).ShouldBeFalse();
        Directory.Exists(_homeReal).ShouldBeTrue();
        File.ReadAllText(Path.Combine(_homeReal, "canary.txt")).ShouldBe("keep me");
        WorkspaceRegistry.FindByRootPath(projectRoot).ShouldBeNull();
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

    private static string NewDir(string path)
    {
        Directory.CreateDirectory(path);
        return WorkspaceCanonicalPath.Canonicalize(path);
    }
}
