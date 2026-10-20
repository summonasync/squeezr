using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing;

public class EmbeddingBudgetTests
{
    [Fact]
    public void Index_BudgetPerPass_CapsEmbeddedAcrossCalls()
    {
        var workspace = NewWorkspace();
        new WorkspaceIndexer().Index(workspace);
        var pendingBefore = PendingCount(workspace);
        pendingBefore.ShouldBeGreaterThanOrEqualTo(12, "fixture must hold enough chunks for three budgeted calls");

        var provider = new CountingEmbeddingProvider();
        var vectorStore = new FakeVectorStore();
        var indexer = new WorkspaceIndexer();

        var first = indexer.Index(workspace, embeddings: provider, vectorStore: vectorStore, embedBudgetPerPass: 4);
        first.Embedded.ShouldBe(4);
        first.EmbeddingsCompleted.ShouldBeTrue();
        provider.EmbeddedTextCount.ShouldBe(4);
        PendingCount(workspace).ShouldBe(pendingBefore - 4);

        var second = indexer.Index(workspace, embeddings: provider, vectorStore: vectorStore, embedBudgetPerPass: 4);
        second.Embedded.ShouldBe(4);
        PendingCount(workspace).ShouldBe(pendingBefore - 8);

        provider.Reset();
        var rest = indexer.Index(workspace, embeddings: provider, vectorStore: vectorStore);
        rest.Embedded.ShouldBe(pendingBefore - 8);
        provider.EmbeddedTextCount.ShouldBe(pendingBefore - 8);
        PendingCount(workspace).ShouldBe(0);
    }

    [Fact]
    public void Index_BudgetBeyondPending_DrainsEverythingInOneCall()
    {
        var workspace = NewWorkspace();
        new WorkspaceIndexer().Index(workspace);
        var pendingBefore = PendingCount(workspace);
        pendingBefore.ShouldBeGreaterThan(0);

        var provider = new CountingEmbeddingProvider();
        var result = new WorkspaceIndexer().Index(
            workspace, embeddings: provider, vectorStore: new FakeVectorStore(), embedBudgetPerPass: 100_000);

        result.Embedded.ShouldBe(pendingBefore);
        PendingCount(workspace).ShouldBe(0);
    }

    [Fact]
    public void Index_ZeroBudget_SkipsTheEmbedPhaseEntirely()
    {
        var workspace = NewWorkspace();
        new WorkspaceIndexer().Index(workspace);
        var pendingBefore = PendingCount(workspace);

        var provider = new CountingEmbeddingProvider();
        var result = new WorkspaceIndexer().Index(
            workspace, embeddings: provider, vectorStore: new FakeVectorStore(), embedBudgetPerPass: 0);

        result.Embedded.ShouldBe(0);
        provider.EmbeddedTextCount.ShouldBe(0);
        result.Warnings.ShouldNotContain(w => w.Contains("embeddings skipped", StringComparison.Ordinal));
        PendingCount(workspace).ShouldBe(pendingBefore);
    }

    [Fact]
    public void Phase_MidDrain_StoreFailure_KeepsTheRemainderPending()
    {
        var dbPath = Path.Combine(TestDirs.NewTempDir(), "meta.db");
        using var store = new MetaStore(dbPath, createIfMissing: true);
        store.AddRepo(new RepoDocument { Id = 1, Name = "demo", Path = "repo" });
        store.InsertChunks(Enumerable.Range(0, 40).Select(i => PendingChunk(1, $"hash-{i:D2}")).ToList());
        var provider = new CountingEmbeddingProvider();
        var vectorStore = new FailingUpsertStore(failAfterCalls: 1);
        var warnings = new List<string>();

        var result = EmbeddingIndexPhase.Run(
            new RepoDocument { Id = 1, Name = "demo" }, store, "onnx/test-model/768/test-profile",
            provider, vectorStore, warnings.Add);

        result.Embedded.ShouldBeGreaterThan(0);
        result.Embedded.ShouldBeLessThan(40);
        result.Completed.ShouldBeFalse("a mid-drain store death leaves the queue tail unattempted");
        provider.EmbeddedTextCount.ShouldBeGreaterThanOrEqualTo(
            result.Embedded, "the failing batch's texts may already have been embedded when the store write died");
        warnings.ShouldContain(w => w.Contains("embeddings skipped", StringComparison.Ordinal));
        store.CountPendingChunks(1).ShouldBe(40 - result.Embedded);
        store.ListChunksByFile(1).Count(c => c.EmbeddingStatus == "ok").ShouldBe(result.Embedded);
    }

    private static ChunkDocument PendingChunk(uint fileId, string hash) =>
        new()
        {
            FileId = fileId,
            RepoId = 1,
            StartLine = 1,
            EndLine = 4,
            Text = $"body {hash}",
            ChunkHash = hash
        };

    private static string NewWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        TestWorkspaceConfig.UseOnnxEmbeddings(workspace);
        manager.AddRepo("repo", "demo");
        for (var i = 0; i < 8; i++)
        {
            WriteFile(workspace, $"repo/File{i:D2}.cs",
                $"class Calc{i}\n{{\n    int Sum{i}(int a) {{ return a + {i}; }}\n\n    int Mul{i}(int a) {{ return a * {i}; }}\n\n    int Sub{i}(int a) {{ return a - {i}; }}\n}}\n");
        }

        return workspace;
    }

    private static void WriteFile(string workspace, string relativePath, string content)
    {
        var fullPath = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private static int PendingCount(string workspace)
    {
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        return store.CountPendingChunks(null);
    }

    private sealed class CountingEmbeddingProvider : IEmbeddingProvider
    {
        public int Dimensions { get; init; } = 4;

        public int EmbeddedTextCount { get; private set; }

        public void Reset() => EmbeddedTextCount = 0;

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbeddingUsage usage, CancellationToken ct)
        {
            EmbeddedTextCount += texts.Count;
            return Task.FromResult(texts.Select(text =>
            {
                var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
                return Enumerable.Range(0, Dimensions).Select(i => bytes[i % bytes.Length] / 255f).ToArray();
            }).ToArray());
        }
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

    private sealed class FailingUpsertStore(int failAfterCalls) : IVectorStore
    {
        private int _calls;

        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

        public Task EnsureCollectionAsync(int dimensions, CancellationToken ct) => Task.CompletedTask;

        public Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) > failAfterCalls)
            {
                throw new IOException("vector store went away mid-drain");
            }

            return Task.CompletedTask;
        }

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
