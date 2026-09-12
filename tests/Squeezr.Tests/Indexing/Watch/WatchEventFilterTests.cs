using Shouldly;
using Squeezr.Core.Indexing.Watch;

namespace Squeezr.Tests.Indexing.Watch;

public class WatchEventFilterTests
{
    [Fact]
    public void ShouldTrack_PathCasingDifferentFromRoot_MatchesOnlyOnCaseInsensitiveFs()
    {
        var root = TestDirs.NewTempDir();
        var repoRoot = Path.GetFullPath(Path.Combine(root, "Repo"));
        var filter = new WatchEventFilter(root, [repoRoot], []);

        var casedPath = Path.Combine(repoRoot.ToLowerInvariant(), "a.cs");
        var expected = OperatingSystem.IsWindows();

        filter.ShouldTrack(casedPath, pathMayBeGone: false).ShouldBe(expected);
    }
}
