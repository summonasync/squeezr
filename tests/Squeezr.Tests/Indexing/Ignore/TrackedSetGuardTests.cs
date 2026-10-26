using Shouldly;
using Squeezr.Cli.Output;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Ignore;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing.Ignore;

public sealed class TrackedSetGuardTests
{
    [Fact]
    public void Load_WithoutGitMarker_ReturnsNull()
    {
        var root = TestDirs.NewTempDir();

        GitTrackedSet.Load(root).ShouldBeNull();
    }

    [Fact]
    public void Load_MapsTrackedFilesAndTheirDirectories()
    {
        var root = NewPluginsRepo();

        var tracked = GitTrackedSet.Load(root);

        tracked.ShouldNotBeNull();
        tracked.ContainsFile("plugins/a.js").ShouldBeTrue();
        tracked.ContainsDirectory("plugins").ShouldBeTrue();
        tracked.ContainsDirectory("deep/plugins").ShouldBeFalse();
    }

    [Fact]
    public void Load_ExcludesUntrackedPaths()
    {
        var root = NewPluginsRepo();

        var tracked = GitTrackedSet.Load(root);

        tracked.ShouldNotBeNull();
        tracked.ContainsFile("plugins/notes.txt").ShouldBeFalse();
        tracked.ContainsFile("missing.js").ShouldBeFalse();
    }

    [Fact]
    public void Decision_TrackedFileMatchingGitignore_IsKeptAndFlagged()
    {
        var root = NewPluginsRepo();

        var decision = IgnoreRuleset.For(root, SqueezrConfig.CreateDefault()).Decision("plugins/a.js", false);

        decision.Excluded.ShouldBeFalse();
        decision.TrackedButExcluded.ShouldBeTrue();
        decision.Source.ShouldBe(IgnoreSource.Gitignore);
    }

    [Fact]
    public void Decision_UntrackedIgnoredFile_StaysExcluded()
    {
        var root = NewPluginsRepo();

        var decision = IgnoreRuleset.For(root, SqueezrConfig.CreateDefault()).Decision("plugins/notes.txt", false);

        decision.Excluded.ShouldBeTrue();
        decision.TrackedButExcluded.ShouldBeFalse();
        decision.Source.ShouldBe(IgnoreSource.Gitignore);
    }

    [Fact]
    public void Decision_DirectoryHoldingTrackedFiles_IsKeptForTheWalk()
    {
        var root = NewPluginsRepo();

        var decision = IgnoreRuleset.For(root, SqueezrConfig.CreateDefault()).Decision("plugins", true);

        decision.Excluded.ShouldBeFalse();
        decision.TrackedButExcluded.ShouldBeTrue();
    }

