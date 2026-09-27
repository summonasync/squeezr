using System.Security.Cryptography;
using System.Text;
using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing;

public class FallbackChunkerTests
{
    private static readonly ChunkingConfig Config = new()
    {
        MaxTokensPerChunk = 512,
        FallbackWindowLines = 10,
        FallbackOverlapLines = 2,
    };

    [Fact]
    public void Chunk_ShortFile_SingleChunkCoveringAllLines()
    {
        var chunks = FallbackChunker.Chunk(Lines(3, "short"), Config);

        var chunk = chunks.ShouldHaveSingleItem();
        chunk.SymbolId.ShouldBeNull();
        chunk.Span.ShouldBe(new CodeSpan(1, 3));
        chunk.TokenCount.ShouldBeGreaterThan(0);
        chunk.ChunkHash.ShouldMatch("^[0-9A-F]{64}$");
    }

    [Fact]
    public void Chunk_ExactlyWindow_SingleChunk()
    {
        var chunks = FallbackChunker.Chunk(Lines(10, "exact"), Config);

        var chunk = chunks.ShouldHaveSingleItem();
        chunk.Span.ShouldBe(new CodeSpan(1, 10));
    }

    [Fact]
    public void Chunk_LongFile_WindowsOverlapAndCoverTail()
    {
        var chunks = FallbackChunker.Chunk(Lines(25, "row"), Config);

        chunks.Count.ShouldBe(3);
        chunks[0].Span.ShouldBe(new CodeSpan(1, 10));
        chunks[1].Span.ShouldBe(new CodeSpan(9, 18));
        chunks[2].Span.ShouldBe(new CodeSpan(17, 25));
        chunks[1].Text.ShouldStartWith("row 9");
        chunks[2].Text.ShouldEndWith("row 25");
    }

    [Fact]
    public void Chunk_WindowOverTokenBudget_SplitsIntoSubChunks()
    {
        var config = Config with { MaxTokensPerChunk = 15 };
        var chunks = FallbackChunker.Chunk(Lines(6, new string('x', 40)), config);

        chunks.Count.ShouldBe(6);
        chunks.ShouldAllBe(c => c.Span.StartLine == c.Span.EndLine);
        chunks.ShouldAllBe(c => c.TokenCount <= 15);
    }

    [Fact]
    public void Index_NonCSharpTextFiles_GetPendingChunks()
    {
        var workspace = NewWorkspaceWithTextFiles();

        new WorkspaceIndexer().Index(workspace);

        var chunks = ListChunks(workspace);
        chunks.Count.ShouldBe(3);
        chunks.ShouldAllBe(c => c.EmbeddingStatus == "pending");
        chunks.ShouldAllBe(c => c.SymbolId == null);
        ChunksForPath(workspace, "docs/guide.md").ShouldHaveSingleItem();
        ChunksForPath(workspace, "app/settings.yml").ShouldHaveSingleItem();
        ChunksForPath(workspace, "pkg/manifest.json").ShouldHaveSingleItem();
    }

    [Fact]
    public void Index_WithEmbeddings_TextChunksBecomeOk()
    {
        var workspace = NewWorkspaceWithTextFiles();

        new WorkspaceIndexer().Index(
            workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: new FakeVectorStore());

        ListChunks(workspace).ShouldAllBe(
            c => c.EmbeddingStatus == "ok" && c.ModelKey == "onnx/nomic-embed-text-v1.5.onnx/768/nomic-embed-text-v1.5");
    }

    [Fact]
    public void Index_IncrementalEdit_RecomputesChunks()
    {
        var workspace = NewWorkspaceWithTextFiles();
        var indexer = new WorkspaceIndexer();
        indexer.Index(workspace);

        WriteFile(workspace, "repo/docs/guide.md", "intro\nbeta marker\n" + Lines(18, "md line"));
        indexer.Index(workspace);

        var chunk = ChunksForPath(workspace, "docs/guide.md").ShouldHaveSingleItem();
        chunk.Text.ShouldContain("beta marker");
    }

