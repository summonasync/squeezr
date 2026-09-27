using System.Security.Cryptography;
using System.Text;
using Shouldly;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing;

public class VectorPointReconcileTests
{
    private const string Source = """
        class Calculator
        {
            int Add(int a, int b)
            {
                var sum = a + b;
                return sum;
            }

            int Mul(int a, int b)
            {
                var product = a * b;
                return product;
            }
        }
        """;

    [Fact]
    public async Task Reconcile_Apply_DeletesOrphansAndKeepsReferencedPoints()
    {
        var workspace = IndexedWorkspace("repo/a.cs", Source);
        var vectorStore = new FakeVectorStore();
        Index(workspace, vectorStore);
        var live = vectorStore.Points.Keys.ToList();
        live.ShouldNotBeEmpty();

        var orphanHashes = new[] { "legacy-1", "legacy-2", "legacy-3" };
        SeedOrphans(vectorStore, orphanHashes);
        vectorStore.Calls.Clear();

        var report = await VectorPointReconciler.ReconcileAsync(workspace, vectorStore, apply: true);

        report.Orphans.ShouldBe(orphanHashes.Length);
        report.Applied.ShouldBeTrue();
        report.PointsBefore.ShouldBe(live.Count + orphanHashes.Length);
        report.PointsAfter.ShouldBe(live.Count);
        report.LiveHashes.ShouldBe(live.Count);
        report.Chunks.ShouldBeGreaterThanOrEqualTo(live.Count);
        report.ChunksWithoutPoints.ShouldBe(0);
        orphanHashes.ShouldAllBe(hash => !vectorStore.Points.ContainsKey(ChunkPointId.FromHash(hash)));
        live.ShouldAllBe(id => vectorStore.Points.ContainsKey(id));
        vectorStore.Calls.ShouldBe(["delete-ids:3"]);
    }

    [Fact]
    public async Task Reconcile_DryRun_ReportsOrphansAndDeletesNothing()
    {
        var workspace = IndexedWorkspace("repo/a.cs", Source);
        var vectorStore = new FakeVectorStore();
        Index(workspace, vectorStore);
        var before = vectorStore.Points.Count;
        var orphanHashes = Enumerable.Range(0, 7).Select(i => $"legacy-{i}").ToList();
        SeedOrphans(vectorStore, orphanHashes);
        vectorStore.Calls.Clear();

        var report = await VectorPointReconciler.ReconcileAsync(workspace, vectorStore, apply: false);

        report.Orphans.ShouldBe(7);
        report.Applied.ShouldBeFalse();
        report.PointsAfter.ShouldBeNull();
        report.OrphanSample.Count.ShouldBe(5);
        report.OrphanSample.ShouldAllBe(id => vectorStore.Points.ContainsKey(id));
        vectorStore.Points.Count.ShouldBe(before + 7);
        vectorStore.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Reconcile_SecondApply_FindsNothingLeftToRemove()
    {
        var workspace = IndexedWorkspace("repo/a.cs", Source);
        var vectorStore = new FakeVectorStore();
        Index(workspace, vectorStore);
        SeedOrphans(vectorStore, ["legacy-1", "legacy-2"]);

        var first = await VectorPointReconciler.ReconcileAsync(workspace, vectorStore, apply: true);
        vectorStore.Calls.Clear();
        var second = await VectorPointReconciler.ReconcileAsync(workspace, vectorStore, apply: true);

        first.Orphans.ShouldBe(2);
        second.Orphans.ShouldBe(0);
        second.PointsAfter.ShouldBe(first.PointsAfter);
        vectorStore.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Reconcile_PointWhoseHashIsLiveInAnotherFile_Survives()
    {
        var workspace = NewWorkspace();
        // identical bodies collapse into one point per chunk hash: the payload names whichever file was
        // upserted last, so a fileId-scoped cleanup would drop a vector the other file still references
        WriteFile(workspace, "repo/a.cs", Source);
        WriteFile(workspace, "repo/b.cs", Source);
        var vectorStore = new FakeVectorStore();
        Index(workspace, vectorStore);

        var aId = FileIdOf(workspace, "a.cs");
        var bId = FileIdOf(workspace, "b.cs");
        aId.ShouldNotBe(bId);
        var shared = vectorStore.Points.Values.ToList();
        shared.Count.ShouldBe(ChunkHashes(workspace, aId).Count);
        shared.Count.ShouldBeLessThan(ChunkHashes(workspace, aId).Count + ChunkHashes(workspace, bId).Count);

        // the collapse leaves the payload naming whichever file was upserted last; pin it to b so the
        // scenario is deterministic — a fileId-scoped delete would take every one of these points with it
        foreach (var point in shared)
        {
            vectorStore.Points[point.Id] = point with { FileId = bId };
        }

        using (var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace)))
        {
            store.DeleteChunksByFile(bId);
        }

        vectorStore.Calls.Clear();
        var report = await VectorPointReconciler.ReconcileAsync(workspace, vectorStore, apply: true);

        report.Orphans.ShouldBe(0);
        report.Chunks.ShouldBeGreaterThan(0);
        vectorStore.Calls.ShouldBeEmpty();
        vectorStore.Points.Count.ShouldBe(shared.Count);
        vectorStore.Points.Values.ShouldAllBe(p => p.FileId == bId);
        ChunkHashes(workspace, aId).ShouldAllBe(hash => vectorStore.Points.ContainsKey(ChunkPointId.FromHash(hash)));
    }

