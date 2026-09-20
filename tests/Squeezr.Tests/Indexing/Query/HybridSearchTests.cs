using System.Security.Cryptography;
using System.Text;
using Shouldly;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing.Query;

public class HybridSearchTests
{
    private const string Source = """
        class Calc
        {
            int Add(int a, int b)
            {
                return a + b;
            }
        }
        """;

    [Fact]
    public void Merge_KnownRanks_ProduceRrfScoresAndOrder()
    {
        SearchMatch Hit(string path) => new("demo", path, 1, 0.0, "snippet");
        var exact = new List<(string Key, SearchMatch Match)>
        {
            ("1:a", Hit("a.cs")),
            ("1:b", Hit("b.cs")),
            ("1:c", Hit("c.cs"))
        };
        var semantic = new List<(string Key, SearchMatch Match)>
        {
            ("1:c", Hit("c.cs")),
            ("1:d", Hit("d.cs"))
        };

        var merged = RrfMerger.Merge(exact, semantic);

        merged.Select(m => m.Path).ShouldBe(["c.cs", "a.cs", "b.cs", "d.cs"]);
        merged[0].Source.ShouldBe("both");
        merged[0].Score.ShouldBe(1.0 / 61 + 1.0 / 63, 1e-9);
        merged[1].Score.ShouldBe(1.0 / 61, 1e-9);
        merged[1].Source.ShouldBe("exact");
        merged[2].Score.ShouldBe(1.0 / 62, 1e-9);
        merged[3].Score.ShouldBe(1.0 / 62, 1e-9);
        merged[3].Source.ShouldBe("semantic");
    }

    [Fact]
    public void Hybrid_HitInBothLists_MergesToBothAndOutranksSingles()
    {
        var workspace = NewIndexedWorkspace(out var chunks, out var repoId);
        var fileId = chunks.First().FileId;
        var chunk = ChunkContaining(chunks, 3);
        var store = new FakeVectorStore
        {
            Hits = [new VectorHit(1, "h", fileId, repoId, chunk.StartLine, chunk.EndLine, 0.95)]
        };

        var response = Search(workspace, "Add", store);

        var hit = response.Matches.ShouldHaveSingleItem();
        hit.Source.ShouldBe("both");
        hit.Score.ShouldBe(2.0 / 61, 1e-9);
        hit.Path.ShouldBe("Calc.cs");
        hit.Line.ShouldBe(3);
        store.SearchCalls.ShouldBe(1);
        store.LastTopK.ShouldBe(20);
    }

    [Fact]
    public void Hybrid_StoreUnavailable_FallsBackToExactWithWarning()
    {
        var workspace = NewIndexedWorkspace(out _, out _);
        var store = new FakeVectorStore { Available = false };

        var response = Search(workspace, "Add", store);

        response.Warnings.ShouldHaveSingleItem().ShouldContain("falling back to exact");
        response.Matches.ShouldNotBeEmpty();
        response.Matches.ShouldAllBe(m => m.Source == "exact");
        store.SearchCalls.ShouldBe(0);
    }

    [Fact]
    public void Hybrid_StoreEmpty_SilentlyExactOnly()
    {
        var workspace = NewIndexedWorkspace(out _, out _);
        var store = new FakeVectorStore();

        var response = Search(workspace, "Add", store);

        response.Warnings.ShouldBeEmpty();
        response.Matches.ShouldAllBe(m => m.Source == "exact");
        store.SearchCalls.ShouldBe(0);
    }

    [Fact]
    public void Hybrid_VectorOnlyHit_AppearsWithSemanticSource()
    {
        var workspace = NewIndexedWorkspace(out var chunks, out var repoId);
        var chunk = chunks.First();
        var store = new FakeVectorStore
        {
            Hits = [new VectorHit(1, "h", chunk.FileId, repoId, chunk.StartLine, chunk.EndLine, 0.8)]
        };

        var response = Search(workspace, "zzznotfound", store);

        var hit = response.Matches.ShouldHaveSingleItem();
        hit.Source.ShouldBe("semantic");
        hit.Path.ShouldBe("Calc.cs");
        hit.Snippet.ShouldBe(chunk.Text.Trim());
    }

    [Fact]
    public void Hybrid_RepoFilter_PropagatesToVectorStore()
    {
        var workspace = NewIndexedWorkspace(out var chunks, out var repoId);
        var chunk = chunks.First();
        var store = new FakeVectorStore
        {
            Hits = [new VectorHit(1, "h", chunk.FileId, repoId, chunk.StartLine, chunk.EndLine, 0.8)]
        };

        var response = Search(workspace, "Add", store, repo: "demo");

        store.LastRepoId.ShouldBe(repoId);
        response.Warnings.ShouldBeEmpty();
    }

    private static ChunkDocument ChunkContaining(List<ChunkDocument> chunks, int line)
    {
        var minStart = chunks.Where(c => c.StartLine <= line && line <= c.EndLine).Min(c => c.StartLine);
        return chunks.First(c => c.StartLine <= line && line <= c.EndLine && c.StartLine == minStart);
    }

    private static SearchResponse Search(
        string workspace, string query, FakeVectorStore store, string? repo = null)
    {
        var engine = new SearchEngine(workspace);
        return engine.SearchHybridAsync(
            new SearchQuery(query, false, null, repo, null, null, null, null),
            new FakeEmbeddingProvider(),
            store,
            CancellationToken.None).GetAwaiter().GetResult();
    }

    private static string NewIndexedWorkspace(out List<ChunkDocument> chunks, out uint repoId)
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        TestWorkspaceConfig.UseOnnxEmbeddings(workspace);
        manager.AddRepo("repo", "demo");

        var fullPath = Path.Combine(workspace, "repo", "Calc.cs");
        File.WriteAllText(fullPath, Source);

        new WorkspaceIndexer().Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: new FakeVectorStore());

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        repoId = store.FindRepo("demo")!.Id;
        chunks = store.ListFiles(repoId).SelectMany(f => store.ListChunksByFile(f.Id)).ToList();
        chunks.ShouldNotBeEmpty();
        return workspace;
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
        public IReadOnlyList<VectorHit> Hits { get; set; } = [];

        public bool Available { get; set; } = true;

        public int SearchCalls { get; private set; }

        public int? LastTopK { get; private set; }

        public uint? LastRepoId { get; private set; }

        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(Available);

        public Task EnsureCollectionAsync(int dimensions, CancellationToken ct) => Task.CompletedTask;

        public Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<VectorHit>> SearchAsync(float[] vector, int topK, uint? repoId, CancellationToken ct)
        {
            SearchCalls++;
            LastTopK = topK;
            LastRepoId = repoId;
            return Task.FromResult(Hits);
        }

        public Task<long> CountAsync(CancellationToken ct) => Task.FromResult((long)Hits.Count);

        public Task DeleteByFileIdsAsync(IReadOnlyList<uint> fileIds, CancellationToken ct) => Task.CompletedTask;

        public Task DeleteCollectionAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
