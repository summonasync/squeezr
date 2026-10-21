using System.Runtime.Versioning;
using Shouldly;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Trigram;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing;

[Collection("ProcessGlobals")]
public sealed class WorkspaceIndexerUnreadableTests
{
    private const string First = "class Alpha\n{\n    int Sum(int a, int b) => a + b;\n}\n";
    private const string Changed = "class Alpha\n{\n    int Sum(int a, int b) => a + b + 1;\n}\n";
    private const string Second = "class Beta\n{\n    string Name() => \"beta\";\n}\n";

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void Index_UnreadableFileKeepsItsRowsWhileAMissingOneStillReaps()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/alpha.cs", First);
        WriteFile(workspace, "repo/beta.cs", Second);
        var vectorStore = new FakeVectorStore();
        var indexer = new WorkspaceIndexer();
        indexer.Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: vectorStore);

        var alphaId = FileIdOf(workspace, "alpha.cs");
        var alphaHashes = ChunkHashes(workspace, alphaId);
        var betaId = FileIdOf(workspace, "beta.cs");
        var betaHashes = ChunkHashes(workspace, betaId);
        var alphaPath = Path.Combine(workspace, "repo", "alpha.cs");
        File.SetUnixFileMode(alphaPath, UnixFileMode.None);
        Should.Throw<UnauthorizedAccessException>(() => File.ReadAllText(alphaPath));
        File.Delete(Path.Combine(workspace, "repo", "beta.cs"));
        vectorStore.Calls.Clear();

        var pass = indexer.Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: vectorStore);

        pass.Warnings.ShouldContain(w => w.Contains("unreadable 'alpha.cs'", StringComparison.Ordinal));
        pass.Warnings.ShouldContain(w => w.Contains("index rows kept", StringComparison.Ordinal));
        PendingReapIds(workspace).ShouldBeEmpty();
        ChunkHashes(workspace, alphaId).ShouldBe(alphaHashes);
        ListChunks(workspace, alphaId).ShouldAllBe(c => c.EmbeddingStatus == "ok");
        alphaHashes.ShouldAllBe(hash => vectorStore.Points.ContainsKey(ChunkPointId.FromHash(hash)));
        vectorStore.Calls.ShouldNotContain($"delete:{alphaId}");

        using (var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace)))
        {
            store.FindFileById(betaId).ShouldBeNull();
        }

        betaHashes.ShouldAllBe(hash => !vectorStore.Points.ContainsKey(ChunkPointId.FromHash(hash)));
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void Index_RestoredReadability_ResumesTheNormalShaPathWithoutDoubleIndexing()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/alpha.cs", First);
        var vectorStore = new FakeVectorStore();
        var indexer = new WorkspaceIndexer();
        indexer.Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: vectorStore);

        var alphaId = FileIdOf(workspace, "alpha.cs");
        var alphaPath = Path.Combine(workspace, "repo", "alpha.cs");
        var originalMode = File.GetUnixFileMode(alphaPath);
        File.SetUnixFileMode(alphaPath, UnixFileMode.None);
        indexer.Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: vectorStore);
        File.SetUnixFileMode(alphaPath, originalMode);

        vectorStore.Calls.Clear();
        indexer.Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: vectorStore);

        vectorStore.Calls.ShouldBeEmpty();
        FileIdOf(workspace, "alpha.cs").ShouldBe(alphaId);

        WriteFile(workspace, "repo/alpha.cs", Changed);
        indexer.Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: vectorStore);

        FileIdOf(workspace, "alpha.cs").ShouldBe(alphaId);
        vectorStore.Calls.ShouldContain($"delete:{alphaId}");
        PointsOfFile(vectorStore, alphaId).Select(p => p.ChunkHash)
            .ShouldBe(ChunkHashes(workspace, alphaId), ignoreOrder: true);
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void FullIndex_UnreadableFileKeepsRowsAndCarriesItsTrigramPostings()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/alpha.cs", First);
        WriteFile(workspace, "repo/beta.cs", Second);
        var vectorStore = new FakeVectorStore();
        var indexer = new WorkspaceIndexer();
        indexer.Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: vectorStore);

        var alphaId = FileIdOf(workspace, "alpha.cs");
        var alphaHashes = ChunkHashes(workspace, alphaId);
        var trigramPath = SqueezrPaths.TrigramIndexPath(workspace);
        File.SetUnixFileMode(Path.Combine(workspace, "repo", "alpha.cs"), UnixFileMode.None);
        vectorStore.Calls.Clear();

        var pass = indexer.Index(workspace, full: true, embeddings: new FakeEmbeddingProvider(), vectorStore: vectorStore);

        pass.Warnings.ShouldContain(w =>
            w.Contains("keeping 'alpha.cs' in the index store: unreadable", StringComparison.Ordinal));
        pass.Warnings.ShouldContain(w => w.Contains("rows kept", StringComparison.Ordinal));
        PendingReapIds(workspace).ShouldBeEmpty();
        ChunkHashes(workspace, alphaId).ShouldBe(alphaHashes);
        ListChunks(workspace, alphaId).ShouldAllBe(c => c.EmbeddingStatus == "ok");
        alphaHashes.ShouldAllBe(hash => vectorStore.Points.ContainsKey(ChunkPointId.FromHash(hash)));
        vectorStore.Calls.ShouldNotContain(c => c.StartsWith("delete:", StringComparison.Ordinal));

        var reader = new TrigramIndexReader(trigramPath);
        var carried = Enumerable.Range(0, (int)reader.DocCount)
            .Select(docId => (DocId: (uint)docId, Doc: reader.GetDoc((uint)docId)))
            .Where(entry => entry.Doc.FileId == alphaId)
            .ToList();
        carried.Count.ShouldBe(1);
        reader.GetDocumentPostings(carried[0].DocId).ShouldNotBeEmpty();
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
                var bytes = System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(text));
                return Enumerable.Range(0, Dimensions).Select(i => bytes[i % bytes.Length] / 255f).ToArray();
            }).ToArray());
    }

    private sealed class FakeVectorStore : IVectorStore
    {
        public Dictionary<ulong, VectorPoint> Points { get; } = new();

        public List<string> Calls { get; } = [];

        public bool Available { get; set; } = true;

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
            Calls.Add($"delete:{string.Join(",", fileIds)}");
            foreach (var id in Points.Where(kv => fileIds.Contains(kv.Value.FileId)).Select(kv => kv.Key).ToList())
            {
                Points.Remove(id);
            }

            return Task.CompletedTask;
        }

        public Task DeleteByPointIdsAsync(IReadOnlyList<ulong> pointIds, CancellationToken ct)
        {
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
