using System.Security.Cryptography;
using System.Text;
using Shouldly;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing.Query;

// the 022/T5 regression pin: a real indexed workspace + a fixed query, pinning the recorded
// order so a silent ranking change fails here instead of shipping inside a release
public class FusionRegressionPinTests
{
    private const string MergerSource = """
        public static class RrfMerger
        {
            public const int K = 60;
        }
        """;

    private const string TunerSource = """
        public sealed record FusionTuning(int? K = null, double ExactWeight = 1.0, double SemanticWeight = 1.0);
        """;

    private const string NotesSource = """
        # notes

        the FusionTuning record and its fusion tuning weights are discussed here in
        prose form, long enough that a ranking regression toward prose noise would
        push this file above the code file
        """;

    [Fact]
    public void IdentifierQuery_PinsCodeFileAboveProse_BothArmAgreement()
    {
        var workspace = BuildIndexedWorkspace(out var repoId);
        try
        {
            ChunkDocument tuningChunk;
            uint tuningFileId;
            using (var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace)))
            {
                var file = store.ListFiles(repoId)
                    .First(f => f.Path.EndsWith("FusionTuning.cs", StringComparison.Ordinal));
                tuningFileId = file.Id;
                tuningChunk = store.ListChunksByFile(file.Id).First();
            }

            var vectorStore = new RecordedVectorStore
            {
                Hits = [new VectorHit(1, "h", tuningFileId, repoId, tuningChunk.StartLine, tuningChunk.EndLine, 0.9)]
            };

            var response = Search(workspace, "FusionTuning", vectorStore);
            var paths = response.Matches.Select(m => m.Path).ToList();

            paths.ShouldNotBeEmpty();
            paths[0].ShouldBe("FusionTuning.cs");
            response.Matches[0].Source.ShouldBe("both");
            paths.ShouldContain("Notes.md");
            paths.IndexOf("FusionTuning.cs").ShouldBeLessThan(paths.IndexOf("Notes.md"));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void FusionTuning_Null_KeepsRecordedBaselineOrder()
    {
        var exact = Hit("a.cs", "b.cs");
        var semantic = Hit("b.cs", "c.cs");

        var plain = RrfMerger.Merge(exact, semantic);
        var pinned = RrfMerger.Merge(exact, semantic, new FusionTuning());

        pinned.Select(m => (m.Path, m.Score, m.Source)).ShouldBe(plain.Select(m => (m.Path, m.Score, m.Source)));
        pinned.Select(m => m.Path).ShouldBe(["b.cs", "a.cs", "c.cs"]);
        pinned[0].Source.ShouldBe("both");
        pinned[0].Score.ShouldBe(1.0 / 61 + 1.0 / 62, 1e-9);
    }

    [Fact]
    public void PassThrough_FusionTuningThroughEngine_PreservesOrder()
    {
        var workspace = BuildIndexedWorkspace(out _);
        try
        {
            var store = new RecordedVectorStore();

            var plain = Search(workspace, "FusionTuning", store);
            var tuned = Search(workspace, "FusionTuning", store, new FusionTuning { K = 1 });

            tuned.Matches.Select(m => m.Path).ShouldBe(plain.Matches.Select(m => m.Path));
            tuned.Matches[0].Score.ShouldNotBe(plain.Matches[0].Score);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    private static SearchResponse Search(string workspace, string query, RecordedVectorStore store, FusionTuning? tuning = null) =>
        new SearchEngine(workspace)
            .SearchHybridAsync(
                new SearchQuery(query, Regex: false, IgnoreCase: null, Repo: null, PathGlob: null,
                    Language: null, TopK: 10, MaxTokens: null, Prefix: null),
                new DeterministicEmbeddingProvider(),
                store,
                CancellationToken.None,
                tuning: tuning)
            .GetAwaiter()
            .GetResult();

    private static List<(string Key, SearchMatch Match)> Hit(params string[] paths) =>
        paths.Select(p => ($"key:{p}", new SearchMatch("demo", p, 1, 0.0, "snippet"))).ToList();

    private static string BuildIndexedWorkspace(out uint repoId)
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        TestWorkspaceConfig.UseOnnxEmbeddings(workspace);
        manager.AddRepo("repo", "demo");

        File.WriteAllText(Path.Combine(workspace, "repo", "RrfMerger.cs"), MergerSource);
        File.WriteAllText(Path.Combine(workspace, "repo", "FusionTuning.cs"), TunerSource);
        File.WriteAllText(Path.Combine(workspace, "repo", "Notes.md"), NotesSource);

        new WorkspaceIndexer().Index(
            workspace, embeddings: new DeterministicEmbeddingProvider(), vectorStore: new RecordedVectorStore());

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        repoId = store.FindRepo("demo")!.Id;
        store.ListFiles(repoId).ShouldNotBeEmpty();
        return workspace;
    }

    private sealed class DeterministicEmbeddingProvider : IEmbeddingProvider
    {
        public int Dimensions => 4;

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbeddingUsage usage, CancellationToken ct) =>
            Task.FromResult(texts.Select(text =>
            {
                var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
                return new[] { bytes[0] / 255f, bytes[1] / 255f, bytes[2] / 255f, bytes[3] / 255f };
            }).ToArray());
    }

    private sealed class RecordedVectorStore : IVectorStore
    {
        public IReadOnlyList<VectorHit> Hits { get; set; } = [];

        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

        public Task EnsureCollectionAsync(int dimensions, CancellationToken ct) => Task.CompletedTask;

        public Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<VectorHit>> SearchAsync(float[] vector, int topK, VectorSearchFilter filter, CancellationToken ct) =>
            Task.FromResult(Hits);

        public Task<long> CountAsync(CancellationToken ct) => Task.FromResult(Hits.Count > 0 ? 1L : 0L);

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
