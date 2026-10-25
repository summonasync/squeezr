using System.Security.Cryptography;
using System.Text;
using Shouldly;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing;

[Collection("ProcessGlobals")]
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

    private const string ThirdSource = """
        class Routing
        {
            string Select(string[] candidates, string term)
            {
                foreach (var candidate in candidates)
                {
                    if (candidate.Contains(term, StringComparison.Ordinal))
                    {
                        return candidate;
                    }
                }

                return term;
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
    public void Index_DeletedFileWithoutVectorStore_JournalsItsPointCleanup()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", Source);
        WriteFile(workspace, "repo/b.cs", OtherSource);
        var vectorStore = new FakeVectorStore();
        var indexer = new WorkspaceIndexer();
        indexer.Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: vectorStore);

        var droppedId = FileIdOf(workspace, "b.cs");
        var droppedHashes = ChunkHashes(workspace, droppedId);
        droppedHashes.ShouldNotBeEmpty();
        File.Delete(Path.Combine(workspace, "repo", "b.cs"));

        var pass = indexer.Index(workspace);

        pass.Warnings.ShouldContain(w => w.Contains("point cleanup", StringComparison.Ordinal));
        PendingReapIds(workspace).ShouldBe([droppedId]);
        droppedHashes.ShouldAllBe(hash => vectorStore.Points.ContainsKey(ChunkPointId.FromHash(hash)));
        using (var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace)))
        {
            store.ListChunksByFile(droppedId).ShouldBeEmpty();
            store.CountPendingChunks(null).ShouldBe(0);
        }
    }

    [Fact]
    public void Index_DeferredReapFromAStorelessPass_IsReplayedByTheNextEmbeddingPass()
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
        indexer.Index(workspace);

        vectorStore.Calls.Clear();
        var replayed = indexer.Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: vectorStore);

        replayed.Warnings.ShouldContain(w => w.Contains("replayed deferred point cleanup", StringComparison.Ordinal));
        vectorStore.Calls.ShouldContain($"delete:{droppedId}");
        PendingReapIds(workspace).ShouldBeEmpty();
        droppedHashes.ShouldAllBe(hash => !vectorStore.Points.ContainsKey(ChunkPointId.FromHash(hash)));
        PointsOfFile(vectorStore, FileIdOf(workspace, "a.cs")).ShouldNotBeEmpty();
    }

    [Fact]
    public void Index_DeletedFileThatNeverHadVectors_JournalsNothing()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", Source);
        WriteFile(workspace, "repo/b.cs", OtherSource);
        // no embeddings and no vector store in the first pass either: nothing was ever upserted
        new WorkspaceIndexer().Index(workspace);
        var droppedId = FileIdOf(workspace, "b.cs");
        ChunkHashes(workspace, droppedId).ShouldNotBeEmpty();

        File.Delete(Path.Combine(workspace, "repo", "b.cs"));
        var pass = new WorkspaceIndexer().Index(workspace);

        PendingReapIds(workspace).ShouldBeEmpty();
        pass.Warnings.ShouldNotContain(w => w.Contains("point cleanup", StringComparison.Ordinal));
    }

    [Fact]
    public void PendingReaps_SurviveStoreReopen()
    {
        var dbPath = Path.Combine(TestDirs.NewTempDir(), "meta.db");
        using (var store = new MetaStore(dbPath, createIfMissing: true))
        {
            store.EnqueuePendingReaps(
                [
                    new PendingReapTarget(7, 3, "deep/a.cs"),
                    new PendingReapTarget(7, 3, "deep/a.cs"),
                    new PendingReapTarget(9, 3, "deep/b.cs")
                ]);
            store.ListPendingReaps().Select(p => p.FileId).ShouldBe([7L, 9L]);
            store.ListPendingReaps().Select(p => p.Path).ShouldBe(["deep/a.cs", "deep/b.cs"]);
            store.CountPendingReaps().ShouldBe(2);
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
            cleared.CountPendingReaps().ShouldBe(0);
        }
    }

    [Fact]
    public void EnqueuePendingReaps_ReusedId_FollowsTheNewestClaimOnThatId()
    {
        var dbPath = Path.Combine(TestDirs.NewTempDir(), "meta.db");
        using var store = new MetaStore(dbPath, createIfMissing: true);
        store.EnqueuePendingReaps([new PendingReapTarget(4, 1, "gone/a.cs")]);
        var queuedAt = store.ListPendingReaps().Single().QueuedAt;

        store.EnqueuePendingReaps([new PendingReapTarget(4, 2, "other/b.cs")]);

        // a delete by fileId can only serve the file that owns the id now, so the entry moves with it
        // rather than freezing on an identity no replay can honour any more
        var entry = store.ListPendingReaps().Single();
        entry.Path.ShouldBe("other/b.cs");
        entry.RepoId.ShouldBe(2);
        entry.QueuedAt.ShouldBe(queuedAt);
        store.CountPendingReaps().ShouldBe(1);
    }

    [Fact]
    public void Index_FileIdRecycledByAnotherFile_ReplayDropsTheEntryAndPointsAtReconcile()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", Source);
        WriteFile(workspace, "repo/b.cs", OtherSource);
        var vectorStore = new FakeVectorStore();
        var indexer = new WorkspaceIndexer();
        indexer.Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: vectorStore);

        var recycledId = FileIdOf(workspace, "b.cs");
        var droppedHashes = ChunkHashes(workspace, recycledId);
        File.Delete(Path.Combine(workspace, "repo", "b.cs"));
        indexer.Index(workspace);
        PendingReapIds(workspace).ShouldBe([recycledId]);
        PendingReapPaths(workspace).ShouldBe(["b.cs"]);

        // the watch pass hands the freed id to the file that arrived next; the next embedding pass must
        // notice the entry names a stranger instead of reaping the stranger by id
        WriteFile(workspace, "repo/c.cs", ThirdSource);
        indexer.Index(workspace);
        FileIdOf(workspace, "c.cs").ShouldBe(recycledId);
        vectorStore.Calls.Clear();

        var replayed = indexer.Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: vectorStore);

        vectorStore.Calls.ShouldNotContain($"delete:{recycledId}");
        replayed.Warnings.ShouldNotContain(w => w.Contains("replayed deferred point cleanup", StringComparison.Ordinal));
        replayed.Warnings.ShouldContain(w => w.Contains("file ids now name different files", StringComparison.Ordinal));
        replayed.Warnings.ShouldContain(w => w.Contains("vectors reconcile", StringComparison.Ordinal));
        PendingReapIds(workspace).ShouldBeEmpty();
        replayed.PendingReaps.ShouldBe(0);
        // the payload of b.cs's orphan points still names this id, so the file's point set is its own hashes
        // plus the orphans the dropped entry could no longer reach
        var survivingPoints = PointsOfFile(vectorStore, recycledId).Select(p => p.ChunkHash).ToList();
        ChunkHashes(workspace, recycledId).ShouldAllBe(hash => survivingPoints.Contains(hash));
        // the points the dropped entry was meant to pay for are orphans now — reachable by the hash-level
        // route the warning names, never by an id that belongs to somebody else
        droppedHashes.ShouldAllBe(hash => vectorStore.Points.ContainsKey(ChunkPointId.FromHash(hash)));
    }

    [Fact]
    public void Index_DeferredReapIdNowNamingALiveFile_KeepsItsPointsAndMarkersIntact()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", Source);
        WriteFile(workspace, "repo/b.cs", OtherSource);
        var vectorStore = new FakeVectorStore();
        var indexer = new WorkspaceIndexer();
        indexer.Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: vectorStore);

        var liveId = FileIdOf(workspace, "b.cs");
        var liveHashes = ChunkHashes(workspace, liveId);
        using (var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace)))
        {
            // the shape an entry written before this fix takes after the id moved on: it names a file that
            // is alive and embedded, under a path the store no longer answers to
            store.EnqueuePendingReaps([new PendingReapTarget(liveId, store.FindRepo("demo")!.Id, "deleted-earlier.cs")]);
        }

        vectorStore.Calls.Clear();
        var replayed = indexer.Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: vectorStore);

        vectorStore.Calls.ShouldNotContain(c => c.StartsWith("delete:", StringComparison.Ordinal));
        replayed.Warnings.ShouldContain(w => w.Contains("file ids now name different files", StringComparison.Ordinal));
        PendingReapIds(workspace).ShouldBeEmpty();
        liveHashes.ShouldAllBe(hash => vectorStore.Points.ContainsKey(ChunkPointId.FromHash(hash)));
        ListChunks(workspace, liveId).ShouldAllBe(c => c.EmbeddingStatus == "ok");
    }

    [Fact]
    public void Index_ReportsDeferredAndPendingReapCounts()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", Source);
        WriteFile(workspace, "repo/b.cs", OtherSource);
        var vectorStore = new FakeVectorStore();
        var indexer = new WorkspaceIndexer();
        indexer.Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: vectorStore);

        File.Delete(Path.Combine(workspace, "repo", "b.cs"));
        var deferred = indexer.Index(workspace);
        deferred.DeferredReaps.ShouldBe(1);
        deferred.PendingReaps.ShouldBe(1);

        var standing = indexer.Index(workspace);
        standing.DeferredReaps.ShouldBe(0);
        standing.PendingReaps.ShouldBe(1);

        var drained = indexer.Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: vectorStore);
        drained.DeferredReaps.ShouldBe(0);
        drained.PendingReaps.ShouldBe(0);
    }

    [Fact]
    public void Index_WithoutDeferredWork_ReportsZeroReapCounters()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", Source);
        var vectorStore = new FakeVectorStore();

        var pass = new WorkspaceIndexer().Index(
            workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: vectorStore);

        pass.DeferredReaps.ShouldBe(0);
        pass.PendingReaps.ShouldBe(0);
    }

    [Fact]
    public void RebuildTrigramIndex_DroppingAFile_CascadesItsRowsAndJournalsItsPointCleanup()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", Source);
        WriteFile(workspace, "repo/b.cs", OtherSource);
        var vectorStore = new FakeVectorStore();
        new WorkspaceIndexer().Index(workspace, embeddings: new FakeEmbeddingProvider(), vectorStore: vectorStore);

        var droppedId = FileIdOf(workspace, "b.cs");
        var droppedHashes = ChunkHashes(workspace, droppedId);
        droppedHashes.ShouldNotBeEmpty();
        File.Delete(Path.Combine(workspace, "repo", "b.cs"));

        var warnings = new List<string>();
        using (var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace)))
        {
            var deferred = WorkspaceIndexer.RebuildTrigramIndex(
                workspace, store, WorkspaceManifestFile.Load(workspace), warnings,
                new Dictionary<uint, string>(), SqueezrPaths.TrigramIndexPath(workspace));

            deferred.ShouldBe(1);
            store.FindFileById(droppedId).ShouldBeNull();
            store.ListChunksByFile(droppedId).ShouldBeEmpty();
            store.ListSymbolsByFile(droppedId).ShouldBeEmpty();
            store.ListPendingReaps().Select(p => (uint)p.FileId).ShouldBe([droppedId]);
            store.ListPendingReaps().Single().Path.ShouldBe("b.cs");
        }

        warnings.ShouldContain(w => w.Contains("file is missing on disk", StringComparison.Ordinal));
        warnings.ShouldContain(w => w.Contains("point cleanup", StringComparison.Ordinal));
        droppedHashes.ShouldAllBe(hash => vectorStore.Points.ContainsKey(ChunkPointId.FromHash(hash)));
    }

    [Fact]
    public void RebuildTrigramIndex_WithAVectorStore_RemovesThePointsInsteadOfJournalingThem()
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

        var warnings = new List<string>();
        using (var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace)))
        {
            WorkspaceIndexer.RebuildTrigramIndex(
                workspace, store, WorkspaceManifestFile.Load(workspace), warnings,
                new Dictionary<uint, string>(), SqueezrPaths.TrigramIndexPath(workspace), vectorStore: vectorStore);

            store.CountPendingReaps().ShouldBe(0);
        }

        vectorStore.Calls.ShouldContain($"delete:{droppedId}");
        droppedHashes.ShouldAllBe(hash => !vectorStore.Points.ContainsKey(ChunkPointId.FromHash(hash)));
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

    private static List<string?> PendingReapPaths(string workspace)
    {
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        return store.ListPendingReaps().Select(p => p.Path).ToList();
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
