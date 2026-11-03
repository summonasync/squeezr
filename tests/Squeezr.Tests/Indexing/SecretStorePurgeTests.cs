using Shouldly;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Indexing.Trigram;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Indexing;

using static Squeezr.Tests.Indexing.IngestionWorkspaces;

// A store that already holds a secret is the case deny-on-ingestion cannot fix, so this is the purge: the rows
// leave through the drop path an ignored file already used, the names a queued point cleanup kept for them go
// too, and acceptance is a grep over the raw bytes of the store files. A document counter proves nothing about
// pages the engine never gave back, and a report naming what it removed would tell more than the index does.
public class SecretStorePurgeTests
{
    private const string AnchoredExtra = "infra/VaultKeys.cs";

    private static readonly string[] PurgedPaths =
    [
        EnvPath, PemPath, SecretGuardFixture.SecretsPath, SecretGuardFixture.TokensPath
    ];

    [Fact]
    public void Compact_PreGuardRowsInStore_PurgesEveryProtectedPathAndCountsTheRows()
    {
        var workspace = IndexedStoreHoldingSecrets();
        var expectedRows = ProtectedRowsInStore(workspace, out var expectedPaths);
        expectedPaths.ShouldBe(4);

        var result = WorkspaceCompactor.Compact(workspace);

        result.PathsPurged.ShouldBe(expectedPaths);
        result.RowsPurged.ShouldBe(expectedRows);
        StoredPaths(workspace).ShouldBe([SecretGuardFixture.ExamplePath, AppPath], StringComparer.Ordinal);
        result.Warnings.ShouldNotBeNull()
            .ShouldContain(w => w.Contains("stored path(s) purged from the index store"));
        foreach (var warning in result.Warnings)
        {
            foreach (var path in PurgedPaths)
            {
                warning.ShouldNotContain(path);
            }
        }
    }

    [Fact]
    public void Compact_PurgedPath_LeavesNoOrphanSymbolChunkOrRefRows()
    {
        var workspace = IndexedStoreHoldingSecrets();
        SecretGuardFixture.PlantSymbol(workspace, EnvPath, "Run", "method");
        SecretGuardFixture.PlantSymbol(workspace, EnvPath, "Configure", "method");
        SecretGuardFixture.PlantReferenceFrom(workspace, "Run", EnvPath, 1);
        SecretGuardFixture.PlantReferenceFrom(workspace, "Configure", AppPath, 2);

        uint envId;
        uint appFileId;
        long[] envSymbolIds;
        int expectedRows;
        using (var before = new MetaStore(SqueezrPaths.MetaDbPath(workspace)))
        {
            envId = before.ListAllFiles().Single(f => f.Path == EnvPath).Id;
            appFileId = before.ListAllFiles().Single(f => f.Path == AppPath).Id;
            envSymbolIds = [.. before.ListSymbolsByFile(envId).Select(s => s.Id)];
        }

        envSymbolIds.Length.ShouldBe(2);
        expectedRows = ProtectedRowsInStore(workspace, out var expectedPaths);
        expectedRows.ShouldBeGreaterThan(6);

        var result = WorkspaceCompactor.Compact(workspace);

        result.PathsPurged.ShouldBe(expectedPaths);
        result.RowsPurged.ShouldBe(expectedRows);
        using var after = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        after.ListSymbolsByFile(envId).ShouldBeEmpty();
        after.ListChunksByFile(envId).ShouldBeEmpty();
        after.ListRefsByFile(envId).ShouldBeEmpty();
        foreach (var symbolId in envSymbolIds)
        {
            after.ListRefsByFromSymbol(symbolId).ShouldBeEmpty();
        }

        // the ordinary file kept its rows: the retired symbols' refs are gone, its own refs are not
        after.ListRefsByFile(appFileId).ShouldNotBeEmpty();
        after.ListAllFiles().SelectMany(f => after.ListRefsByFile(f.Id))
            .Where(r => envSymbolIds.Contains(r.FromSymbolId))
            .ShouldBeEmpty();
        after.ListAllFiles().Select(f => f.Path).ShouldBe(
            [SecretGuardFixture.ExamplePath, AppPath], StringComparer.Ordinal);
    }

