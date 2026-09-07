using Shouldly;
using Squeezr.Cli.Embeddings;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Embeddings;

public class QdrantStatusProbeTests
{
    [Fact]
    public async Task Probe_NullStore_ReportsUnavailable_WithPendingCount()
    {
        using var store = NewMetaStore(2);

        var status = await QdrantStatusProbe.ProbeAsync(null, store, CancellationToken.None);

        status.ShouldBe(new QdrantStatus(false, null, 2));
    }

    [Fact]
    public async Task Probe_AvailableStore_ReportsPointsAndPending()
    {
        using var store = NewMetaStore(2);
        var fake = new FakeVectorStore { Available = true, Points = 3 };

        var status = await QdrantStatusProbe.ProbeAsync(fake, store, CancellationToken.None);

        status.ShouldBe(new QdrantStatus(true, 3, 2));
    }

    [Fact]
    public async Task Probe_UnavailableStore_PointsAreNull()
    {
        using var store = NewMetaStore(0);
        var fake = new FakeVectorStore { Available = false, Points = 3 };

        var status = await QdrantStatusProbe.ProbeAsync(fake, store, CancellationToken.None);

        status.ShouldBe(new QdrantStatus(false, null, 0));
        fake.CountCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Probe_CountThrows_FallsBackToUnavailable()
    {
        using var store = NewMetaStore(0);
        var fake = new FakeVectorStore { Available = true, ThrowOnCount = true };

        var status = await QdrantStatusProbe.ProbeAsync(fake, store, CancellationToken.None);

        status.ShouldBe(new QdrantStatus(false, null, 0));
    }

    private static MetaStore NewMetaStore(int pendingChunks)
    {
        var store = new MetaStore(Path.Combine(TestDirs.NewTempDir(), "meta.db"));
        if (pendingChunks > 0)
        {
            store.InsertChunks(Enumerable.Range(0, pendingChunks).Select(i => new ChunkDocument
            {
                RepoId = 1,
                FileId = (uint)i,
                Text = "snippet",
                ChunkHash = $"h{i}",
                EmbeddingStatus = "pending"
            }));
        }

        return store;
    }

    private sealed class FakeVectorStore : IVectorStore
    {
        public bool Available { get; set; }
        public long Points { get; set; }
        public bool ThrowOnCount { get; set; }
        public int CountCalls { get; private set; }

        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(Available);

        public Task<long> CountAsync(CancellationToken ct)
        {
            CountCalls++;
            return ThrowOnCount
                ? Task.FromException<long>(new InvalidOperationException("count failed"))
                : Task.FromResult(Points);
        }

        public Task EnsureCollectionAsync(int dimensions, CancellationToken ct) => Task.CompletedTask;

        public Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<VectorHit>> SearchAsync(float[] vector, int topK, uint? repoId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<VectorHit>>([]);

        public Task DeleteByFileIdsAsync(IReadOnlyList<uint> fileIds, CancellationToken ct) => Task.CompletedTask;

        public Task DeleteCollectionAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
