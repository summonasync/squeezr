using System.Security.Cryptography;
using System.Text;
using Shouldly;
using Squeezr.Core.Configuration;
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
    public void Index_DimensionMismatch_PreflightFailsBeforeAnyRepoWork()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", Source);
        var provider = new FakeEmbeddingProvider { Dimensions = 768 };
        var vectorStore = new FakeVectorStore { CollectionSize = 1536 };

        Should.Throw<VectorDimensionMismatchException>(() =>
            new WorkspaceIndexer().Index(workspace, embeddings: provider, vectorStore: vectorStore));

        provider.EmbedCallCount.ShouldBe(0);
        using var store = OpenStore(workspace);
        store.ListAllFiles().ShouldBeEmpty();
    }

    [Fact]
    public void Index_PreflightVectorStoreUnavailable_SkipsSilentlyAndIndexes()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", Source);
        var vectorStore = new FakeVectorStore { Available = false };

        var result = new WorkspaceIndexer().Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: vectorStore);

        result.Warnings.ShouldNotContain(w => w.Contains("vector preflight", StringComparison.OrdinalIgnoreCase));
        result.Warnings.ShouldContain(w => w.Contains("embeddings skipped", StringComparison.OrdinalIgnoreCase));
        ListChunks(workspace).ShouldNotBeEmpty();
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
    public void Index_AutoProviderWithResolution_ComputesModelKeyFromResolvedProvider()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", Source);
        var config = ConfigFile.Load(workspace);
        ConfigFile.Save(workspace, config with
        {
            Embeddings = config.Embeddings with { Provider = "auto" }
        });

        new WorkspaceIndexer().Index(
            workspace,
            embeddings: new FakeEmbeddingProvider(),
            vectorStore: new FakeVectorStore(),
            embeddingProviderResolution: new EmbeddingProviderResolution("ollama", null));

        ListChunks(workspace).ShouldAllBe(c =>
            c.EmbeddingStatus == "ok" && c.ModelKey == "ollama/nomic-embed-text/768/nomic-embed-text-v1.5");
    }

    [Fact]
    public void Index_AutoProviderWithoutResolution_ThrowsInsteadOfComputingAutoModelKey()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", Source);
        var config = ConfigFile.Load(workspace);
        ConfigFile.Save(workspace, config with
        {
            Embeddings = config.Embeddings with { Provider = "auto" }
        });

        var error = Should.Throw<WorkspaceException>(() =>
            new WorkspaceIndexer().Index(
                workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: new FakeVectorStore()));

        error.Message.ShouldContain("must be resolved");
    }

    [Fact]
    public void IndexFile_AutoProviderWithResolution_ComputesModelKeyFromResolvedProvider()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", Source);
        var config = ConfigFile.Load(workspace);
        ConfigFile.Save(workspace, config with
        {
            Embeddings = config.Embeddings with { Provider = "auto" }
        });

        new WorkspaceIndexer().IndexFile(
            workspace, "demo", "a.cs",
            new FakeEmbeddingProvider(), new FakeVectorStore(),
            embeddingProviderResolution: new EmbeddingProviderResolution("ollama", null));

        ListChunks(workspace).ShouldAllBe(c =>
            c.EmbeddingStatus == "ok" && c.ModelKey == "ollama/nomic-embed-text/768/nomic-embed-text-v1.5");
    }

    [Fact]
    public void IndexFile_AutoProviderWithoutResolution_ThrowsInsteadOfComputingAutoModelKey()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", Source);
        var config = ConfigFile.Load(workspace);
        ConfigFile.Save(workspace, config with
        {
            Embeddings = config.Embeddings with { Provider = "auto" }
        });

        var error = Should.Throw<WorkspaceException>(() =>
            new WorkspaceIndexer().IndexFile(
                workspace, "demo", "a.cs", new FakeEmbeddingProvider(), new FakeVectorStore()));

        error.Message.ShouldContain("must be resolved");
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

    [Fact]
    public void Run_HundredPendingChunks_UpsertsAndMarksPerBatch()
    {
        var dbPath = NewMetaDb(out var repoId, 100);
        using var store = new MetaStore(dbPath);
        var vectorStore = new FakeVectorStore();

        var result = EmbeddingIndexPhase.Run(
            store.FindRepo("demo")!, store, TestModelKey, new FakeEmbeddingProvider(), vectorStore, _ => { });

        result.Embedded.ShouldBe(100);
        vectorStore.UpsertCallCount.ShouldBe(4);
        vectorStore.UpsertBatchSizes.ShouldAllBe(size => size > 0 && size <= 32);
        CountChunkStates(store, repoId).ShouldBe((Total: 100, Pending: 0));
    }

    [Fact]
    public void Run_UpsertFailureAfterSecondBatch_MarksOnlyCompletedBatches()
    {
        var dbPath = NewMetaDb(out var repoId, 100);
        var vectorStore = new FakeVectorStore { FailUpsertOnCall = 3 };
        var warnings = new List<string>();

        EmbeddingPhaseResult result;
        using (var store = new MetaStore(dbPath))
        {
            result = EmbeddingIndexPhase.Run(
                store.FindRepo("demo")!, store, TestModelKey, new FakeEmbeddingProvider(), vectorStore, warnings.Add);
        }

        result.Embedded.ShouldBe(64);
        warnings.ShouldContain(w => w.Contains("embeddings skipped", StringComparison.OrdinalIgnoreCase));
        vectorStore.UpsertCallCount.ShouldBe(3);
        using (var store = new MetaStore(dbPath))
        {
            CountChunkStates(store, repoId).ShouldBe((Total: 100, Pending: 36));
        }
    }

    [Fact]
    public void Run_AfterFailedRun_ResumesEmbeddingOnlyRemainingChunks()
    {
        var dbPath = NewMetaDb(out var repoId, 100);
        var vectorStore = new FakeVectorStore { FailUpsertOnCall = 3 };
        using (var store = new MetaStore(dbPath))
        {
            EmbeddingIndexPhase.Run(
                store.FindRepo("demo")!, store, TestModelKey, new FakeEmbeddingProvider(), vectorStore, _ => { });
        }

        vectorStore.FailUpsertOnCall = null;
        var resumeProvider = new FakeEmbeddingProvider();
        EmbeddingPhaseResult resumed;
        using (var store = new MetaStore(dbPath))
        {
            resumed = EmbeddingIndexPhase.Run(
                store.FindRepo("demo")!, store, TestModelKey, resumeProvider, vectorStore, _ => { });
        }

        resumed.Embedded.ShouldBe(36);
        resumeProvider.EmbeddedTextCount.ShouldBe(36);
        vectorStore.UpsertedPointCount.ShouldBe(100);
        vectorStore.Points.Count.ShouldBe(100);
        using (var store = new MetaStore(dbPath))
        {
            CountChunkStates(store, repoId).ShouldBe((Total: 100, Pending: 0));
        }
    }

    [Fact]
    public void Run_ReportsCumulativeEmbeddedProgressAgainstTotal()
    {
        var dbPath = NewMetaDb(out var repoId, 100);
        using var store = new MetaStore(dbPath);
        var progress = new RecordingProgress();

        EmbeddingIndexPhase.Run(
            store.FindRepo("demo")!, store, TestModelKey, new FakeEmbeddingProvider(), new FakeVectorStore(), _ => { }, progress);

        progress.EmbeddingsTotal.ShouldBe(100);
        progress.EmbeddedSteps.ShouldBe(Enumerable.Range(1, 100).ToList());
    }

    [Fact]
    public void Run_DuplicateChunkHashAcrossBatches_EmbedsOnceAndMarksBothChunks()
    {
        var dbPath = NewMetaDb(out var repoId, 33, i => i == 33 ? "hash-0001" : $"hash-{i:D4}");
        using var store = new MetaStore(dbPath);
        var provider = new FakeEmbeddingProvider();
        var vectorStore = new FakeVectorStore();

        var result = EmbeddingIndexPhase.Run(
            store.FindRepo("demo")!, store, TestModelKey, provider, vectorStore, _ => { });

        result.Embedded.ShouldBe(33);
        provider.EmbedCallCount.ShouldBe(1);
        provider.EmbeddedTextCount.ShouldBe(32);
        vectorStore.UpsertCallCount.ShouldBe(2);
        vectorStore.UpsertBatchSizes.ShouldBe([32, 1]);
        vectorStore.Points.Count.ShouldBe(32);
        CountChunkStates(store, repoId).ShouldBe((Total: 33, Pending: 0));
    }

    private const string TestModelKey = "test/model/4";

    private static string NewMetaDb(out uint repoId, int chunkCount, Func<int, string>? hashSelector = null)
    {
        var dir = TestDirs.NewTempDir();
        var dbPath = Path.Combine(dir, "meta.db");
        using (var store = new MetaStore(dbPath, createIfMissing: true))
        {
            var repo = new RepoDocument { Id = 1, Name = "demo", Path = Path.Combine(dir, "repo") };
            store.AddRepo(repo);
            store.InsertChunks(Enumerable.Range(1, chunkCount).Select(i =>
            {
                var hash = hashSelector?.Invoke(i) ?? $"hash-{i:D4}";
                return new ChunkDocument
                {
                    Id = i,
                    FileId = 1,
                    RepoId = repo.Id,
                    StartLine = 1,
                    EndLine = 5,
                    Text = $"chunk text {hash}",
                    ChunkHash = hash,
                };
            }));
            repoId = repo.Id;
        }

        return dbPath;
    }

    private static (int Total, int Pending) CountChunkStates(MetaStore store, uint repoId) =>
        (store.CountChunks(repoId), store.CountPendingChunks(repoId, TestModelKey));

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

        public int EmbedCallCount { get; private set; }

        public int EmbeddedTextCount { get; private set; }

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbeddingUsage usage, CancellationToken ct)
        {
            EmbedCallCount++;
            EmbeddedTextCount += texts.Count;
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

        public int UpsertCallCount { get; private set; }

        public List<int> UpsertBatchSizes { get; } = [];

        public int? FailUpsertOnCall { get; set; }

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
            UpsertCallCount++;
            if (FailUpsertOnCall == UpsertCallCount)
            {
                return Task.FromException(new HttpRequestException("qdrant is down"));
            }

            UpsertBatchSizes.Add(points.Count);
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

        public Task<IReadOnlyList<ulong>> ScrollPointIdsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ulong>>(Points.Keys.ToList());

        public Task DeleteByFileIdsAsync(IReadOnlyList<uint> fileIds, CancellationToken ct)
        {
            if (FailDelete)
            {
                return Task.FromException(new HttpRequestException("qdrant is down"));
            }

            DeletedFileIds.AddRange(fileIds);
            return Task.CompletedTask;
        }

        public Task DeleteByPointIdsAsync(IReadOnlyList<ulong> pointIds, CancellationToken ct)
        {
            if (FailDelete)
            {
                return Task.FromException(new HttpRequestException("qdrant is down"));
            }

            foreach (var id in pointIds)
            {
                Points.Remove(id);
            }

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

        public void ResetCounters()
        {
            UpsertedPointCount = 0;
            UpsertCallCount = 0;
            UpsertBatchSizes.Clear();
        }
    }

    private sealed class RecordingProgress : IIndexProgress
    {
        public List<string> Events { get; } = [];

        public List<int> EmbeddedSteps { get; } = [];

        public int EmbeddingsTotal { get; private set; }

        private bool _inEmbeddings;
        private int _embeddedItems;

        public void PhaseStarted(string repoName, IndexPhase phase, int totalItems)
        {
            _inEmbeddings = phase == IndexPhase.Embeddings;
            if (_inEmbeddings)
            {
                EmbeddingsTotal = totalItems;
                _embeddedItems = 0;
            }

            Events.Add($"phase:{repoName}:{phase}:{totalItems}");
        }

        public void ItemProcessed(string repoName, string currentItem, int? symbolsInserted = null)
        {
            Events.Add($"item:{repoName}:{currentItem}");
            if (_inEmbeddings)
            {
                _embeddedItems++;
                EmbeddedSteps.Add(_embeddedItems);
            }
        }

        public void RepoCompleted(string repoName) =>
            Events.Add($"done:{repoName}");
    }
}