    [Fact]
    public void Compact_RawStoreBytes_HoldNeitherTheCanaryValuesNorTheProtectedNames()
    {
        var workspace = IndexedStoreHoldingSecrets();

        WorkspaceCompactor.Compact(workspace);

        var meta = ReadAscii(SqueezrPaths.MetaDbPath(workspace));
        var trigram = ReadAscii(SqueezrPaths.TrigramIndexPath(workspace));
        foreach (var canary in SecretGuardFixture.ProtectedCanaries)
        {
            meta.ShouldNotContain(canary);
            trigram.ShouldNotContain(canary);
        }

        foreach (var path in new[] { PemPath, SecretGuardFixture.SecretsPath, SecretGuardFixture.TokensPath })
        {
            meta.ShouldNotContain(path);
            trigram.ShouldNotContain(path);
        }

        // the carve-out row is a legitimate neighbour whose name literally contains ".env", so a sweep for the
        // bare name has to subtract it before the answer means anything
        meta.Replace(".env.example", string.Empty).ShouldNotContain(".env");

        StoreDatabases(workspace).ShouldBe(["meta.db"], StringComparer.Ordinal);
    }

    [Fact]
    public void Compact_PurgedPostings_LeaveTheTrigramIndexAndTheFileShrinks()
    {
        var workspace = IndexedStoreHoldingSecrets();
        var purgedIds = PurgedFileIds(workspace);
        var appFileId = StoredId(workspace, AppPath);
        var sizeBefore = new FileInfo(SqueezrPaths.TrigramIndexPath(workspace)).Length;

        var result = WorkspaceCompactor.Compact(workspace);

        purgedIds.Length.ShouldBe(4);
        new FileInfo(SqueezrPaths.TrigramIndexPath(workspace)).Length.ShouldBeLessThan(sizeBefore);
        result.SizeAfterBytes.ShouldBeLessThan(result.SizeBeforeBytes);
        var reader = new TrigramIndexReader(SqueezrPaths.TrigramIndexPath(workspace));
        var liveDocs = Enumerable.Range(0, (int)reader.DocCount)
            .Select(i => reader.GetDoc((uint)i))
            .ToList();
        liveDocs.ShouldNotContain(doc => purgedIds.Contains(doc.FileId));
        liveDocs.ShouldContain(doc => doc.FileId == appFileId);
        reader.SegmentStats!.TombstonedSegments.ShouldBe(0);
    }

    [Fact]
    public void Compact_ReapJournalNamingAProtectedPath_RedactsTheNameAndKeepsTheOwedCleanup()
    {
        var workspace = IndexedStoreHoldingSecrets();
        uint orphanedId;
        uint repoId;
        using (var before = new MetaStore(SqueezrPaths.MetaDbPath(workspace)))
        {
            // a journal an older binary wrote: it names the file the id meant, because a freed id gets recycled
            orphanedId = before.ListAllFiles().Single(f => f.Path == PemPath).Id;
            repoId = RepoId(before);
            before.EnqueuePendingReaps(
            [
                new PendingReapTarget(orphanedId, repoId, PemPath),
                new PendingReapTarget(4242, repoId, AppPath),
            ]);
        }

        var result = WorkspaceCompactor.Compact(workspace);

        result.ReapJournalRedacted.ShouldBe(1);
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var entries = store.ListPendingReaps();
        entries.ShouldContain(e => e.Path == null && e.FileId == orphanedId);
        entries.ShouldContain(e => e.FileId == 4242 && e.Path == AppPath);
        ReadAscii(SqueezrPaths.MetaDbPath(workspace)).ShouldNotContain(PemPath);
        result.Warnings.ShouldNotBeNull()
            .ShouldContain(w => w.Contains("queued point cleanup(s) redacted in the reap journal"));
    }

