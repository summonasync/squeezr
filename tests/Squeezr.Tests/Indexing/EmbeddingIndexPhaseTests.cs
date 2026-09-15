using System.Security.Cryptography;
using System.Text;
using Shouldly;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing;

public class EmbeddingIndexPhaseTests
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
    public void Index_EmbedsPendingChunks_DedupsByChunkHash()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", Source);
        WriteFile(workspace, "repo/b.cs", Source);
        var provider = new FakeEmbeddingProvider();
        var vectorStore = new FakeVectorStore();

        new WorkspaceIndexer().Index(workspace, embeddings: provider, vectorStore: vectorStore);

        var chunks = ListChunks(workspace);
        chunks.Count.ShouldBeGreaterThanOrEqualTo(2);
        chunks.ShouldAllBe(c => c.EmbeddingStatus == "ok" && c.ModelKey == "onnx/nomic-embed-text-v1.5.onnx/768/nomic-embed-text-v1.5");

        var distinctHashes = chunks.Select(c => c.ChunkHash).Distinct().ToList();
        vectorStore.UpsertedPointCount.ShouldBe(distinctHashes.Count);
        vectorStore.DimensionsEnsured.ShouldBe(provider.Dimensions);
        foreach (var hash in distinctHashes)
        {
            vectorStore.Points.ShouldContainKey(ChunkPointId.FromHash(hash));
        }
    }

    [Fact]
    public void Index_WithoutChanges_UpsertsNothing()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", Source);
        var vectorStore = new FakeVectorStore();
        var indexer = new WorkspaceIndexer();
        indexer.Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: vectorStore);

        vectorStore.ResetCounters();
        indexer.Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: vectorStore);

        vectorStore.UpsertedPointCount.ShouldBe(0);
    }

    [Fact]
    public void Index_VectorStoreUnavailable_WarnsAndLeavesChunksPending()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", Source);
        var vectorStore = new FakeVectorStore { Available = false };

        var result = new WorkspaceIndexer().Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: vectorStore);

        result.Warnings.ShouldContain(w => w.Contains("embeddings skipped", StringComparison.OrdinalIgnoreCase));
        ListChunks(workspace).ShouldAllBe(c => c.EmbeddingStatus == "pending");
    }

    [Fact]
    public void Index_DimensionMismatch_ThrowsWithReembedInstruction()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", Source);
        var vectorStore = new FakeVectorStore { CollectionSize = 1536 };

        var ex = Should.Throw<VectorDimensionMismatchException>(() =>
            new WorkspaceIndexer().Index(
                workspace, embeddings: new FakeEmbeddingProvider { Dimensions = 768 }, vectorStore: vectorStore));

        ex.CollectionSize.ShouldBe(1536UL);
        ex.ModelDimensions.ShouldBe(768);
        ex.Message.ShouldContain("1536");
        ex.Message.ShouldContain("768");
        ex.Message.ShouldContain("squeezr index --reembed");
        ListChunks(workspace).ShouldAllBe(c => c.EmbeddingStatus == "pending");
    }

    [Fact]
    public void Index_Reembed_RecreatesCollectionWithNewDimensions()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", Source);
        var vectorStore = new FakeVectorStore();
        var indexer = new WorkspaceIndexer();
        indexer.Index(workspace, embeddings: new FakeEmbeddingProvider { Dimensions = 768 }, vectorStore: vectorStore);
        vectorStore.CollectionSize.ShouldBe(768);
        var hashCount = ListChunks(workspace).Select(c => c.ChunkHash).Distinct().Count();

        vectorStore.ResetCounters();
        indexer.Index(workspace, embeddings: new FakeEmbeddingProvider { Dimensions = 1536 }, vectorStore: vectorStore, reembed: true);

        vectorStore.DeleteCollectionCallCount.ShouldBe(1);
        vectorStore.CollectionSize.ShouldBe(1536);
        vectorStore.DimensionsEnsured.ShouldBe(1536);
        vectorStore.UpsertedPointCount.ShouldBe(hashCount);
        ListChunks(workspace).ShouldAllBe(c => c.EmbeddingStatus == "ok");
    }

    [Fact]
    public void Index_Reembed_EmbedsAllChunksAgain()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", Source);
        var provider = new FakeEmbeddingProvider();
        var vectorStore = new FakeVectorStore();
        var indexer = new WorkspaceIndexer();
        indexer.Index(workspace, embeddings: provider, vectorStore: vectorStore);
        var hashCount = ListChunks(workspace).Select(c => c.ChunkHash).Distinct().Count();

        vectorStore.ResetCounters();
        indexer.Index(workspace, embeddings: provider, vectorStore: vectorStore, reembed: true);

        vectorStore.UpsertedPointCount.ShouldBe(hashCount);
        ListChunks(workspace).ShouldAllBe(c => c.EmbeddingStatus == "ok");
    }

    [Fact]
    public void Index_ModelKeyMismatch_ReembedsChunk()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", Source);
        var provider = new FakeEmbeddingProvider();
        var vectorStore = new FakeVectorStore();
        var indexer = new WorkspaceIndexer();
        indexer.Index(workspace, embeddings: provider, vectorStore: vectorStore);

        using (var store = OpenStore(workspace))
        {
            var repoId = store.FindRepo("demo")!.Id;
            var chunks = store.ListPendingChunks(repoId, "unused", int.MaxValue);
            store.MarkChunksEmbedded(chunks.Select(c => c.Id), "onnx/old-model/768");
        }

        vectorStore.ResetCounters();
        indexer.Index(workspace, embeddings: provider, vectorStore: vectorStore);

        vectorStore.UpsertedPointCount.ShouldBeGreaterThan(0);
        ListChunks(workspace).ShouldAllBe(c => c.ModelKey == "onnx/nomic-embed-text-v1.5.onnx/768/nomic-embed-text-v1.5");
    }

    [Fact]
    public void Index_DeletedFile_DeletesItsVectorsFromStore()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", Source);
        WriteFile(workspace, "repo/b.cs", Source);
        var vectorStore = new FakeVectorStore();
        var indexer = new WorkspaceIndexer();
        indexer.Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: vectorStore);

        uint fileId;
        using (var store = OpenStore(workspace))
        {
            var repoId = store.FindRepo("demo")!.Id;
            fileId = store.ListFiles(repoId).Single(f => f.Path == "b.cs").Id;
        }

        File.Delete(Path.Combine(workspace, "repo", "b.cs"));
        indexer.Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: vectorStore);

        vectorStore.DeletedFileIds.ShouldBe([fileId]);
    }

    [Fact]
    public void Index_VectorDeleteFails_WarnsAndContinues()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", Source);
        WriteFile(workspace, "repo/b.cs", Source);
        var vectorStore = new FakeVectorStore();
        var indexer = new WorkspaceIndexer();
        indexer.Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: vectorStore);
        vectorStore.FailDelete = true;

        File.Delete(Path.Combine(workspace, "repo", "b.cs"));
        var result = indexer.Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: vectorStore);

        result.Warnings.ShouldContain(w => w.Contains("failed to remove vectors", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Index_WithoutVectorStore_DeletesFilesWithoutVectorCleanup()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", Source);
        var indexer = new WorkspaceIndexer();
        indexer.Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: new FakeVectorStore());

        File.Delete(Path.Combine(workspace, "repo", "a.cs"));
        var result = indexer.Index(workspace);

        result.Warnings.ShouldNotContain(w => w.Contains("vectors", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Index_WithoutEmbeddingsProvider_NoEmbeddingsPhaseEvents()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", Source);
        var progress = new RecordingProgress();

        new WorkspaceIndexer().Index(workspace, progress: progress);

        progress.Events.ShouldNotContain(e => e.Contains(":Embeddings:", StringComparison.Ordinal));
    }

    [Fact]
    public void Index_WithEmbeddings_ReportsEmbeddingsPhase()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", Source);
        var progress = new RecordingProgress();

        new WorkspaceIndexer().Index(
            workspace, progress: progress, embeddings: new FakeEmbeddingProvider(), vectorStore: new FakeVectorStore());

        progress.Events.ShouldContain(e =>
            e.StartsWith("phase:demo:Embeddings:", StringComparison.Ordinal) && !e.EndsWith(":0", StringComparison.Ordinal));
    }

    private static string NewWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        manager.AddRepo("repo", "demo");
        return workspace;
    }

    private static void WriteFile(string workspace, string relativePath, string content)
    {
        var fullPath = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private static MetaStore OpenStore(string workspace) => new(SqueezrPaths.MetaDbPath(workspace));

    private static List<ChunkDocument> ListChunks(string workspace)
    {
        using var store = OpenStore(workspace);
        var repoId = store.FindRepo("demo")!.Id;
        return store.ListFiles(repoId).SelectMany(f => store.ListChunksByFile(f.Id)).ToList();
    }

    private sealed class FakeEmbeddingProvider : IEmbeddingProvider
    {
        public int Dimensions { get; init; } = 4;

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbeddingUsage usage, CancellationToken ct)
        {
            return Task.FromResult(texts.Select(text =>
            {
                var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
                var vector = new float[Dimensions];
                for (var i = 0; i < Dimensions; i++)
                {
                    vector[i] = bytes[i % bytes.Length] / 255f;
                }

                return vector;
            }).ToArray());
        }
    }

    private sealed class FakeVectorStore : IVectorStore
    {
        public Dictionary<ulong, VectorPoint> Points { get; } = new();

        public bool Available { get; set; } = true;

        public bool FailDelete { get; set; }

        public int? CollectionSize { get; set; }

        public int DeleteCollectionCallCount { get; private set; }

        public int DimensionsEnsured { get; private set; }

        public int UpsertedPointCount { get; private set; }

        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(Available);

        public Task EnsureCollectionAsync(int dimensions, CancellationToken ct)
        {
            if (CollectionSize.HasValue && CollectionSize.Value != dimensions)
            {
                return Task.FromException(new VectorDimensionMismatchException(
                    "squeezr-fake", (ulong)CollectionSize.Value, dimensions));
            }

            CollectionSize = dimensions;
            DimensionsEnsured = dimensions;
            return Task.CompletedTask;
        }

        public Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct)
        {
            UpsertedPointCount += points.Count;
            foreach (var point in points)
            {
                Points[point.Id] = point;
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<VectorHit>> SearchAsync(float[] vector, int topK, uint? repoId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<long> CountAsync(CancellationToken ct) => Task.FromResult((long)Points.Count);

        public Task DeleteByFileIdsAsync(IReadOnlyList<uint> fileIds, CancellationToken ct)
        {
            if (FailDelete)
            {
                return Task.FromException(new HttpRequestException("qdrant is down"));
            }

            DeletedFileIds.AddRange(fileIds);
            return Task.CompletedTask;
        }

        public List<uint> DeletedFileIds { get; } = [];

        public Task DeleteCollectionAsync(CancellationToken ct)
        {
            DeleteCollectionCallCount++;
            Points.Clear();
            CollectionSize = null;
            return Task.CompletedTask;
        }

        public void ResetCounters() => UpsertedPointCount = 0;
    }

    private sealed class RecordingProgress : IIndexProgress
    {
        public List<string> Events { get; } = [];

        public void PhaseStarted(string repoName, IndexPhase phase, int totalItems) =>
            Events.Add($"phase:{repoName}:{phase}:{totalItems}");

        public void ItemProcessed(string repoName, string currentItem, int? symbolsInserted = null) =>
            Events.Add($"item:{repoName}:{currentItem}");

        public void RepoCompleted(string repoName) =>
            Events.Add($"done:{repoName}");
    }
}
