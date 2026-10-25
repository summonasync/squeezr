using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Storage;

public class MetaStoreKindCountsTests
{
    [Fact]
    public void CountChunksByKind_TalliesChunksThroughTheirFileKind()
    {
        var workspace = NewIndexedWorkspace(out var repoId, out var codeFile, out var docsFile);
        using var meta = new MetaStore(SqueezrPaths.MetaDbPath(workspace));

        var counts = meta.CountChunksByKind();

        counts["code"].ShouldBe(meta.ListChunksByFile(codeFile.Id).Count);
        counts["docs"].ShouldBe(meta.ListChunksByFile(docsFile.Id).Count);
        counts.Values.Sum().ShouldBe(
            meta.ListFiles(repoId).Sum(f => meta.ListChunksByFile(f.Id).Count));
    }

    [Fact]
    public void CountChunksByKind_LegacyRowsWithoutKind_DeriveFromLanguage()
    {
        var workspace = NewIndexedWorkspace(out _, out _, out var docsFile);
        using var meta = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var before = meta.CountChunksByKind();

        var legacy = new FileDocument
        {
            Id = docsFile.Id,
            RepoId = docsFile.RepoId,
            Path = docsFile.Path,
            Sha256 = docsFile.Sha256,
            Language = "markdown",
            Kind = "",
            SizeBytes = docsFile.SizeBytes,
            LineCount = docsFile.LineCount,
            IndexedAt = docsFile.IndexedAt
        };
        meta.UpsertFile(legacy);

        meta.CountChunksByKind().ShouldBe(before);
    }

    private static string NewIndexedWorkspace(out uint repoId, out FileDocument codeFile, out FileDocument docsFile)
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init("demo");
        manager.AddRepo("repo", "demo");
        Directory.CreateDirectory(Path.Combine(workspace, "repo", "src"));
        Directory.CreateDirectory(Path.Combine(workspace, "repo", "docs"));
        File.WriteAllText(Path.Combine(workspace, "repo", "src", "Watcher.cs"), "class Watcher\n{\n}\n");
        File.WriteAllText(Path.Combine(workspace, "repo", "docs", "Notes.md"), "# Notes\n\nhello there.\n");
        new WorkspaceIndexer().Index(workspace);

        using var meta = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        repoId = meta.FindRepo("demo")!.Id;
        var files = meta.ListFiles(repoId);
        codeFile = files.Single(f => f.Path == "src/Watcher.cs");
        docsFile = files.Single(f => f.Path == "docs/Notes.md");
        return workspace;
    }
}
