using Shouldly;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing.Query;

public class CappedArmEngineTests
{
    private const string Source = """
        public class Probe
        {
            public int Add(int a, int b)
            {
                return a + b;
            }
        }
        """;

    private static readonly string[] RepoFiles =
    [
        "repo/src/Core/Alpha.cs",
        "repo/src/Cli/Beta.cs",
        "repo/lib/util.js",
        "repo/docs/Guide.md",
        "repo/audits/Note.md"
    ];

    [Fact]
    public void Hybrid_NarrowScopeSaturatedStore_WarnsCappedWithTheNarrowingMove()
    {
        var fixture = NewIndexedWorkspace();
        var alpha = fixture.Chunks["src/Core/Alpha.cs"];
        var store = new FakeVectorStore
        {
            Hits = SaturatedScopePool(fixture.RepoId, [alpha], scopedScore: 0.9)
        };

        var response = Search(fixture.Root, "zznotaword", store, prefix: "src/");

        store.LastTopK.ShouldBe(ScopedVectorBudget.For(2, 5));
        store.LastTopK.ShouldBe(50);
        response.Matches.ShouldContain(m => m.Path == "src/Core/Alpha.cs");
        response.Warnings.ShouldHaveSingleItem().ShouldBe(
            "semantic arm capped at 50 examined candidates; 1 in scope, 1 delivered — narrow with kind=code, narrower prefix or repo scope");
    }

