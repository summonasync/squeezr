using LiteDB;
using Squeezr.Core.Indexing;
using Squeezr.Core.Workspace;

namespace Squeezr.Core.Storage;

public sealed class MetaStore : IDisposable
{
    public const string ReposCollection = "repos";
    public const string FilesCollection = "files";
    public const string SymbolsCollection = "symbols";
    public const string RefsCollection = "refs";
    public const string ChunksCollection = "chunks";
    public const string IndexStateCollection = "index_state";
    public const string PendingReapsCollection = "pending_reaps";

    private LiteDatabase _db;
    private readonly string _dbPath;

    private static readonly object MapperWarmupLock = new();

    public MetaStore(string dbPath, bool createIfMissing = false)
    {
        if (!createIfMissing && !File.Exists(dbPath))
        {
            throw new WorkspaceException($"Index store '{dbPath}' is missing. Run '{RemedyText.IndexCommand}' first.");
        }

        _dbPath = Path.GetFullPath(dbPath);
        _db = OpenDatabase(_dbPath);
        EnsureCoreIndexes();
    }

    private static LiteDatabase OpenDatabase(string dbPath)
    {
        // LiteDB 5.0.21 exposes a half-built EntityMapper to concurrent readers (empty mapper is cached
        // before its members are populated), so first-time mapping of an entity type must be serialized.
        lock (MapperWarmupLock)
        {
            // Direct mode opens meta.db with FileShare.Read, so a second read-write connection (a search
            // during a background watch pass) dies with a sharing violation. Shared mode keeps writers
            // coordinated by LiteDB's own locks; single-writer discipline is enforced by IndexLock upstream.
            var db = new LiteDatabase(new ConnectionString
            {
                Filename = dbPath,
                Connection = ConnectionType.Shared
            });
            db.Mapper.ToDocument(new RepoDocument());
            db.Mapper.ToDocument(new FileDocument());
            db.Mapper.ToDocument(new IndexStateDocument { Stats = new IndexStateStats() });
            db.Mapper.ToDocument(new SymbolDocument());
            db.Mapper.ToDocument(new RefDocument());
            db.Mapper.ToDocument(new ChunkDocument());
            db.Mapper.ToDocument(new PendingReapDocument());
            return db;
        }
    }

    private void EnsureCoreIndexes()
    {
        _db.GetCollection(ReposCollection).EnsureIndex("name", unique: true);
        _db.GetCollection(FilesCollection).EnsureIndex("repoId");
        _db.GetCollection(FilesCollection).EnsureIndex("path");
        _db.GetCollection(SymbolsCollection).EnsureIndex("fileId");
        _db.GetCollection(SymbolsCollection).EnsureIndex("repoId");
        _db.GetCollection(RefsCollection).EnsureIndex("fromSymbolId");
        _db.GetCollection(RefsCollection).EnsureIndex("fileId");
        _db.GetCollection(ChunksCollection).EnsureIndex("fileId");
        _db.GetCollection(ChunksCollection).EnsureIndex("repoId");
    }

    public static MetaStore? TryOpen(string dbPath) =>
        TryOpen(dbPath, out _);

    // The same door with the refusal class attached. The one-bit answer stayed for every caller that only
    // asks 'will this open'; the callers that report WHY it would not are the ones that were being handed a
    // null and inventing a cause. Each class is named off the exception type the open actually raised — see
    // StoreOpenCause for what this box measures — and an exception this switch cannot attribute stays
    // Unattributed rather than becoming a guess.
    public static MetaStore? TryOpen(string dbPath, out StoreOpenCause cause)
    {
        try
        {
            var store = new MetaStore(dbPath);
            cause = StoreOpenCause.None;
            return store;
        }
        // A header whose LiteDB magic stands while the fields behind it do not dies on a DateTime rather
        // than on a LiteException, and every consumer of this door (fleet, stats, search, the worktree
        // verbs) asks 'will this open' rather than 'open this' — so the crash is this method's to swallow.
        catch (Exception ex)
            when (ex is WorkspaceException or IOException or UnauthorizedAccessException or LiteException
                or InvalidOperationException or ArgumentOutOfRangeException)
        {
            cause = Classify(ex);
            return null;
        }
    }

