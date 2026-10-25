using System.Runtime.Versioning;
using System.Security.Cryptography;
using Shouldly;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Storage;

// The 2026-10-19 weapon configuration, whole and inside the per-run temp dir: a fake HOME holding a
// decoy central store, SQUEEZR_HOME pinned to a scratch directory, init run from a working directory
// beneath the fake home. That combination moved the live ~/.squeezr into the scratch home in about
// two minutes. The guard set shipped by 071 (absolute centrality, structural signature, fail-loud
// leftover tail) must hold the decoy byte-still instead.
[Collection("ProcessGlobals")]
public sealed class Incident20261019RegressionTests : IDisposable
{
    private const string HomeEnvironmentVariable = "SQUEEZR_HOME";
    private const string ProfileEnvironmentVariable = "HOME";
    private readonly string? _previousEnv;
    private readonly string? _previousHome;
    private readonly List<string> _createdDirs = [];

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void Init_WeaponConfigurationOf20261019_DoesNotTouchTheDecoyCentralStore()
    {
        var temp = NewDir();
        var profile = NewDir(Path.Combine(temp, "home"));
        var pinnedHome = NewDir(Path.Combine(temp, "scratch"));
        var fresh = NewDir(Path.Combine(profile, "projects", "fresh"));
        var decoyStore = NewDir(Path.Combine(profile, SqueezrPaths.StoreDirName));
        Directory.CreateDirectory(Path.Combine(decoyStore, SqueezrPaths.WorkspacesDirName, "w1"));
        Directory.CreateDirectory(Path.Combine(decoyStore, SqueezrPaths.InfraDirName));
        File.WriteAllText(Path.Combine(decoyStore, SqueezrPaths.RegistryFileName), """[{"name":"real"}]""");
        File.WriteAllText(Path.Combine(decoyStore, SqueezrPaths.MetaDbFileName), "index bytes");
        File.WriteAllText(Path.Combine(decoyStore, SqueezrPaths.WorkspacesDirName, "w1", "payload.txt"), "keep me");
        File.WriteAllText(Path.Combine(decoyStore, "canary.txt"), "keep me");
        var fingerprintBefore = Fingerprint(decoyStore);

        Environment.SetEnvironmentVariable(ProfileEnvironmentVariable, profile);
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, pinnedHome);
        SqueezrHome.Reset();
        WorkspaceCanonicalPath.ResolveThroughLinks(SqueezrHome.DefaultHome())
            .ShouldStartWith(WorkspaceCanonicalPath.Canonicalize(temp));

        var discovery = WorkspaceStore.Resolve(NewDir(Path.Combine(fresh, "deep")));
        discovery.IsWorkspace.ShouldBeFalse();

        var init = new WorkspaceManager(fresh).Init("probe");

        init.WorkspaceName.ShouldBe("probe");
        Fingerprint(decoyStore).ShouldBe(fingerprintBefore);
        File.ReadAllText(Path.Combine(decoyStore, "canary.txt")).ShouldBe("keep me");
        File.ReadAllText(Path.Combine(decoyStore, SqueezrPaths.WorkspacesDirName, "w1", "payload.txt"))
            .ShouldBe("keep me");
        var probeRow = WorkspaceRegistry.FindByRootPath(WorkspaceCanonicalPath.Canonicalize(fresh));
        probeRow.ShouldNotBeNull();
        SqueezrPaths.StoreDir(fresh).ShouldStartWith(WorkspaceCanonicalPath.Canonicalize(pinnedHome));
        WorkspaceRegistry.Load().ShouldHaveSingleItem();
        Directory.Exists(Path.Combine(pinnedHome, SqueezrPaths.WorkspacesDirName)).ShouldBeTrue();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _previousEnv);
        Environment.SetEnvironmentVariable(ProfileEnvironmentVariable, _previousHome);
        SqueezrHome.Reset();
        foreach (var dir in _createdDirs)
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private static string Fingerprint(string root) =>
        string.Join(
            "\n",
            Directory
                .EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(path => (
                    Relative: Path.GetRelativePath(root, path),
                    Hash: Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))))
                .OrderBy(entry => entry.Relative, StringComparer.Ordinal)
                .Select(entry => $"{entry.Relative}:{entry.Hash}"));

    private string NewDir(string? path = null)
    {
        var dir = Path.GetFullPath(path ?? TestDirs.NewTempDir());
        Directory.CreateDirectory(dir);
        _createdDirs.Add(dir);
        return dir;
    }
}
