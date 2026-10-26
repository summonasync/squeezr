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

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void FullIndex_UnreadableDirectoryKeepsSubtreeRowsAndBothKeepsAgree()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/open.cs", First);
        WriteFile(workspace, "repo/vault/deep.cs", Second);
        var vectorStore = new FakeVectorStore();
        var indexer = new WorkspaceIndexer();
        indexer.Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: vectorStore);

        var deepId = FileIdOf(workspace, "vault/deep.cs");
        var deepHashes = ChunkHashes(workspace, deepId);
        deepHashes.ShouldNotBeEmpty();
        var vaultPath = Path.Combine(workspace, "repo", "vault");
        var originalMode = File.GetUnixFileMode(vaultPath);
        File.SetUnixFileMode(vaultPath, UnixFileMode.None);
        try
        {
            // the premise is asserted, not assumed: stat into the subtree must be denied, and File.Exists
            // must answer false for a file that is still on disk — that collapse is the bug's whole shape
            Should.Throw<UnauthorizedAccessException>(() => Directory.EnumerateFileSystemEntries(vaultPath).ToList());
            Should.Throw<UnauthorizedAccessException>(() => File.GetAttributes(Path.Combine(vaultPath, "deep.cs")));
            File.Exists(Path.Combine(vaultPath, "deep.cs")).ShouldBeFalse();

            var pass = indexer.Index(
                workspace, full: true, embeddings: new FakeEmbeddingProvider(), vectorStore: vectorStore);

            using (var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace)))
            {
                store.FindFileById(deepId).ShouldNotBeNull(
                    "a directory the walk declared kept must not be reaped by the rebuild in the same pass");
            }

            pass.Warnings.ShouldContain(w =>
                w.Contains("unreadable directory 'vault'", StringComparison.Ordinal)
                && w.Contains("index rows under it kept", StringComparison.Ordinal));
            pass.Warnings.ShouldContain(w =>
                w.Contains("keeping 'vault/deep.cs' in the index store: unreadable", StringComparison.Ordinal)
                && w.Contains("rows kept", StringComparison.Ordinal));
            pass.Warnings.ShouldNotContain(w =>
                w.Contains("vault/deep.cs", StringComparison.Ordinal)
                && w.Contains("missing on disk", StringComparison.Ordinal));

            PendingReapIds(workspace).ShouldBeEmpty();
            ChunkHashes(workspace, deepId).ShouldBe(deepHashes);
            ListChunks(workspace, deepId).ShouldAllBe(c => c.EmbeddingStatus == "ok");
            deepHashes.ShouldAllBe(hash => vectorStore.Points.ContainsKey(ChunkPointId.FromHash(hash)));

            var reader = new TrigramIndexReader(SqueezrPaths.TrigramIndexPath(workspace));
            var carried = Enumerable.Range(0, (int)reader.DocCount)
                .Select(docId => (DocId: (uint)docId, Doc: reader.GetDoc((uint)docId)))
                .Where(entry => entry.Doc.FileId == deepId)
                .ToList();
            carried.Count.ShouldBe(1);
            reader.GetDocumentPostings(carried[0].DocId).ShouldNotBeEmpty();
        }
        finally
        {
            File.SetUnixFileMode(vaultPath, originalMode);
        }
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void Index_UnreadableDirectory_KeepsSubtreeRowsOnTheIncrementalPath()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/open.cs", First);
        WriteFile(workspace, "repo/vault/deep.cs", Second);
        var vectorStore = new FakeVectorStore();
        var indexer = new WorkspaceIndexer();
        indexer.Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: vectorStore);

        var deepId = FileIdOf(workspace, "vault/deep.cs");
        var deepHashes = ChunkHashes(workspace, deepId);
        var vaultPath = Path.Combine(workspace, "repo", "vault");
        var originalMode = File.GetUnixFileMode(vaultPath);
        File.SetUnixFileMode(vaultPath, UnixFileMode.None);
        try
        {
            Should.Throw<UnauthorizedAccessException>(() => Directory.EnumerateFileSystemEntries(vaultPath).ToList());

            var pass = indexer.Index(
                workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: vectorStore);

            using (var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace)))
            {
                store.FindFileById(deepId).ShouldNotBeNull();
            }

            pass.Warnings.ShouldContain(w =>
                w.Contains("unreadable directory 'vault'", StringComparison.Ordinal)
                && w.Contains("index rows under it kept", StringComparison.Ordinal));
            pass.Warnings.ShouldNotContain(w =>
                w.Contains("vault/deep.cs", StringComparison.Ordinal)
                && (w.Contains("missing on disk", StringComparison.Ordinal)
                    || w.Contains("dropping", StringComparison.Ordinal)));
            PendingReapIds(workspace).ShouldBeEmpty();
            ChunkHashes(workspace, deepId).ShouldBe(deepHashes);
            deepHashes.ShouldAllBe(hash => vectorStore.Points.ContainsKey(ChunkPointId.FromHash(hash)));
        }
        finally
        {
            File.SetUnixFileMode(vaultPath, originalMode);
        }
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void RebuildTrigramIndex_RowUnderUnreadableDirectory_KeepsWhileDeletedSiblingStillReaps()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/vault/deep.cs", Second);
        WriteFile(workspace, "repo/gone.cs", First);
        new WorkspaceIndexer().Index(workspace);

        var deepId = FileIdOf(workspace, "vault/deep.cs");
        var goneId = FileIdOf(workspace, "gone.cs");
        File.Delete(Path.Combine(workspace, "repo", "gone.cs"));
        var vaultPath = Path.Combine(workspace, "repo", "vault");
        var originalMode = File.GetUnixFileMode(vaultPath);
        File.SetUnixFileMode(vaultPath, UnixFileMode.None);
        try
        {
            Should.Throw<UnauthorizedAccessException>(() => File.GetAttributes(Path.Combine(vaultPath, "deep.cs")));

            var warnings = new List<string>();
            using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
            WorkspaceIndexer.RebuildTrigramIndex(
                workspace, store, WorkspaceManifestFile.Load(workspace), warnings,
                new Dictionary<uint, string>(), SqueezrPaths.TrigramIndexPath(workspace));

            store.FindFileById(deepId).ShouldNotBeNull();
            store.FindFileById(goneId).ShouldBeNull(
                "a readable directory with no file under it is still a deletion: the keep must not swallow it");
            warnings.ShouldContain(w =>
                w.Contains("keeping 'vault/deep.cs' in the index store: unreadable", StringComparison.Ordinal)
                && w.Contains("rows kept", StringComparison.Ordinal));
            warnings.ShouldContain(w =>
                w.Contains("dropping 'gone.cs' from index store: file is missing on disk", StringComparison.Ordinal));
        }
        finally
        {
            File.SetUnixFileMode(vaultPath, originalMode);
        }
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void FreshnessRefresh_RowUnderDeniedDirectory_KeepsWhileDeletedSiblingStillReaps()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/vault/deep.cs", Second);
        WriteFile(workspace, "repo/gone.cs", First);
        new WorkspaceIndexer().Index(workspace);

        var deepId = FileIdOf(workspace, "vault/deep.cs");
        var goneId = FileIdOf(workspace, "gone.cs");
        File.Delete(Path.Combine(workspace, "repo", "gone.cs"));
        var vaultPath = Path.Combine(workspace, "repo", "vault");
        var originalMode = File.GetUnixFileMode(vaultPath);
        File.SetUnixFileMode(vaultPath, UnixFileMode.None);
        try
        {
            // the premise is asserted, not assumed: stat into the denied subtree fails, and File.Exists
            // answers the same false a real deletion would — that collapse is the bug's whole shape
            Should.Throw<UnauthorizedAccessException>(() => File.GetAttributes(Path.Combine(vaultPath, "deep.cs")));
            File.Exists(Path.Combine(vaultPath, "deep.cs")).ShouldBeFalse();

            var result = FreshnessGuard.CheckAndRefresh(
                workspace, [("demo", "vault/deep.cs"), ("demo", "gone.cs")]);

            result.Refreshed.ShouldContain("demo/gone.cs");

            using (var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace)))
            {
                store.FindFileById(deepId).ShouldNotBeNull(
                    "a denied stat is not a deletion: the freshness refresh must not reap the row");
                store.FindFileById(goneId).ShouldBeNull(
                    "a genuine deletion still reaps on the same route");
            }
        }
        finally
        {
            File.SetUnixFileMode(vaultPath, originalMode);
        }
    }

    [Fact]
    public void IndexFile_PathTheProbeCannotShape_NeverEscapesTheProbe()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/real.cs", First);
        new WorkspaceIndexer().Index(workspace);

        Should.NotThrow(() => new WorkspaceIndexer().IndexFile(workspace, "demo", "bad\0path.cs"));
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void FreshnessRefresh_FileReplacedByDirectory_ReapsWithoutThrowing()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/swapped", Second);
        WriteFile(workspace, "repo/keep.cs", First);
        new WorkspaceIndexer().Index(workspace);

        var swappedId = FileIdOf(workspace, "swapped");
        File.Delete(Path.Combine(workspace, "repo", "swapped"));
        Directory.CreateDirectory(Path.Combine(workspace, "repo", "swapped"));
        // premise asserted: the stored path now resolves to a directory — present for stat, but no
        // regular file behind it, which is the shape the probe-present fallthrough has to judge
        File.Exists(Path.Combine(workspace, "repo", "swapped")).ShouldBeFalse();
        UnixFileType.IsRegularFile(Path.Combine(workspace, "repo", "swapped")).ShouldBeFalse();

        FreshnessResult result = default;
        Should.NotThrow(() => result = FreshnessGuard.CheckAndRefresh(
            workspace, [("demo", "swapped"), ("demo", "keep.cs")]));

        result.Refreshed.ShouldContain("demo/swapped");
        result.Refreshed.ShouldContain("demo/keep.cs");

        using (var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace)))
        {
            store.FindFileById(FileIdOf(workspace, "keep.cs")).ShouldNotBeNull(
                "the readable sibling must survive the same refresh untouched");
        }

        using (var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace)))
        {
            store.FindFileById(swappedId).ShouldBeNull(
                "a directory took the path: nothing readable backs the row, the swap reaps without a throw");
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

        public Task<IReadOnlyList<VectorHit>> SearchAsync(float[] vector, int topK, VectorSearchFilter filter, CancellationToken ct) =>
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