    private static StoreOpenCause Classify(Exception ex) => ex switch
    {
        // the only 'missing' this door can state: MetaStore's own probe before the file is handed to LiteDB,
        // because LiteDB's shared connection creates lazily and would answer an absent path with a handle
        WorkspaceException => StoreOpenCause.Missing,
        UnauthorizedAccessException => StoreOpenCause.Denied,
        // the bytes did not parse as a database this reader can use — LiteException 103 for a bad header,
        // the mapper's own range and shape crashes for a header that stood over fields that did not
        LiteException or InvalidOperationException or ArgumentOutOfRangeException => StoreOpenCause.Unparseable,
        _ => StoreOpenCause.Unattributed,
    };

    public void AddRepo(RepoDocument repo)
    {
        var repos = _db.GetCollection<RepoDocument>(ReposCollection);
        if (repos.Exists(r => r.Name == repo.Name))
        {
            throw new WorkspaceException($"Repository '{repo.Name}' is already present in the index store.");
        }
        repos.Insert(repo);
    }

    public void UpsertRepo(RepoDocument repo) => _db.GetCollection<RepoDocument>(ReposCollection).Upsert(repo);

    public RepoDocument? FindRepo(string name) =>
        _db.GetCollection<RepoDocument>(ReposCollection).FindOne(r => r.Name == name);

    // Query filters resolve repo names case-insensitively: LiteDB's FindOne is exact-match, while the exact
    // arm compares names with OrdinalIgnoreCase, so `--repo DEMO` used to hit in one arm and return nothing
    // in the others. One rule for every arm: exact first, then the case-insensitive fallback.
    public RepoDocument? FindRepoIgnoringCase(string name) =>
        FindRepo(name) ?? ListRepos()
            .FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<RepoDocument> ListRepos() =>
        _db.GetCollection<RepoDocument>(ReposCollection).FindAll().OrderBy(r => r.Name).ToList();

    public uint GetNextRepoId() => NextId(ReposCollection);

    public IReadOnlyList<FileDocument> ListFiles(uint repoId) =>
        _db.GetCollection<FileDocument>(FilesCollection)
            .Find(f => f.RepoId == repoId)
            .OrderBy(f => f.Id)
            .ToList();

    public IReadOnlyList<FileDocument> ListAllFiles() =>
        _db.GetCollection<FileDocument>(FilesCollection).FindAll().OrderBy(f => f.Id).ToList();

    public FileDocument? FindFileById(uint id) => _db.GetCollection<FileDocument>(FilesCollection).FindById((long)id);

    public FileDocument? FindFile(uint repoId, string path) =>
        _db.GetCollection<FileDocument>(FilesCollection)
            .Find(f => f.Path == path)
            .FirstOrDefault(f => f.RepoId == repoId);

    public void UpsertFile(FileDocument file) => _db.GetCollection<FileDocument>(FilesCollection).Upsert(file);

    public void DeleteFile(uint id) => _db.GetCollection<FileDocument>(FilesCollection).Delete((long)id);

    public int CountFiles(uint repoId) => _db.GetCollection<FileDocument>(FilesCollection).Count(f => f.RepoId == repoId);

    public int CountAllFiles() => _db.GetCollection<FileDocument>(FilesCollection).Count();

    public uint GetNextFileId() => NextId(FilesCollection);

    public IReadOnlyList<SymbolDocument> ListSymbolsByFile(uint fileId) =>
        _db.GetCollection<SymbolDocument>(SymbolsCollection).Find(s => s.FileId == fileId).ToList();

    public IReadOnlyList<SymbolDocument> ListSymbolsByRepo(uint repoId) =>
        _db.GetCollection<SymbolDocument>(SymbolsCollection).Find(s => s.RepoId == repoId).ToList();

