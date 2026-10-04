using Squeezr.Core.Configuration;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Indexing.Ignore;
using Squeezr.Core.Indexing.Roslyn;
using Squeezr.Core.Indexing.TreeSitter;
using Squeezr.Core.Indexing.Trigram;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Core.Indexing;

public sealed class WorkspaceIndexer
{
    private const double CompactHintThreshold = 0.3;

    private static readonly string[] ProjectLayoutExtensions = [".csproj", ".sln", ".slnx", ".props", ".targets"];

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
        EmbeddingProviderResolution? embeddingProviderResolution = null,
        IReadOnlyList<string>? changedPaths = null,
        SemanticSessionPool? sessions = null)
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

        if (sessions is not null)
        {
            // a repo dropped from the manifest must drop its session too: each entry is a live Roslyn
            // workspace worth hundreds of MB, and nothing else in the pass ever revisits composition
            sessions.PruneTo(manifest.Repos
                .Select(repo => ResolveRepoRoot(workspaceRoot, repo))
                .ToList());
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
                .Select(repo => IndexRepo(workspaceRoot, store, repo, full, config, effectiveNoSemantic, warnings, textCache, progress, embeddings, vectorStore, modelKey, Pacing(sessions, changedPaths)))
                .ToList();

            var changedDocs = works.SelectMany(w => w.Changed).ToList();
            var reaps = works.SelectMany(w => w.Reaps).ToList();
            var tombstones = reaps.Select(r => (r.RepoId, r.Id)).ToList();

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
                    reaps.Add(CaptureReap(store, file));
                    CascadeSemanticRecords(file.Id, store);
                    store.DeleteFile(file.Id);
                    tombstones.Add((file.RepoId, file.Id));
                }
            }

            var deferredReaps = works.Sum(w => w.DeferredReaps) +
                RemoveVectorsOfDeletedFiles(vectorStore, store, reaps, warnings);

            deferredReaps += ApplyTrigramUpdates(
                workspaceRoot, store, manifest, warnings, textCache, SqueezrPaths.TrigramIndexPath(workspaceRoot),
                changedDocs, tombstones, full, progress, vectorStore);

            if (progress is not null)
            {
                foreach (var repo in targets)
                {
                    progress.RepoCompleted(repo.Name);
                }
            }

            return new IndexResult(
                works.Select(w => w.Result).ToList(), DateTime.UtcNow, warnings,
                deferredReaps, store.CountPendingReaps());
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

    private static int ApplyTrigramUpdates(
        string workspaceRoot,
        MetaStore store,
        WorkspaceManifest manifest,
        List<string> warnings,
        Dictionary<uint, string> textCache,
        string trigramPath,
        List<FileDocument> changedDocs,
        List<(uint RepoId, uint Id)> tombstones,
        bool full,
        IIndexProgress? progress,
        IVectorStore? vectorStore)
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
                return 0;
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

            return 0;
        }

        return RebuildTrigramIndex(
            workspaceRoot, store, manifest, warnings, textCache, trigramPath, progress, vectorStore);
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
            var rules = IgnoreRuleset.ForFile(repoRoot, config, relativePath, warnings.Add);

            if (embeddings is not null && vectorStore is not null)
            {
                ReplayPendingReaps(vectorStore, store, warnings);
            }

            List<FileDocument> changed;
            List<(uint RepoId, uint Id)> tombstones;

            if (File.Exists(fullPath) && rules.Decision(relativePath, isDirectory: false).Excluded)
            {
                if (existing is null)
                {
                    return null;
                }

                DropFile(existing, store, vectorStore, warnings);
                changed = [];
                tombstones = [(existing.RepoId, existing.Id)];
            }
            else if (!File.Exists(fullPath))
            {
                if (existing is null)
                {
                    return null;
                }

                DropFile(existing, store, vectorStore, warnings);
                changed = [];
                tombstones = [(existing.RepoId, existing.Id)];
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
                    var reaped = RequeueReapedFiles(store, FilesWithStalePoints(store, priorChunkHashes));
                    DeleteVectorsForFiles(vectorStore, store, reaped, warnings);
                    var modelKey = EmbeddingModelKey.From(
                        WithResolvedProvider(config.Embeddings, embeddingProviderResolution));
                    EmbeddingIndexPhase.Run(repoDoc, store, modelKey, embeddings, vectorStore, warnings.Add);
                }

                changed = [doc];
                tombstones = [];
            }

            ApplyTrigramUpdates(
                workspaceRoot, store, manifest, warnings, textCache, SqueezrPaths.TrigramIndexPath(workspaceRoot),
                changed, tombstones, full: false, progress: null, vectorStore);

            var priorState = store.GetIndexState(repo.Name);
            store.SaveIndexState(new IndexStateDocument
            {
                RepoKey = repo.Name,
                LastFullIndexAt = now,
                Stats = new IndexStateStats
                {
                    Files = store.CountFiles(repoDoc.Id),
                    Symbols = store.CountSymbols(repoDoc.Id),
                    Chunks = store.CountChunks(repoDoc.Id),
                    IgnoredTotal = priorState?.Stats.IgnoredTotal ?? 0,
                    IgnoredBySource = priorState?.Stats.IgnoredBySource
                }
            });
            repoDoc.LastIndexedAt = now;
            store.UpsertRepo(repoDoc);

            return changed.Count > 0 ? changed[0] : null;
        }
    }

    private sealed record RepoWork(
        RepoIndexResult Result,
        List<FileDocument> Changed,
        List<FileReap> Reaps,
        int DeferredReaps);

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
        string? modelKey,
        PacedPass? pacing)
    {
        var repoDoc = store.FindRepo(repo.Name)
                      ?? throw new WorkspaceException($"Repository '{repo.Name}' is missing from the index store.");
        var repoRoot = ResolveRepoRoot(workspaceRoot, repo);
        if (!Directory.Exists(repoRoot))
        {
            throw new WorkspaceException($"Repository path '{repoRoot}' does not exist.");
        }

        progress?.PhaseStarted(repo.Name, IndexPhase.Walk, 0);
        var rules = IgnoreRuleset.For(repoRoot, config, warnings.Add);
        var walk = RepoFileWalker.Walk(repoRoot, rules, config.Indexing.MaxFileSizeKb, warnings.Add);
        progress?.PhaseStarted(repo.Name, IndexPhase.Files, walk.Files.Count);
        var skipped = walk.Skipped;
        var dbFiles = store.ListFiles(repoDoc.Id);
        var dbByPath = dbFiles.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var walkedPaths = walk.Files.Select(f => f.RelativePath).ToHashSet(StringComparer.Ordinal);
        var deleted = dbFiles.Where(f => !walkedPaths.Contains(f.Path)).ToList();
        var reaps = new List<FileReap>(deleted.Count);
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
            reaps.Add(CaptureReap(store, doc));
            CascadeSemanticRecords(doc.Id, store);
            store.DeleteFile(doc.Id);
        }

        Dictionary<uint, HashSet<string>> priorChunkHashes =
            vectorsEnabled ? SnapshotChunkHashes(store, reapCandidates) : [];

        var incremental = pacing is not null && pacing.IsIncremental(repoRoot, !noSemantic) && !full;
        HashSet<string>? touched = null;
        if (pacing is not null)
        {
            if (incremental && ProjectLayoutChanged(pacing))
            {
                pacing.Sessions.Discard(repoRoot);
            }

            if (incremental)
            {
                touched = TouchedPaths(changed, pacing, repoRoot);
                touched.UnionWith(pacing.Sessions.DeferredSemantic(repoRoot));
                pacing.Sessions.RemoveDocuments(
                    repoRoot, deleted.Where(d => d.Language == "csharp").Select(d => d.Path).ToList());
            }
            else
            {
                // cold pool or an explicit --full: the pass cannot scope to the changed set, so the pooled
                // session goes and the next one opens a fresh one from disk
                pacing.Sessions.Discard(repoRoot);
            }
        }

        var csFiles = walk.Files
            .Where(f => f.Language == "csharp" && (touched is null || touched.Contains(f.RelativePath)))
            .ToList();
        var changedByPath = changed.ToDictionary(c => c.Path, StringComparer.Ordinal);
        var dbFilesByPath = store.ListFiles(repoDoc.Id).ToDictionary(f => f.Path, StringComparer.Ordinal);

        var semantic = pacing is null
            ? SemanticIndexPhase.Run(
                repoRoot, repoDoc, store, csFiles, changedByPath, dbFilesByPath, config, noSemantic, warnings.Add, progress)
            : SemanticIndexPhase.RunPooled(
                pacing.Sessions, repoRoot, repoDoc, store, csFiles, changedByPath, dbFilesByPath, config,
                noSemantic, warnings.Add, progress, syncDocuments: touched is not null);
        skipped += semantic.Skipped;
        if (pacing is not null)
        {
            // the walk commits each sha before this phase runs, so a file the phase did not write would
            // otherwise never come back into scope: the pool carries the set into the next pass instead
            pacing.Sessions.ReplaceDeferredSemantic(repoRoot, semantic.Deferred ?? []);
        }

        var tsTargets = changed.Where(d => IsTreeSitterLanguage(d.Language)).ToList();
        var tsResult = TreeSitterIndexPhase.Run(
            repoRoot, repoDoc, store, tsTargets, textCache, config.Chunking, noSemantic,
            new TreeSitterProvider(config), warnings.Add, progress);
        skipped += tsResult.Skipped;

        var providerPaths = csFiles.Select(f => f.RelativePath)
            .Concat(tsTargets.Select(d => d.Path).Except(tsResult.FailedPaths))
            .ToHashSet(StringComparer.Ordinal);
        IndexFallbackChunks(store, repoDoc, changedByPath, providerPaths, config.Chunking, textCache);

        var deferredReaps = 0;
        if (embeddings is not null && vectorStore is not null)
        {
            var reaped = RequeueReapedFiles(store, FilesWithStalePoints(store, priorChunkHashes));
            deferredReaps = DeleteVectorsForFiles(vectorStore, store, reaped, warnings);
            EmbeddingIndexPhase.Run(repoDoc, store, modelKey!, embeddings, vectorStore, warnings.Add, progress);
        }

        var totalFiles = store.CountFiles(repoDoc.Id);
        var ignoredBySource = walk.IgnoredBySource
            .Where(kv => kv.Value > 0)
            .ToDictionary(kv => IgnoreSourceKey.Of(kv.Key), kv => kv.Value);
        var ignoredSnapshot = ignoredBySource.Count > 0 ? ignoredBySource : null;
        store.SaveIndexState(new IndexStateDocument
        {
            RepoKey = repo.Name,
            LastFullIndexAt = now,
            Stats = new IndexStateStats
            {
                Files = totalFiles,
                Symbols = store.CountSymbols(repoDoc.Id),
                Chunks = store.CountChunks(repoDoc.Id),
                IgnoredTotal = walk.Ignored,
                IgnoredBySource = ignoredSnapshot
            }
        });
        repoDoc.LastIndexedAt = now;
        store.UpsertRepo(repoDoc);

        return new RepoWork(
            new RepoIndexResult(repo.Name, added, modified, reaps.Count, skipped, totalFiles, walk.Ignored, ignoredSnapshot),
            changed,
            reaps,
            deferredReaps);
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

    // Identity is captured while the row is still in front of us: the journal has to name the file an id
    // meant at queue time, because MetaStore hands freed ids out to the next added file.
    private readonly record struct FileReap(uint Id, uint RepoId, string Path, bool HadVectors)
    {
        public PendingReapTarget Target => new(Id, RepoId, Path);
    }

    private static FileReap CaptureReap(MetaStore store, FileDocument file) =>
        new(file.Id, file.RepoId, file.Path, store.HasEmbeddedChunks(file.Id));

    // A deleted file row is the only name a point has. When the pass carries no vector store the points it
    // cannot remove go to the reap journal for the next pass that can — and only for files that actually held
    // vectors: a never-embedded id has nothing to reap, and the walk hands freed ids out again.
    private static int RemoveVectorsOfDeletedFiles(
        IVectorStore? vectorStore,
        MetaStore store,
        IReadOnlyList<FileReap> deleted,
        List<string> warnings)
    {
        if (vectorStore is not null)
        {
            return DeleteVectorsForFiles(vectorStore, store, deleted.Select(r => r.Target).ToList(), warnings);
        }

        var owed = deleted.Where(r => r.HadVectors).Select(r => r.Target).ToList();
        return owed.Count > 0
            ? DeferReap(store, owed, warnings, "this pass carries no vector store")
            : 0;
    }

    private static int DropFile(
        FileDocument file, MetaStore store, IVectorStore? vectorStore, List<string> warnings)
    {
        var reap = CaptureReap(store, file);
        CascadeSemanticRecords(reap.Id, store);
        store.DeleteFile(reap.Id);
        return RemoveVectorsOfDeletedFiles(vectorStore, store, [reap], warnings);
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

    // A carried marker is only honest while the point behind it exists: the reap removes every point of the
    // file, chunks with an unchanged hash included, so the whole file goes back into the queue with it.
    // Scoped to the reaped file — a point shared with another file (identical chunk text) is that other
    // file's problem, and reconcile's re-queue is what closes it.
    private static IReadOnlyList<PendingReapTarget> RequeueReapedFiles(MetaStore store, List<uint> fileIds)
    {
        var targets = new List<PendingReapTarget>(fileIds.Count);
        foreach (var fileId in fileIds)
        {
            store.MarkFileChunksPending(fileId);
            // the row is alive here (a reaped file was just re-chunked), so its identity is still readable
            var row = store.FindFileById(fileId);
            targets.Add(new PendingReapTarget(fileId, row?.RepoId ?? 0, row?.Path));
        }

        return targets;
    }

    private static int DeleteVectorsForFiles(
        IVectorStore vectorStore, MetaStore store, IReadOnlyList<PendingReapTarget> files, List<string> warnings)
    {
        if (files.Count == 0)
        {
            return 0;
        }

        var fileIds = files.Select(f => f.FileId).ToList();
        try
        {
            if (!vectorStore.IsAvailableAsync(CancellationToken.None).GetAwaiter().GetResult())
            {
                return DeferReap(store, files, warnings, "vector store unavailable");
            }

            vectorStore.DeleteByFileIdsAsync(fileIds, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not OperationCanceledException)
        {
            return DeferReap(store, files, warnings, $"failed to remove vectors for {fileIds.Count} file(s): {ex.Message}");
        }

        return 0;
    }

    private static int DeferReap(
        MetaStore store, IReadOnlyList<PendingReapTarget> files, List<string> warnings, string reason)
    {
        try
        {
            store.EnqueuePendingReaps(files);
            warnings.Add($"{reason}; point cleanup for {files.Count} file(s) deferred to a later pass");
            return files.Count;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not OperationCanceledException)
        {
            warnings.Add(
                $"{reason}; point cleanup for {files.Count} file(s) NOT deferred " +
                $"(journal write failed: {ex.Message}) — those points stay orphaned");
            return 0;
        }
    }

    // An entry only names a live file while its recorded identity still matches the row behind the id.
    // MetaStore recycles freed ids, so a row that answers to a different repo/path means the entry was
    // written for a file that no longer exists: deleting by that id would destroy a stranger's vectors and
    // spend the entry on the impostor, leaving the real orphans unattributed forever. Such entries are
    // dropped and handed to the hash-level route ('squeezr vectors reconcile'), which never names a file.
    private static void ReplayPendingReaps(IVectorStore vectorStore, MetaStore store, List<string> warnings)
    {
        var entries = store.ListPendingReaps();
        if (entries.Count == 0)
        {
            return;
        }

        var owed = new List<uint>();
        var recycled = new List<uint>();
        foreach (var entry in entries)
        {
            var fileId = (uint)entry.FileId;
            var live = store.FindFileById(fileId);
            if (live is null
                || (entry.Path is not null && live.RepoId == entry.RepoId &&
                    string.Equals(live.Path, entry.Path, StringComparison.Ordinal)))
            {
                owed.Add(fileId);
            }
            else
            {
                recycled.Add(fileId);
            }
        }

        if (recycled.Count > 0)
        {
            store.ClearPendingReaps(recycled);
            warnings.Add(
                $"{recycled.Count} deferred point cleanup(s) dropped: those file ids now name different files — " +
                "run 'squeezr vectors reconcile --apply' to remove the points their deletions left behind");
        }

        if (owed.Count == 0)
        {
            return;
        }

        try
        {
            if (!vectorStore.IsAvailableAsync(CancellationToken.None).GetAwaiter().GetResult())
            {
                return;
            }

            vectorStore.DeleteByFileIdsAsync(owed, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not OperationCanceledException)
        {
            warnings.Add($"deferred point cleanup for {owed.Count} file(s) failed again: {ex.Message}");
            return;
        }

        var requeued = owed.Sum(store.MarkFileChunksPending);
        store.ClearPendingReaps(owed);
        warnings.Add(
            $"replayed deferred point cleanup for {owed.Count} file(s); {requeued} chunk(s) re-queued for embedding");
    }

    internal static int RebuildTrigramIndex(
        string workspaceRoot,
        MetaStore store,
        WorkspaceManifest manifest,
        List<string> warnings,
        Dictionary<uint, string> textCache,
        string targetPath,
        IIndexProgress? progress = null,
        IVectorStore? vectorStore = null)
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

        var deferredReaps = 0;
        foreach (var file in allFiles)
        {
            if (!reposById.TryGetValue(file.RepoId, out var repoDoc)
                || !entriesByName.TryGetValue(repoDoc.Name, out var entry))
            {
                warnings.Add($"dropping '{file.Path}' from index store: repository is not in the workspace manifest");
                deferredReaps += DropFile(file, store, vectorStore, warnings);
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
                        deferredReaps += DropFile(file, store, vectorStore, warnings);
                        continue;
                    }

                    try
                    {
                        text = File.ReadAllText(fullPath);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        warnings.Add($"dropping '{file.Path}' from index store: unreadable ({ex.Message})");
                        deferredReaps += DropFile(file, store, vectorStore, warnings);
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
        return deferredReaps;
    }

    private static string ResolveRepoRoot(string workspaceRoot, RepoEntry repo) =>
        Path.GetFullPath(Path.IsPathRooted(repo.Path) ? repo.Path : Path.Combine(workspaceRoot, repo.Path));

    private static PacedPass? Pacing(SemanticSessionPool? sessions, IReadOnlyList<string>? changedPaths) =>
        sessions is null ? null : new PacedPass(sessions, changedPaths ?? []);

    // a warm session carries the project metadata it opened with: references, package versions, compile
    // items. an edited project file is the batch event that makes the session itself wrong rather than only
    // the rows it produces, so the pass drops the session and re-opens from disk — one extra open, and that
    // open is what heals a solution that gained a project or a package
    private static bool ProjectLayoutChanged(PacedPass pacing) =>
        pacing.ChangedPaths.Any(path =>
            ProjectLayoutExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase));

    private static HashSet<string> TouchedPaths(List<FileDocument> changed, PacedPass pacing, string repoRoot)
    {
        var touched = changed.Select(c => c.Path).ToHashSet(StringComparer.Ordinal);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = repoRoot + Path.DirectorySeparatorChar;
        foreach (var path in pacing.ChangedPaths)
        {
            if (!Path.IsPathRooted(path))
            {
                continue;
            }

            var full = Path.GetFullPath(path);
            if (full.StartsWith(prefix, comparison))
            {
                touched.Add(full[prefix.Length..].Replace(Path.DirectorySeparatorChar, '/'));
            }
        }

        return touched;
    }
}