    [Fact]
    public async Task Reconcile_ChunkWithoutAPoint_IsCountedAndNotRecreated()
    {
        var workspace = IndexedWorkspace("repo/a.cs", Source);
        var vectorStore = new FakeVectorStore();
        Index(workspace, vectorStore);
        var dropped = vectorStore.Points.Keys.First();
        vectorStore.Points.Remove(dropped);
        var remaining = vectorStore.Points.Count;
        vectorStore.Calls.Clear();

        var report = await VectorPointReconciler.ReconcileAsync(workspace, vectorStore, apply: true);

        report.ChunksWithoutPoints.ShouldBe(1);
        report.Orphans.ShouldBe(0);
        report.PointsAfter.ShouldBe(remaining);
        vectorStore.Points.Count.ShouldBe(remaining);
        vectorStore.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Reconcile_StoreWithoutMetaDb_RefusesWithIndexNotBuilt()
    {
        var workspace = NewWorkspace();
        File.Delete(SqueezrPaths.MetaDbPath(workspace));
        var vectorStore = new FakeVectorStore();
        SeedOrphans(vectorStore, ["legacy-1"]);

        var ex = await Should.ThrowAsync<WorkspaceException>(
            () => VectorPointReconciler.ReconcileAsync(workspace, vectorStore, apply: true));

        ex.Code.ShouldBe(WorkspaceException.IndexNotBuiltCode);
        vectorStore.Points.Count.ShouldBe(1);
        vectorStore.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Reconcile_StoreUnavailable_RefusesBeforeTouchingPoints()
    {
        var workspace = IndexedWorkspace("repo/a.cs", Source);
        var vectorStore = new FakeVectorStore();
        Index(workspace, vectorStore);
        SeedOrphans(vectorStore, ["legacy-1"]);
        var pointsBefore = vectorStore.Points.Count;
        vectorStore.Available = false;
        vectorStore.Calls.Clear();

        var ex = await Should.ThrowAsync<WorkspaceException>(
            () => VectorPointReconciler.ReconcileAsync(workspace, vectorStore, apply: true));

        ex.Message.ShouldContain("Qdrant is unavailable");
        vectorStore.Points.Count.ShouldBe(pointsBefore);
        vectorStore.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Reconcile_EmptyIndexOverPopulatedCollection_RefusesToWipeIt()
    {
        // a store recreated under a fresh SQUEEZR_HOME keeps the old collection: every point would look
        // like an orphan, so the pass refuses instead of emptying a collection it cannot explain
        var workspace = NewWorkspace();
        var vectorStore = new FakeVectorStore();
        SeedOrphans(vectorStore, ["legacy-1", "legacy-2"]);

        var ex = await Should.ThrowAsync<WorkspaceException>(
            () => VectorPointReconciler.ReconcileAsync(workspace, vectorStore, apply: true));

        ex.Message.ShouldContain("no chunks");
        vectorStore.Points.Count.ShouldBe(2);
        vectorStore.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Reconcile_DeleteFailure_ReportsThatARerunFinishesTheJob()
    {
        var workspace = IndexedWorkspace("repo/a.cs", Source);
        var vectorStore = new FakeVectorStore();
        Index(workspace, vectorStore);
        SeedOrphans(vectorStore, ["legacy-1"]);
        vectorStore.FailDelete = true;

        var ex = await Should.ThrowAsync<WorkspaceException>(
            () => VectorPointReconciler.ReconcileAsync(workspace, vectorStore, apply: true));

        ex.Message.ShouldContain("idempotent");
    }

    private static string IndexedWorkspace(string relativePath, string content)
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, relativePath, content);
        return workspace;
    }

    private static void Index(string workspace, FakeVectorStore vectorStore) =>
        new WorkspaceIndexer().Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: vectorStore);

    private static void SeedOrphans(FakeVectorStore vectorStore, IEnumerable<string> chunkHashes)
    {
        foreach (var hash in chunkHashes)
        {
            var id = ChunkPointId.FromHash(hash);
            vectorStore.Points[id] = new VectorPoint(id, [0.25f, 0.5f, 0.75f, 1f], hash, 4242, 1, 3, 9);
        }
    }

    private static string NewWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        TestWorkspaceConfig.UseOnnxEmbeddings(workspace);
        manager.AddRepo("repo", "demo");
        return workspace;
    }