    public int DeleteSymbolsByFile(uint fileId) =>
        _db.GetCollection<SymbolDocument>(SymbolsCollection).DeleteMany(s => s.FileId == fileId);

    public void InsertSymbols(IEnumerable<SymbolDocument> symbols) =>
        _db.GetCollection<SymbolDocument>(SymbolsCollection).InsertBulk(symbols);

    public int CountSymbols(uint repoId) =>
        _db.GetCollection<SymbolDocument>(SymbolsCollection).Count(s => s.RepoId == repoId);

    public int DeleteRefsByFromSymbolIds(IReadOnlyCollection<long> symbolIds)
    {
        if (symbolIds.Count == 0)
        {
            return 0;
        }

        return _db.GetCollection<RefDocument>(RefsCollection).DeleteMany(r => symbolIds.Contains(r.FromSymbolId));
    }

    public IReadOnlyList<RefDocument> ListRefsByFromSymbol(long symbolId) =>
        _db.GetCollection<RefDocument>(RefsCollection).Find(r => r.FromSymbolId == symbolId).ToList();

    // the ghost-refs lag scan joins every row against two file stamps and reads nothing else, so the
    // row bodies (ToName, EnclosingSymbol) are dropped while streaming instead of being kept per row
    public IReadOnlyList<(long FromSymbolId, uint FileId)> ListRefEndpoints() =>
        _db.GetCollection<RefDocument>(RefsCollection)
            .FindAll()
            .Select(r => (r.FromSymbolId, r.FileId))
            .ToList();

    public IReadOnlyList<RefDocument> ListRefsByFile(uint fileId) =>
        _db.GetCollection<RefDocument>(RefsCollection).Find(r => r.FileId == fileId).ToList();

    public int DeleteRefsByFile(uint fileId) =>
        _db.GetCollection<RefDocument>(RefsCollection).DeleteMany(r => r.FileId == fileId);

    public int DeleteRefsByFileIds(IReadOnlyCollection<uint> fileIds)
    {
        if (fileIds.Count == 0)
        {
            return 0;
        }

        return _db.GetCollection<RefDocument>(RefsCollection).DeleteMany(r => fileIds.Contains(r.FileId));
    }

    public void InsertRefs(IEnumerable<RefDocument> refs) =>
        _db.GetCollection<RefDocument>(RefsCollection).InsertBulk(refs);

    public IReadOnlyList<ChunkDocument> ListChunksByFile(uint fileId) =>
        _db.GetCollection<ChunkDocument>(ChunksCollection).Find(c => c.FileId == fileId).ToList();

    // The fileId index answers this with a seek: the callers run it per deleted file, including on every
    // watch pass, and materializing the chunk documents (Text included) to answer an Any() is not free.
    public bool HasEmbeddedChunks(uint fileId) =>
        _db.GetCollection<ChunkDocument>(ChunksCollection)
            .Exists(c => c.FileId == fileId && c.EmbeddingStatus == "ok");

    public IReadOnlyList<string> ListChunkHashesByFile(uint fileId) =>
        _db.GetCollection<ChunkDocument>(ChunksCollection)
            .Find(c => c.FileId == fileId)
            .Select(c => c.ChunkHash)
            .ToList();

    public IReadOnlyList<string> ListDistinctChunkHashes() =>
        _db.GetCollection<ChunkDocument>(ChunksCollection)
            .FindAll()
            .Select(c => c.ChunkHash)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    public IReadOnlyList<string> ListEmbeddedChunkHashes() =>
        _db.GetCollection<ChunkDocument>(ChunksCollection)
            .Find(c => c.EmbeddingStatus == "ok")
            .Select(c => c.ChunkHash)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    // Closes the lying-marker case: a row claiming "ok" whose point is gone (shared hash reaped through
    // another file, a collection dropped out of band). Resetting the marker puts the chunk back in the
    // queue, where the next embedding pass gives it a vector again.
    public int MarkChunksPendingByHash(IReadOnlyCollection<string> chunkHashes)
    {
        if (chunkHashes.Count == 0)
        {
            return 0;
        }

        var collection = _db.GetCollection<ChunkDocument>(ChunksCollection);
        var stale = collection.Find(c => c.EmbeddingStatus == "ok" && chunkHashes.Contains(c.ChunkHash)).ToList();
        foreach (var chunk in stale)
        {
            chunk.EmbeddingStatus = "pending";
            chunk.ModelKey = null;
        }

        if (stale.Count > 0)
        {
            collection.Update(stale);
        }

        return stale.Count;
    }

