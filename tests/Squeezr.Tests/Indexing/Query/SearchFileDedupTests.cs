using System.Security.Cryptography;
using System.Text;
using Shouldly;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing.Query;

public class SearchFileDedupTests
{
    private const string Unmatched = "zzznotfound";

    [Fact]
    public void Hybrid_SeveralSpansOfOneFile_LeaveOneHitCarryingTheCount()
    {
        var workspace = NewIndexedWorkspace(out var docChunks, out var calcChunk, out var repoId);
        var store = VectorStore(repoId, docChunks, calcChunk);

        var response = Hybrid(workspace, store);

        response.Matches.Count.ShouldBe(2);
        var doc = response.Matches[0];
        doc.Path.ShouldBe("docs/Guide.md");
        doc.Line.ShouldBe(docChunks[0].StartLine);
        doc.AlsoMatched.ShouldBe(2);
        response.Matches[1].Path.ShouldBe("src/Calc.cs");
        response.Matches[1].AlsoMatched.ShouldBe(0);
    }

    [Fact]
    public void Hybrid_TopK_BuysDistinctFiles_NotExtraSpansOfOneFile()
    {
        var workspace = NewIndexedWorkspace(out var docChunks, out var calcChunk, out var repoId);
        var store = VectorStore(repoId, docChunks, calcChunk);

        var response = Hybrid(workspace, store, topK: 2);

        response.Matches.Select(m => m.Path).ShouldBe(["docs/Guide.md", "src/Calc.cs"]);
    }

    [Fact]
    public void Hybrid_ExactAndSemanticSpansOfOneFile_StillCollapseToOneHit()
    {
        var workspace = NewIndexedWorkspace(out var docChunks, out var calcChunk, out var repoId);
        var store = VectorStore(repoId, docChunks, calcChunk);

        var response = Hybrid(workspace, store, query: "migration guide");

        var doc = response.Matches.Single(m => m.Path == "docs/Guide.md");
        doc.AlsoMatched.ShouldBeGreaterThanOrEqualTo(1);
        response.Matches.Count(m => m.Path == "docs/Guide.md").ShouldBe(1);
    }

    private static SearchResponse Hybrid(
        string workspace, FakeVectorStore store, int? topK = null, string? query = null) =>
        new SearchEngine(workspace).SearchHybridAsync(
            new SearchQuery(query ?? Unmatched, false, null, null, null, null, topK, null),
            new FakeEmbeddingProvider(),
            store,
            CancellationToken.None).GetAwaiter().GetResult();

    private static FakeVectorStore VectorStore(uint repoId, List<ChunkDocument> docChunks, ChunkDocument calcChunk) =>
        new()
        {
            // the doc file owns the top two ranks, so a pre-collapse topK of 2 would spend both on it
            Hits =
            [
                Point(docChunks[0], repoId, 1, 0.95),
                Point(docChunks[1], repoId, 2, 0.90),
                Point(calcChunk, repoId, 3, 0.50),
                Point(docChunks[2], repoId, 4, 0.40)
            ]
        };

    private static VectorHit Point(ChunkDocument chunk, uint repoId, ulong id, double score) =>
        new(id, chunk.ChunkHash, chunk.FileId, repoId, chunk.StartLine, chunk.EndLine, score);

    private static string NewIndexedWorkspace(
        out List<ChunkDocument> docChunks, out ChunkDocument calcChunk, out uint repoId)
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        TestWorkspaceConfig.UseOllamaEmbeddings(workspace);
        manager.AddRepo("repo", "demo");

        WriteFile(workspace, "repo/docs/Guide.md",
            string.Join("\n", Enumerable.Range(1, 200).Select(i => $"line {i} of the migration guide")) + "\n");
        WriteFile(workspace, "repo/src/Calc.cs", """
            public class Calc
            {
                public int Add(int a, int b) => a + b;
            }
            """);

        new WorkspaceIndexer().Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: new FakeVectorStore());

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        repoId = store.FindRepo("demo")!.Id;
        var files = store.ListFiles(repoId);
        docChunks = store.ListChunksByFile(files.Single(f => f.Path == "docs/Guide.md").Id)
            .OrderBy(c => c.StartLine).ToList();
        calcChunk = store.ListChunksByFile(files.Single(f => f.Path == "src/Calc.cs").Id)
            .OrderBy(c => c.StartLine).First();

        docChunks.Count.ShouldBeGreaterThanOrEqualTo(3);
        return workspace;
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
        public IReadOnlyList<VectorHit> Hits { get; init; } = [];

        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

        public Task EnsureCollectionAsync(int dimensions, CancellationToken ct) => Task.CompletedTask;

        public Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<VectorHit>> SearchAsync(float[] vector, int topK, VectorSearchFilter filter, CancellationToken ct) =>
            Task.FromResult(Hits);

        public Task<long> CountAsync(CancellationToken ct) => Task.FromResult((long)Hits.Count);

        public Task<IReadOnlyList<ulong>> ScrollPointIdsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ulong>>([]);

        public Task<IReadOnlyList<uint>> ScrollFileIdsWithoutKindAsync(uint repoId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<uint>>([]);

        public Task SetKindPayloadsAsync(IReadOnlyDictionary<uint, string> kindByFileId, CancellationToken ct) =>
            Task.CompletedTask;

        public Task DeleteByFileIdsAsync(IReadOnlyList<uint> fileIds, CancellationToken ct) => Task.CompletedTask;

        public Task DeleteByPointIdsAsync(IReadOnlyList<ulong> pointIds, CancellationToken ct) => Task.CompletedTask;

        public Task DeleteCollectionAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