    [Fact]
    public void Compact_RecordedTelemetryNamingAProtectedPath_RedactsItAndKeepsTheLine()
    {
        var workspace = IndexedStoreHoldingSecrets();
        var session = Guid.NewGuid();
        QueryTelemetry.Record(
            workspace, session, "expand", new { file = EnvPath, startLine = 12, maxTokens = 300 },
            40, false, 1, 4);
        QueryTelemetry.Record(
            workspace, session, "code_search", new { query = "how does Load work", prefix = "src/" },
            60, false, 3, 9);

        var result = WorkspaceCompactor.Compact(workspace);

        result.TelemetryValuesRedacted.ShouldBe(1);
        var lines = File.ReadAllLines(SqueezrPaths.QueriesLogPath(workspace));
        lines.Length.ShouldBe(2);
        string.Join('\n', lines).ShouldNotContain(EnvPath);
        lines[0].ShouldContain(QueryTelemetry.RedactedPlaceholder);
        lines[0].ShouldContain("\"startLine\":12");
        lines[1].ShouldContain("how does Load work");
        lines[1].ShouldContain("src/");
        result.Warnings.ShouldNotBeNull()
            .ShouldContain(w => w.Contains("recorded parameter value(s) redacted"));
    }

    [Fact]
    public void Compact_TelemetryLockBusy_SaysSoInTheReportInsteadOfASilentZero()
    {
        var workspace = IndexedStoreHoldingSecrets();
        QueryTelemetry.Record(
            workspace, Guid.NewGuid(), "expand", new { file = EnvPath, startLine = 3 },
            40, false, 1, 4);

        using (var held = TelemetryWriteLock.TryAcquire(SqueezrPaths.StoreDir(workspace)))
        {
            held.ShouldNotBeNull();
            var busy = WorkspaceCompactor.Compact(workspace);

            busy.TelemetryValuesRedacted.ShouldBe(0);
            busy.Warnings.ShouldNotBeNull()
                .ShouldContain(w => w.Contains("queries.jsonl scrub skipped"));
        }

        var retried = WorkspaceCompactor.Compact(workspace);

        retried.TelemetryValuesRedacted.ShouldBe(1);
        File.ReadAllText(SqueezrPaths.QueriesLogPath(workspace)).ShouldNotContain(EnvPath);
    }

    [Fact]
    public void Index_VectorStoreUnavailable_QueuesTheReapsWithoutWritingTheProtectedNames()
    {
        var workspace = IndexedStoreHoldingSecrets();
        MarkChunksEmbedded(workspace, PemPath);
        var purgedIds = PurgedFileIds(workspace);
        var vectorStore = new UnavailableVectorStore();

        new WorkspaceIndexer().Index(
            workspace,
            embeddings: new SecretGuardFixture.FixedEmbeddings(),
            vectorStore: vectorStore,
            embeddingProviderResolution: new EmbeddingProviderResolution("ollama", null));

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var entries = store.ListPendingReaps();
        entries.Select(e => (uint)e.FileId).ShouldBe(purgedIds, ignoreOrder: true);
        entries.ShouldAllBe(e => e.Path == null);
        vectorStore.Deleted.ShouldBeEmpty();
        ReadAscii(SqueezrPaths.MetaDbPath(workspace)).ShouldNotContain(PemPath);
        StoredPaths(workspace).ShouldNotContain(PemPath);
    }

    [Fact]
    public void Compact_TelemetryNamingAProtectedPathInAQualifiedForm_StillFoldsAndRedacts()
    {
        var workspace = IndexedStoreHoldingSecrets();
        SecretGuardFixture.SetGuardExtra(workspace, "**/tokens.txt", AnchoredExtra);
        var session = Guid.NewGuid();
        QueryTelemetry.Record(
            workspace, session, "expand", new { file = EnvPath, startLine = 6 }, 40, false, 1, 4);
        // the anchored extra is the shape a qualified or absolute parameter breaks on: read raw, neither one
        // matches 'infra/VaultKeys.cs', and the name would survive the pass that retired the row it points at
        QueryTelemetry.Record(
            workspace, session, "expand", new { file = $"demo/{AnchoredExtra}", startLine = 7 }, 40, false, 1, 4);
        QueryTelemetry.Record(
            workspace, session, "expand",
            new { file = IngestionWorkspaces.Full(workspace, AnchoredExtra), startLine = 8 }, 40, false, 1, 4);
        QueryTelemetry.Record(
            workspace, session, "expand", new { file = AppPath, startLine = 9 }, 40, false, 1, 4);
        var before = File.ReadAllText(SqueezrPaths.QueriesLogPath(workspace));
        before.ShouldContain(AnchoredExtra);
        SecretGuard.IsProtected($"demo/{AnchoredExtra}", [AnchoredExtra]).ShouldBeFalse();

        var result = WorkspaceCompactor.Compact(workspace);

        result.TelemetryValuesRedacted.ShouldBe(3);
        var after = File.ReadAllText(SqueezrPaths.QueriesLogPath(workspace));
        after.ShouldNotContain(AnchoredExtra);
        after.ShouldNotContain(EnvPath);
        after.ShouldContain(AppPath);
        after.ShouldContain("\"startLine\":7");
        after.ShouldContain("\"startLine\":9");
    }