    public int DeleteChunksByFile(uint fileId) =>
        _db.GetCollection<ChunkDocument>(ChunksCollection).DeleteMany(c => c.FileId == fileId);

    public void InsertChunks(IEnumerable<ChunkDocument> chunks) =>
        _db.GetCollection<ChunkDocument>(ChunksCollection).InsertBulk(chunks);

    // Vector points are keyed by chunk hash alone, so a file that re-chunks into the same chunk texts
    // already owns its vectors: the embedded marker rides along with the hash instead of being reset.
    // Chunks whose text moved come back pending, and the caller that reaps a file whose hash set changed
    // marks that whole file pending (see WorkspaceIndexer.RequeueReapedFiles) — a carried "ok" never
    // outlives its point within the same file. Across files the guarantee does not hold: identical chunk
    // text in two files shares one point, so reaping either drops the shared point while the other keeps
    // its marker. Nothing here closes that — 'squeezr vectors reconcile' re-queues marker-bearing rows that
    // have no point behind them (059).
    public void ReplaceFileChunks(uint fileId, IReadOnlyList<ChunkDocument> chunks)
    {
        var collection = _db.GetCollection<ChunkDocument>(ChunksCollection);
        var markers = collection.Find(c => c.FileId == fileId && c.EmbeddingStatus == "ok")
            .Select(c => (c.ChunkHash, c.ModelKey))
            .ToList()
            .GroupBy(m => m.ChunkHash)
            .ToDictionary(g => g.Key, g => g.First().ModelKey, StringComparer.Ordinal);

        collection.DeleteMany(c => c.FileId == fileId);
        foreach (var chunk in chunks)
        {
            if (markers.TryGetValue(chunk.ChunkHash, out var modelKey) && modelKey is not null)
            {
                chunk.EmbeddingStatus = "ok";
                chunk.ModelKey = modelKey;
            }
        }

        collection.InsertBulk(chunks);
    }

    public int CountChunks(uint repoId) =>
        _db.GetCollection<ChunkDocument>(ChunksCollection).Count(c => c.RepoId == repoId);

    public int CountPendingChunks(uint? repoId, string? modelKey = null)
    {
        var chunks = _db.GetCollection<ChunkDocument>(ChunksCollection);
        if (modelKey is null)
        {
            return repoId.HasValue
                ? chunks.Count(c => c.RepoId == repoId.Value && c.EmbeddingStatus != "ok")
                : chunks.Count(c => c.EmbeddingStatus != "ok");
        }

        return repoId.HasValue
            ? chunks.Count(c => c.RepoId == repoId.Value && (c.EmbeddingStatus != "ok" || c.ModelKey != modelKey))
            : chunks.Count(c => c.EmbeddingStatus != "ok" || c.ModelKey != modelKey);
    }

    public SemanticCoverage GetSemanticCoverage()
    {
        var chunks = _db.GetCollection<ChunkDocument>(ChunksCollection);
        return new SemanticCoverage(chunks.Count(), chunks.Count(c => c.EmbeddingStatus != "ok"));
    }

