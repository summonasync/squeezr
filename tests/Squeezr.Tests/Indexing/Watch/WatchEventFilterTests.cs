using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing.Ignore;
using Squeezr.Core.Indexing.Watch;

namespace Squeezr.Tests.Indexing.Watch;

public class WatchEventFilterTests
{
    [Fact]
    public void ShouldTrack_PathCasingDifferentFromRoot_MatchesOnlyOnCaseInsensitiveFs()
    {
        var root = TestDirs.NewTempDir();
        var repoRoot = Path.GetFullPath(Path.Combine(root, "Repo"));
        var rules = IgnoreRuleset.For(repoRoot, SqueezrConfig.CreateDefault());
        var filter = new WatchEventFilter(root, [repoRoot], new Dictionary<string, IgnoreRuleset> { [repoRoot] = rules });

        var casedPath = Path.Combine(repoRoot.ToLowerInvariant(), "a.cs");
        var expected = OperatingSystem.IsWindows();

        filter.ShouldTrack(casedPath, pathMayBeGone: false).ShouldBe(expected);
    }

    [Fact]
    public void IsIgnoreFileChange_Gitignore_ReturnsTrue()
    {
        WatchEventFilter.IsIgnoreFileChange("/repo/.gitignore").ShouldBeTrue();
    }

    [Fact]
    public void IsIgnoreFileChange_Squeezrignore_ReturnsTrue()
    {
        WatchEventFilter.IsIgnoreFileChange("/repo/.squeezrignore").ShouldBeTrue();
    }

    [Fact]
    public void IsIgnoreFileChange_RegularFile_ReturnsFalse()
    {
        WatchEventFilter.IsIgnoreFileChange("/repo/src/Program.cs").ShouldBeFalse();
    }

    [Fact]
    public void ShouldTrack_ResidualGitignorePattern_KeepsTrackingTheFile()
    {
        var root = TestDirs.NewTempDir();
        var repoRoot = Path.GetFullPath(Path.Combine(root, "Repo"));
        Directory.CreateDirectory(repoRoot);
        File.WriteAllText(Path.Combine(repoRoot, ".gitignore"), "[[:alpha:]]\n");

        var rules = IgnoreRuleset.For(repoRoot, SqueezrConfig.CreateDefault());
        var filter = new WatchEventFilter(root, [repoRoot], new Dictionary<string, IgnoreRuleset> { [repoRoot] = rules });

        filter.ShouldTrack(Path.Combine(repoRoot, "a]"), pathMayBeGone: true).ShouldBeTrue();
    }

    [Fact]
    public void ShouldTrack_ExcludedFile_ReturnsFalse()
    {
        var root = TestDirs.NewTempDir();
        var repoRoot = Path.GetFullPath(Path.Combine(root, "Repo"));
        Directory.CreateDirectory(repoRoot);
        File.WriteAllText(Path.Combine(repoRoot, ".gitignore"), "build/\n");

        var config = SqueezrConfig.CreateDefault();
        var rules = IgnoreRuleset.For(repoRoot, config);
        var filter = new WatchEventFilter(root, [repoRoot], new Dictionary<string, IgnoreRuleset> { [repoRoot] = rules });

        var excludedPath = Path.Combine(repoRoot, "build", "output.js");
        filter.ShouldTrack(excludedPath, pathMayBeGone: false).ShouldBeFalse();
    }
}
