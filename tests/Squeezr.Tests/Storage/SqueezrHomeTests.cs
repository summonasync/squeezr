using Shouldly;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Storage;

[Collection("ProcessGlobals")]
public sealed class SqueezrHomeTests : IDisposable
{
    private const string HomeEnvironmentVariable = "SQUEEZR_HOME";
    private readonly string? _previousEnv;
    private readonly List<string> _createdDirs = [];

    public SqueezrHomeTests()
    {
        _previousEnv = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
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

    private string UseHome(string home)
    {
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, home);
        SqueezrHome.Reset();
        return SqueezrHome.Resolve();
    }

    private string NewTempDir()
    {
        var dir = TestDirs.NewTempDir();
        _createdDirs.Add(dir);
        return dir;
    }
}