    [Fact]
    public void Compact_VectorsOwedForEveryPurgedPath_TellsOnceForTheWholePurge()
    {
        var workspace = IndexedStoreHoldingSecrets();
        foreach (var path in PurgedPaths)
        {
            MarkChunksEmbedded(workspace, path);
        }

        var result = WorkspaceCompactor.Compact(workspace);

        result.PointsQueued.ShouldBe(PurgedPaths.Length);
        var deferred = result.Warnings!
            .Where(w => w.Contains("deferred to a later pass", StringComparison.Ordinal))
            .ToList();

        // one line for four retired files: a store holding 300 secrets would otherwise print 300 identical
        // warnings and bury the report the operator ran the command for
        deferred.Count.ShouldBe(1);
        deferred[0].ShouldContain($"point cleanup for {PurgedPaths.Length} file(s)");
        foreach (var path in PurgedPaths)
        {
            deferred[0].ShouldNotContain(path);
        }
    }

    // The half of the W-1 shape the purge itself cannot prove: an entry that lost its name still has to pay for
    // the points it owes, and it must never pay for the points of whoever inherited the id. Without these two
    // pins "keep the id, drop the name" is only a claim about bytes.
    [Fact]
    public void Index_RedactedEntryWithNoRowBehindTheId_ReapsTheOwedPointsAndDrainsTheJournal()
    {
        var workspace = IndexedStoreHoldingSecrets();
        foreach (var path in PurgedPaths)
        {
            MarkChunksEmbedded(workspace, path);
        }

        var doomedIds = PurgedFileIds(workspace);
        var survivorId = StoredId(workspace, SecretGuardFixture.AppPath);
        var vectorStore = new ReplayingVectorStore();
        vectorStore.Seed(doomedIds);
        vectorStore.Seed([survivorId]);

        WorkspaceCompactor.Compact(workspace);
        using (var queued = new MetaStore(SqueezrPaths.MetaDbPath(workspace)))
        {
            var entries = queued.ListPendingReaps();
            entries.ShouldNotBeEmpty();
            entries.ShouldAllBe(entry => entry.Path == null);
        }

        vectorStore.Calls.Clear();
        var replayed = new WorkspaceIndexer().Index(
            workspace,
            embeddings: new SecretGuardFixture.FixedEmbeddings(),
            vectorStore: vectorStore,
            embeddingProviderResolution: new EmbeddingProviderResolution("ollama", null));

        replayed.Warnings.ShouldContain(w => w.Contains("replayed deferred point cleanup", StringComparison.Ordinal));
        foreach (var id in doomedIds)
        {
            vectorStore.Calls.ShouldContain($"delete:{id}");
            vectorStore.PointsOf(id).ShouldBeEmpty($"id {id} still answers to points the purge owed");
        }

        vectorStore.PointsOf(survivorId).ShouldNotBeEmpty();
        using var drained = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        drained.CountPendingReaps().ShouldBe(0);
    }

