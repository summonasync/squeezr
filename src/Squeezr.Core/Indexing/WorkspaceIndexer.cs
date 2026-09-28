using Squeezr.Core.Configuration;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Indexing.Roslyn;
using Squeezr.Core.Indexing.TreeSitter;
using Squeezr.Core.Indexing.Trigram;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Core.Indexing;

public sealed class WorkspaceIndexer
{
    private const double CompactHintThreshold = 0.3;

    public IndexResult Index(
        string workspaceRoot,
        string? repoName = null,
        bool full = false,
        bool noSemantic = false,
        IIndexProgress? progress = null,
        IEmbeddingProvider? embeddings = null,
        IVectorStore? vectorStore = null,
        bool reembed = false,
        Action<string>? onStaleTakeover = null,
        EmbeddingProviderResolution? embeddingProviderResolution = null)
    {
        if (!WorkspaceManifestFile.Exists(workspaceRoot))
        {
            throw new WorkspaceException(
                "Workspace is not initialized. Run 'squeezr init' first.",
                WorkspaceException.NotInitializedCode);
        }

        var manifest = WorkspaceManifestFile.Load(workspaceRoot);
        var config = ConfigFile.Load(workspaceRoot);
        var effectiveNoSemantic = noSemantic || config.Indexing.NoSemantic;

        var targets = repoName is null
            ? manifest.Repos
            : manifest.Repos.Where(r => string.Equals(r.Name, repoName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (targets.Count == 0)
        {
            throw new WorkspaceException($"Repository '{repoName}' is not in the workspace.");
        }

        using (IndexLock.Acquire(SqueezrPaths.StoreDir(workspaceRoot), onStaleTakeover))
        using (var store = new MetaStore(SqueezrPaths.MetaDbPath(workspaceRoot)))
        {
            var warnings = new List<string>();
            var textCache = new Dictionary<uint, string>();

            if (reembed && embeddings is not null && vectorStore is not null)
            {
                store.MarkAllChunksPending(null);
                try
                {
                    vectorStore.DeleteCollectionAsync(CancellationToken.None).GetAwaiter().GetResult();
                    store.ClearAllPendingReaps();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not OperationCanceledException)
                {
                    warnings.Add($"--reembed: failed to drop the existing vector collection: {ex.Message}");
                }
            }

            var modelKey = embeddings is not null
                ? EmbeddingModelKey.From(WithResolvedProvider(config.Embeddings, embeddingProviderResolution))
                : null;

            if (!reembed && embeddings is not null && vectorStore is not null)
            {
                EnsureVectorCompatibility(embeddings, vectorStore, warnings);
                ReplayPendingReaps(vectorStore, store, warnings);
            }

            var works = targets
                .Select(repo => IndexRepo(workspaceRoot, store, repo, full, config, effectiveNoSemantic, warnings, textCache, progress, embeddings, vectorStore, modelKey))
                .ToList();

            var changedDocs = works.SelectMany(w => w.Changed).ToList();
            var tombstones = works.SelectMany(w => w.Deleted).Select(d => (d.RepoId, d.Id)).ToList();
            var deletedVectorFileIds = works.SelectMany(w => w.Deleted).Select(d => d.Id).ToList();

            if (repoName is null)
            {
                var manifestNames = manifest.Repos.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var reposById = store.ListRepos().ToDictionary(r => r.Id);
                foreach (var file in store.ListAllFiles())
                {
                    if (reposById.TryGetValue(file.RepoId, out var repoDoc) && manifestNames.Contains(repoDoc.Name))
                    {
                        continue;
                    }

                    warnings.Add($"dropping '{file.Path}' from index store: repository is not in the workspace manifest");
                    store.DeleteFile(file.Id);
                    tombstones.Add((file.RepoId, file.Id));
                    deletedVectorFileIds.Add(file.Id);
                }
            }

            DeleteVectorsForFiles(vectorStore, store, deletedVectorFileIds, warnings);

            ApplyTrigramUpdates(
                workspaceRoot, store, manifest, warnings, textCache, SqueezrPaths.TrigramIndexPath(workspaceRoot),
                changedDocs, tombstones, full, progress);

            if (progress is not null)
            {
                foreach (var repo in targets)
                {
                    progress.RepoCompleted(repo.Name);
                }
            }

            return new IndexResult(works.Select(w => w.Result).ToList(), DateTime.UtcNow, warnings);
        }
    }

    private static Dictionary<uint, int[]> ToPostings(string text) =>
        TrigramExtractor.ExtractPostings(text).ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());

    private static EmbeddingsConfig WithResolvedProvider(EmbeddingsConfig config, EmbeddingProviderResolution? resolution) =>
        resolution is null ? config : config with { Provider = resolution.Provider };

    private static string DecodeUtf8(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            ? System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3)
            : System.Text.Encoding.UTF8.GetString(bytes);

    private static void ApplyTrigramUpdates(
        string workspaceRoot,
        MetaStore store,
        WorkspaceManifest manifest,
        List<string> warnings,
        Dictionary<uint, string> textCache,
        string trigramPath,
        List<FileDocument> changedDocs,
        List<(uint RepoId, uint Id)> tombstones,
        bool full,
        IIndexProgress? progress)
    {
        uint? version = null;
        if (!full)
        {
            try
            {
                version = TrigramSegment.TryReadVersion(trigramPath);
            }
            catch (InvalidDataException)
            {
                version = null;
            }
        }

        if (version == TrigramIndexFormat.CurrentVersion)
        {
            if (changedDocs.Count == 0 && tombstones.Count == 0)
            {
                return;
            }

            var repoNames = store.ListRepos().ToDictionary(r => r.Id, r => r.Name);
            var trigramTotals = changedDocs
                .GroupBy(d => d.RepoId)
                .ToDictionary(g => g.Key, g => g.Count());
            if (progress is not null)
            {
                foreach (var (repoId, total) in trigramTotals)
                {
                    if (repoNames.TryGetValue(repoId, out var name))
                    {
                        progress.PhaseStarted(name, IndexPhase.Trigram, total);
                    }
                }
            }

            var documents = changedDocs.Select(doc =>
                new TrigramDocument(doc.RepoId, doc.Id, ToPostings(textCache[doc.Id])));
            TrigramIndexWriter.AppendBatch(trigramPath, documents, tombstones);

            if (progress is not null)
            {
                foreach (var doc in changedDocs)
                {
                    if (repoNames.TryGetValue(doc.RepoId, out var name))
                    {
                        progress.ItemProcessed(name, doc.Path);
                    }
                }
            }

            var stats = TrigramSegment.Scan(trigramPath);
            if (stats.TombstoneRatio > CompactHintThreshold)
            {
                warnings.Add(
                    $"trigram index is {stats.TombstoneRatio:P0} tombstones; run 'squeezr compact' to reclaim space");
            }
        }
        else
        {
            RebuildTrigramIndex(workspaceRoot, store, manifest, warnings, textCache, trigramPath, progress);
        }
    }

    public FileDocument? IndexFile(
        string workspaceRoot,
        string repoName,
        string relativePath,
        IEmbeddingProvider? embeddings = null,
        IVectorStore? vectorStore = null,
        Action<string>? onStaleTakeover = null,
        EmbeddingProviderResolution? embeddingProviderResolution = null)
    {
        if (!WorkspaceManifestFile.Exists(workspaceRoot))
        {
            throw new WorkspaceException(
                "Workspace is not initialized. Run 'squeezr index' first.",
                WorkspaceException.NotInitializedCode);
        }

        var manifest = WorkspaceManifestFile.Load(workspaceRoot);
        var config = ConfigFile.Load(workspaceRoot);
        var repo = manifest.Repos.FirstOrDefault(r => string.Equals(r.Name, repoName, StringComparison.OrdinalIgnoreCase))
                   ?? throw new WorkspaceException($"Repository '{repoName}' is not in the workspace.");

        using (IndexLock.Acquire(SqueezrPaths.StoreDir(workspaceRoot), onStaleTakeover))
        using (var store = new MetaStore(SqueezrPaths.MetaDbPath(workspaceRoot)))
        {
            var warnings = new List<string>();
            var textCache = new Dictionary<uint, string>();
            var repoDoc = store.FindRepo(repo.Name)
                          ?? throw new WorkspaceException($"Repository '{repo.Name}' is missing from the index store.");
            var repoRoot = ResolveRepoRoot(workspaceRoot, repo);
            var fullPath = Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var now = DateTime.UtcNow;
            var existing = store.ListFiles(repoDoc.Id)
                .FirstOrDefault(f => string.Equals(f.Path, relativePath, StringComparison.Ordinal));

            if (embeddings is not null && vectorStore is not null)
            {
                ReplayPendingReaps(vectorStore, store, warnings);
            }

            List<FileDocument> changed;
            List<(uint RepoId, uint Id)> tombstones;

            if (!File.Exists(fullPath))
            {
                if (existing is null)
                {
                    return null;
                }

                CascadeSemanticRecords(existing.Id, store);
                store.DeleteFile(existing.Id);
                changed = [];
                tombstones = [(existing.RepoId, existing.Id)];
                DeleteVectorsForFiles(vectorStore, store, [existing.Id], warnings);
            }
            else
            {
                byte[] bytes;
                try
                {
                    bytes = File.ReadAllBytes(fullPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new WorkspaceException($"File '{relativePath}' is unreadable: {ex.Message}");
                }

                var text = DecodeUtf8(bytes);

                var doc = existing ?? new FileDocument { Id = store.GetNextFileId(), RepoId = repoDoc.Id };
                doc.Path = relativePath;
                doc.Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
                doc.Language = LanguageDetector.Detect(fullPath);
                doc.SizeBytes = bytes.Length;
                doc.LineCount = text.Length == 0
                    ? 0
                    : text.Count(c => c == '\n') + (text.EndsWith('\n') ? 0 : 1);
                doc.IndexedAt = now;
                store.UpsertFile(doc);
                textCache[doc.Id] = text;

                var changedByPath = new Dictionary<string, FileDocument>(StringComparer.Ordinal) { [relativePath] = doc };
                var dbFilesByPath = store.ListFiles(repoDoc.Id).ToDictionary(f => f.Path, StringComparer.Ordinal);
                var csFiles = doc.Language == "csharp"
                    ? new List<SourceFile> { new(relativePath, fullPath, doc.Language, doc.SizeBytes, doc.Sha256) }
                    : [];
                var tsTargets = IsTreeSitterLanguage(doc.Language) ? new List<FileDocument> { doc } : [];
                Dictionary<uint, HashSet<string>> priorChunkHashes =
                    embeddings is not null && vectorStore is not null && existing is not null
                        ? SnapshotChunkHashes(store, [existing.Id])
                        : [];

                SemanticIndexPhase.Run(
                    repoRoot, repoDoc, store, csFiles, changedByPath, dbFilesByPath, config,
                    config.Indexing.NoSemantic, warnings.Add);
                var tsResult = TreeSitterIndexPhase.Run(
                    repoRoot, repoDoc, store, tsTargets, textCache, config.Chunking, config.Indexing.NoSemantic,
                    new TreeSitterProvider(config), warnings.Add);
                var providerPaths = csFiles.Select(f => f.RelativePath)
                    .Concat(tsTargets.Select(d => d.Path).Except(tsResult.FailedPaths))
                    .ToHashSet(StringComparer.Ordinal);
                IndexFallbackChunks(
                    store, repoDoc, changedByPath, providerPaths, config.Chunking, textCache);

                if (embeddings is not null && vectorStore is not null)
                {
                    DeleteVectorsForFiles(
                        vectorStore, store, FilesWithStalePoints(store, priorChunkHashes), warnings);
                    var modelKey = EmbeddingModelKey.From(
                        WithResolvedProvider(config.Embeddings, embeddingProviderResolution));
                    EmbeddingIndexPhase.Run(repoDoc, store, modelKey, embeddings, vectorStore, warnings.Add);
                }

                changed = [doc];
                tombstones = [];
            }

            ApplyTrigramUpdates(
                workspaceRoot, store, manifest, warnings, textCache, SqueezrPaths.TrigramIndexPath(workspaceRoot),
                changed, tombstones, full: false, progress: null);

            store.SaveIndexState(new IndexStateDocument
            {
                RepoKey = repo.Name,
                LastFullIndexAt = now,
                Stats = new IndexStateStats
                {
                    Files = store.CountFiles(repoDoc.Id),
                    Symbols = store.CountSymbols(repoDoc.Id),
                    Chunks = store.CountChunks(repoDoc.Id)
                }
            });
            repoDoc.LastIndexedAt = now;
            store.UpsertRepo(repoDoc);

            return changed.Count > 0 ? changed[0] : null;
        }
    }

    private sealed record RepoWork(RepoIndexResult Result, List<FileDocument> Changed, List<FileDocument> Deleted);

    private static RepoWork IndexRepo(
        string workspaceRoot,
        MetaStore store,
        RepoEntry repo,
        bool full,
        SqueezrConfig config,
        bool noSemantic,
        List<string> warnings,
        Dictionary<uint, string> textCache,
        IIndexProgress? progress,
        IEmbeddingProvider? embeddings,
        IVectorStore? vectorStore,
        string? modelKey)
    {
        var repoDoc = store.FindRepo(repo.Name)
                      ?? throw new WorkspaceException($"Repository '{repo.Name}' is missing from the index store.");
        var repoRoot = ResolveRepoRoot(workspaceRoot, repo);
        if (!Directory.Exists(repoRoot))
        {
            throw new WorkspaceException($"Repository path '{repoRoot}' does not exist.");
        }

        progress?.PhaseStarted(repo.Name, IndexPhase.Walk, 0);
        var walk = RepoFileWalker.Walk(repoRoot, config.Indexing.Ignore, config.Indexing.MaxFileSizeKb, warnings.Add);
        progress?.PhaseStarted(repo.Name, IndexPhase.Files, walk.Files.Count);
        var skipped = walk.Skipped;
        var dbFiles = store.ListFiles(repoDoc.Id);
        var dbByPath = dbFiles.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var walkedPaths = walk.Files.Select(f => f.RelativePath).ToHashSet(StringComparer.Ordinal);
        var deleted = dbFiles.Where(f => !walkedPaths.Contains(f.Path)).ToList();
        var now = DateTime.UtcNow;
        var added = 0;
        var modified = 0;
        var nextId = store.GetNextFileId();
        var changed = new List<FileDocument>();
        // a reap drops every point of a re-chunked file, so it is only safe in a pass that can embed the
        // replacement chunks right after — a --no-semantic pass leaves the vector store alone entirely
        var vectorsEnabled = embeddings is not null && vectorStore is not null;
        var reapCandidates = new List<uint>();

        foreach (var file in walk.Files)
        {
            progress?.ItemProcessed(repo.Name, file.RelativePath);

            var isNew = !dbByPath.TryGetValue(file.RelativePath, out var existing);
            if (!full && !isNew && existing!.Sha256 == file.Sha256)
            {
                continue;
            }

            string text;
            try
            {
                text = File.ReadAllText(file.FullPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"skipped '{file.RelativePath}': {ex.Message}");
                skipped++;
                continue;
            }

            if (isNew)
            {
                added++;
            }
            else
            {
                modified++;
                if (vectorsEnabled && existing is not null && (full || existing.Sha256 != file.Sha256))
                {
                    reapCandidates.Add(existing.Id);
                }
            }

            var doc = existing ?? new FileDocument { Id = nextId++, RepoId = repoDoc.Id };
            doc.Path = file.RelativePath;
            doc.Sha256 = file.Sha256;
            doc.Language = file.Language;
            doc.SizeBytes = file.SizeBytes;
            doc.LineCount = text.Length == 0
                ? 0
                : text.Count(c => c == '\n') + (text.EndsWith('\n') ? 0 : 1);
            doc.IndexedAt = now;
            store.UpsertFile(doc);
            textCache[doc.Id] = text;
            changed.Add(doc);
        }

        foreach (var doc in deleted)
        {
            CascadeSemanticRecords(doc.Id, store);
            store.DeleteFile(doc.Id);
        }

        Dictionary<uint, HashSet<string>> priorChunkHashes =
            vectorsEnabled ? SnapshotChunkHashes(store, reapCandidates) : [];

        var csFiles = walk.Files.Where(f => f.Language == "csharp").ToList();
        var changedByPath = changed.ToDictionary(c => c.Path, StringComparer.Ordinal);
        var dbFilesByPath = store.ListFiles(repoDoc.Id).ToDictionary(f => f.Path, StringComparer.Ordinal);

        var semantic = SemanticIndexPhase.Run(
            repoRoot, repoDoc, store, csFiles, changedByPath, dbFilesByPath, config, noSemantic, warnings.Add, progress);
        skipped += semantic.Skipped;

        var tsTargets = changed.Where(d => IsTreeSitterLanguage(d.Language)).ToList();
        var tsResult = TreeSitterIndexPhase.Run(
            repoRoot, repoDoc, store, tsTargets, textCache, config.Chunking, noSemantic,
            new TreeSitterProvider(config), warnings.Add, progress);
        skipped += tsResult.Skipped;

        var providerPaths = csFiles.Select(f => f.RelativePath)
            .Concat(tsTargets.Select(d => d.Path).Except(tsResult.FailedPaths))
            .ToHashSet(StringComparer.Ordinal);
        IndexFallbackChunks(store, repoDoc, changedByPath, providerPaths, config.Chunking, textCache);

        if (embeddings is not null && vectorStore is not null)
        {
            DeleteVectorsForFiles(vectorStore, store, FilesWithStalePoints(store, priorChunkHashes), warnings);
            EmbeddingIndexPhase.Run(repoDoc, store, modelKey!, embeddings, vectorStore, warnings.Add, progress);
        }

        var totalFiles = store.CountFiles(repoDoc.Id);
        store.SaveIndexState(new IndexStateDocument
        {
            RepoKey = repo.Name,
            LastFullIndexAt = now,
            Stats = new IndexStateStats
            {
                Files = totalFiles,
                Symbols = store.CountSymbols(repoDoc.Id),
                Chunks = store.CountChunks(repoDoc.Id)
            }
        });
        repoDoc.LastIndexedAt = now;
        store.UpsertRepo(repoDoc);

        return new RepoWork(
            new RepoIndexResult(repo.Name, added, modified, deleted.Count, skipped, totalFiles),
            changed,
            deleted);
    }

    private static void EnsureVectorCompatibility(IEmbeddingProvider embeddings, IVectorStore vectorStore, List<string> warnings)
    {
        try
        {
            if (!vectorStore.IsAvailableAsync(CancellationToken.None).GetAwaiter().GetResult())
            {
                return;
            }

            vectorStore.EnsureCollectionAsync(embeddings.Dimensions, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (VectorDimensionMismatchException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not OperationCanceledException)
        {
            warnings.Add($"vector preflight skipped: {ex.Message}");
        }
    }

    internal static void CascadeSemanticRecords(uint fileId, MetaStore store)
    {
        var oldIds = store.ListSymbolsByFile(fileId).Select(s => s.Id).ToList();
        store.DeleteRefsByFromSymbolIds(oldIds);
        store.DeleteRefsByFile(fileId);
        store.DeleteSymbolsByFile(fileId);
        store.DeleteChunksByFile(fileId);
    }

    private static bool IsTreeSitterLanguage(string language) =>
        language is "javascript" or "typescript" or "tsx";

    private static void IndexFallbackChunks(
        MetaStore store,
        RepoDocument repoDoc,
        IReadOnlyDictionary<string, FileDocument> changedByPath,
        IReadOnlyCollection<string> providerPaths,
        ChunkingConfig chunking,
        Dictionary<uint, string> textCache)
    {
        foreach (var (path, doc) in changedByPath)
        {
            if (providerPaths.Contains(path) || !textCache.TryGetValue(doc.Id, out var text))
            {
                continue;
            }

            FallbackChunker.Persist(store, repoDoc, doc, text, chunking);
        }
    }

    private static Dictionary<uint, HashSet<string>> SnapshotChunkHashes(MetaStore store, IEnumerable<uint> fileIds)
    {
        var snapshot = new Dictionary<uint, HashSet<string>>();
        foreach (var fileId in fileIds)
        {
            var hashes = store.ListChunkHashesByFile(fileId);
            if (hashes.Count > 0)
            {
                snapshot[fileId] = hashes.ToHashSet(StringComparer.Ordinal);
            }
        }

        return snapshot;
    }

    // Re-chunking replaces a file's chunk rows wholesale, so a modified file whose chunk set moved leaves
    // its previous points behind in the vector store. Only a differing hash set earns a delete request: an
    // edit that cannot move a chunk boundary re-upserts the very same point ids, so there is nothing to reap.
    private static List<uint> FilesWithStalePoints(
        MetaStore store, IReadOnlyDictionary<uint, HashSet<string>> priorChunkHashes)
    {
        var stale = new List<uint>();
        foreach (var (fileId, before) in priorChunkHashes)
        {
            if (!before.SetEquals(store.ListChunkHashesByFile(fileId)))
            {
                stale.Add(fileId);
            }
        }

        return stale;
    }

    private static void DeleteVectorsForFiles(
        IVectorStore? vectorStore, MetaStore store, IReadOnlyList<uint> fileIds, List<string> warnings)
    {
        if (vectorStore is null || fileIds.Count == 0)
        {
            return;
        }

        try
        {
            if (!vectorStore.IsAvailableAsync(CancellationToken.None).GetAwaiter().GetResult())
            {
                DeferReap(store, fileIds, warnings, "vector store unavailable");
                return;
            }

            vectorStore.DeleteByFileIdsAsync(fileIds, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not OperationCanceledException)
        {
            DeferReap(store, fileIds, warnings, $"failed to remove vectors for {fileIds.Count} file(s): {ex.Message}");
        }
    }

    private static void DeferReap(MetaStore store, IReadOnlyList<uint> fileIds, List<string> warnings, string reason)
    {
        try
        {
            store.EnqueuePendingReaps(fileIds);
            warnings.Add($"{reason}; point cleanup for {fileIds.Count} file(s) deferred to a later pass");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not OperationCanceledException)
        {
            warnings.Add(
                $"{reason}; point cleanup for {fileIds.Count} file(s) NOT deferred " +
                $"(journal write failed: {ex.Message}) — those points stay orphaned");
        }
    }

    private static void ReplayPendingReaps(IVectorStore vectorStore, MetaStore store, List<string> warnings)
    {
        var fileIds = store.ListPendingReaps().Select(p => (uint)p.FileId).ToList();
        if (fileIds.Count == 0)
        {
            return;
        }

        try
        {
            if (!vectorStore.IsAvailableAsync(CancellationToken.None).GetAwaiter().GetResult())
            {
                return;
            }

            vectorStore.DeleteByFileIdsAsync(fileIds, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not OperationCanceledException)
        {
            warnings.Add($"deferred point cleanup for {fileIds.Count} file(s) failed again: {ex.Message}");
            return;
        }

        var requeued = fileIds.Sum(store.MarkFileChunksPending);
        store.ClearPendingReaps(fileIds);
        warnings.Add(
            $"replayed deferred point cleanup for {fileIds.Count} file(s); {requeued} chunk(s) re-queued for embedding");
    }

    internal static void RebuildTrigramIndex(
        string workspaceRoot,
        MetaStore store,
        WorkspaceManifest manifest,
        List<string> warnings,
        Dictionary<uint, string> textCache,
        string targetPath,
        IIndexProgress? progress = null)
    {
        var writer = new TrigramIndexWriter();
        var reposById = store.ListRepos().ToDictionary(r => r.Id);
        var entriesByName = manifest.Repos.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
        var allFiles = store.ListAllFiles().ToList();

        if (progress is not null)
        {
            var totals = allFiles
                .Where(f => reposById.TryGetValue(f.RepoId, out var repoDoc) && entriesByName.ContainsKey(repoDoc.Name))
                .GroupBy(f => reposById[f.RepoId].Name)
                .ToDictionary(g => g.Key, g => g.Count());
            foreach (var (name, total) in totals)
            {
                progress.PhaseStarted(name, IndexPhase.Trigram, total);
            }
        }

        foreach (var file in allFiles)
        {
            if (!reposById.TryGetValue(file.RepoId, out var repoDoc)
                || !entriesByName.TryGetValue(repoDoc.Name, out var entry))
            {
                warnings.Add($"dropping '{file.Path}' from index store: repository is not in the workspace manifest");
                store.DeleteFile(file.Id);
                continue;
            }

            string text;
            try
            {
                if (textCache.TryGetValue(file.Id, out var cached))
                {
                    text = cached;
                }
                else
                {
                    var fullPath = Path.Combine(ResolveRepoRoot(workspaceRoot, entry), file.Path.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(fullPath))
                    {
                        warnings.Add($"dropping '{file.Path}' from index store: file is missing on disk");
                        store.DeleteFile(file.Id);
                        continue;
                    }

                    try
                    {
                        text = File.ReadAllText(fullPath);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        warnings.Add($"dropping '{file.Path}' from index store: unreadable ({ex.Message})");
                        store.DeleteFile(file.Id);
                        continue;
                    }
                }

                var postings = TrigramExtractor.ExtractPostings(text);
                writer.AddDocument(new TrigramDocument(
                    file.RepoId,
                    file.Id,
                    postings.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray())));
            }
            finally
            {
                progress?.ItemProcessed(repoDoc.Name, file.Path);
            }
        }

        writer.Write(targetPath);
    }

    private static string ResolveRepoRoot(string workspaceRoot, RepoEntry repo) =>
        Path.GetFullPath(Path.IsPathRooted(repo.Path) ? repo.Path : Path.Combine(workspaceRoot, repo.Path));
}
