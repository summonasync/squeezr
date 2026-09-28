using System.Security.Cryptography;
using System.Text;
using Shouldly;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing;

public class VectorPointReapTests
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

    private const string MovedBody = """
        class Calculator
        {
            int Add(int a, int b)
            {
                var sum = a + b;
                return sum;
            }

            int Mul(int a, int b)
            {
                var product = a * b * 1;
                return product;
            }
        }
        """;

    // a comment sits in the class's leading trivia, so every symbol span shifts a line down while the
    // chunk texts stay byte-identical — the file hash moves, the chunk hashes do not
    private const string ReflowedSource = "// reflowed\n" + Source;

    // the second file must not share chunk text with the first: points are keyed by chunk hash alone, so
    // identical bodies collapse into one point whose payload names a single file
    private const string OtherSource = """
        class Ledger
        {
            decimal Total(decimal[] rows)
            {
                var sum = 0m;
                foreach (var row in rows)
                {
                    sum += row;
                }

                return sum;
            }
        }
        """;

    [Fact]
    public void Index_ModifiedFile_DropsOldPointsBeforeUpsertingNewOnes()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", Source);
        WriteFile(workspace, "repo/b.cs", OtherSource);
        var vectorStore = new FakeVectorStore();
        var indexer = new WorkspaceIndexer();
        indexer.Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: vectorStore);

        var fileId = FileIdOf(workspace, "a.cs");
        var untouchedPoints = vectorStore.Points.Values.Where(p => p.FileId != fileId).ToList();
        var staleHashes = ChunkHashes(workspace, fileId);
        vectorStore.Calls.Clear();

        WriteFile(workspace, "repo/a.cs", MovedBody);
        indexer.Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: vectorStore);

        var deletedAt = vectorStore.Calls.FindIndex(c => c.StartsWith("delete:", StringComparison.Ordinal));
        var upsertedAt = vectorStore.Calls.FindIndex(c => c.StartsWith("upsert:", StringComparison.Ordinal));
        deletedAt.ShouldBeGreaterThanOrEqualTo(0);
        vectorStore.Calls[deletedAt].ShouldBe($"delete:{fileId}");
        upsertedAt.ShouldBeGreaterThan(deletedAt);

        var freshHashes = ChunkHashes(workspace, fileId);
        freshHashes.ShouldNotBe(staleHashes);
        foreach (var hash in staleHashes.Except(freshHashes))
        {
            vectorStore.Points.ShouldNotContainKey(ChunkPointId.FromHash(hash));
        }

        PointsOfFile(vectorStore, fileId).Select(p => p.ChunkHash).ShouldBe(freshHashes, ignoreOrder: true);
        PointsOfFile(vectorStore, fileId).Count.ShouldBe(freshHashes.Count);
        untouchedPoints.ShouldAllBe(p => vectorStore.Points.ContainsKey(p.Id));
    }

    [Fact]
    public void Index_ModifiedFileWithIdenticalChunkHashes_SendsNoDelete()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", Source);
        var vectorStore = new FakeVectorStore();
        var indexer = new WorkspaceIndexer();
        indexer.Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: vectorStore);
        var pointsBefore = vectorStore.Points.Count;

        WriteFile(workspace, "repo/a.cs", ReflowedSource);
        vectorStore.Calls.Clear();
        indexer.Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: vectorStore);

        vectorStore.Calls.ShouldNotContain(c => c.StartsWith("delete:", StringComparison.Ordinal));
        vectorStore.Points.Count.ShouldBe(pointsBefore);
    }

    [Fact]
    public void Index_NoChanges_SendsNoPointTraffic()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", Source);
        var vectorStore = new FakeVectorStore();
        var indexer = new WorkspaceIndexer();
        indexer.Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: vectorStore);

        vectorStore.Calls.Clear();
        indexer.Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: vectorStore);

        vectorStore.Calls.ShouldBeEmpty();
    }

    [Fact]
    public void Index_FullPassWithoutEdits_RechunksEveryFileButSendsNoDelete()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", Source);
        WriteFile(workspace, "repo/b.cs", OtherSource);
        var vectorStore = new FakeVectorStore();
        var indexer = new WorkspaceIndexer();
        indexer.Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: vectorStore);
        var pointsBefore = vectorStore.Points.Count;

        vectorStore.Calls.Clear();
        indexer.Index(workspace, full: true, embeddings: new FakeEmbeddingProvider(), vectorStore: vectorStore);

        vectorStore.Calls.ShouldNotContain(c => c.StartsWith("delete:", StringComparison.Ordinal));
        vectorStore.Points.Count.ShouldBe(pointsBefore);
    }

    [Fact]
    public void Index_ReapWhileStoreDown_QueuesMarkerAndReplaysItNextPass()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", Source);
        WriteFile(workspace, "repo/b.cs", OtherSource);
        var vectorStore = new FakeVectorStore();
        var indexer = new WorkspaceIndexer();
        indexer.Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: vectorStore);

        var fileId = FileIdOf(workspace, "a.cs");
        var staleHashes = ChunkHashes(workspace, fileId);
        vectorStore.Available = false;
        WriteFile(workspace, "repo/a.cs", MovedBody);

        var blocked = indexer.Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: vectorStore);

        blocked.Warnings.ShouldContain(w => w.Contains("deferred to a later pass", StringComparison.Ordinal));
        PendingReapIds(workspace).ShouldBe([fileId]);
        staleHashes.ShouldAllBe(hash => vectorStore.Points.ContainsKey(ChunkPointId.FromHash(hash)));

        vectorStore.Available = true;
        vectorStore.Calls.Clear();
        var replayed = indexer.Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: vectorStore);

        replayed.Warnings.ShouldContain(w => w.Contains("replayed deferred point cleanup", StringComparison.Ordinal));
        PendingReapIds(workspace).ShouldBeEmpty();
        vectorStore.Calls.ShouldContain($"delete:{fileId}");
        PointsOfFile(vectorStore, fileId).Select(p => p.ChunkHash)
            .ShouldBe(ChunkHashes(workspace, fileId), ignoreOrder: true);
        PointsOfFile(vectorStore, FileIdOf(workspace, "b.cs")).ShouldNotBeEmpty();
    }

    [Fact]
    public void Index_DeleteThrowsMidPass_RetainsMarkerAndReplayDoesNotLoseVectors()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", Source);
        var vectorStore = new FakeVectorStore();
        var indexer = new WorkspaceIndexer();
        indexer.Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: vectorStore);

        var fileId = FileIdOf(workspace, "a.cs");
        vectorStore.FailDelete = true;
        WriteFile(workspace, "repo/a.cs", MovedBody);

        var failed = indexer.Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: vectorStore);

        failed.Warnings.ShouldContain(w => w.Contains("failed to remove vectors", StringComparison.Ordinal));
        PendingReapIds(workspace).ShouldBe([fileId]);
        ListChunks(workspace, fileId).ShouldAllBe(c => c.EmbeddingStatus == "ok");

        vectorStore.FailDelete = false;
        indexer.Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: vectorStore);

        PendingReapIds(workspace).ShouldBeEmpty();
        ListChunks(workspace, fileId).ShouldAllBe(c => c.EmbeddingStatus == "ok");
        PointsOfFile(vectorStore, fileId).Select(p => p.ChunkHash)
            .ShouldBe(ChunkHashes(workspace, fileId), ignoreOrder: true);
    }

    [Fact]
    public void Index_DeletedFileWhileStoreDown_ReplaysTheDropNextPass()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", Source);
        WriteFile(workspace, "repo/b.cs", OtherSource);
        var vectorStore = new FakeVectorStore();
        var indexer = new WorkspaceIndexer();
        indexer.Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: vectorStore);

        var droppedId = FileIdOf(workspace, "b.cs");
        var droppedHashes = ChunkHashes(workspace, droppedId);
        File.Delete(Path.Combine(workspace, "repo", "b.cs"));
        vectorStore.Available = false;
        indexer.Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: vectorStore);
        PendingReapIds(workspace).ShouldBe([droppedId]);

        vectorStore.Available = true;
        indexer.Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: vectorStore);

        PendingReapIds(workspace).ShouldBeEmpty();
        droppedHashes.ShouldAllBe(hash => !vectorStore.Points.ContainsKey(ChunkPointId.FromHash(hash)));
    }

    [Fact]
    public void IndexFile_ModifiedFile_ReapsItsOldPoints()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", Source);
        var vectorStore = new FakeVectorStore();
        var indexer = new WorkspaceIndexer();
        indexer.Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: vectorStore);

        var fileId = FileIdOf(workspace, "a.cs");
        var staleHashes = ChunkHashes(workspace, fileId);
        WriteFile(workspace, "repo/a.cs", MovedBody);
        vectorStore.Calls.Clear();

        indexer.IndexFile(workspace, "demo", "a.cs", new FakeEmbeddingProvider(), vectorStore);

        vectorStore.Calls.ShouldContain($"delete:{fileId}");
        var freshHashes = ChunkHashes(workspace, fileId);
        freshHashes.ShouldNotBe(staleHashes);
        PointsOfFile(vectorStore, fileId).Select(p => p.ChunkHash).ShouldBe(freshHashes, ignoreOrder: true);
    }

    [Fact]
    public void IndexFile_WithoutChanges_SendsNoDelete()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", Source);
        var vectorStore = new FakeVectorStore();
        var indexer = new WorkspaceIndexer();
        indexer.Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: vectorStore);
        vectorStore.Calls.Clear();

        indexer.IndexFile(workspace, "demo", "a.cs", new FakeEmbeddingProvider(), vectorStore);

        vectorStore.Calls.ShouldNotContain(c => c.StartsWith("delete:", StringComparison.Ordinal));
    }

    [Fact]
    public void Index_WithoutEmbeddingsProvider_KeepsPointsAndWritesNoMarker()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", Source);
        var vectorStore = new FakeVectorStore();
        var indexer = new WorkspaceIndexer();
        indexer.Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: vectorStore);
        var pointsBefore = vectorStore.Points.Count;

        WriteFile(workspace, "repo/a.cs", MovedBody);
        indexer.Index(workspace, vectorStore: vectorStore);

        vectorStore.Points.Count.ShouldBe(pointsBefore);
        PendingReapIds(workspace).ShouldBeEmpty();
    }

    [Fact]
    public void PendingReaps_SurviveStoreReopen()
    {
        var dbPath = Path.Combine(TestDirs.NewTempDir(), "meta.db");
        using (var store = new MetaStore(dbPath, createIfMissing: true))
        {
            store.EnqueuePendingReaps([7u, 7u, 9u]);
            store.ListPendingReaps().Select(p => p.FileId).ShouldBe([7L, 9L]);
        }

        using (var reopened = new MetaStore(dbPath))
        {
            reopened.ListPendingReaps().Select(p => (uint)p.FileId).ShouldBe([7u, 9u]);
            reopened.ClearPendingReaps([7u]);
            reopened.ListPendingReaps().Select(p => (uint)p.FileId).ShouldBe([9u]);
        }

        using (var cleared = new MetaStore(dbPath))
        {
            cleared.ClearAllPendingReaps();
            cleared.ListPendingReaps().ShouldBeEmpty();
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

    private static IReadOnlyList<ChunkDocument> ListChunks(string workspace, uint fileId)
    {
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        return store.ListChunksByFile(fileId);
    }

    private static List<uint> PendingReapIds(string workspace)
    {
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        return store.ListPendingReaps().Select(p => (uint)p.FileId).ToList();
    }

    private static List<VectorPoint> PointsOfFile(FakeVectorStore store, uint fileId) =>
        store.Points.Values.Where(p => p.FileId == fileId).ToList();

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

        public int? CollectionSize { get; private set; }

        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(Available);

        public Task EnsureCollectionAsync(int dimensions, CancellationToken ct)
        {
            CollectionSize = dimensions;
            return Task.CompletedTask;
        }

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
            CollectionSize = null;
            return Task.CompletedTask;
        }
    }
}
