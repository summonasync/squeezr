using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing.Query;

public class SearchEngineTests
{
    private const string FixtureContent = "workspacemanager";

    [Fact]
    public void Literal_FindsMatchWithCoordinates()
    {
        var workspace = NewIndexedWorkspace();
        var engine = new SearchEngine(workspace);

        var response = engine.Search(new SearchQuery("workspacemanager", false, null, null, null, null, null, null));

        var paths = response.Matches.Select(m => m.Path).ToList();
        paths.ShouldContain("src/WorkspaceManager.cs");
        paths.ShouldContain("src/worker.cs");
        response.Truncated.ShouldBeFalse();

        var hit = response.Matches.First(m => m.Path == "src/WorkspaceManager.cs");
        hit.Repo.ShouldBe("demo");
        hit.Line.ShouldBe(1);
        hit.Snippet.ShouldContain("class WorkspaceManager");
    }

    [Fact]
    public void SmartCase_LowerQueryMatchesAnyCase_UpperQueryDoesNot_FlagForcesInsensitive()
    {
        var workspace = NewIndexedWorkspace();
        var engine = new SearchEngine(workspace);

        engine.Search(new SearchQuery("workspacemanager", false, null, null, null, null, null, null)).Matches.ShouldNotBeEmpty();

        engine.Search(new SearchQuery("Workspacemanager", false, null, null, null, null, null, null)).Matches.ShouldBeEmpty();

        engine.Search(new SearchQuery("Workspacemanager", false, false, null, null, null, null, null)).Matches.ShouldNotBeEmpty();
    }

    [Fact]
    public void ShortLiteral_FallsBackToFullScan()
    {
        var workspace = NewIndexedWorkspace();
        var engine = new SearchEngine(workspace);

        var response = engine.Search(new SearchQuery("Wo", false, null, null, null, null, null, null));

        response.Matches.ShouldHaveSingleItem().Path.ShouldBe("src/WorkspaceManager.cs");
    }

    [Fact]
    public void Regex_WithExtractableTrigrams_FindsMatches()
    {
        var workspace = NewIndexedWorkspace();
        var engine = new SearchEngine(workspace);

        var response = engine.Search(new SearchQuery("W.rkspaceM.nager", true, null, null, null, null, null, null));

        response.Matches.ShouldHaveSingleItem().Path.ShouldBe("src/WorkspaceManager.cs");
    }

    [Fact]
    public void Regex_WithoutTrigrams_DegradesToFullScan()
    {
        var workspace = NewIndexedWorkspace();
        var engine = new SearchEngine(workspace);

        var response = engine.Search(new SearchQuery("\\d+", true, null, null, null, null, null, null));

        response.Matches.ShouldBeEmpty();
    }

    [Fact]
    public void Filters_NarrowResults()
    {
        var workspace = NewIndexedWorkspace(addSecondRepo: true);
        var engine = new SearchEngine(workspace);
        var baseQuery = new SearchQuery("workspacemanager", false, null, null, null, null, null, null);

        engine.Search(baseQuery with { Repo = "other" }).Matches.ShouldHaveSingleItem().Repo.ShouldBe("other");
        engine.Search(baseQuery with { PathGlob = "**/lib/**" }).Matches.ShouldHaveSingleItem().Path.ShouldBe("lib/util.js");
        engine.Search(baseQuery with { Language = "markdown" }).Matches.ShouldHaveSingleItem().Path.ShouldBe("docs/notes.md");
    }

    [Fact]
    public void Ranking_FilenameMatchBeatsBody_TestFilesAreDemoted()
    {
        var workspace = NewIndexedWorkspace();
        var engine = new SearchEngine(workspace);

        var response = engine.Search(new SearchQuery("workspacemanager", false, null, null, null, null, null, null));

        var paths = response.Matches.Select(m => m.Path).ToList();
        paths[0].ShouldBe("src/WorkspaceManager.cs");
        paths[^1].ShouldBe("src/WidgetTests.cs");
    }

    [Fact]
    public void TopK_LimitsResultCount()
    {
        var workspace = NewIndexedWorkspace();
        var engine = new SearchEngine(workspace);

        var response = engine.Search(new SearchQuery("workspacemanager", false, null, null, null, null, 2, null));

        response.Matches.Count.ShouldBe(2);
    }