    [Fact]
    public void Decision_TrackedFileOutsideThePattern_Unaffected()
    {
        var root = NewPluginsRepo();
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, "src", "app.js"), "const x = 1;\n");

        var decision = IgnoreRuleset.For(root, SqueezrConfig.CreateDefault()).Decision("src/app.js", false);

        decision.Excluded.ShouldBeFalse();
        decision.TrackedButExcluded.ShouldBeFalse();
    }

    [Fact]
    public void ForFile_TrackedFileUnderGitignoredDir_IsKept()
    {
        var root = NewPluginsRepo();

        var rules = IgnoreRuleset.ForFile(root, SqueezrConfig.CreateDefault(), "plugins/a.js");
        var decision = rules.Decision("plugins/a.js", false);

        decision.Excluded.ShouldBeFalse();
        decision.TrackedButExcluded.ShouldBeTrue();
    }

    [Fact]
    public void Decision_ConfigSourcedExclusion_StillDropsTrackedFile()
    {
        var root = NewPluginsRepo();
        var defaults = SqueezrConfig.CreateDefault().Indexing;
        var config = SqueezrConfig.CreateDefault() with
        {
            Indexing = defaults with { Ignore = ["plugins"], RespectGitignore = false }
        };

        var decision = IgnoreRuleset.For(root, config).Decision("plugins/a.js", false);

        decision.Excluded.ShouldBeTrue();
        decision.TrackedButExcluded.ShouldBeFalse();
    }

    [Fact]
    public void Walk_IndexesTrackedFileUnderGitignoredDir_AndCountsTheRescue()
    {
        var root = NewPluginsRepo();

        var walk = RepoFileWalker.Walk(
            root, IgnoreRuleset.For(root, SqueezrConfig.CreateDefault()), SecretGuardIngestion.Load(root), 512);

        walk.Files.ShouldContain(f => f.RelativePath == "plugins/a.js");
        walk.Files.ShouldNotContain(f => f.RelativePath == "plugins/notes.txt");
        walk.TrackedButExcluded.ShouldBe(1);
        walk.Ignored.ShouldBe(1);
        walk.IgnoredBySource[IgnoreSource.Gitignore].ShouldBe(1);
    }

    [Fact]
    public void WatchFilter_TracksTheTrackedFileAndDropsTheIgnoredOne()
    {
        var root = NewPluginsRepo();
        var rules = IgnoreRuleset.For(root, SqueezrConfig.CreateDefault());
        var filter = new WatchEventFilter(root, [root], new Dictionary<string, IgnoreRuleset> { [root] = rules });

        filter.ShouldTrack(Path.Combine(root, "plugins", "a.js"), pathMayBeGone: false).ShouldBeTrue();
        filter.ShouldTrack(Path.Combine(root, "plugins", "notes.txt"), pathMayBeGone: false).ShouldBeFalse();
    }

    [Fact]
    public void Index_TrackedFileUnderGitignorePattern_IsIndexedCountedAndReported()
    {
        var workspace = NewIndexedPluginsWorkspace();

        using (var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace)))
        {
            store.ListSymbolsByRepo(store.ListRepos().Single().Id).ShouldNotBeEmpty();
        }

        var status = new WorkspaceManager(workspace).GetStatus().Repos.Single();
        status.TrackedButExcluded.ShouldBe(1);
        IngestionWorkspaces.StoredPaths(workspace).ShouldContain("plugins/a.js");
        IngestionWorkspaces.StoredPaths(workspace).ShouldNotContain("plugins/notes.txt");
    }

    [Fact]
    public void IndexFile_ChangeToTrackedGitignoredFile_SurvivesTheSingleFileRoute()
    {
        var workspace = NewIndexedPluginsWorkspace();

        var doc = new WorkspaceIndexer().IndexFile(workspace, "demo", "plugins/a.js");

        doc.ShouldNotBeNull();
        IngestionWorkspaces.StoredPaths(workspace).ShouldContain("plugins/a.js");
    }

    [Fact]
    public void StatusReport_TrackedButExcluded_WarnsWhenAboveZero()
    {
        var status = new WorkspaceStatus(
            "demo",
            [new RepoStatus("repo", "repo", 5, 1, 2, DateTime.UtcNow, TrackedButExcluded: 35)]);

        var report = WorkspaceStatusReport.Create(status, []);

        report.Warnings.ShouldContain(w => w.Contains("35") && w.Contains("git tracks"));
        report.Repos.Single().Ignored!.TrackedButExcluded.ShouldBe(35);
    }

    private static string NewPluginsRepo()
    {
        var root = TestDirs.NewTempDir();
        TestGitRepo.Init(root);
        Directory.CreateDirectory(Path.Combine(root, "plugins"));
        File.WriteAllText(Path.Combine(root, "plugins", "a.js"), "export function boot() { return 1; }\n");
        File.WriteAllText(Path.Combine(root, "plugins", "notes.txt"), "scratch\n");
        File.WriteAllText(Path.Combine(root, ".gitignore"), "/plugins\n");
        TestGitRepo.Add(root, ".gitignore", "plugins/a.js");
        return root;
    }

    private static string NewIndexedPluginsWorkspace()
    {
        var workspace = IngestionWorkspaces.NewEmptyWorkspace();
        var repoRoot = IngestionWorkspaces.RepoRoot(workspace);
        TestGitRepo.Init(repoRoot);
        Directory.CreateDirectory(Path.Combine(repoRoot, "plugins"));
        File.WriteAllText(Path.Combine(repoRoot, "plugins", "a.js"), "export function boot() { return 1; }\n");
        File.WriteAllText(Path.Combine(repoRoot, "plugins", "notes.txt"), "scratch\n");
        File.WriteAllText(Path.Combine(repoRoot, ".gitignore"), "/plugins\n");
        TestGitRepo.Add(repoRoot, ".gitignore", "plugins/a.js");

        new WorkspaceIndexer().Index(workspace);
        return workspace;
    }
}