    [Fact]
    public void Hybrid_StoreUnderTheAsk_WholePoolExamined_NoWarning()
    {
        var fixture = NewIndexedWorkspace();
        var alpha = fixture.Chunks["src/Core/Alpha.cs"];
        var beta = fixture.Chunks["src/Cli/Beta.cs"];
        var store = new FakeVectorStore
        {
            Hits =
            [
                Hit(1, alpha, fixture.RepoId, 0.9),
                Hit(2, beta, fixture.RepoId, 0.85),
                ..Enumerable.Range(0, 28).Select(i => OutOfScopeHit((ulong)(i + 3), fixture.RepoId, 0.5))
            ]
        };

        var response = Search(fixture.Root, "zznotaword", store, prefix: "src/");

        store.LastTopK.ShouldBe(50);
        response.Matches.Count(m => m.Source is "semantic" or "both").ShouldBe(2);
        response.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void Hybrid_UnscopedSaturatedStore_NoCappedWarningNoise()
    {
        var fixture = NewIndexedWorkspace();
        var alpha = fixture.Chunks["src/Core/Alpha.cs"];
        var store = new FakeVectorStore
        {
            Hits = BaseBudgetPool(fixture.RepoId, alpha, saturatedUnderfilled: 19)
        };

        var response = Search(fixture.Root, "zznotaword", store);

        store.LastTopK.ShouldBe(ScopedVectorBudget.BaseTopK);
        response.Warnings.ShouldBeEmpty();
        response.Matches.ShouldNotBeEmpty();
    }

    [Fact]
    public void Hybrid_CappedAndWeak_WarningsFireTogetherWithoutMerging()
    {
        var fixture = NewIndexedWorkspace();
        var alpha = fixture.Chunks["src/Core/Alpha.cs"];
        var store = new FakeVectorStore
        {
            Hits = SaturatedScopePool(fixture.RepoId, [alpha], scopedScore: 0.60)
        };

        var response = Search(fixture.Root, "zznotaword", store, prefix: "src/");

        response.Matches.ShouldNotBeEmpty();
        response.Warnings.Count.ShouldBe(2);
        response.Warnings.ShouldContain(w =>
            w.StartsWith("semantic arm capped at 50 examined candidates; 1 in scope, 1 delivered"));
        response.Warnings.ShouldContain(
            "no exact-arm match; 1 semantic hit at low confidence — narrow with kind=code or prefix");
    }

    [Fact]
    public void Hybrid_FullyRankExcludedPrefix_EmptyScopeStaysSilent()
    {
        var fixture = NewIndexedWorkspace();
        var store = new FakeVectorStore
        {
            Hits = Enumerable.Range(0, ScopedVectorBudget.Ceiling)
                .Select(i => OutOfScopeHit((ulong)(i + 1), fixture.RepoId, 0.9))
                .ToList()
        };
        var engine = new SearchEngine(fixture.Root, RankExclusion.Parse(["docs/**"]));

        var response = engine.SearchHybridAsync(
            new SearchQuery("zznotaword", false, null, null, null, null, null, null, "docs/"),
            new FakeEmbeddingProvider(),
            store,
            CancellationToken.None).GetAwaiter().GetResult();

        store.LastTopK.ShouldBe(ScopedVectorBudget.Ceiling);
        response.Matches.ShouldBeEmpty();
        response.Warnings.ShouldBeEmpty();
    }

    private static IReadOnlyList<VectorHit> SaturatedScopePool(
        uint repoId, IReadOnlyList<ChunkDocument> scopedChunks, double scopedScore)
    {
        var hits = scopedChunks.Select((chunk, i) => Hit((ulong)(i + 1), chunk, repoId, scopedScore)).ToList();
        hits.AddRange(Enumerable.Range(0, 50 - hits.Count).Select(i => OutOfScopeHit((ulong)(i + 100), repoId, 0.4)));
        return hits;
    }

    private static IReadOnlyList<VectorHit> BaseBudgetPool(uint repoId, ChunkDocument scopedChunk, int saturatedUnderfilled)
    {
        var hits = new List<VectorHit> { Hit(1, scopedChunk, repoId, 0.9) };
        hits.AddRange(Enumerable.Range(0, saturatedUnderfilled)
            .Select(i => new VectorHit((ulong)(i + 2), "h", scopedChunk.FileId, repoId, 999, 1000, 0.4)));
        return hits;
    }

    private static VectorHit Hit(ulong id, ChunkDocument chunk, uint repoId, double score) =>
        new(id, "h", chunk.FileId, repoId, chunk.StartLine, chunk.EndLine, score);

    private static VectorHit OutOfScopeHit(ulong id, uint repoId, double score) =>
        new(id, "h", (uint)(999_000 + id), repoId, 1, 2, score);

    private static SearchResponse Search(
        string workspace, string query, FakeVectorStore store, string? prefix = null)
    {
        var engine = new SearchEngine(workspace);
        return engine.SearchHybridAsync(
            new SearchQuery(query, false, null, null, null, null, null, null, prefix),
            new FakeEmbeddingProvider(),
            store,
            CancellationToken.None).GetAwaiter().GetResult();
    }

    private sealed record WorkspaceFixture(string Root, uint RepoId, Dictionary<string, ChunkDocument> Chunks);

    private static WorkspaceFixture NewIndexedWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        TestWorkspaceConfig.UseOnnxEmbeddings(workspace);

        foreach (var relative in RepoFiles)
        {
            var fullPath = Path.Combine([workspace, .. relative.Split('/')]);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, Source);
        }

        manager.AddRepo("repo", "demo");

        new WorkspaceIndexer().Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: new FakeVectorStore());

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoId = store.FindRepo("demo")!.Id;
        var chunks = store.ListFiles(repoId).ToDictionary(
            f => f.Path,
            f => store.ListChunksByFile(f.Id).First());
        chunks.Count.ShouldBe(5);
        return new WorkspaceFixture(workspace, repoId, chunks);
    }

    private sealed class FakeEmbeddingProvider : IEmbeddingProvider
    {
        public int Dimensions => 4;

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbeddingUsage usage, CancellationToken ct) =>
            Task.FromResult(texts.Select(_ => new float[] { 0.1f, 0.2f, 0.3f, 0.4f }).ToArray());
    }

    private sealed class FakeVectorStore : IVectorStore
    {
        public IReadOnlyList<VectorHit> Hits { get; set; } = [];

        public int? LastTopK { get; private set; }

        public VectorSearchFilter? LastFilter { get; private set; }

        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

        public Task EnsureCollectionAsync(int dimensions, CancellationToken ct) => Task.CompletedTask;

        public Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<VectorHit>> SearchAsync(float[] vector, int topK, VectorSearchFilter filter, CancellationToken ct)
        {
            LastTopK = topK;
            LastFilter = filter;
            return Task.FromResult<IReadOnlyList<VectorHit>>(Hits.Take(topK).ToList());
        }

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