    [Fact]
    public void Index_RedactedEntryNamingALiveRow_RefusesTheCleanupAndLeavesTheStrangersPoints()
    {
        var workspace = IndexedStoreHoldingSecrets();
        WorkspaceCompactor.Compact(workspace);
        var appId = StoredId(workspace, SecretGuardFixture.AppPath);
        var vectorStore = new ReplayingVectorStore();
        vectorStore.Seed([appId]);
        using (var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace)))
        {
            // the state a freed id lands in: an unnamed entry, and a live row that inherited the same id
            store.EnqueuePendingReaps([new PendingReapTarget(appId, RepoId(store), null)]);
        }

        vectorStore.Calls.Clear();
        var replayed = new WorkspaceIndexer().Index(
            workspace,
            embeddings: new SecretGuardFixture.FixedEmbeddings(),
            vectorStore: vectorStore,
            embeddingProviderResolution: new EmbeddingProviderResolution("ollama", null));

        replayed.Warnings.ShouldNotContain(
            w => w.Contains("replayed deferred point cleanup", StringComparison.Ordinal));
        replayed.Warnings.ShouldContain(
            w => w.Contains("file ids now name different files", StringComparison.Ordinal));
        replayed.Warnings.ShouldContain(w => w.Contains("vectors reconcile", StringComparison.Ordinal));
        vectorStore.Calls.ShouldNotContain($"delete:{appId}");
        vectorStore.PointsOf(appId).ShouldNotBeEmpty();
        using var drained = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        drained.CountPendingReaps().ShouldBe(0);
        StoredPaths(workspace).ShouldContain(SecretGuardFixture.AppPath);
    }

    [Fact]
    public void Compact_WithoutProtectedRows_PurgesNothingAndKeepsTheIndexAnswering()
    {
        var workspace = IngestionWorkspaces.NewWorkspaceWithPlainFile();
        IngestionWorkspaces.WriteFile(
            workspace, AppPath, "public class App { public string Greeting => \"T4-PURGE-PUBLIC\"; }\n");
        new WorkspaceIndexer().Index(workspace);

        var result = WorkspaceCompactor.Compact(workspace);

        result.PathsPurged.ShouldBe(0);
        result.RowsPurged.ShouldBe(0);
        result.ReapJournalRedacted.ShouldBe(0);
        result.TelemetryValuesRedacted.ShouldBe(0);
        result.Warnings.ShouldBeNull();
        new SearchEngine(workspace)
            .Search(new SearchQuery("Greeting", false, null, null, null, null, null, null))
            .Matches.ShouldNotBeEmpty();
    }

    [Fact]
    public void Compact_Twice_SecondPassPurgesNothing()
    {
        var workspace = IndexedStoreHoldingSecrets();

        var first = WorkspaceCompactor.Compact(workspace);
        var second = WorkspaceCompactor.Compact(workspace);

        first.PathsPurged.ShouldBeGreaterThan(0);
        second.PathsPurged.ShouldBe(0);
        second.RowsPurged.ShouldBe(0);
        second.ReapJournalRedacted.ShouldBe(0);
    }

    // The fixture store is built the way a pre-guard store was written: Env/Pem/Secrets rows, chunks and
    // postings planted straight in, and notes/tokens.txt indexed for real by the first pass before the masks
    // were widened — so both halves of the classifier have something to retire, and .env.example stays as the
    // control that this is not a blanket delete.
    private static string IndexedStoreHoldingSecrets()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        SecretGuardFixture.SetGuardExtra(workspace, "**/tokens.txt");
        return workspace;
    }

    private static int ProtectedRowsInStore(string workspace, out int paths)
    {
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var guard = SecretGuardIngestion.Load(workspace);
        var doomed = store.ListAllFiles().Where(f => guard.IsProtected(f.Path)).ToList();
        paths = doomed.Count;
        return doomed.Sum(file => RowsRetiredByCascade(store, file.Id));
    }

    // exactly what one retirement takes out of the store: the file row, its chunks, its symbols, and every ref
    // that either lives in the file or starts at one of its symbols
    private static int RowsRetiredByCascade(MetaStore store, uint fileId)
    {
        var symbolIds = store.ListSymbolsByFile(fileId).Select(s => s.Id).ToList();
        var refs = store.ListRefsByFile(fileId).ToList();
        foreach (var symbolId in symbolIds)
        {
            refs.AddRange(store.ListRefsByFromSymbol(symbolId));
        }

        return 1 + store.ListChunksByFile(fileId).Count + symbolIds.Count + refs.Select(r => r.Id).Distinct().Count();
    }

    private static uint[] PurgedFileIds(string workspace)
    {
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var guard = SecretGuardIngestion.Load(workspace);
        return [.. store.ListAllFiles().Where(f => guard.IsProtected(f.Path)).Select(f => f.Id)];
    }

    private static uint StoredId(string workspace, string relativePath)
    {
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        return store.ListAllFiles().Single(f => f.Path == relativePath).Id;
    }

    private static IReadOnlyList<string> StoreDatabases(string workspace) =>
        [.. Directory.EnumerateFiles(SqueezrPaths.StoreDir(workspace), "meta*")
            .Where(f => Path.GetExtension(f).Equals(".db", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)!];

    private static void MarkChunksEmbedded(string workspace, string relativePath)
    {
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var file = store.ListAllFiles().Single(f => f.Path == relativePath);
        store.MarkChunksEmbedded([.. store.ListChunksByFile(file.Id).Select(c => c.Id)], "test-model");
    }

    private sealed class UnavailableVectorStore : IVectorStore
    {
        public List<uint> Deleted { get; } = [];

        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(false);

        public Task EnsureCollectionAsync(int dimensions, CancellationToken ct) => Task.CompletedTask;

        public Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<VectorHit>> SearchAsync(
            float[] vector, int topK, VectorSearchFilter filter, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<VectorHit>>([]);

        public Task<long> CountAsync(CancellationToken ct) => Task.FromResult(0L);

        public Task<IReadOnlyList<ulong>> ScrollPointIdsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ulong>>([]);

        public Task<IReadOnlyList<uint>> ScrollFileIdsWithoutKindAsync(uint repoId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<uint>>([]);

        public Task SetKindPayloadsAsync(IReadOnlyDictionary<uint, string> kindByFileId, CancellationToken ct) =>
            Task.CompletedTask;

        public Task DeleteByFileIdsAsync(IReadOnlyList<uint> fileIds, CancellationToken ct)
        {
            Deleted.AddRange(fileIds);
            return Task.CompletedTask;
        }

        public Task DeleteByPointIdsAsync(IReadOnlyList<ulong> pointIds, CancellationToken ct) => Task.CompletedTask;

        public Task DeleteCollectionAsync(CancellationToken ct) => Task.CompletedTask;
    }

    // a vector side that answers and remembers: the replay pins need both halves of the truth — which ids the
    // pass deleted by, and which points were still sitting there when it decided
    private sealed class ReplayingVectorStore : IVectorStore
    {
        private readonly Dictionary<ulong, VectorPoint> _points = [];
        private ulong _nextId;

        public List<string> Calls { get; } = [];

        public void Seed(IReadOnlyList<uint> fileIds)
        {
            foreach (var fileId in fileIds)
            {
                _points[_nextId] = new VectorPoint(
                    _nextId, [0.1f, 0.2f, 0.3f, 0.4f], $"seeded-{_nextId}", fileId, 1, 1, 2, DocKindDetector.Code);
                _nextId++;
            }
        }

        public IReadOnlyList<VectorPoint> PointsOf(uint fileId) =>
            [.. _points.Values.Where(point => point.FileId == fileId)];

        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

        public Task EnsureCollectionAsync(int dimensions, CancellationToken ct) => Task.CompletedTask;

        public Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct)
        {
            Calls.Add($"upsert:{points.Count}");
            foreach (var point in points)
            {
                _points[point.Id] = point;
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<VectorHit>> SearchAsync(
            float[] vector, int topK, VectorSearchFilter filter, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<long> CountAsync(CancellationToken ct) => Task.FromResult((long)_points.Count);

        public Task<IReadOnlyList<ulong>> ScrollPointIdsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ulong>>([.. _points.Keys]);

        public Task<IReadOnlyList<uint>> ScrollFileIdsWithoutKindAsync(uint repoId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<uint>>([]);

        public Task SetKindPayloadsAsync(IReadOnlyDictionary<uint, string> kindByFileId, CancellationToken ct) =>
            Task.CompletedTask;

        public Task DeleteByFileIdsAsync(IReadOnlyList<uint> fileIds, CancellationToken ct)
        {
            foreach (var fileId in fileIds)
            {
                Calls.Add($"delete:{fileId}");
            }

            foreach (var id in _points.Where(pair => fileIds.Contains(pair.Value.FileId))
                         .Select(pair => pair.Key).ToList())
            {
                _points.Remove(id);
            }

            return Task.CompletedTask;
        }

        public Task DeleteByPointIdsAsync(IReadOnlyList<ulong> pointIds, CancellationToken ct)
        {
            foreach (var id in pointIds)
            {
                _points.Remove(id);
            }

            return Task.CompletedTask;
        }

        public Task DeleteCollectionAsync(CancellationToken ct)
        {
            _points.Clear();
            return Task.CompletedTask;
        }
    }
}
