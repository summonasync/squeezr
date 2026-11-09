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

    internal static readonly string[] ProjectLayoutExtensions = [".csproj", ".sln", ".slnx", ".props", ".targets"];

    // Why a stored row left the index. The reason is always reported; for a row the guard refuses the path
    // never is, because the name of the secret is exactly what such a warning would leak.
    private enum StoreDrop
    {
        RepositoryGone,
        MissingOnDisk,
        Unreadable,
    }

    private static string StoreDropReason(StoreDrop drop) => drop switch
    {
        StoreDrop.RepositoryGone => "repository is not in the workspace manifest",
        StoreDrop.MissingOnDisk => "file is missing on disk",
        _ => "unreadable",
    };

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
        string? embeddingsUnavailableReason = null,
        IReadOnlyList<string>? changedPaths = null,
        SemanticSessionPool? sessions = null,
        int? embedBudgetPerPass = null,
        CancellationToken cancellationToken = default)
    {
        if (!WorkspaceManifestFile.Exists(workspaceRoot))
        {
            throw new WorkspaceException(
                RemedyText.WorkspaceNotInitialized,
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
            var guard = SecretGuardIngestion.Load(workspaceRoot);

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

            var works = new List<RepoWork>();
            var embedRemaining = embedBudgetPerPass;
            foreach (var repo in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var work = IndexRepo(workspaceRoot, store, repo, full, config, effectiveNoSemantic, warnings,
                    textCache, progress, embeddings, vectorStore, modelKey, embedRemaining, Pacing(sessions, changedPaths),
                    embeddingsUnavailableReason, cancellationToken);
                if (embedRemaining is not null)
                {
                    embedRemaining = Math.Max(0, embedRemaining.Value - work.Result.Embedded);
                }

                works.Add(work);
            }

            var changedDocs = works.SelectMany(w => w.Changed).ToList();
            var reaps = works.SelectMany(w => w.Reaps).ToList();
            var tombstones = reaps.Select(r => (r.RepoId, r.Id)).ToList();

            if (repoName is null)
            {
                var manifestNames = manifest.Repos.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var reposById = store.ListRepos().ToDictionary(r => r.Id);
                var protectedDrops = new Dictionary<StoreDrop, int>();
                foreach (var file in store.ListAllFiles())
                {
                    if (reposById.TryGetValue(file.RepoId, out var repoDoc) && manifestNames.Contains(repoDoc.Name))
                    {
                        continue;
                    }

                    NoteStoreDrop(
                        guard, protectedDrops, warnings, file.Path, StoreDrop.RepositoryGone);
                    reaps.Add(CaptureReap(store, file));
                    CascadeSemanticRecords(file.Id, store);
                    store.DeleteFile(file.Id);
                    tombstones.Add((file.RepoId, file.Id));
                }

                FlushStoreDrops(protectedDrops, warnings);
            }

            var deferredReaps = works.Sum(w => w.DeferredReaps) +
                RemoveVectorsOfDeletedFiles(vectorStore, store, reaps, warnings, guard);

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

            var embeddingsCompleted = works.All(w => w.Result.EmbeddingsCompleted);
            return new IndexResult(
                works.Select(w => w.Result).ToList(), DateTime.UtcNow, warnings,
                deferredReaps, store.CountPendingReaps(),
                works.Sum(w => w.Result.GuardWithheld),
                works.Sum(w => w.Result.GuardWithheldBuiltIns),
                works.Sum(w => w.Result.GuardDropped),
                works.Sum(w => w.Result.Embedded),
                embeddingsCompleted,
                works.Sum(w => w.Result.Throttled),
                embeddingsCompleted ? null : embeddingsUnavailableReason);
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
                RemedyText.WorkspaceNotInitialized,
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
            var now = DateTime.UtcNow;
            var existing = store.ListFiles(repoDoc.Id)
                .FirstOrDefault(f => string.Equals(f.Path, relativePath, StringComparison.Ordinal));
            var guard = SecretGuardIngestion.Load(workspaceRoot);

            // The guard answers before the path is turned into a file: a protected name is never opened here,
            // never reported unreadable, and never written back. A row that predates the guard leaves the same
            // way an ignored path does — dropped, not thrown at — because this route is what a read call
            // triggers, and a daemon must not fail a request over a secret it is refusing.
            if (guard.Deny(relativePath))
            {
                if (existing is not null)
                {
                    DropFile(existing, store, vectorStore, warnings, guard);
                    SaveWithheldState(store, repo.Name, repoDoc, now, guard, rowDropped: true);
                }

                return null;
            }

            var fullPath = Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var rules = IgnoreRuleset.ForFile(repoRoot, config, relativePath, warnings.Add);

            if (embeddings is not null && vectorStore is not null)
            {
                ReplayPendingReaps(vectorStore, store, warnings);
            }

            List<FileDocument> changed = [];
            List<(uint RepoId, uint Id)> tombstones = [];

            if (File.Exists(fullPath) && rules.Decision(relativePath, isDirectory: false).Excluded)
            {
                if (existing is null)
                {
                    return null;
                }

                DropFile(existing, store, vectorStore, warnings, guard);
                changed = [];
                tombstones = [(existing.RepoId, existing.Id)];
            }
            else if (!File.Exists(fullPath))
            {
                // File.Exists answers the same false for a deletion and for a path a non-executable parent
                // hides from stat; the probe's exception shape is the split — a denied stat keeps the row,
                // a deletion or a shape swap reaps it, and a present regular file falls through to the
                // regular-file gate below and indexes normally
                var denial = ProbeMissingStat(fullPath, out var present);
                if (denial is not null)
                {
                    if (existing is null)
                    {
                        return null;
                    }

                    warnings.Add($"keeping '{relativePath}' in the index store: unreadable ({denial.Message}); rows kept");
                    changed = [];
                    tombstones = [];
                }
                else if (!present || !UnixFileType.IsRegularFile(fullPath))
                {
                    // a directory (or a symlink now pointing at one) that took the path is a shape swap,
                    // not content the row could keep: nothing readable lives there — reap, exactly as
                    // the pre-probe chain reaped it
                    if (existing is null)
                    {
                        return null;
                    }

                    DropFile(existing, store, vectorStore, warnings, guard);
                    changed = [];
                    tombstones = [(existing.RepoId, existing.Id)];
                }
                else
                {
                    IndexPresentFile();
                }
            }
            else if (!UnixFileType.IsRegularFile(fullPath))
            {
                if (existing is null)
                {
                    return null;
                }

                DropFile(existing, store, vectorStore, warnings, guard);
                changed = [];
                tombstones = [(existing.RepoId, existing.Id)];
            }
            else
            {
                IndexPresentFile();
            }

            void IndexPresentFile()
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

                var text = SecretSnippetRedactor.Redact(relativePath, DecodeUtf8(bytes));

                var doc = existing ?? new FileDocument { Id = store.GetNextFileId(), RepoId = repoDoc.Id };
                doc.Path = relativePath;
                doc.Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
                doc.Language = LanguageDetector.Detect(fullPath);
                doc.Kind = DocKindDetector.Detect(doc.Language);
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
                    config.Indexing.NoSemantic, guard, warnings.Add);
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
                    DeleteVectorsForFiles(vectorStore, store, reaped, warnings, guard);
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
                    IgnoredBySource = priorState?.Stats.IgnoredBySource,
                    GuardWithheld = priorState?.Stats.GuardWithheld ?? 0,
                    GuardWithheldBuiltIns = priorState?.Stats.GuardWithheldBuiltIns ?? 0,
                    GuardDropped = priorState?.Stats.GuardDropped ?? 0,
                    TrackedButExcluded = priorState?.Stats.TrackedButExcluded ?? 0
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

    // The ledger's row counters are recomputed from the store, the guard counters are not: this route decides
    // one path, so whatever earlier passes refused has to survive the write. Carrying the tally forward is what
    // the last full pass left in place of IgnoredTotal, and a single-file refusal must not erase it.
    private static void SaveWithheldState(
        MetaStore store,
        string repoKey,
        RepoDocument repoDoc,
        DateTime now,
        SecretGuardIngestion guard,
        bool rowDropped)
    {
        var prior = store.GetIndexState(repoKey);
        store.SaveIndexState(new IndexStateDocument
        {
            RepoKey = repoKey,
            LastFullIndexAt = now,
            Stats = new IndexStateStats
            {
                Files = store.CountFiles(repoDoc.Id),
                Symbols = store.CountSymbols(repoDoc.Id),
                Chunks = store.CountChunks(repoDoc.Id),
                IgnoredTotal = prior?.Stats.IgnoredTotal ?? 0,
                IgnoredBySource = prior?.Stats.IgnoredBySource,
                GuardWithheld = (prior?.Stats.GuardWithheld ?? 0) + guard.Withheld,
                GuardWithheldBuiltIns = (prior?.Stats.GuardWithheldBuiltIns ?? 0) + guard.WithheldByBuiltIns,
                GuardDropped = (prior?.Stats.GuardDropped ?? 0) + (rowDropped ? 1 : 0),
                TrackedButExcluded = prior?.Stats.TrackedButExcluded ?? 0
            }
        });
        repoDoc.LastIndexedAt = now;
        store.UpsertRepo(repoDoc);
    }

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
        int? embedBudget,
        PacedPass? pacing,
        string? embeddingsUnavailableReason,
        CancellationToken cancellationToken)
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
        var guard = SecretGuardIngestion.Load(workspaceRoot);
        var walk = RepoFileWalker.Walk(
            repoRoot, rules, guard, config.Indexing.MaxFileSizeKb, warnings.Add,
            cancellationToken: cancellationToken);
        if (guard.WarningLine() is { } withheld)
        {
            warnings.Add(withheld);
        }

        progress?.PhaseStarted(repo.Name, IndexPhase.Files, walk.Files.Count);
        var skipped = walk.Skipped;
        var dbFiles = store.ListFiles(repoDoc.Id);
        var dbByPath = dbFiles.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var walkedPaths = walk.Files.Select(f => f.RelativePath).ToHashSet(StringComparer.Ordinal);
        // unreadable is not missing: the walk ledgers every path it could not read and the reconciliation
        // must not reap those rows — they survive until the file reads again or is really gone
        var deleted = dbFiles
            .Where(f => !walkedPaths.Contains(f.Path) && !walk.Keeps(f.Path))
            .ToList();
        // a protected row can only reach here through the guard's own refusal — nothing walked it, so nothing
        // in this store may still hold it. The name stays out of the count; Deleted keeps it in the ledger.
        var deletedProtected = deleted.Count(f => guard.IsProtected(f.Path));
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
            cancellationToken.ThrowIfCancellationRequested();
            progress?.ItemProcessed(repo.Name, file.RelativePath);

            var isNew = !dbByPath.TryGetValue(file.RelativePath, out var existing);
            if (!full && !isNew && existing!.Sha256 == file.Sha256)
            {
                continue;
            }

            string text;
            try
            {
                text = SecretSnippetRedactor.Redact(
                    file.RelativePath, File.ReadAllText(file.FullPath));
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
            doc.Kind = DocKindDetector.Detect(doc.Language);
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
                repoRoot, repoDoc, store, csFiles, changedByPath, dbFilesByPath, config, noSemantic,
                guard, warnings.Add, progress)
            : SemanticIndexPhase.RunPooled(
                pacing.Sessions, repoRoot, repoDoc, store, csFiles, changedByPath, dbFilesByPath, config,
                noSemantic, guard, warnings.Add, progress, syncDocuments: touched is not null);
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
        var embedded = 0;
        var embeddingsCompleted = true;
        if (embeddings is not null && vectorStore is not null)
        {
            var reaped = RequeueReapedFiles(store, FilesWithStalePoints(store, priorChunkHashes));
            deferredReaps = DeleteVectorsForFiles(vectorStore, store, reaped, warnings, guard);
            if (embedBudget is null or > 0)
            {
                var phase = EmbeddingIndexPhase.Run(
                    repoDoc, store, modelKey!, embeddings, vectorStore, warnings.Add, progress, embedBudget);
                embedded = phase.Embedded;
                embeddingsCompleted = phase.Completed;
            }
        }
        else if (embeddingsUnavailableReason is not null && !noSemantic)
        {
            embeddingsCompleted = false;
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
                IgnoredBySource = ignoredSnapshot,
                GuardWithheld = guard.Withheld,
                GuardWithheldBuiltIns = guard.WithheldByBuiltIns,
                GuardDropped = deletedProtected,
                TrackedButExcluded = walk.TrackedButExcluded
            }
        });
        repoDoc.LastIndexedAt = now;
        store.UpsertRepo(repoDoc);

        return new RepoWork(
            new RepoIndexResult(
                repo.Name, added, modified, reaps.Count, skipped, totalFiles, walk.Ignored, ignoredSnapshot,
                guard.Withheld, guard.WithheldByBuiltIns, deletedProtected, embedded, embeddingsCompleted,
                semantic.Throttled, embeddingsCompleted ? null : embeddingsUnavailableReason),
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

    // Every row that hangs off a file row leaves with it, and the count comes back: a purge that says how many
    // rows it removed is only honest if the same number is what retired them, and a dangling symbol or ref
    // would be a leak with no file row left to blame.
    internal static int CascadeSemanticRecords(uint fileId, MetaStore store)
    {
        var oldIds = store.ListSymbolsByFile(fileId).Select(s => s.Id).ToList();
        var refsRemoved = store.DeleteRefsByFromSymbolIds(oldIds) + store.DeleteRefsByFile(fileId);
        var symbolsRemoved = store.DeleteSymbolsByFile(fileId);
        var chunksRemoved = store.DeleteChunksByFile(fileId);
        return refsRemoved + symbolsRemoved + chunksRemoved;
    }

    // A drop warning is the one line where a secret name could still reach a machine-readable stream, so a
    // path the guard refuses trades its name for a count and keeps the reason.
    private static void NoteStoreDrop(
        SecretGuardIngestion guard,
        Dictionary<StoreDrop, int> protectedDrops,
        List<string> warnings,
        string relativePath,
        StoreDrop reason,
        string detail = "")
    {
        if (guard.IsProtected(relativePath))
        {
            protectedDrops[reason] = protectedDrops.GetValueOrDefault(reason) + 1;
            return;
        }

        warnings.Add($"dropping '{relativePath}' from index store: {StoreDropReason(reason)}{detail}");
    }

    private static void FlushStoreDrops(Dictionary<StoreDrop, int> protectedDrops, List<string> warnings)
    {
        foreach (var (reason, count) in protectedDrops)
        {
            warnings.Add(
                $"dropping {count} protected path(s) from index store: {StoreDropReason(reason)} " +
                "(paths withheld by the secret guard)");
        }
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
        List<string> warnings,
        SecretGuardIngestion guard)
    {
        if (vectorStore is not null)
        {
            return DeleteVectorsForFiles(vectorStore, store, deleted.Select(r => r.Target).ToList(), warnings, guard);
        }

        var owed = deleted.Where(r => r.HadVectors).Select(r => r.Target).ToList();
        return owed.Count > 0
            ? DeferReap(store, owed, warnings, "this pass carries no vector store", guard)
            : 0;
    }

    private static DropOutcome DropFile(
        FileDocument file, MetaStore store, IVectorStore? vectorStore, List<string> warnings, SecretGuardIngestion guard)
    {
        var reap = CaptureReap(store, file);
        var rowsRemoved = CascadeSemanticRecords(reap.Id, store);
        store.DeleteFile(reap.Id);
        return new DropOutcome(
            rowsRemoved + 1,
            RemoveVectorsOfDeletedFiles(vectorStore, store, [reap], warnings, guard));
    }

    // What one file's retirement took out of the store: its own row plus everything that hung off it, and how
    // many point cleanups the drop could not finish and had to queue.
    private readonly record struct DropOutcome(int RowsRemoved, int ReapsQueued);

    // The store-side half of the guard: rows that entered the store before it existed, and the names a queued
    // point cleanup kept for them, leave through the same primitives an ignored file uses — no second delete
    // path, and nothing here reaches into the vector store. Owed point cleanup goes to the reap journal,
    // which 059 replay and 'squeezr vectors reconcile --apply' already consume.
    internal static StorePurgeResult PurgeProtectedPaths(
        string workspaceRoot,
        MetaStore store,
        SecretGuardIngestion guard,
        List<string> warnings)
    {
        var doomed = store.ListAllFiles().Where(f => guard.IsProtected(f.Path)).ToList();
        var tombstones = new List<(uint RepoId, uint Id)>(doomed.Count);
        var reaps = new List<FileReap>(doomed.Count);
        var rowsRemoved = 0;

        foreach (var file in doomed)
        {
            var reap = CaptureReap(store, file);
            rowsRemoved += CascadeSemanticRecords(reap.Id, store) + 1;
            store.DeleteFile(reap.Id);
            reaps.Add(reap);
            tombstones.Add((file.RepoId, file.Id));
        }

        // one batched half for the whole purge: the journal takes every owed cleanup at once and the pass says
        // it once, instead of one identical warning per retired file
        var reapsQueued = RemoveVectorsOfDeletedFiles(vectorStore: null, store, reaps, warnings, guard);

        var namedEntries = store.ListPendingReaps()
            .Where(entry => entry.Path is not null && guard.IsProtected(entry.Path))
            .Select(entry => entry.FileId)
            .ToList();
        var journalRedacted = store.RedactPendingReapPaths(namedEntries);

        if (tombstones.Count > 0)
        {
            TombstonePurgedDocs(SqueezrPaths.TrigramIndexPath(workspaceRoot), tombstones);
            warnings.Add(
                $"secret guard: {doomed.Count} stored path(s) purged from the index store, {rowsRemoved} row(s) " +
                "removed (paths withheld by the secret guard)");
        }

        if (journalRedacted > 0)
        {
            warnings.Add(
                $"secret guard: {journalRedacted} queued point cleanup(s) redacted in the reap journal " +
                "(paths withheld by the secret guard)");
        }

        return new StorePurgeResult(doomed.Count, rowsRemoved, journalRedacted, reapsQueued);
    }

    // The postings for a purged row are retired by tombstoning them, which is how every other deletion leaves
    // the trigram index — the rewrite that follows in the same command is what takes the bytes back. A legacy
    // v1 index needs no tombstones: it is rebuilt from the store, which no longer holds these rows.
    private static void TombstonePurgedDocs(string trigramPath, List<(uint RepoId, uint Id)> tombstones)
    {
        uint? version;
        try
        {
            version = TrigramSegment.TryReadVersion(trigramPath);
        }
        catch (InvalidDataException)
        {
            return;
        }

        if (version != TrigramIndexFormat.CurrentVersion)
        {
            return;
        }

        TrigramIndexWriter.AppendBatch(trigramPath, [], tombstones);
    }

    private static bool IsTreeSitterLanguage(string language) =>
        language is "javascript" or "typescript" or "tsx" or "rust";

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
        IVectorStore vectorStore,
        MetaStore store,
        IReadOnlyList<PendingReapTarget> files,
        List<string> warnings,
        SecretGuardIngestion guard)
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
                return DeferReap(store, files, warnings, "vector store unavailable", guard);
            }

            vectorStore.DeleteByFileIdsAsync(fileIds, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not OperationCanceledException)
        {
            return DeferReap(
                store, files, warnings, $"failed to remove vectors for {fileIds.Count} file(s): {ex.Message}", guard);
        }

        return 0;
    }

    private static int DeferReap(
        MetaStore store,
        IReadOnlyList<PendingReapTarget> files,
        List<string> warnings,
        string reason,
        SecretGuardIngestion guard)
    {
        try
        {
            store.EnqueuePendingReaps(WithoutProtectedPaths(files, guard));
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

    // The journal is meta.db content like any other row, so a queued cleanup for a protected path would keep
    // that name in the store bytes after the file row itself is gone. The id is what replay deletes by; the
    // name only proves the id still belongs to the file it was queued for, and "no live row behind the id" is
    // the honest answer for a purged file — replay reaps the point, and a recycled id goes to reconcile.
    private static IReadOnlyList<PendingReapTarget> WithoutProtectedPaths(
        IReadOnlyList<PendingReapTarget> files,
        SecretGuardIngestion guard)
    {
        if (!files.Any(f => f.Path is not null && guard.IsProtected(f.Path)))
        {
            return files;
        }

        return [.. files.Select(f => f.Path is not null && guard.IsProtected(f.Path) ? f with { Path = null } : f)];
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
        var guard = SecretGuardIngestion.Load(workspaceRoot);
        var protectedDrops = new Dictionary<StoreDrop, int>();
        TrigramIndexReader? previousIndex = null;
        Dictionary<uint, uint>? previousDocIds = null;

        // unreadable is not missing: the row survives and the previous index donates its postings, so
        // search keeps answering with the old content until the path reads again. A protected path is the
        // exception — its content may not stay in the store at all, so it goes the way an ignored path does.
        void KeepUnreadableRow(FileDocument row, string reason)
        {
            if (guard.IsProtected(row.Path))
            {
                NoteStoreDrop(guard, protectedDrops, warnings, row.Path, StoreDrop.Unreadable, $" ({reason})");
                deferredReaps += DropFile(row, store, vectorStore, warnings, guard).ReapsQueued;
                return;
            }

            warnings.Add($"keeping '{row.Path}' in the index store: unreadable ({reason}); rows kept");
            if (previousIndex is null)
            {
                previousIndex = TryOpenTrigramIndex(targetPath);
                previousDocIds = MapDocIds(previousIndex);
            }

            if (!CarryTrigramPostings(previousIndex, previousDocIds!, writer, row))
            {
                warnings.Add(
                    $"keeping '{row.Path}' in the index store: its postings are not in the previous " +
                    "trigram index — search misses it until the file reads again");
            }
        }

        foreach (var file in allFiles)
        {
            if (!reposById.TryGetValue(file.RepoId, out var repoDoc)
                || !entriesByName.TryGetValue(repoDoc.Name, out var entry))
            {
                NoteStoreDrop(guard, protectedDrops, warnings, file.Path, StoreDrop.RepositoryGone);
                deferredReaps += DropFile(file, store, vectorStore, warnings, guard).ReapsQueued;
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
                        // File.Exists answers the same false for a deletion and for a path a non-executable
                        // parent hides from stat; the exception shape is what tells them apart, and a denied
                        // stat is the walk's unreadable keep, not a reap
                        var denial = ProbeMissingStat(fullPath, out var present);
                        if (denial is not null)
                        {
                            KeepUnreadableRow(file, denial.Message);
                            continue;
                        }

                        if (!present)
                        {
                            NoteStoreDrop(guard, protectedDrops, warnings, file.Path, StoreDrop.MissingOnDisk);
                            deferredReaps += DropFile(file, store, vectorStore, warnings, guard).ReapsQueued;
                            continue;
                        }
                    }

                    // what reaches this keep: shapes stat resolves but no read can serve — channels,
                    // sockets, devices, and directories that took a stored path (the missing probe above
                    // answers present for them). A dangling symlink reads as ENOENT instead, so it keeps
                    // through the read-failure catch below, not here.
                    if (!UnixFileType.IsRegularFile(fullPath))
                    {
                        KeepUnreadableRow(file, "not a regular file (directory, fifo, socket or device)");
                        continue;
                    }

                    try
                    {
                        text = SecretSnippetRedactor.Redact(file.Path, File.ReadAllText(fullPath));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        KeepUnreadableRow(file, ex.Message);
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

        FlushStoreDrops(protectedDrops, warnings);
        writer.Write(targetPath);
        return deferredReaps;
    }

    private static string ResolveRepoRoot(string workspaceRoot, RepoEntry repo) =>
        Path.GetFullPath(Path.IsPathRooted(repo.Path) ? repo.Path : Path.Combine(workspaceRoot, repo.Path));

    // the exists-check has no shape for a parent directory that denies the stat, while GetAttributes keeps
    // the exception: UnauthorizedAccessException is the unreadable subtree, a plain IOException is the
    // deletion, and an ArgumentException is a path stat can never resolve — the same reap
    private static Exception? ProbeMissingStat(string fullPath, out bool present)
    {
        try
        {
            _ = File.GetAttributes(fullPath);
            present = true;
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            present = false;
            return ex;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            present = false;
            return null;
        }
    }

    private static TrigramIndexReader? TryOpenTrigramIndex(string path)
    {
        try
        {
            return new TrigramIndexReader(path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return null;
        }
    }

    private static Dictionary<uint, uint> MapDocIds(TrigramIndexReader? index)
    {
        if (index is null)
        {
            return [];
        }

        var docIdsByFile = new Dictionary<uint, uint>();
        for (uint docId = 0; docId < index.DocCount; docId++)
        {
            var (_, fileId) = index.GetDoc(docId);
            docIdsByFile[fileId] = docId;
        }

        return docIdsByFile;
    }

    private static bool CarryTrigramPostings(
        TrigramIndexReader? previousIndex,
        Dictionary<uint, uint> docIdsByFile,
        TrigramIndexWriter writer,
        FileDocument file)
    {
        if (previousIndex is null || !docIdsByFile.TryGetValue(file.Id, out var docId))
        {
            return false;
        }

        writer.AddDocument(new TrigramDocument(
            file.RepoId, file.Id, previousIndex.GetDocumentPostings(docId)));
        return true;
    }

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