    [Fact]
    public void MaxTokens_TruncatesWithinBudget()
    {
        var workspace = NewIndexedWorkspace();
        var engine = new SearchEngine(workspace);

        var response = engine.Search(new SearchQuery("workspacemanager", false, null, null, null, null, null, 15));

        response.Matches.ShouldHaveSingleItem();
        response.TokensUsed.ShouldBeLessThanOrEqualTo(15);
        response.Truncated.ShouldBeTrue();
    }

    [Fact]
    public void Search_WithoutIndex_ThrowsFriendlyError()
    {
        var workspace = TestDirs.NewTempDir();

        Should.Throw<WorkspaceException>(() =>
            new SearchEngine(workspace).Search(new SearchQuery("anything", false, null, null, null, null, null, null)));
    }

    [Fact]
    public void Ranking_ContestsFileIsNotDemoted_TestsSuffixIs()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        WriteFile(workspace, "repo/src/Contests.cs", $"class Contests {{ /* {FixtureContent} */ }}\n");
        WriteFile(workspace, "repo/src/FooTests.cs", $"class FooTests {{ /* {FixtureContent} */ }}\n");
        manager.AddRepo("repo", "demo");
        new WorkspaceIndexer().Index(workspace);

        var engine = new SearchEngine(workspace);
        var response = engine.Search(new SearchQuery(FixtureContent, false, null, null, null, null, null, null));

        var paths = response.Matches.Select(m => m.Path).ToList();
        paths[0].ShouldBe("src/Contests.cs");
        paths[^1].ShouldBe("src/FooTests.cs");
        response.Matches.First(m => m.Path == "src/Contests.cs").Score
            .ShouldBeGreaterThan(response.Matches.First(m => m.Path == "src/FooTests.cs").Score);
    }

    [Fact]
    public void Snippet_LongLineIsTruncatedWithMarker()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        var longLine = $"// {new string('x', 250)} {FixtureContent} {new string('y', 250)}";
        WriteFile(workspace, "repo/a.cs", longLine + "\n");
        manager.AddRepo("repo", "demo");
        new WorkspaceIndexer().Index(workspace);

        var engine = new SearchEngine(workspace);
        var match = engine.Search(new SearchQuery(FixtureContent, false, null, null, null, null, null, null))
            .Matches.ShouldHaveSingleItem();

        match.Snippet.ShouldEndWith("…");
        match.Snippet.Length.ShouldBe(201);
    }

    [Fact]
    public void Regex_InvalidPattern_ThrowsFriendlyError()
    {
        var workspace = NewIndexedWorkspace();
        var engine = new SearchEngine(workspace);

        var ex = Should.Throw<WorkspaceException>(() =>
            engine.Search(new SearchQuery("[", true, null, null, null, null, null, null)));

        ex.Message.ShouldContain("Invalid regex pattern");
    }

    [Fact]
    public void Regex_CatastrophicBacktracking_SkipsCandidateWithoutCrashing()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        WriteFile(workspace, "repo/padded.cs", new string('a', 1500) + "X");
        manager.AddRepo("repo", "demo");
        new WorkspaceIndexer().Index(workspace);

        var engine = new SearchEngine(workspace);
        var response = engine.Search(new SearchQuery("(a+)+$", true, null, null, null, null, null, null));

        response.Matches.ShouldBeEmpty();
    }

    private static string NewIndexedWorkspace(bool addSecondRepo = false)
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        WriteFile(workspace, "repo/src/WorkspaceManager.cs", "public class WorkspaceManager\n{\n}\n");
        WriteFile(workspace, "repo/src/worker.cs", $"// uses {FixtureContent} internally\nvar helper = \"{FixtureContent}\";\n");
        WriteFile(workspace, "repo/src/WidgetTests.cs", $"class WidgetTests {{ /* {FixtureContent} */ }}\n");
        WriteFile(workspace, "repo/lib/util.js", $"function {FixtureContent}Helper() {{ }}\n");
        WriteFile(workspace, "repo/docs/notes.md", $"notes about {FixtureContent}\n");
        manager.AddRepo("repo", "demo");

        if (addSecondRepo)
        {
            WriteFile(workspace, "other/clone.cs", $"class Clone {{ /* {FixtureContent} */ }}\n");
            manager.AddRepo("other", "other");
        }

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
