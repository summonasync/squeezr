using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Indexing.Trigram;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing;

public class IncrementalIndexTests
{
    [Fact]
    public void ModifiedFile_AppendsSegment_SearchSeesNewContentNotOld()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"oldneedle\"; }");
        new WorkspaceIndexer().Index(workspace);
        var sizeBefore = IndexSize(workspace);

        WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"newneedle\"; }");
        var result = new WorkspaceIndexer().Index(workspace);

        var repo = result.Repos.ShouldHaveSingleItem();
        repo.Modified.ShouldBe(1);
        IndexSize(workspace).ShouldBeGreaterThan(sizeBefore);

        var engine = new SearchEngine(workspace);
        engine.Search(Query("oldneedle")).Matches.ShouldBeEmpty();
        engine.Search(Query("newneedle")).Matches.ShouldHaveSingleItem().Path.ShouldBe("a.cs");
    }

    [Fact]
    public void SecondIndex_WithoutChanges_AppendsNothing()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"stabileneedle\"; }");
        new WorkspaceIndexer().Index(workspace);
        var sizeBefore = IndexSize(workspace);

        new WorkspaceIndexer().Index(workspace);

        IndexSize(workspace).ShouldBe(sizeBefore);
    }

    [Fact]
    public void DeletedFile_Tombstoned_RemovedFromStoreAndSearch()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"goneedle\"; }");
        WriteFile(workspace, "repo/b.cs", "class Beta { }");
        new WorkspaceIndexer().Index(workspace);

        File.Delete(Path.Combine(workspace, "repo", "a.cs"));
        var result = new WorkspaceIndexer().Index(workspace);

        result.Repos.ShouldHaveSingleItem().Deleted.ShouldBe(1);
        new SearchEngine(workspace).Search(Query("goneedle")).Matches.ShouldBeEmpty();

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoDoc = store.FindRepo("demo")!;
        store.ListFiles(repoDoc.Id).ShouldHaveSingleItem().Path.ShouldBe("b.cs");
    }

    [Fact]
    public void HighTombstoneRatio_EmitsCompactHint()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", Large("firstneedle"));
        new WorkspaceIndexer().Index(workspace);

        WriteFile(workspace, "repo/a.cs", Large("secondneedle"));
        var result = new WorkspaceIndexer().Index(workspace);

        result.Warnings.ShouldContain(w => w.Contains("squeezr compact"));
    }

    [Fact]
    public void FullIndex_AfterIncrements_RewritesWithoutTombstones()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", Large("firstneedle"));
        new WorkspaceIndexer().Index(workspace);
        WriteFile(workspace, "repo/a.cs", Large("secondneedle"));
        new WorkspaceIndexer().Index(workspace);

        new WorkspaceIndexer().Index(workspace, full: true);

        using var reader = new TrigramIndexReader(SqueezrPaths.TrigramIndexPath(workspace));
        reader.SegmentStats!.TombstonedSegments.ShouldBe(0);
        reader.SegmentStats.LiveSegments.ShouldBe(1);
    }

    [Fact]
    public void Compact_DropsTombstones_ShrinksFile_PreservesResults()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", Large("keepneedle"));
        WriteFile(workspace, "repo/b.cs", Large("targetneedle"));
        new WorkspaceIndexer().Index(workspace);
        WriteFile(workspace, "repo/b.cs", Large("rewrittenneedle"));
        new WorkspaceIndexer().Index(workspace);
        File.Delete(Path.Combine(workspace, "repo", "a.cs"));
        new WorkspaceIndexer().Index(workspace);

        var engine = new SearchEngine(workspace);
        var beforeKeep = engine.Search(Query("keepneedle")).Matches;
        var beforeNew = engine.Search(Query("rewrittenneedle")).Matches;
        beforeKeep.ShouldBeEmpty();
        beforeNew.ShouldNotBeEmpty();

        var result = WorkspaceCompactor.Compact(workspace);

        result.SegmentsDropped.ShouldBeGreaterThan(0);
        result.SizeAfterBytes.ShouldBeLessThan(result.SizeBeforeBytes);

        using (var reader = new TrigramIndexReader(SqueezrPaths.TrigramIndexPath(workspace)))
        {
            reader.SegmentStats!.TombstonedSegments.ShouldBe(0);
        }

        var afterEngine = new SearchEngine(workspace);
        afterEngine.Search(Query("keepneedle")).Matches.ShouldBeEmpty();
        afterEngine.Search(Query("rewrittenneedle")).Matches.Select(Signature)
            .ShouldBe(beforeNew.Select(Signature));
    }

    [Fact]
    public void Compact_WithoutTombstones_PreservesResults()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"quietneedle\"; }");
        new WorkspaceIndexer().Index(workspace);

        var result = WorkspaceCompactor.Compact(workspace);

        result.SegmentsDropped.ShouldBe(0);
        new SearchEngine(workspace).Search(Query("quietneedle")).Matches.ShouldHaveSingleItem();
    }

    [Fact]
    public void Compact_WithoutIndex_ThrowsFriendlyError()
    {
        var workspace = NewWorkspace();

        Should.Throw<WorkspaceException>(() => WorkspaceCompactor.Compact(workspace))
            .Message.ShouldContain("squeezr index");
    }

    private static string Signature(SearchMatch match) => $"{match.Repo}|{match.Path}|{match.Line}|{match.Snippet}";

    private static SearchQuery Query(string text) => new(text, false, null, null, null, null, null, null);

    private static long IndexSize(string workspace) => new FileInfo(SqueezrPaths.TrigramIndexPath(workspace)).Length;

    private static string Large(string needle) =>
        string.Join("\n", Enumerable.Range(0, 40).Select(i => $"// filler line {i} padding padding padding")) + "\n" + needle + "\n";

    private static string NewWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        manager.AddRepo("repo", "demo");
        return workspace;
    }

    private static void WriteFile(string workspace, string relativePath, string content)
    {
        var fullPath = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
