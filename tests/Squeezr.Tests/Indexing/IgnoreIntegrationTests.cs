using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Ignore;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Semantic.Configuration;

namespace Squeezr.Tests.Indexing;

public class IgnoreIntegrationTests
{
    [Fact]
    public void TwoPass_GitignoreAdded_SecondPass_RemovesFileFromIndex()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/src/keep.cs", "class Keep { }");
        WriteFile(workspace, "repo/src/remove.cs", "class Remove { }");

        var firstResult = new WorkspaceIndexer().Index(workspace);
        firstResult.Repos.ShouldHaveSingleItem().Added.ShouldBe(2);

        WriteFile(workspace, "repo/.gitignore", "src/remove.cs\n");

        var secondResult = new WorkspaceIndexer().Index(workspace);
        var repo = secondResult.Repos.ShouldHaveSingleItem();
        repo.Deleted.ShouldBe(1);
        repo.Ignored.ShouldBe(1);

        using (var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace)))
        {
            var repoDoc = store.FindRepo("demo");
            repoDoc.ShouldNotBeNull();
            var files = store.ListFiles(repoDoc!.Id).Select(f => f.Path).OrderBy(p => p).ToList();
            files.ShouldBe([".gitignore", "src/keep.cs"]);
        }
    }

    [Fact]
    public void TwoPass_DirGitignore_SecondPass_RemovesWholeSubtree()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/src/keep.cs", "class Keep { }");
        WriteFile(workspace, "repo/build/gen/a.js", "var a;");
        WriteFile(workspace, "repo/build/gen/b.js", "var b;");
        WriteFile(workspace, "repo/build/src/app.js", "function app() { }");

        var firstResult = new WorkspaceIndexer().Index(workspace);
        firstResult.Repos.ShouldHaveSingleItem().Added.ShouldBe(4);

        WriteFile(workspace, "repo/build/.gitignore", "gen/\n");

        var secondResult = new WorkspaceIndexer().Index(workspace);
        var repo = secondResult.Repos.ShouldHaveSingleItem();
        repo.Deleted.ShouldBe(2);
        repo.Ignored.ShouldBe(2);

        using (var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace)))
        {
            var repoDoc = store.FindRepo("demo");
            repoDoc.ShouldNotBeNull();
            var files = store.ListFiles(repoDoc!.Id).Select(f => f.Path).OrderBy(p => p).ToList();
            files.ShouldBe(["build/.gitignore", "build/src/app.js", "src/keep.cs"]);
        }
    }

    [Fact]
    public void IndexFile_AppliesCascadeAlongTheFilesOwnChain()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/src/a/b/tool.cs", "class Tool { }");
        WriteFile(workspace, "repo/src/keep.cs", "class Keep { }");
        new WorkspaceIndexer().Index(workspace);

        WriteFile(workspace, "repo/src/a/b/.gitignore", "*.cs\n");
        new WorkspaceIndexer().IndexFile(workspace, "demo", "src/a/b/tool.cs").ShouldBeNull();

        WriteFile(workspace, "repo/src/other/.gitignore", "*.cs\n");
        new WorkspaceIndexer().IndexFile(workspace, "demo", "src/keep.cs")
            .ShouldNotBeNull()
            .Path.ShouldBe("src/keep.cs");

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoDoc = store.FindRepo("demo");
        repoDoc.ShouldNotBeNull();
        store.ListFiles(repoDoc!.Id).Select(f => f.Path).OrderBy(p => p, StringComparer.Ordinal)
            .ShouldBe(["src/keep.cs"]);
    }

    [Fact]
    public void RespectGitignoreFalse_DoesNotRemovePreviouslyIndexedFiles()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/src/keep.cs", "class Keep { }");
        WriteFile(workspace, "repo/build/gen/a.js", "var a;");

        var firstResult = new WorkspaceIndexer().Index(workspace);
        firstResult.Repos.ShouldHaveSingleItem().Added.ShouldBe(2);

        WriteFile(workspace, "repo/.gitignore", "build/gen/\n");

        var config = SemanticConfigFile.Load(workspace);
        SemanticConfigFile.Save(workspace, config with
        {
            Indexing = config.Indexing with { RespectGitignore = false }
        });

        var secondResult = new WorkspaceIndexer().Index(workspace);
        var repo = secondResult.Repos.ShouldHaveSingleItem();
        repo.Deleted.ShouldBe(0);
        repo.Ignored.ShouldBe(0);

        using (var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace)))
        {
            var repoDoc = store.FindRepo("demo");
            repoDoc.ShouldNotBeNull();
            store.ListFiles(repoDoc!.Id).Count.ShouldBe(3);
        }
    }

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
