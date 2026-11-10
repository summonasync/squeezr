using Shouldly;
using Squeezr.Core.Indexing.Watch.Supervision;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Indexing.Watch.Supervision;

public sealed class WatchdPathsTests
{
    private const string Home = "/var/tmp/squeezr-home";

    [Fact]
    public void LockPathUnder_ExplicitHome_NamesTheLockTheCarriedHomeWillOpen() =>
        WatchdPaths.LockPathUnder(Path.Combine(Home, "carry")).ShouldBe(
            Path.Combine(Home, "carry", WatchdPaths.DirName, WatchdPaths.LockFileName));

    // An install that was told no home bakes none into the unit, so the instance it starts resolves the
    // ambient one and writes its claim there. Blank counts as "not told" the same way the generator
    // treats it — a shell that exported an empty variable must not steer the read into a relative path.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void LockPathUnder_WithoutAnExplicitHome_FallsBackToTheAmbientStore(string? squeezrHome)
    {
        var path = WatchdPaths.LockPathUnder(squeezrHome);

        path.ShouldBe(WatchdPaths.LockPath);
        path.ShouldBe(Path.Combine(SqueezrHome.Resolve(), WatchdPaths.DirName, WatchdPaths.LockFileName));
    }
}
