using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing;

public class IgnoreSnapshotTests
{
    [Fact]
    public void IndexPass_PersistsTheBreakdownItReports()
    {
        var workspace = PrepareGitignoreFixture();
        var result = new WorkspaceIndexer().Index(workspace);
        var reported = result.Repos.ShouldHaveSingleItem();

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var state = store.GetIndexState("demo");

        state.ShouldNotBeNull();
        reported.Ignored.ShouldBe(8);
        state.Stats.IgnoredTotal.ShouldBe(reported.Ignored);
        reported.IgnoredBySource.ShouldNotBeNull();
        state.Stats.IgnoredBySource.ShouldBe(reported.IgnoredBySource);
        state.Stats.IgnoredBySource!.Values.Sum().ShouldBe(8);
        reported.IgnoredBySource!.Keys.OrderBy(key => key, StringComparer.Ordinal)
            .ShouldBe(["gitignore", "squeezrignore"]);
        reported.IgnoredBySource["gitignore"].ShouldBe(5);
        reported.IgnoredBySource["squeezrignore"].ShouldBe(3);
    }

    [Fact]
    public void IndexPass_PersistsIgnoredBySource()
    {
        var workspace = NewWorkspaceWithGitignore();
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var state = store.GetIndexState("demo");

        state.ShouldNotBeNull();
        state.Stats.IgnoredTotal.ShouldBe(2);
        state.Stats.IgnoredBySource.ShouldNotBeNull();
        state.Stats.IgnoredBySource.ShouldContainKey("gitignore");
        state.Stats.IgnoredBySource["gitignore"].ShouldBe(2);
        state.Stats.IgnoredBySource.Values.Sum().ShouldBe(state.Stats.IgnoredTotal);
    }

    [Fact]
    public void StatusAfterFileDeletion_ShowsSameIgnoredNumbers()
    {
        var workspace = NewWorkspaceWithGitignore();

        var manager = new WorkspaceManager(workspace);
        var statusBefore = manager.GetStatus();
        var repoBefore = statusBefore.Repos.ShouldHaveSingleItem();
        repoBefore.IgnoredTotal.ShouldBe(2);

        var ignoredFilePath = Path.Combine(workspace, "repo", "web", "out", "bundle.js");
        File.Delete(ignoredFilePath);
        File.Exists(ignoredFilePath).ShouldBeFalse();

        var statusAfter = manager.GetStatus();
        var repoAfter = statusAfter.Repos.ShouldHaveSingleItem();

        repoAfter.IgnoredTotal.ShouldBe(repoBefore.IgnoredTotal);
        repoAfter.IgnoredBySource.ShouldBe(repoBefore.IgnoredBySource);
    }

    [Fact]
    public void IgnoredNumbersComeFromStore_NotFromADiskWalk()
    {
        var workspace = NewWorkspaceWithGitignore();
        var manager = new WorkspaceManager(workspace);
        var before = manager.GetStatus().Repos.ShouldHaveSingleItem();
        before.IgnoredTotal.ShouldBe(2);
        before.IgnoredBySource!.ShouldBe(new Dictionary<string, int> { ["gitignore"] = 2 });

        Directory.Move(
            Path.Combine(workspace, "repo", "web"), Path.Combine(workspace, "moved-outside-the-repo"));

        var after = manager.GetStatus().Repos.ShouldHaveSingleItem();

        after.IgnoredTotal.ShouldBe(2);
        after.IgnoredBySource.ShouldBe(before.IgnoredBySource);
    }

    [Fact]
    public void OldWorkspaceWithoutBreakdown_RendersWithoutExceptions()
    {
        var workspace = NewWorkspaceWithGitignore();
        using (var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace)))
        {
            var state = store.GetIndexState("demo");
            state.ShouldNotBeNull();
            state.Stats.IgnoredTotal = 0;
            state.Stats.IgnoredBySource = null;
            store.SaveIndexState(state);
        }

        var manager = new WorkspaceManager(workspace);
        var status = manager.GetStatus();
        var repo = status.Repos.ShouldHaveSingleItem();

        repo.IgnoredTotal.ShouldBe(0);
        repo.IgnoredBySource.ShouldBeNull();
    }

    [Fact]
    public void IgnoreRuleset_DeduplicatesConfigPatternsThatMatchDefaults()
    {
        var workspace = NewWorkspaceWithMigratedDefaults();
        var config = ConfigFile.Load(workspace);
        var ruleset = Squeezr.Core.Indexing.Ignore.IgnoreRuleset.For(
            Path.Combine(workspace, "repo"), config);

        var decision = ruleset.Decision("bin/out.dll", isDirectory: false);
        decision.Excluded.ShouldBeTrue();
        decision.Source.ShouldBe(Squeezr.Core.Indexing.Ignore.IgnoreSource.Defaults);
    }

    private static string PrepareGitignoreFixture()
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init("demo");
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        manager.AddRepo("repo", "demo");

        WriteFile(workspace, "repo/src/App.cs", "class App { }");
        WriteFile(workspace, "repo/out/a.js", "var a;");
        WriteFile(workspace, "repo/out/b.js", "var b;");
        WriteFile(workspace, "repo/out/nested/c.js", "var c;");
        WriteFile(workspace, "repo/web/out/e.js", "var e;");
        WriteFile(workspace, "repo/web/out/deep/d.js", "var d;");
        WriteFile(workspace, "repo/.qwen/tmp/x.json", "{}");
        WriteFile(workspace, "repo/.qwen/tmp/y.json", "{}");
        WriteFile(workspace, "repo/.qwen/tmp/deep/z.json", "{}");
        File.WriteAllText(Path.Combine(workspace, "repo", ".gitignore"), "out/\nweb/out/\n");
        File.WriteAllText(Path.Combine(workspace, "repo", ".squeezrignore"), ".qwen/tmp/\n");
        return workspace;
    }

    private static string NewWorkspaceWithGitignore()
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init("demo");
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        manager.AddRepo("repo", "demo");

        WriteFile(workspace, "repo/a.cs", "class Alpha { }");
        WriteFile(workspace, "repo/web/out/bundle.js", "var x = 1;");
        WriteFile(workspace, "repo/web/out/vendor.js", "var y = 2;");
        File.WriteAllText(Path.Combine(workspace, "repo", ".gitignore"), "web/out/\n");

        new WorkspaceIndexer().Index(workspace);
        return workspace;
    }

    private static string NewWorkspaceWithMigratedDefaults()
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init("demo");
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        manager.AddRepo("repo", "demo");

        var defaults = SqueezrConfig.CreateDefault().Indexing.Ignore;
        var config = ConfigFile.Load(workspace);
        ConfigFile.Save(workspace, config with
        {
            Indexing = config.Indexing with { Ignore = defaults.ToList() }
        });

        WriteFile(workspace, "repo/a.cs", "class Alpha { }");
        WriteFile(workspace, "repo/bin/out.dll", "binary");

        new WorkspaceIndexer().Index(workspace);
        return workspace;
    }

    private static void WriteFile(string workspace, string relativePath, string content)
    {
        var fullPath = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