    [Fact]
    public void Index_DeletedFile_RemovesItsChunks()
    {
        var workspace = NewWorkspaceWithTextFiles();
        var indexer = new WorkspaceIndexer();
        indexer.Index(workspace);

        File.Delete(Path.Combine(workspace, "repo", "docs", "guide.md"));
        indexer.Index(workspace);

        ListChunks(workspace).Count.ShouldBe(2);
    }

    [Fact]
    public void IndexFile_MarkdownFile_RecomputesFallbackChunks()
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init("demo");
        manager.AddRepo("repo", "demo");
        WriteFile(workspace, "repo/notes.md", Lines(15, "note"));
        var indexer = new WorkspaceIndexer();
        indexer.Index(workspace);

        WriteFile(workspace, "repo/notes.md", "intro\n" + Lines(20, "fresh"));
        indexer.IndexFile(workspace, "demo", "notes.md");

        var chunk = ChunksForPath(workspace, "notes.md").ShouldHaveSingleItem();
        chunk.SymbolId.ShouldBeNull();
        chunk.EmbeddingStatus.ShouldBe("pending");
        chunk.StartLine.ShouldBe(1);
        chunk.EndLine.ShouldBe(21);
        chunk.Text.ShouldContain("fresh 20");
        chunk.Text.ShouldNotContain("note 15");
    }

    [Fact]
    public void Index_CSharpFile_KeepsSymbolChunksWithoutFallbackDuplicates()
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init("demo");
        manager.AddRepo("repo", "demo");
        WriteFile(workspace, "repo/a.cs", "class Calc {\n    int Add(int a, int b) {\n        return a + b;\n    }\n}\n");
        WriteFile(workspace, "repo/notes.md", Lines(5, "note"));

        new WorkspaceIndexer().Index(workspace);

        ChunksForPath(workspace, "notes.md").ShouldAllBe(c => c.SymbolId == null);
        ChunksForPath(workspace, "a.cs").ShouldAllBe(c => c.SymbolId != null);
    }

    private static string Lines(int count, string prefix) =>
        string.Join("\n", Enumerable.Range(1, count).Select(i => $"{prefix} {i}"));

    private static string NewWorkspaceWithTextFiles()
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init("demo");
        TestWorkspaceConfig.UseOnnxEmbeddings(workspace);
        manager.AddRepo("repo", "demo");
        WriteFile(workspace, "repo/docs/guide.md", Lines(20, "md line"));
        WriteFile(workspace, "repo/app/settings.yml", Lines(5, "yml line"));
        WriteFile(workspace, "repo/pkg/manifest.json", Lines(3, "json line"));
        return workspace;
    }

    private static List<ChunkDocument> ListChunks(string workspace)
    {
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoId = store.FindRepo("demo")!.Id;
        return store.ListFiles(repoId).SelectMany(f => store.ListChunksByFile(f.Id)).ToList();
    }

    private static List<ChunkDocument> ChunksForPath(string workspace, string relativePath)
    {
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoId = store.FindRepo("demo")!.Id;
        var file = store.ListFiles(repoId).Single(f => f.Path == relativePath);
        return store.ListChunksByFile(file.Id).ToList();
    }

    private static void WriteFile(string workspace, string relativePath, string content)
    {
        var fullPath = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private sealed class FakeEmbeddingProvider : IEmbeddingProvider
    {
        public int Dimensions => 4;

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbeddingUsage usage, CancellationToken ct) =>
            Task.FromResult(texts.Select(text =>
            {
                var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
                return new[] { bytes[0] / 255f, bytes[1] / 255f, bytes[2] / 255f, bytes[3] / 255f };
            }).ToArray());
    }

    private sealed class FakeVectorStore : IVectorStore
    {
        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

        public Task EnsureCollectionAsync(int dimensions, CancellationToken ct) => Task.CompletedTask;

        public Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<VectorHit>> SearchAsync(float[] vector, int topK, uint? repoId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<long> CountAsync(CancellationToken ct) => Task.FromResult(0L);

        public Task<IReadOnlyList<ulong>> ScrollPointIdsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ulong>>([]);

        public Task DeleteByFileIdsAsync(IReadOnlyList<uint> fileIds, CancellationToken ct) => Task.CompletedTask;

        public Task DeleteByPointIdsAsync(IReadOnlyList<ulong> pointIds, CancellationToken ct) => Task.CompletedTask;

        public Task DeleteCollectionAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
