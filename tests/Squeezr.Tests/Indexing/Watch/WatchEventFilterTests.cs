using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Ignore;
using Squeezr.Core.Indexing.Watch;

namespace Squeezr.Tests.Indexing.Watch;

public class WatchEventFilterTests
{
    private static readonly string[] NoiseFiles =
    [
        ".Squeezr.slnx.swp",
        "4913",
        "Directory.Build.props.bak",
        "Feed.csproj.user",
        "Squeezr.slnx.tmp",
        "Squeezr.slnx~",
        "bin/Debug/net10.0/Squeezr.dll",
        "notes.unknownext",
        "obj/Debug/net10.0/Feed.csproj.nuget.g.props",
        "upgrade-report.log"
    ];

    public static IEnumerable<object[]> ProjectLayoutExtensions() =>
        WorkspaceIndexer.ProjectLayoutExtensions
            .Select(extension => new object[] { extension })
            .ToList();

    [Theory]
    [MemberData(nameof(ProjectLayoutExtensions))]
    public void ShouldTrack_EditToAProjectLayoutFile_SurvivesTheFilter(string extension)
    {
        var filter = NewFilter(out var repoRoot);

        filter.ShouldTrack(Path.Combine(repoRoot, "Solution" + extension), pathMayBeGone: false).ShouldBeTrue(
            $"an edit to '{extension}' has to reach the pass that drops the stale session");
    }

    [Fact]
    public void ShouldTrack_OverANoiseFixtureDirectory_DropsEverythingTheHealTriggerDoesNotOwn()
    {
        var filter = NewFilter(out var repoRoot);
        foreach (var extension in WorkspaceIndexer.ProjectLayoutExtensions)
        {
            Write(repoRoot, "Solution" + extension, "<Project />");
        }
        foreach (var noise in NoiseFiles)
        {
            Write(repoRoot, noise, "noise");
        }

        var dropped = Directory.EnumerateFiles(repoRoot, "*", SearchOption.AllDirectories)
            .Where(path => !filter.ShouldTrack(path, pathMayBeGone: false))
            .Select(path => ToRelative(repoRoot, path))
            .Order()
            .ToList();

        (WorkspaceIndexer.ProjectLayoutExtensions.Length + NoiseFiles.Length)
            .ShouldBe(Directory.EnumerateFiles(repoRoot, "*", SearchOption.AllDirectories).Count(),
                "the fixture directory has to hold every file this split claims");
        dropped.ShouldBe(NoiseFiles.Order().ToList(),
            "a layout lookalike that is not an exact extension match must never wake the watcher");
    }

    [Fact]
    public void ShouldTrack_DeletionOfAProjectLayoutFile_SurvivesTheFilter()
    {
        var filter = NewFilter(out var repoRoot);

        filter.ShouldTrack(Path.Combine(repoRoot, "Squeezr.slnx"), pathMayBeGone: true).ShouldBeTrue();
    }

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

    [Theory]
    [InlineData("/repo/.git/index")]
    [InlineData("/repo/.git/index.lock")]
    [InlineData("/repo/.git/logs/HEAD")]
    [InlineData("/repo/.git/refs/heads/main")]
    [InlineData("C:\\repo\\.git\\index")]
    public void IsGitInternalChange_GitHousekeepingPath_ReturnsTrue(string fullPath)
    {
        WatchEventFilter.IsGitInternalChange(fullPath).ShouldBeTrue();
    }

    [Theory]
    [InlineData("/repo/src/Program.cs")]
    [InlineData("/repo/.github/workflows/ci.yml")]
    [InlineData("/repo/gitignore")]
    [InlineData("/myrepo/x")]
    public void IsGitInternalChange_RegularPath_ReturnsFalse(string fullPath)
    {
        WatchEventFilter.IsGitInternalChange(fullPath).ShouldBeFalse();
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

    private static WatchEventFilter NewFilter(out string repoRoot)
    {
        var root = TestDirs.NewTempDir();
        repoRoot = Path.GetFullPath(Path.Combine(root, "Repo"));
        Directory.CreateDirectory(repoRoot);
        var rules = IgnoreRuleset.For(repoRoot, SqueezrConfig.CreateDefault());
        return new WatchEventFilter(root, [repoRoot], new Dictionary<string, IgnoreRuleset> { [repoRoot] = rules });
    }

    private static void Write(string repoRoot, string fileName, string content)
    {
        var full = Path.Combine(repoRoot, fileName.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static string ToRelative(string repoRoot, string fullPath) =>
        Path.GetRelativePath(repoRoot, fullPath).Replace(Path.DirectorySeparatorChar, '/');
}