    // Kind rides the file row; rows whose file is already reaped count toward no kind, so the
    // counts sum to the live chunk rows exactly.
    public IReadOnlyDictionary<string, int> CountChunksByKind()
    {
        var kindByFileId = _db.GetCollection<FileDocument>(FilesCollection)
            .FindAll()
            .ToDictionary(f => f.Id, DocKindDetector.Detect);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var fileId in _db.GetCollection(ChunksCollection).FindAll().Select(d => (uint)d["fileId"].AsInt64))
        {
            if (!kindByFileId.TryGetValue(fileId, out var kind))
            {
                continue;
            }

            counts[kind] = counts.GetValueOrDefault(kind) + 1;
        }

        return counts;
    }

    // Marks carried over from a previous model are a one-time re-queue, not a refill: counting them apart
    // from never-embedded rows is what lets a pass say "the model changed" instead of re-embedding silently.
    public int CountChunksWithOtherModelKey(uint? repoId, string modelKey)
    {
        var chunks = _db.GetCollection<ChunkDocument>(ChunksCollection);
        return repoId.HasValue
            ? chunks.Count(c => c.RepoId == repoId.Value && c.EmbeddingStatus == "ok" && c.ModelKey != modelKey)
            : chunks.Count(c => c.EmbeddingStatus == "ok" && c.ModelKey != modelKey);
    }

    public IReadOnlyList<ChunkDocument> ListPendingChunks(uint repoId, string modelKey, int limit) =>
        _db.GetCollection<ChunkDocument>(ChunksCollection)
            .Find(c => c.RepoId == repoId && (c.EmbeddingStatus != "ok" || c.ModelKey != modelKey))
            .OrderBy(c => c.Id)
            .Take(limit)
            .ToList();

    public void MarkChunksEmbedded(IEnumerable<long> chunkIds, string modelKey)
    {
        var chunks = _db.GetCollection<ChunkDocument>(ChunksCollection);
        foreach (var id in chunkIds)
        {
            var chunk = chunks.FindById(id);
            if (chunk is null)
            {
                continue;
            }

            chunk.EmbeddingStatus = "ok";
            chunk.ModelKey = modelKey;
            chunks.Update(chunk);
        }
    }

    public void MarkAllChunksPending(uint? repoId)
    {
        var chunks = _db.GetCollection<ChunkDocument>(ChunksCollection);
        var targets = repoId.HasValue
            ? chunks.Find(c => c.RepoId == repoId.Value).Select(c => c.Id).ToList()
            : chunks.FindAll().Select(c => c.Id).ToList();
        foreach (var id in targets)
        {
            var chunk = chunks.FindById(id);
            if (chunk is null)
            {
                continue;
            }

            chunk.EmbeddingStatus = "pending";
            chunk.ModelKey = null;
            chunks.Update(chunk);
        }
    }

    public int MarkFileChunksPending(uint fileId)
    {
        var chunks = _db.GetCollection<ChunkDocument>(ChunksCollection);
        var rows = chunks.Find(c => c.FileId == fileId).ToList();
        if (rows.Count == 0)
        {
            return 0;
        }

        foreach (var chunk in rows)
        {
            chunk.EmbeddingStatus = "pending";
            chunk.ModelKey = null;
        }

        chunks.Update(rows);
        return rows.Count;
    }

    // A vector delete that could not run (store down, or it threw halfway) is queued here and replayed
    // by the next pass that has both a store and an embeddings provider. Replay re-deletes by fileId, so
    // the file's chunks are marked pending on success — otherwise points the delete half-removed while the
    // same pass re-upserted would be lost for good once the chunks reported themselves embedded.
    // The path/repo identity travelling with the id is what lets replay notice that MetaStore.NextId has
    // already handed that id to a different file and keep its hands off the stranger's points.
    public IReadOnlyList<PendingReapDocument> ListPendingReaps() =>
        _db.GetCollection<PendingReapDocument>(PendingReapsCollection)
            .FindAll()
            .OrderBy(p => p.QueuedAt)
            .ToList();

    public int CountPendingReaps() => _db.GetCollection(PendingReapsCollection).Count();

    public void EnqueuePendingReaps(IReadOnlyList<PendingReapTarget> targets)
    {
        var reaps = _db.GetCollection<PendingReapDocument>(PendingReapsCollection);
        var queuedAt = DateTime.UtcNow;
        foreach (var target in targets.DistinctBy(t => t.FileId))
        {
            var existing = reaps.FindById((long)target.FileId);
            if (existing is null)
            {
                reaps.Insert(new PendingReapDocument
                {
                    FileId = target.FileId,
                    RepoId = target.RepoId,
                    Path = target.Path,
                    QueuedAt = queuedAt
                });
                continue;
            }

            if (existing.RepoId != target.RepoId
                || !string.Equals(existing.Path, target.Path, StringComparison.Ordinal))
            {
                // the id changed hands while the entry stood, and a delete-by-fileId can only serve the file
                // that owns it now — the entry follows the newest claim instead of going stale
                existing.RepoId = target.RepoId;
                existing.Path = target.Path;
                reaps.Update(existing);
            }
        }
    }

    public void ClearPendingReaps(IEnumerable<uint> fileIds)
    {
        var reaps = _db.GetCollection<PendingReapDocument>(PendingReapsCollection);
        foreach (var fileId in fileIds)
        {
            reaps.Delete((long)fileId);
        }
    }

    public void ClearAllPendingReaps() => _db.GetCollection(PendingReapsCollection).DeleteAll();

    // An owed cleanup keeps its id (the replay still needs it) and loses its name: a queued reap for a path
    // the secret guard refuses would otherwise keep that name alive in meta.db bytes long after the file row
    // is gone. Identity match in replay falls back to "the id names no live row", which is exactly what a
    // purged file is, so the point still gets reaped.
    public int RedactPendingReapPaths(IReadOnlyCollection<long> fileIds)
    {
        if (fileIds.Count == 0)
        {
            return 0;
        }

        var reaps = _db.GetCollection<PendingReapDocument>(PendingReapsCollection);
        var named = reaps.Find(p => p.Path != null && fileIds.Contains(p.FileId)).ToList();
        foreach (var reap in named)
        {
            reap.Path = null;
            reaps.Update(reap);
        }

        return named.Count;
    }

    public IndexStateDocument? GetIndexState(string repoName) =>
        _db.GetCollection<IndexStateDocument>(IndexStateCollection).FindById(repoName);

    public DateTime? GetLatestFullIndexAt() =>
        _db.GetCollection<IndexStateDocument>(IndexStateCollection).FindAll()
            .Max(state => (DateTime?)state.LastFullIndexAt);

    public void SaveIndexState(IndexStateDocument state) =>
        _db.GetCollection<IndexStateDocument>(IndexStateCollection).Upsert(state);

    // A shared connection keeps its newest writes in the log file next to the data file, so a store read
    // as a plain file before this call holds rows split across two places. The copy path flushes and then
    // reads bytes; it never rebuilds, because a rebuild would rewrite the store someone else is serving.
    public void Checkpoint() => _db.Checkpoint();

    // A delete in LiteDB frees a record slot; it does not hand the space back to the filesystem. Checkpoint
    // flushes what is pending, rebuild rewrites the file from live pages only — and rebuild moves the
    // pre-rebuild file aside under a '-backup' name: a second full copy of every byte the purge just retired.
    // Those copies go with the reclaim, the ones an earlier crashed pass left behind included, and the count
    // of what stayed is part of the result so the pass can say it instead of reporting a clean store.
    // The engine's own rebuild refuses stores past its loop guard's early calibration ("Detected loop in
    // FindAll({0})", the placeholder unfilled — an upstream 5.0.21 quirk), which is exactly the state the
    // secret purge needs to clean, so a refusal hands the file to the copy-out rebuilder and the result
    // says which path carried it.
    public StoreReclaimResult Reclaim()
    {
        var sizeBefore = SizeOnDisk();
        try
        {
            _db.Checkpoint();
            _db.Rebuild();
            return new StoreReclaimResult(
                sizeBefore, SizeOnDisk(), Reclaimed: true, SkippedReason: null, ClearSnapshotCopies(_dbPath));
        }
        catch (Exception ex) when (ex is LiteException or IOException or UnauthorizedAccessException)
        {
            return ReclaimByCopyOut(sizeBefore, ex.Message);
        }
    }

    private StoreReclaimResult ReclaimByCopyOut(long sizeBefore, string engineReason)
    {
        DisposeQuietly(_db);
        var fallback = new StoreFallbackRebuilder(_dbPath).Rebuild();

        try
        {
            _db = OpenDatabase(_dbPath);
            EnsureCoreIndexes();
        }
        catch (Exception ex) when (ex is LiteException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new StoreReclaimResult(
                sizeBefore,
                SizeOnDisk(),
                Reclaimed: false,
                $"{RebuildRefusalText.FillLoopPlaceholder(engineReason, fallback.FailedCollection)} — fallback rebuild refused: {fallback.FailureReason}; store reopen failed: {ex.Message}",
                ClearSnapshotCopies(_dbPath),
                StoreReclaimResult.CopyOutFallbackMode);
        }

        if (fallback.Rebuilt)
        {
            return new StoreReclaimResult(
                sizeBefore,
                SizeOnDisk(),
                Reclaimed: true,
                SkippedReason: null,
                ClearSnapshotCopies(_dbPath),
                StoreReclaimResult.CopyOutFallbackMode,
                RebuildRefusalText.FillLoopPlaceholder(engineReason, null));
        }

        return new StoreReclaimResult(
            sizeBefore,
            SizeOnDisk(),
            Reclaimed: false,
            $"{RebuildRefusalText.FillLoopPlaceholder(engineReason, fallback.FailedCollection)} — fallback rebuild refused: {fallback.FailureReason}",
            ClearSnapshotCopies(_dbPath),
            StoreReclaimResult.CopyOutFallbackMode);
    }

    private static void DisposeQuietly(LiteDatabase db)
    {
        try
        {
            db.Dispose();
        }
        catch (Exception)
        {
        }
    }

    public void Dispose() => _db.Dispose();

    private long SizeOnDisk() => File.Exists(_dbPath) ? new FileInfo(_dbPath).Length : 0;

    // The keep-when-gone half is not caution for its own sake: the rebuild renames the live file to the backup
    // name and the rebuilt file into the live name, so an attempt that died between those two moves leaves the
    // backup as the only copy of the store. Clearing it there would turn a skipped reclaim into a destroyed
    // index. Refusing to clear is not a silent answer either — the copies that stayed are counted and reported,
    // because a snapshot next to a vanished store holds exactly the bytes the purge was meant to take back.
    internal static int ClearSnapshotCopies(string dbPath)
    {
        var fullPath = Path.GetFullPath(dbPath);
        var dir = Path.GetDirectoryName(fullPath);
        if (dir is null || !Directory.Exists(dir))
        {
            return 0;
        }

        var copies = SnapshotCopies(fullPath, dir);
        if (!File.Exists(fullPath))
        {
            return copies.Count;
        }

        var left = 0;
        foreach (var copy in copies)
        {
            try
            {
                File.Delete(copy);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                left++;
            }
        }

        return left;
    }

    private static List<string> SnapshotCopies(string dbPath, string dir)
    {
        var stem = Path.GetFileNameWithoutExtension(dbPath);
        return [.. Directory.EnumerateFiles(dir, stem + "*")
            .Select(Path.GetFullPath)
            .Where(path => IsSnapshotCopy(path, dbPath))];
    }

    private static bool IsSnapshotCopy(string path, string dbPath) =>
        !string.Equals(path, dbPath, StringComparison.Ordinal)
        && Path.GetExtension(path).Equals(".db", StringComparison.OrdinalIgnoreCase);

    private uint NextId(string name)
    {
        var last = _db.GetCollection(name).Find(Query.All(Query.Descending), 0, 1).FirstOrDefault();
        return last is null ? 0u : (uint)last["_id"].AsInt64 + 1u;
    }
}
