using System.Security.Cryptography;
using System.Text;
using Shouldly;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing;

[Collection("ProcessGlobals")]
public class PendingQueueRefillTests
{
    private const string ModelKey = "onnx/nomic-embed-text-v1.5.onnx/768/nomic-embed-text-v1.5";

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

    // a comment sits in the class's leading trivia, so every symbol span shifts while the chunk texts
    // stay byte-identical: the file hash moves, the chunk hashes do not
    private const string ReflowedSource = "// reflowed\n" + Source;

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

    [Fact]
    public void Index_FullPassOverEmbeddedContent_EmbedsNothingAgain()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", Source);
        WriteFile(workspace, "repo/b.cs", OtherSource);
        var provider = new CountingEmbeddingProvider();
        var vectorStore = new FakeVectorStore();
        var indexer = new WorkspaceIndexer();
        indexer.Index(workspace, embeddings: provider, vectorStore: vectorStore);
        provider.EmbeddedTextCount.ShouldBeGreaterThan(0);

        provider.Reset();
        vectorStore.Calls.Clear();
        indexer.Index(workspace, full: true, embeddings: provider, vectorStore: vectorStore);

        provider.EmbeddedTextCount.ShouldBe(0);
        vectorStore.Calls.ShouldNotContain(c => c.StartsWith("upsert:", StringComparison.Ordinal));
        ListChunks(workspace).ShouldAllBe(c => c.EmbeddingStatus == "ok" && c.ModelKey == ModelKey);
    }

    [Fact]
    public void Index_FullPassWithoutEmbeddings_LeavesPendingQueueUnchanged()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", Source);
        WriteFile(workspace, "repo/b.cs", OtherSource);
        var indexer = new WorkspaceIndexer();
        indexer.Index(workspace, embeddings: new CountingEmbeddingProvider(), vectorStore: new FakeVectorStore());
        var pendingBefore = PendingCount(workspace);
        pendingBefore.ShouldBe(0);

        indexer.Index(workspace, full: true);

        PendingCount(workspace).ShouldBe(pendingBefore);
        ListChunks(workspace).ShouldAllBe(c => c.EmbeddingStatus == "ok" && c.ModelKey == ModelKey);
    }

    [Fact]
    public void IndexFile_ReflowedFileWithoutEmbeddings_KeepsEveryChunkMarker()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", Source);
        var indexer = new WorkspaceIndexer();
        indexer.Index(workspace, embeddings: new CountingEmbeddingProvider(), vectorStore: new FakeVectorStore());
        var fileId = FileIdOf(workspace, "a.cs");

        WriteFile(workspace, "repo/a.cs", ReflowedSource);
        indexer.IndexFile(workspace, "demo", "a.cs");

        PendingCount(workspace).ShouldBe(0);
        ListChunks(workspace, fileId).ShouldAllBe(c => c.EmbeddingStatus == "ok" && c.ModelKey == ModelKey);
    }

    [Fact]
    public void Index_EditedFileChunks_ReembedTheReapedFile()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", Source);
        WriteFile(workspace, "repo/b.cs", OtherSource);
        var provider = new CountingEmbeddingProvider();
        var vectorStore = new FakeVectorStore();
        var indexer = new WorkspaceIndexer();
        indexer.Index(workspace, embeddings: provider, vectorStore: vectorStore);

        var fileId = FileIdOf(workspace, "a.cs");
        WriteFile(workspace, "repo/a.cs", MovedBody);
        provider.Reset();
        vectorStore.Calls.Clear();

        indexer.Index(workspace, embeddings: provider, vectorStore: vectorStore);

        vectorStore.Calls.ShouldContain($"delete:{fileId}");
        provider.EmbeddedTextCount.ShouldBeGreaterThan(0);
        ListChunks(workspace, fileId).ShouldAllBe(c => c.EmbeddingStatus == "ok" && c.ModelKey == ModelKey);
        ListChunks(workspace, FileIdOf(workspace, "b.cs")).ShouldAllBe(c => c.EmbeddingStatus == "ok");
        // the reap deleted every point of the file, so a marker is only honest if the vectors are back:
        // without the re-queue in RequeueReapedFiles these rows would claim vectors that are not in the store
        foreach (var chunk in ListChunks(workspace, fileId))
        {
            vectorStore.Points.ContainsKey(ChunkPointId.FromHash(chunk.ChunkHash)).ShouldBeTrue();
        }
    }

    [Fact]
    public void Index_ModelKeyChanged_RequeuesOnceAndSaysSo()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", Source);
        var indexer = new WorkspaceIndexer();
        indexer.Index(workspace, embeddings: new CountingEmbeddingProvider(), vectorStore: new FakeVectorStore());
        var fileId = FileIdOf(workspace, "a.cs");
        using (var store = OpenStore(workspace))
        {
            store.MarkChunksEmbedded(store.ListChunksByFile(fileId).Select(c => c.Id), "onnx/an-older-model/768/older");
        }

        var provider = new CountingEmbeddingProvider();
        var first = indexer.Index(workspace, embeddings: provider, vectorStore: new FakeVectorStore());

        first.Warnings.ShouldContain(w => w.Contains("another embedding model", StringComparison.Ordinal));
        provider.EmbeddedTextCount.ShouldBeGreaterThan(0);
        ListChunks(workspace, fileId).ShouldAllBe(c => c.EmbeddingStatus == "ok" && c.ModelKey == ModelKey);

        provider.Reset();
        var second = indexer.Index(workspace, embeddings: provider, vectorStore: new FakeVectorStore());

        second.Warnings.ShouldNotContain(w => w.Contains("another embedding model", StringComparison.Ordinal));
        provider.EmbeddedTextCount.ShouldBe(0);
    }

    [Fact]
    public void ReplaceFileChunks_ReinsertsIdenticalHashes_KeepsMarkerAndClearsTheRest()
    {
        var dbPath = Path.Combine(TestDirs.NewTempDir(), "meta.db");
        using var store = new MetaStore(dbPath, createIfMissing: true);
        store.AddRepo(new RepoDocument { Id = 1, Name = "demo", Path = "repo" });
        store.InsertChunks([EmbeddedChunk(1, "hash-keep"), EmbeddedChunk(1, "hash-drop")]);

        store.ReplaceFileChunks(1, [NewChunk(1, "hash-keep"), NewChunk(1, "hash-new")]);

        var rows = store.ListChunksByFile(1);
        rows.Count.ShouldBe(2);
        rows.Single(c => c.ChunkHash == "hash-keep").EmbeddingStatus.ShouldBe("ok");
        rows.Single(c => c.ChunkHash == "hash-keep").ModelKey.ShouldBe(ModelKey);
        rows.Single(c => c.ChunkHash == "hash-new").EmbeddingStatus.ShouldBe("pending");
        store.CountPendingChunks(null).ShouldBe(1);
    }

    [Fact]
    public void ReplaceFileChunks_EmptyList_DropsEveryRowOfTheFile()
    {
        var dbPath = Path.Combine(TestDirs.NewTempDir(), "meta.db");
        using var store = new MetaStore(dbPath, createIfMissing: true);
        store.AddRepo(new RepoDocument { Id = 1, Name = "demo", Path = "repo" });
        store.InsertChunks([EmbeddedChunk(1, "hash-a"), EmbeddedChunk(2, "hash-b")]);

        store.ReplaceFileChunks(1, []);

        store.ListChunksByFile(1).ShouldBeEmpty();
        store.ListChunksByFile(2).ShouldHaveSingleItem();
    }

    private static ChunkDocument EmbeddedChunk(uint fileId, string hash) =>
        new()
        {
            FileId = fileId,
            RepoId = 1,
            StartLine = 1,
            EndLine = 4,
            Text = $"body {hash}",
            ChunkHash = hash,
            EmbeddingStatus = "ok",
            ModelKey = ModelKey
        };

    private static ChunkDocument NewChunk(uint fileId, string hash) =>
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

    private static uint FileIdOf(string workspace, string path)
    {
        using var store = OpenStore(workspace);
        var repoId = store.FindRepo("demo")!.Id;
        return store.ListFiles(repoId).Single(f => f.Path == path).Id;
    }

    private static int PendingCount(string workspace)
    {
        using var store = OpenStore(workspace);
        return store.CountPendingChunks(null);
    }

    private static List<ChunkDocument> ListChunks(string workspace)
    {
        using var store = OpenStore(workspace);
        var repoId = store.FindRepo("demo")!.Id;
        return store.ListFiles(repoId).SelectMany(f => store.ListChunksByFile(f.Id)).ToList();
    }

    private static List<ChunkDocument> ListChunks(string workspace, uint fileId)
    {
        using var store = OpenStore(workspace);
        return store.ListChunksByFile(fileId).ToList();
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
                var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
                return Enumerable.Range(0, Dimensions).Select(i => bytes[i % bytes.Length] / 255f).ToArray();
            }).ToArray());
        }
    }

    private sealed class FakeVectorStore : IVectorStore
    {
        public Dictionary<ulong, VectorPoint> Points { get; } = new();

        public List<string> Calls { get; } = [];

        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

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

        public Task<IReadOnlyList<uint>> ScrollFileIdsWithoutKindAsync(uint repoId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<uint>>([]);

        public Task SetKindPayloadsAsync(IReadOnlyDictionary<uint, string> kindByFileId, CancellationToken ct) =>
            Task.CompletedTask;

        public Task DeleteByFileIdsAsync(IReadOnlyList<uint> fileIds, CancellationToken ct)
        {
            Calls.Add($"delete:{string.Join(",", fileIds)}");
            foreach (var id in Points.Where(kv => fileIds.Contains(kv.Value.FileId)).Select(kv => kv.Key).ToList())
            {
                Points.Remove(id);
            }

            return Task.CompletedTask;
        }

        public Task DeleteByPointIdsAsync(IReadOnlyList<ulong> pointIds, CancellationToken ct)
        {
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
