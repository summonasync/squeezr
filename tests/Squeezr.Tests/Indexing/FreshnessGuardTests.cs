using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing;

public class FreshnessGuardTests
{
    [Fact]
    public void UnchangedFile_LeavesIndexUntouched()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"stableneedle\"; }");
        new WorkspaceIndexer().Index(workspace);
        var sizeBefore = new FileInfo(SqueezrPaths.TrigramIndexPath(workspace)).Length;
        var indexedAtBefore = IndexedAt(workspace, "a.cs");

        var result = FreshnessGuard.CheckAndRefresh(workspace, [("demo", "a.cs")]);

        result.Refreshed.ShouldBeEmpty();
        result.Stale.ShouldBeEmpty();
        new FileInfo(SqueezrPaths.TrigramIndexPath(workspace)).Length.ShouldBe(sizeBefore);
        IndexedAt(workspace, "a.cs").ShouldBe(indexedAtBefore);
    }

    [Fact]
    public void TouchedFile_SameContent_ServedFromIndexWithoutReindex()
    {
        var workspace = NewWorkspace();
        var fullPath = Path.Combine(workspace, "repo", "a.cs");
        WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"touchneedle\"; }");
        new WorkspaceIndexer().Index(workspace);
        var indexedAtBefore = IndexedAt(workspace, "a.cs");
        File.SetLastWriteTimeUtc(fullPath, DateTime.UtcNow.AddMinutes(5));

        var result = FreshnessGuard.CheckAndRefresh(workspace, [("demo", "a.cs")]);

        result.Refreshed.ShouldBeEmpty();
        result.Stale.ShouldBeEmpty();
        IndexedAt(workspace, "a.cs").ShouldBe(indexedAtBefore);
    }

    [Fact]
    public void ModifiedFile_IsRefreshedAndNewContentIsSearchable()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"oldneedle\"; }");
        new WorkspaceIndexer().Index(workspace);

        WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"freshneedle\"; }");
        var result = FreshnessGuard.CheckAndRefresh(workspace, [("demo", "a.cs")]);

        result.Refreshed.ShouldBe(["demo/a.cs"]);
        result.Stale.ShouldBeEmpty();

        var engine = new SearchEngine(workspace);
        engine.Search(Query("oldneedle")).Matches.ShouldBeEmpty();
        engine.Search(Query("freshneedle")).Matches.ShouldHaveSingleItem().Path.ShouldBe("a.cs");
    }

    [Fact]
    public void DeletedFile_IsRemovedFromIndex()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"goneneedle\"; }");
        WriteFile(workspace, "repo/b.cs", "class Beta { }");
        new WorkspaceIndexer().Index(workspace);

        File.Delete(Path.Combine(workspace, "repo", "a.cs"));
        var result = FreshnessGuard.CheckAndRefresh(workspace, [("demo", "a.cs")]);

        result.Refreshed.ShouldBe(["demo/a.cs"]);
        result.Stale.ShouldBeEmpty();
        new SearchEngine(workspace).Search(Query("goneneedle")).Matches.ShouldBeEmpty();
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoDoc = store.FindRepo("demo")!;
        store.ListFiles(repoDoc.Id).ShouldHaveSingleItem().Path.ShouldBe("b.cs");
    }

    [Fact]
    public void LockedIndex_FileMarkedStale_NoException()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"lockneedle\"; }");
        new WorkspaceIndexer().Index(workspace);
        WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"changedneedle\"; }");

        using (IndexLock.Acquire(SqueezrPaths.StoreDir(workspace)))
        {
            var result = FreshnessGuard.CheckAndRefresh(workspace, [("demo", "a.cs")]);

            result.Refreshed.ShouldBeEmpty();
            result.Stale.ShouldBe(["demo/a.cs"]);
        }

        var after = FreshnessGuard.CheckAndRefresh(workspace, [("demo", "a.cs")]);
        after.Refreshed.ShouldBe(["demo/a.cs"]);
        after.Stale.ShouldBeEmpty();
    }

    [Fact]
    public void DeletedFile_WithLockHeld_MarkedStale_IndexUntouched()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"lockedgone\"; }");
        new WorkspaceIndexer().Index(workspace);
        File.Delete(Path.Combine(workspace, "repo", "a.cs"));

        using (IndexLock.Acquire(SqueezrPaths.StoreDir(workspace)))
        {
            var result = FreshnessGuard.CheckAndRefresh(workspace, [("demo", "a.cs")]);

            result.Refreshed.ShouldBeEmpty();
            result.Stale.ShouldBe(["demo/a.cs"]);
        }

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        store.FindRepo("demo")!.ShouldNotBeNull();
        store.ListFiles(store.FindRepo("demo")!.Id).ShouldHaveSingleItem().Path.ShouldBe("a.cs");
    }

    [Fact]
    public void CandidateMissingFromIndex_GetsIndexed()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", "class Alpha { }");
        new WorkspaceIndexer().Index(workspace);
        WriteFile(workspace, "repo/new.cs", "class Fresh { string Term = \"newneedle\"; }");

        var result = FreshnessGuard.CheckAndRefresh(workspace, [("demo", "new.cs")]);

        result.Refreshed.ShouldBe(["demo/new.cs"]);
        result.Stale.ShouldBeEmpty();
        new SearchEngine(workspace).Search(Query("newneedle")).Matches.ShouldHaveSingleItem().Path.ShouldBe("new.cs");
    }

    private static DateTime IndexedAt(string workspace, string path)
    {
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoDoc = store.FindRepo("demo")!;
        return store.ListFiles(repoDoc.Id).First(f => f.Path == path).IndexedAt;
    }

    private static SearchQuery Query(string text) => new(text, false, null, null, null, null, null, null);

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
