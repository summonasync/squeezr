using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Trigram;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing;

public class WorkspaceIndexerTests
{
    [Fact]
    public void FirstIndex_PopulatesStoreAndBuildsTrigramIndex()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/src/Program.cs", "class Program { static void Main() { } }");
        WriteFile(workspace, "repo/lib/util.js", "function add(a, b) { return a + b; }");
        WriteFile(workspace, "repo/data/blob.bin", new byte[] { 9, 0, 1 });

        var result = new WorkspaceIndexer().Index(workspace);

        var repo = result.Repos.ShouldHaveSingleItem();
        repo.Name.ShouldBe("demo");
        repo.Added.ShouldBe(2);
        repo.Modified.ShouldBe(0);
        repo.Deleted.ShouldBe(0);
        repo.Skipped.ShouldBe(1);
        repo.Files.ShouldBe(2);

        File.Exists(SqueezrPaths.TrigramIndexPath(workspace)).ShouldBeTrue();

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoDoc = store.FindRepo("demo");
        repoDoc.ShouldNotBeNull();
        var files = store.ListFiles(repoDoc!.Id);
        files.Count.ShouldBe(2);
        files.ShouldAllBe(f => f.Sha256.Length == 64 && f.LineCount > 0 && f.Language != "unknown");

        var state = store.GetIndexState("demo");
        state.ShouldNotBeNull();
        state!.Stats.Files.ShouldBe(2);
        state.LastFullIndexAt.ShouldNotBeNull();

        var reader = new TrigramIndexReader(SqueezrPaths.TrigramIndexPath(workspace));
        reader.DocCount.ShouldBe(2u);
        var hash = TrigramHasher.ComputeHash('c', 'l', 'a');
        var postings = reader.Find(hash);
        postings.ShouldNotBeNull();
        postings!.DocIds.Length.ShouldBe(1);
        reader.GetDoc(postings.DocIds[0]).ShouldBe((repoDoc.Id, files.Single(f => f.Path == "src/Program.cs").Id));
    }

    [Fact]
    public void SecondIndex_WithoutChanges_ProcessesNothing()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", "class A { }");
        new WorkspaceIndexer().Index(workspace);

        var result = new WorkspaceIndexer().Index(workspace);

        var repo = result.Repos.ShouldHaveSingleItem();
        repo.Added.ShouldBe(0);
        repo.Modified.ShouldBe(0);
        repo.Deleted.ShouldBe(0);
        repo.Skipped.ShouldBe(0);
        repo.Files.ShouldBe(1);
    }

    [Fact]
    public void Index_DetectsModifiedAddedAndDeletedFiles()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", "class A { }");
        WriteFile(workspace, "repo/b.cs", "class B { }");
        new WorkspaceIndexer().Index(workspace);

        WriteFile(workspace, "repo/a.cs", "class A { int x; }");
        File.Delete(Path.Combine(workspace, "repo", "b.cs"));
        WriteFile(workspace, "repo/c.cs", "class C { }");

        var result = new WorkspaceIndexer().Index(workspace);

        var repo = result.Repos.ShouldHaveSingleItem();
        repo.Added.ShouldBe(1);
        repo.Modified.ShouldBe(1);
        repo.Deleted.ShouldBe(1);
        repo.Files.ShouldBe(2);
    }

    [Fact]
    public void FullIndex_ReprocessesAllKnownFiles()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", "class A { }");
        WriteFile(workspace, "repo/b.cs", "class B { }");
        new WorkspaceIndexer().Index(workspace);

        var result = new WorkspaceIndexer().Index(workspace, full: true);

        var repo = result.Repos.ShouldHaveSingleItem();
        repo.Added.ShouldBe(0);
        repo.Modified.ShouldBe(2);
        repo.Files.ShouldBe(2);
    }

    [Fact]
    public void Index_WithRepoFilter_IndexesOnlyThatRepo()
    {
        var workspace = TestDirs.NewTempDir();
        WriteFile(workspace, "one/a.cs", "class A { }");
        WriteFile(workspace, "two/b.cs", "class B { }");
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        manager.AddRepo("one", "one");
        manager.AddRepo("two", "two");

        var result = new WorkspaceIndexer().Index(workspace, repoName: "two");

        result.Repos.ShouldHaveSingleItem().Name.ShouldBe("two");
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        store.GetIndexState("one").ShouldBeNull();
        store.GetIndexState("two").ShouldNotBeNull();
    }

    [Fact]
    public void Index_UnknownRepo_Throws()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", "class A { }");

        Should.Throw<WorkspaceException>(() => new WorkspaceIndexer().Index(workspace, repoName: "ghost"));
    }

    [Fact]
    public void Index_WhileLocked_ThrowsPoliteError()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", "class A { }");
        using var guard = IndexLock.Acquire(SqueezrPaths.StoreDir(workspace));

        Should.Throw<WorkspaceLockedException>(() => new WorkspaceIndexer().Index(workspace));
    }

    [Fact]
    public void Index_CountsLinesWithoutTrailingNewlineBias()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/with-newline.cs", "class A {\n}\n");
        WriteFile(workspace, "repo/no-newline.cs", "class B {\n}");

        new WorkspaceIndexer().Index(workspace);

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoDoc = store.FindRepo("demo")!;
        var lineCounts = store.ListFiles(repoDoc.Id).ToDictionary(f => f.Path, f => f.LineCount);
        lineCounts["with-newline.cs"].ShouldBe(2);
        lineCounts["no-newline.cs"].ShouldBe(2);
    }

    private static string NewWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        manager.AddRepo("repo", "demo");
        return workspace;
    }

    private static void WriteFile(string workspace, string relativePath, string content)
    {
        var fullPath = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private static void WriteFile(string workspace, string relativePath, byte[] content)
    {
        var fullPath = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, content);
    }
}
