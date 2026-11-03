using Shouldly;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing.Query;

public class KindFilterTests
{
    private const string SharedLine = "reindex after debounce passes";

    [Fact]
    public void Kind_Code_KeepsDocsFilesOutOfBothArms()
    {
        var workspace = NewIndexedWorkspace(out var docsChunk);
        var store = new KindAwareVectorStore();
        store.Seed(DocKindDetector.Docs, docsChunk);

        var response = Search(workspace, SharedLine, store, DocKindDetector.Code);

        response.Matches.Select(m => m.Path).ShouldBe(["src/Watcher.cs"]);
        store.LastFilter.ShouldBe(new VectorSearchFilter(null, DocKindDetector.Code));
    }

    [Fact]
    public void Kind_Docs_KeepsCodeFilesOutOfBothArms()
    {
        var workspace = NewIndexedWorkspace(out var docsChunk);
        var store = new KindAwareVectorStore();
        store.Seed(DocKindDetector.Docs, docsChunk);

        var response = Search(workspace, SharedLine, store, DocKindDetector.Docs);

        response.Matches.Select(m => m.Path).ShouldBe(["docs/Notes.md"]);
        store.LastFilter.ShouldBe(new VectorSearchFilter(null, DocKindDetector.Docs));
    }

    [Fact]
    public void Kind_Default_AndAll_ReproduceTheUnfilteredResult()
    {
        var workspace = NewIndexedWorkspace(out var docsChunk);
        var store = new KindAwareVectorStore();
        store.Seed(DocKindDetector.Docs, docsChunk);

        var byDefault = Search(workspace, SharedLine, store, null);
        var byAll = Search(workspace, SharedLine, store, DocKindDetector.All);

        byDefault.Matches.Select(m => m.Path).OrderBy(p => p, StringComparer.Ordinal)
            .ShouldBe(["docs/Notes.md", "src/Watcher.cs"]);
        byAll.Matches.Select(m => m.Path).ShouldBe(byDefault.Matches.Select(m => m.Path));
        byAll.Matches.Select(m => m.Score).ShouldBe(byDefault.Matches.Select(m => m.Score));
        store.LastFilter.ShouldBe(new VectorSearchFilter(null, null));
    }

    [Fact]
    public void Kind_AllowedValues_AreCanonicalAndCaseInsensitive()
    {
        DocKindDetector.TryNormalizeFilter(null, out var none).ShouldBeTrue();
        none.ShouldBeNull();
        DocKindDetector.TryNormalizeFilter("", out var empty).ShouldBeTrue();
        empty.ShouldBeNull();
        DocKindDetector.TryNormalizeFilter(" ALL ", out var all).ShouldBeTrue();
        all.ShouldBeNull();
        DocKindDetector.TryNormalizeFilter("Code", out var code).ShouldBeTrue();
        code.ShouldBe(DocKindDetector.Code);
        DocKindDetector.TryNormalizeFilter("docs", out var docs).ShouldBeTrue();
        docs.ShouldBe(DocKindDetector.Docs);
        DocKindDetector.TryNormalizeFilter("sql", out _).ShouldBeFalse();
        DocKindDetector.TryNormalizeFilter("doc", out _).ShouldBeFalse();
    }

    private static SearchResponse Search(string workspace, string query, KindAwareVectorStore store, string? kind)
    {
        var engine = new SearchEngine(workspace);
        return engine.SearchHybridAsync(
            new SearchQuery(query, false, null, null, null, null, null, null, null, kind),
            new FakeEmbeddingProvider(),
            store,
            CancellationToken.None).GetAwaiter().GetResult();
    }

    private static string NewIndexedWorkspace(out VectorHit docsChunk)
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        TestWorkspaceConfig.UseOllamaEmbeddings(workspace);
        manager.AddRepo("repo", "demo");

        Directory.CreateDirectory(Path.Combine(workspace, "repo", "src"));
        Directory.CreateDirectory(Path.Combine(workspace, "repo", "docs"));
        File.WriteAllText(
            Path.Combine(workspace, "repo", "src", "Watcher.cs"),
            $"class Watcher\n{{\n    void OnEvent() => Reindex(\"{SharedLine}\");\n}}\n");
        File.WriteAllText(
            Path.Combine(workspace, "repo", "docs", "Notes.md"),
            $"# Watch notes\n\n{SharedLine}.\n");

        new WorkspaceIndexer().Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: new KindAwareVectorStore());

        using var meta = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoId = meta.FindRepo("demo")!.Id;
        var docsFile = meta.ListFiles(repoId).Single(f => f.Path == "docs/Notes.md");
        var chunk = meta.ListChunksByFile(docsFile.Id).ShouldHaveSingleItem();
        docsChunk = new VectorHit(1, chunk.ChunkHash, docsFile.Id, repoId, chunk.StartLine, chunk.EndLine, 0.9);
        return workspace;
    }

    private sealed class FakeEmbeddingProvider : IEmbeddingProvider
    {
        public int Dimensions => 4;

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbeddingUsage usage, CancellationToken ct) =>
            Task.FromResult(texts.Select(_ => new float[] { 0.1f, 0.2f, 0.3f, 0.4f }).ToArray());
    }

    private sealed class KindAwareVectorStore : IVectorStore
    {
        private readonly Dictionary<string, List<VectorHit>> _hitsByKind = new();

        public VectorSearchFilter? LastFilter { get; private set; }

        public void Seed(string kind, VectorHit hit)
        {
            if (!_hitsByKind.TryGetValue(kind, out var hits))
            {
                hits = [];
                _hitsByKind[kind] = hits;
            }

            hits.Add(hit);
        }

        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

        public Task EnsureCollectionAsync(int dimensions, CancellationToken ct) => Task.CompletedTask;

        public Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<VectorHit>> SearchAsync(float[] vector, int topK, VectorSearchFilter filter, CancellationToken ct)
        {
            LastFilter = filter;
            IEnumerable<VectorHit> matched = filter.Kind is null
                ? _hitsByKind.Values.SelectMany(h => h)
                : _hitsByKind.TryGetValue(filter.Kind, out var hits) ? hits : [];
            return Task.FromResult<IReadOnlyList<VectorHit>>([.. matched]);
        }

        public Task<long> CountAsync(CancellationToken ct) =>
            Task.FromResult((long)_hitsByKind.Values.Sum(h => h.Count));

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
