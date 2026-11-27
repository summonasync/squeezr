using Shouldly;
using LiteDB;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Storage;
using Squeezr.Tests.Indexing;

namespace Squeezr.Tests.Storage;

// A LiteDB delete frees a record slot; whether the bytes of the retired row stay readable in the file is a
// different question, and that question is what the purge is judged on. These tests grep the raw file rather
// than a document counter, and pin the snapshot copy the rebuild leaves behind.
public class MetaStoreReclaimTests
{
    private const string Canary = "T4-CANARY-5820193746";
    private const string CapCanary = "RECLAIM-CAP-CANARY";
    private const string ProtectedName = "deploy/prod-key.pem";
    private const string BulkPath = "src/Bulk.cs";

    [Fact]
    public void Reclaim_AfterChunksAreDeleted_TakesTheirBytesAndTheirPagesBack()
    {
        var workspace = StoreWithBulkChunks();
        uint bulkId;
        using (var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace)))
        {
            bulkId = store.ListAllFiles().Single(f => f.Path == BulkPath).Id;
            store.DeleteChunksByFile(bulkId);
        }

        using var reopen = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var reclaim = reopen.Reclaim();

        reclaim.Reclaimed.ShouldBeTrue();
        reclaim.SkippedReason.ShouldBeNull();
        reclaim.RebuildMode.ShouldBe(StoreReclaimResult.EngineRebuildMode);
        reclaim.SizeAfterBytes.ShouldBeLessThan(reclaim.SizeBeforeBytes);
        reclaim.BytesFreed.ShouldBe(reclaim.SizeBeforeBytes - reclaim.SizeAfterBytes);
        reopen.ListChunksByFile(bulkId).ShouldBeEmpty();
        reopen.ListAllFiles().ShouldContain(f => f.Path == IngestionWorkspaces.AppPath);
    }

    [Fact]
    public void Reclaim_PurgedSecretRow_LeavesNeitherValueNorNameInTheRawFile()
    {
        var workspace = StoreWithAPlantedSecret();
        ReadRaw(workspace).ShouldContain(Canary);

        uint fileId;
        using (var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace)))
        {
            fileId = store.ListAllFiles().Single(f => f.Path == ProtectedName).Id;
            WorkspaceIndexer.CascadeSemanticRecords(fileId, store);
            store.DeleteFile(fileId);
            store.Reclaim().Reclaimed.ShouldBeTrue();
        }

        var raw = ReadRaw(workspace);
        raw.ShouldNotContain(Canary);
        raw.ShouldNotContain(ProtectedName);
        raw.ShouldNotContain("t4-canary-hash");
        using var check = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        check.ListAllFiles().ShouldNotContain(f => f.Path == ProtectedName);
        var survivors = check.ListAllFiles().Where(f => f.Path != ProtectedName).ToList();
        check.GetSemanticCoverage().TotalChunks.ShouldBe(survivors.Sum(f => check.ListChunksByFile(f.Id).Count));
    }

    [Fact]
    public void Reclaim_LeavesNoSnapshotCopyOfTheStoreBehind()
    {
        var workspace = StoreWithBulkChunks();

        using (var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace)))
        {
            store.Reclaim().Reclaimed.ShouldBeTrue();
        }

        StoreDatabases(workspace).ShouldBe(["meta.db"], StringComparer.Ordinal);

        using var again = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        again.Reclaim().Reclaimed.ShouldBeTrue();
        StoreDatabases(workspace).ShouldBe(["meta.db"], StringComparer.Ordinal);
    }

    [Fact]
    public void ClearSnapshotCopies_ASnapshotAnEarlierCrashedPassLeft_GoesWithTheNextReclaim()
    {
        var workspace = StoreWithBulkChunks();
        var dbPath = SqueezrPaths.MetaDbPath(workspace);
        var stale = Path.Combine(SqueezrPaths.StoreDir(workspace), "meta-backup.db");
        File.WriteAllBytes(stale, File.ReadAllBytes(dbPath));

        using var store = new MetaStore(dbPath);
        var reclaim = store.Reclaim();

        reclaim.Reclaimed.ShouldBeTrue();
        reclaim.SnapshotCopiesLeft.ShouldBe(0);
        File.Exists(stale).ShouldBeFalse();
        StoreDatabases(workspace).ShouldBe(["meta.db"], StringComparer.Ordinal);
    }

    [Fact]
    public void ClearSnapshotCopies_WhileTheStoreFileIsGone_KeepsEveryCopy()
    {
        var workspace = StoreWithBulkChunks();
        var dbPath = SqueezrPaths.MetaDbPath(workspace);
        var storeDir = SqueezrPaths.StoreDir(workspace);
        var backup = Path.Combine(storeDir, "meta-backup.db");
        var recovery = Path.Combine(storeDir, "meta-rename.tmp");
        File.WriteAllText(backup, "the only copy of the store");

        // the window a rebuild dies in: the live file has been renamed away and the rebuilt one has not landed
        // yet — clearing the backup here would trade a skipped reclaim for a destroyed index, and silence about
        // the copy that stayed would be the second half of the same lie
        File.Move(dbPath, recovery);
        try
        {
            MetaStore.ClearSnapshotCopies(dbPath).ShouldBe(1);
            File.Exists(backup).ShouldBeTrue();
        }
        finally
        {
            File.Move(recovery, dbPath);
        }

        MetaStore.ClearSnapshotCopies(dbPath).ShouldBe(0);
        File.Exists(backup).ShouldBeFalse();
    }

    [Fact]
    public void Reclaim_KeepsEveryLiveRowReadable()
    {
        var workspace = StoreWithBulkChunks();
        int files;
        int chunks;
        using (var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace)))
        {
            files = store.ListAllFiles().Count;
            chunks = store.GetSemanticCoverage().TotalChunks;
            chunks.ShouldBeGreaterThan(1);
        }

        using var reopen = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        reopen.Reclaim();

        reopen.ListAllFiles().Count.ShouldBe(files);
        reopen.GetSemanticCoverage().TotalChunks.ShouldBe(chunks);
        new SearchEngine(workspace)
            .Search(new SearchQuery("Greeting", false, null, null, null, null, null, null))
            .Matches.ShouldNotBeEmpty();
    }

    [Fact]
    public void RedactPendingReapPaths_KeepsTheOwedIdAndLosesTheName()
    {
        var workspace = StoreWithBulkChunks();
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repoId = store.ListRepos().First().Id;
        store.EnqueuePendingReaps([
            new PendingReapTarget(77, repoId, ProtectedName),
            new PendingReapTarget(78, repoId, BulkPath),
        ]);

        var redacted = store.RedactPendingReapPaths([77L]);

        redacted.ShouldBe(1);
        var entries = store.ListPendingReaps();
        entries.Single(e => e.FileId == 77).Path.ShouldBeNull();
        entries.Single(e => e.FileId == 78).Path.ShouldBe(BulkPath);
        store.RedactPendingReapPaths([77L]).ShouldBe(0);
        store.RedactPendingReapPaths([]).ShouldBe(0);
    }

    // The live-store refusal shape: LiteDB 5.0.21's RebuildContent creates each collection's IndexService
    // with _disk.MAX_ITEMS_COUNT captured from the barely-born temp file (about 2.5k), so the EnsureIndex
    // pass over a larger collection walks past that counter and the FindAll loop guard fires as a false
    // positive — "Detected loop in FindAll({0})", deterministic on every big store, invisible on small ones.
    private static string StoreAboveTheEngineRebuildCap()
    {
        var dbPath = Path.Combine(TestDirs.NewTempDir(), "meta.db");
        using (var db = new LiteDatabase(dbPath))
        {
            var rows = db.GetCollection("rows");
            rows.EnsureIndex("v_idx", "$.v", false);
            for (var batch = 0; batch < 40; batch++)
            {
                rows.Insert(Enumerable.Range(0, 500).Select(i =>
                {
                    var id = batch * 500 + i;
                    var doc = new BsonDocument
                    {
                        ["_id"] = id,
                        ["v"] = $"v-{id}",
                        ["pad"] = new string('r', 48),
                    };
                    if (id % 2 == 0 && id < 128)
                    {
                        // the live-store retention shape: a purged row big enough to chain extended pages
                        // leaves whole free pages holding its bytes; small freed slots get rewritten away
                        doc["gone"] = CapCanary + new string('z', 20_000);
                    }

                    return doc;
                }));
            }

            db.Checkpoint();
            rows.DeleteMany("_id % 2 = 0");
            db.Checkpoint();
        }

        return dbPath;
    }

    private static string ReadRawPath(string dbPath) =>
        System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(dbPath));

    [Fact]
    public void Reclaim_StoreAboveTheEngineRebuildCap_FallsBackAndKeepsEveryLiveRow()
    {
        var dbPath = StoreAboveTheEngineRebuildCap();
        int liveRows;
        using (var probe = new LiteDatabase(dbPath))
        {
            liveRows = probe.GetCollection("rows").Count();
        }

        liveRows.ShouldBe(10_000);

        using (var store = new MetaStore(dbPath, createIfMissing: true))
        {
            var reclaim = store.Reclaim();

            reclaim.Reclaimed.ShouldBeTrue();
            reclaim.SkippedReason.ShouldBeNull();
            reclaim.RebuildMode.ShouldBe(StoreReclaimResult.CopyOutFallbackMode);
            reclaim.FallbackNote.ShouldNotBeNull();
            reclaim.SizeAfterBytes.ShouldBeGreaterThan(0);
            store.ListRepos().ShouldBeEmpty();
        }

        using (var verify = new LiteDatabase(dbPath))
        {
            var rows = verify.GetCollection("rows");
            rows.Count().ShouldBe(liveRows);
            rows.FindById(17).ShouldNotBeNull();
            rows.FindById(16).ShouldBeNull();
            verify.GetCollection("$indexes").FindAll()
                .Any(i => i["collection"] == "rows" && i["name"] == "v_idx")
                .ShouldBeTrue();
        }

        ReadRawPath(dbPath).ShouldNotContain(CapCanary);
        Directory.GetFiles(Path.GetDirectoryName(dbPath)!, "meta*")
            .Select(Path.GetFileName)
            .ShouldBe(["meta.db"], StringComparer.Ordinal);
    }

    [Fact]
    public void FillLoopPlaceholder_NamesTheCollectionTheEngineLeftUnnamed()
    {
        RebuildRefusalText.FillLoopPlaceholder("Detected loop in FindAll({0})", "chunks")
            .ShouldBe("Detected loop in FindAll(chunks)");
        RebuildRefusalText.FillLoopPlaceholder("Detected loop in FindAll({0})", null)
            .ShouldBe("Detected loop in FindAll(unknown)");
        RebuildRefusalText.FillLoopPlaceholder("sharing violation", "chunks")
            .ShouldBe("sharing violation");
    }

    [LinuxFact]
    public void Reclaim_WhenEvenTheFallbackRefuses_ReportsTheRefusalAndKeepsTheStoreIntact()
    {
        var dbPath = StoreAboveTheEngineRebuildCap();
        var storeDir = Path.GetDirectoryName(dbPath)!;
        int liveRows;
        using (var probe = new LiteDatabase(dbPath))
        {
            liveRows = probe.GetCollection("rows").Count();
        }

        using var store = new MetaStore(dbPath, createIfMissing: true);
        var originalMode = File.GetUnixFileMode(storeDir);
        File.SetUnixFileMode(storeDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var reclaim = store.Reclaim();

            reclaim.Reclaimed.ShouldBeFalse();
            reclaim.SkippedReason.ShouldNotBeNull();
            reclaim.RebuildMode.ShouldBe(StoreReclaimResult.CopyOutFallbackMode);
            reclaim.SizeAfterBytes.ShouldBe(reclaim.SizeBeforeBytes);
        }
        finally
        {
            File.SetUnixFileMode(storeDir, originalMode);
        }

        using var reopen = new LiteDatabase(dbPath);
        reopen.GetCollection("rows").Count().ShouldBe(liveRows);
    }

    private static string StoreWithBulkChunks()
    {
        var workspace = IngestionWorkspaces.NewWorkspaceWithPlainFile();
        IngestionWorkspaces.WriteFile(
            workspace, IngestionWorkspaces.AppPath,
            "public class App { public string Greeting => \"T4-RECLAIM-PUBLIC\"; }\n");
        new WorkspaceIndexer().Index(workspace);
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repo = store.ListRepos().First();
        var file = new FileDocument
        {
            Id = store.GetNextFileId(),
            RepoId = repo.Id,
            Path = BulkPath,
            Sha256 = new string('1', 64),
            Language = "csharp",
            SizeBytes = 120_000,
            LineCount = 1_200,
            IndexedAt = DateTime.UtcNow,
        };
        store.UpsertFile(file);
        store.InsertChunks(
        [
            .. Enumerable.Range(0, 60).Select(i => new ChunkDocument
            {
                FileId = file.Id,
                RepoId = repo.Id,
                ChunkHash = $"t4-bulk-{i}",
                StartLine = i * 20 + 1,
                EndLine = i * 20 + 20,
                TokenCount = 500,
                Text = $"{new string('q', 2000)} bulk chunk {i}",
                VectorStatus = "pending",
            }),
        ]);
        return workspace;
    }

    private static string StoreWithAPlantedSecret()
    {
        var workspace = IngestionWorkspaces.NewWorkspaceWithPlainFile();
        new WorkspaceIndexer().Index(workspace);
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repo = store.ListRepos().First();
        var file = new FileDocument
        {
            Id = store.GetNextFileId(),
            RepoId = repo.Id,
            Path = ProtectedName,
            Sha256 = new string('0', 64),
            Language = "text",
            SizeBytes = 4_100,
            LineCount = 2,
            IndexedAt = DateTime.UtcNow,
        };
        store.UpsertFile(file);
        store.InsertChunks(
        [
            new ChunkDocument
            {
                FileId = file.Id,
                RepoId = repo.Id,
                ChunkHash = "t4-canary-hash",
                StartLine = 1,
                EndLine = 2,
                TokenCount = 1_000,
                Text = $"PRIVATE KEY BEGINS {Canary} {new string('m', 4_000)}",
                VectorStatus = "pending",
            },
        ]);
        return workspace;
    }

    private static string ReadRaw(string workspace) =>
        System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(SqueezrPaths.MetaDbPath(workspace)));

    private static IReadOnlyList<string> StoreDatabases(string workspace) =>
        [.. Directory.EnumerateFiles(SqueezrPaths.StoreDir(workspace), "meta*")
            .Where(f => Path.GetExtension(f).Equals(".db", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)!];
}