    private static void WriteFile(string workspace, string relativePath, string content)
    {
        var fullPath = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private static uint FileIdOf(string workspace, string path)
    {
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoId = store.FindRepo("demo")!.Id;
        return store.ListFiles(repoId).Single(f => f.Path == path).Id;
    }

    private static List<string> ChunkHashes(string workspace, uint fileId)
    {
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        return store.ListChunkHashesByFile(fileId).Distinct().ToList();
    }

    private sealed class FakeEmbeddingProvider : IEmbeddingProvider
    {
        public int Dimensions { get; init; } = 4;

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbeddingUsage usage, CancellationToken ct) =>
            Task.FromResult(texts.Select(text =>
            {
                var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
                return Enumerable.Range(0, Dimensions).Select(i => bytes[i % bytes.Length] / 255f).ToArray();
            }).ToArray());
    }

    private sealed class FakeVectorStore : IVectorStore
    {
        public Dictionary<ulong, VectorPoint> Points { get; } = new();

        public List<string> Calls { get; } = [];

        public bool Available { get; set; } = true;

        public bool FailDelete { get; set; }

        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(Available);

        public Task EnsureCollectionAsync(int dimensions, CancellationToken ct) => Task.CompletedTask;

        public Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct)
        {
            Calls.Add($"upsert:{points.Count}");
            foreach (var point in points)
            {
                Points[point.Id] = point;
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<VectorHit>> SearchAsync(float[] vector, int topK, uint? repoId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<long> CountAsync(CancellationToken ct) => Task.FromResult((long)Points.Count);

        public Task<IReadOnlyList<ulong>> ScrollPointIdsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ulong>>(Points.Keys.ToList());

        public Task DeleteByFileIdsAsync(IReadOnlyList<uint> fileIds, CancellationToken ct)
        {
            if (FailDelete)
            {
                return Task.FromException(new HttpRequestException("qdrant is down"));
            }

            Calls.Add($"delete:{string.Join(",", fileIds)}");
            foreach (var id in Points.Where(kv => fileIds.Contains(kv.Value.FileId)).Select(kv => kv.Key).ToList())
            {
                Points.Remove(id);
            }

            return Task.CompletedTask;
        }

        public Task DeleteByPointIdsAsync(IReadOnlyList<ulong> pointIds, CancellationToken ct)
        {
            if (FailDelete)
            {
                return Task.FromException(new HttpRequestException("qdrant is down"));
            }

            Calls.Add($"delete-ids:{pointIds.Count}");
            foreach (var id in pointIds)
            {
                Points.Remove(id);
            }

            return Task.CompletedTask;
        }

        public Task DeleteCollectionAsync(CancellationToken ct)
        {
            Points.Clear();
            return Task.CompletedTask;
        }
    }
}
