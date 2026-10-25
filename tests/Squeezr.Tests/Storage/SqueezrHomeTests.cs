using System.Runtime.Versioning;
using Shouldly;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Storage;

[Collection("ProcessGlobals")]
public sealed class SqueezrHomeTests : IDisposable
{
    private const string HomeEnvironmentVariable = "SQUEEZR_HOME";
    private const string ProfileEnvironmentVariable = "HOME";
    private readonly string? _previousEnv;
    private readonly string? _previousHome;
    private readonly List<string> _createdDirs = [];

    public SqueezrHomeTests()
    {
        _previousEnv = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        _previousHome = Environment.GetEnvironmentVariable(ProfileEnvironmentVariable);
        UseHome(NewTempDir());
    }

    [Fact]
    public void Resolve_UsesEnvironmentVariableValueAsIs()
    {
        var home = NewTempDir();

        var resolved = UseHome(home);

        resolved.ShouldBe(home);
    }

    [Fact]
    public void Resolve_WhitespaceOnlyEnvironmentVariable_FallsBackToUserProfile()
    {
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, "   ");
        SqueezrHome.Reset();

        SqueezrHome.Resolve().ShouldBe(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".squeezr"));
    }

    [Fact]
    public void Resolve_WithoutEnvironmentVariable_UsesUserProfileSqueezrDir()
    {
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, null);
        SqueezrHome.Reset();

        SqueezrHome.Resolve().ShouldBe(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".squeezr"));
    }

    [Fact]
    public void Resolve_CachesValueUntilReset()
    {
        var first = NewTempDir();
        var second = NewTempDir();
        UseHome(first);

        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, second);
        SqueezrHome.Resolve().ShouldBe(first);

        SqueezrHome.Reset();
        SqueezrHome.Resolve().ShouldBe(second);
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void DefaultHome_FakedProfileInsideRunTemp_ResolvesStoreInsideIt()
    {
        var temp = NewTempDir();
        var profile = NewDir(temp, "profile");

        var defaultHome = UseProfile(profile);

        defaultHome.ShouldBe(Path.Combine(profile, SqueezrPaths.StoreDirName));
        ResolvedHomeStaysInsideRunTemp(defaultHome);
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void DefaultHome_FakedHomeChangesBetweenCalls_RecomputesWithoutReset()
    {
        var temp = NewTempDir();
        var first = NewDir(temp, "home-a");
        var second = NewDir(temp, "home-b");
        Environment.SetEnvironmentVariable(ProfileEnvironmentVariable, first);
        SqueezrHome.Reset();
        SqueezrHome.DefaultHome().ShouldBe(Path.Combine(first, SqueezrPaths.StoreDirName));

        Environment.SetEnvironmentVariable(ProfileEnvironmentVariable, second);

        var defaultHome = SqueezrHome.DefaultHome();
        defaultHome.ShouldBe(Path.Combine(second, SqueezrPaths.StoreDirName));
        ResolvedHomeStaysInsideRunTemp(defaultHome);
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void IsCentralStore_FakedDefaultHomeUnderPinnedOverride_IsCentral()
    {
        var temp = NewTempDir();
        var profile = NewDir(temp, "profile");
        var defaultStore = NewDir(profile, SqueezrPaths.StoreDirName);
        Directory.CreateDirectory(Path.Combine(defaultStore, SqueezrPaths.WorkspacesDirName));
        File.WriteAllText(Path.Combine(defaultStore, SqueezrPaths.RegistryFileName), "[]");
        var pinnedHome = NewDir(temp, "pinned-home");
        UseHome(pinnedHome);
        Environment.SetEnvironmentVariable(ProfileEnvironmentVariable, profile);
        SqueezrHome.Reset();

        SqueezrHome.DefaultHome().ShouldBe(defaultStore);
        SqueezrHome.IsCentralStore(defaultStore).ShouldBeTrue();
        SqueezrHome.IsCentralStore(pinnedHome).ShouldBeTrue();
        SqueezrHome.IsDefaultHome(defaultStore).ShouldBeTrue();
        SqueezrHome.IsDefaultHome(pinnedHome).ShouldBeFalse();
        SqueezrHome.IsCentralStore(Path.Combine(defaultStore, SqueezrPaths.WorkspacesDirName)).ShouldBeFalse();
        SqueezrHome.IsCentralStore(NewDir(temp, "plain")).ShouldBeFalse();
        ResolvedHomeStaysInsideRunTemp(SqueezrHome.DefaultHome());
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void IsCentralStore_DefaultHomeWithoutOverride_IsCentral()
    {
        var temp = NewTempDir();
        var profile = NewDir(temp, "profile");
        var defaultStore = NewDir(profile, SqueezrPaths.StoreDirName);
        Directory.CreateDirectory(Path.Combine(defaultStore, SqueezrPaths.WorkspacesDirName));
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, null);
        Environment.SetEnvironmentVariable(ProfileEnvironmentVariable, profile);
        SqueezrHome.Reset();

        SqueezrHome.IsCentralStore(defaultStore).ShouldBeTrue();
        SqueezrHome.Resolve().ShouldBe(defaultStore);
        SqueezrHome.IsDefaultHome(defaultStore).ShouldBeTrue();
        SqueezrHome.IsDefaultHome(NewDir(temp, "other")).ShouldBeFalse();
        ResolvedHomeStaysInsideRunTemp(SqueezrHome.DefaultHome());
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void IsCentralStore_FakedDefaultHomeNamedThroughALinkedAncestor_IsCentral()
    {
        var temp = NewTempDir();
        var real = NewDir(temp, "real");
        Directory.CreateSymbolicLink(Path.Combine(temp, "link"), real);
        var defaultStore = NewDir(real, SqueezrPaths.StoreDirName);
        var pinnedHome = NewDir(temp, "pinned-home");
        UseHome(pinnedHome);
        Environment.SetEnvironmentVariable(ProfileEnvironmentVariable, real);
        SqueezrHome.Reset();
        var linked = Path.Combine(temp, "link", SqueezrPaths.StoreDirName);

        SqueezrHome.IsCentralStore(linked).ShouldBeTrue();
        SqueezrHome.IsCentralStore(defaultStore).ShouldBeTrue();
        SqueezrHome.IsDefaultHome(linked).ShouldBeTrue();
        SqueezrHome.IsDefaultHome(pinnedHome).ShouldBeFalse();
    }

    [Fact]
    public void LooksLikeCentralStore_RegistryFileInDir_IsTrueAndEmptyDirIsFalse()
    {
        var temp = NewTempDir();
        var withRegistry = NewDir(temp, "registry-store");
        File.WriteAllText(Path.Combine(withRegistry, SqueezrPaths.RegistryFileName), "[]");

        SqueezrHome.LooksLikeCentralStore(withRegistry).ShouldBeTrue();
        SqueezrHome.LooksLikeCentralStore(NewDir(temp, "empty")).ShouldBeFalse();
    }

    [Fact]
    public void LooksLikeCentralStore_WorkspacesDirInDir_IsTrueAndPlainDirIsFalse()
    {
        var temp = NewTempDir();
        var withWorkspaces = NewDir(temp, "workspaces-store");
        Directory.CreateDirectory(Path.Combine(withWorkspaces, SqueezrPaths.WorkspacesDirName));

        SqueezrHome.LooksLikeCentralStore(withWorkspaces).ShouldBeTrue();
        SqueezrHome.LooksLikeCentralStore(NewDir(temp, "plain")).ShouldBeFalse();
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

    private string UseHome(string home)
    {
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, home);
        SqueezrHome.Reset();
        return SqueezrHome.Resolve();
    }

    private string UseProfile(string profile)
    {
        Environment.SetEnvironmentVariable(ProfileEnvironmentVariable, profile);
        SqueezrHome.Reset();
        return SqueezrHome.DefaultHome();
    }

    private static void ResolvedHomeStaysInsideRunTemp(string resolvedHome)
    {
        var tempRoot = WorkspaceCanonicalPath.Canonicalize(Path.GetTempPath());
        WorkspaceCanonicalPath.ResolveThroughLinks(resolvedHome).ShouldStartWith(tempRoot);
    }

    private string NewTempDir()
    {
        var dir = TestDirs.NewTempDir();
        _createdDirs.Add(dir);
        return dir;
    }

    private string NewDir(string parent, string name)
    {
        var dir = Directory.CreateDirectory(Path.Combine(parent, name)).FullName;
        _createdDirs.Add(dir);
        return dir;
    }
}
