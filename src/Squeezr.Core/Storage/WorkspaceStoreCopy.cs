using LiteDB;
using Squeezr.Core.Indexing.Trigram;
using Squeezr.Core.Workspace;

namespace Squeezr.Core.Storage;

// The store half of filling one workspace store from another. meta.db and trigram.idx move as bytes; the
// rows they describe keep their identity; the child manifest ends up naming the repo rows that landed,
// because a database whose rows no manifest entry answers for is unreadable bytes, not an index.
//
// Every file is copied into a temp name inside the destination store and swapped in only after it has
// answered for itself, and the numbers the report carries are measured on the installed file: source,
// staged, installed. A failure costs a temp file inside a store dir the removal path deletes whole, never
// a half-store that reads as filled. The trigram index installs first and the database last, because the
// database is the file that makes a store read as filled — a pass that cannot clear the database's final
// check removes what it installed, which puts the lane back on the retry path instead of stranding it.
//
// Locks: the source store's index.lock is held for the whole pass, and the source is read under it —
// readers never take that lock while every writer does, so an unlocked pre-read would admit a parent pass
// that re-embeds between the check and the copy and ship foreign vectors under a clean check. The
// destination lock is taken second. The order is worktree ancestry and ancestry is acyclic: a fill's
// source is always the store of the tree above this one, so a lane never reaches downward for a lock it
// already holds upward, and nested lanes chain the same way. A single-store holder takes one lock and
// cannot meet either end of that chain. Stale-takeover rules are the product's own — neither bypassed nor
// widened here. What stays OUTSIDE both locks is the destination's model key: it comes from the lane's own
// config.json, which nothing else writes, and asking it means a provider round trip that must not freeze
// the parent for its budget.
public static class WorkspaceStoreCopy
{
    internal const string TempSuffix = ".copy-tmp";

    // The write-ahead log a shared LiteDB connection keeps next to the file it opened. The name follows
    // the path it was opened under, so after the swap it is a leftover of the temp, not of the store.
    private const string LogSuffix = "-log";

    public static WorkspaceStoreCopyResult Copy(
        WorkspaceStoreCopyRequest request,
        string? targetModelKey,
        Action<string>? onStaleTakeover = null)
    {
        var sourceMetaDb = Path.Combine(request.SourceStoreDir, SqueezrPaths.MetaDbFileName);
        if (!File.Exists(sourceMetaDb))
        {
            throw new WorkspaceException(
                $"The store at '{request.SourceStoreDir}' holds no '{SqueezrPaths.MetaDbFileName}', so there is " +
                $"nothing to copy from it. Index the main worktree first: '{RemedyText.IndexCommand}'.",
                WorkspaceException.IndexNotBuiltCode);
        }

        if (WorkspaceCanonicalPath.SamePhysicalPath(request.SourceStoreDir, request.TargetStoreDir))
        {
            throw new WorkspaceException(
                $"Source and destination store are the same directory ('{request.SourceStoreDir}'): a worktree " +
                "copies from its main worktree's store, never from itself. This comparison resolves every " +
                "component of both paths, so a link spelling of the same store is the same store here too.",
                WorkspaceException.WorktreeSameStoreCode);
        }

        // The source lock first, and the model check inside it: a store that is busy refuses before a
        // destination directory is even created, and the rows the guard counts are the rows the copy moves.
        using (IndexLock.Acquire(request.SourceStoreDir, onStaleTakeover))
        {
            SourceStoreAudit audit;
            using (var source = new MetaStore(sourceMetaDb))
            {
                source.Checkpoint();
                audit = StoreCopyModelGuard.Audit(source, targetModelKey);
            }

            EnsureTargetDir(request.TargetStoreDir);
            using (IndexLock.Acquire(request.TargetStoreDir, onStaleTakeover))
            {
                var trigram = CopyTrigram(request);
                var database = CopyMetaDb(request, sourceMetaDb, audit);
                var seeded = SeedManifest(request, database.RepoNames);

                return new WorkspaceStoreCopyResult(database.Stats, trigram, seeded.Owned, seeded.Unowned);
            }
        }
    }

    // The manifest write is the last step, because a store whose rows no manifest entry answers for is the
    // inert state this pass exists to prevent: when the write itself fails, the install goes back out.
    private static WorktreeManifestSeed.Result SeedManifest(WorkspaceStoreCopyRequest request,
        IReadOnlyList<string> installedRepoNames)
    {
        try
        {
            return WorktreeManifestSeed.OwnCopiedRows(
                request.TargetManifestPath,
                request.TargetRoot,
                request.SourceRepos,
                installedRepoNames);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            UndoInstall(request.TargetStoreDir);
            throw new WorkspaceException(
                $"The copied store at '{request.TargetStoreDir}' holds rows this worktree's manifest at " +
                $"'{request.TargetManifestPath}' could not be made to answer for ({ex.Message}): the " +
                "installed files were removed, so the store reads as unfilled and a later run of this " +
                "verb fills it again.",
                WorkspaceException.StorageUnavailableCode,
                ex);
        }
    }

    private static void EnsureTargetDir(string targetStoreDir)
    {
        try
        {
            Directory.CreateDirectory(targetStoreDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new WorkspaceException(
                $"Could not prepare the destination store dir '{targetStoreDir}': {ex.Message}",
                WorkspaceException.StorageUnavailableCode,
                ex);
        }
    }

    private static TrigramCopyStats CopyTrigram(WorkspaceStoreCopyRequest request)
    {
        var source = Path.Combine(request.SourceStoreDir, SqueezrPaths.TrigramIndexFileName);
        if (!File.Exists(source))
        {
            return TrigramCopyStats.NotCopied;
        }

        var target = Path.Combine(request.TargetStoreDir, SqueezrPaths.TrigramIndexFileName);
        var temp = target + TempSuffix;
        DeleteQuietly(temp);

        try
        {
            var sourceBytes = new FileInfo(source).Length;
            var sourceDocs = CountTrigramDocs(source);
            File.Copy(source, temp, overwrite: true);
            var copiedBytes = new FileInfo(temp).Length;
            RequireStagedBytes(copiedBytes, sourceBytes, temp, source);
            var copiedDocs = CountTrigramDocs(temp);
            if (copiedDocs != sourceDocs)
            {
                throw new WorkspaceException(
                    $"The staged trigram index at '{temp}' parses {copiedDocs} doc(s) where the source holds " +
                    $"{sourceDocs}: refusing to install an index that lost documents. Nothing was swapped in.",
                    WorkspaceException.StorageUnavailableCode);
            }

            SwapIntoPlace(temp, target);
            var targetDocs = CountInstalledTrigramDocs(target, sourceDocs, request.TargetStoreDir);
            return new TrigramCopyStats(
                Copied: true,
                sourceBytes,
                copiedBytes,
                new FileInfo(target).Length,
                sourceDocs,
                copiedDocs,
                targetDocs);
        }
        catch (Exception ex) when (ex is not WorkspaceException)
        {
            throw Wrap(ex, source, temp);
        }
        finally
        {
            DeleteQuietly(temp);
        }
    }

    private static int CountInstalledTrigramDocs(string target, int sourceDocs, string targetStoreDir)
    {
        try
        {
            return CountTrigramDocs(target);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            UndoInstall(targetStoreDir);
            throw new WorkspaceException(
                $"The installed trigram index at '{target}' does not parse the way the verified copy did " +
                $"({sourceDocs} doc(s)): {ex.Message}. The installed files were removed, so this store reads " +
                "as unfilled and a later run of this verb fills it again.",
                WorkspaceException.StorageUnavailableCode,
                ex);
        }
    }

    private static MetaDbStep CopyMetaDb(WorkspaceStoreCopyRequest request, string sourceMetaDb, SourceStoreAudit audit)
    {
        var target = Path.Combine(request.TargetStoreDir, SqueezrPaths.MetaDbFileName);
        var temp = target + TempSuffix;
        DeleteQuietly(temp);
        DeleteQuietly(temp + LogSuffix);

        try
        {
            var sourceBytes = new FileInfo(sourceMetaDb).Length;
            File.Copy(sourceMetaDb, temp, overwrite: true);
            var copiedBytes = new FileInfo(temp).Length;
            RequireStagedBytes(copiedBytes, sourceBytes, temp, sourceMetaDb);

            var staged = OpenAndCount(temp, "staged", sourceMetaDb);
            if (staged.Files != audit.Files || staged.Chunks != audit.TotalChunks)
            {
                throw new WorkspaceException(
                    $"The staged copy at '{temp}' answers with {staged.Files} file row(s) and " +
                    $"{staged.Chunks} chunk row(s) where the source holds {audit.Files} and {audit.TotalChunks}: " +
                    "refusing to install a store that does not match the one it came from. Nothing was swapped in.",
                    WorkspaceException.StorageUnavailableCode);
            }

            SwapIntoPlace(temp, target);
            var installed = OpenAndCount(target, "installed", sourceMetaDb);
            if (installed.Files != audit.Files || installed.Chunks != audit.TotalChunks)
            {
                UndoInstall(request.TargetStoreDir);
                throw new WorkspaceException(
                    $"The installed meta.db at '{target}' answers with {installed.Files} file row(s) and " +
                    $"{installed.Chunks} chunk row(s) where the source holds {audit.Files} and {audit.TotalChunks}: " +
                    "the verified copy did not survive being put in place. The installed files were removed, so " +
                    "this store reads as unfilled and a later run of this verb fills it again.",
                    WorkspaceException.StorageUnavailableCode);
            }

            return new MetaDbStep(
                new DatabaseCopyStats(
                    sourceBytes,
                    copiedBytes,
                    new FileInfo(target).Length,
                    audit.Files,
                    staged.Files,
                    installed.Files,
                    audit.TotalChunks,
                    staged.Chunks,
                    installed.Chunks),
                installed.RepoNames);
        }
        catch (Exception ex) when (ex is not WorkspaceException)
        {
            throw Wrap(ex, sourceMetaDb, temp);
        }
        finally
        {
            DeleteQuietly(temp);
            DeleteQuietly(temp + LogSuffix);
        }
    }

    private static void RequireStagedBytes(long copiedBytes, long sourceBytes, string temp, string source)
    {
        if (copiedBytes == sourceBytes)
        {
            return;
        }

        throw new WorkspaceException(
            $"The staged copy at '{temp}' holds {copiedBytes} byte(s) where the source '{source}' holds " +
            $"{sourceBytes}: refusing to install a file that is not the byte-for-byte copy it claims to be. " +
            "Nothing was swapped in.",
            WorkspaceException.StorageUnavailableCode);
    }

    private static int CountTrigramDocs(string path) => (int)new TrigramIndexReader(path).DocCount;

    private static StoreRead OpenAndCount(string path, string role, string sourcePath)
    {
        try
        {
            using var store = new MetaStore(path);
            return new StoreRead(
                store.CountAllFiles(),
                store.GetSemanticCoverage().TotalChunks,
                [.. store.ListRepos().Select(repo => repo.Name)]);
        }
        catch (Exception ex) when (ex is LiteException or IOException or UnauthorizedAccessException
                                       or InvalidOperationException or WorkspaceException)
        {
            throw new WorkspaceException(
                $"The {role} meta.db at '{path}' does not open as a squeezr store (source '{sourcePath}'): " +
                $"{ex.Message}. Nothing was swapped in.",
                WorkspaceException.StorageUnavailableCode,
                ex);
        }
    }

    // File.Move with overwrite is the swap: within one filesystem it is a rename, so the destination either
    // holds the verified copy or still holds what it had.
    private static void SwapIntoPlace(string temp, string target)
    {
        try
        {
            File.Move(temp, target, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new WorkspaceException(
                $"'{temp}' was verified but could not be put in place at '{target}': {ex.Message}. " +
                $"The destination store still holds its previous '{Path.GetFileName(target)}'.",
                WorkspaceException.StorageUnavailableCode,
                ex);
        }
    }

    // A fill installs into a store this verb found empty, so the bytes it removes are its own. Deleting the
    // database is what matters: it is the file whose rows make a store read as filled, and with it gone the
    // next pass of the verb sees an unfilled store and finishes the job instead of skipping forever. The
    // empty database the mint left behind is re-created rather than mourned, and if that re-creation fails,
    // a store holding no database is the very same unfilled state — so a failed pass does not trade its own
    // failure for a second one.
    private static void UndoInstall(string targetStoreDir)
    {
        var metaDb = Path.Combine(targetStoreDir, SqueezrPaths.MetaDbFileName);
        DeleteQuietly(metaDb);
        DeleteQuietly(metaDb + LogSuffix);
        DeleteQuietly(Path.Combine(targetStoreDir, SqueezrPaths.TrigramIndexFileName));
        try
        {
            using var reminted = new MetaStore(metaDb, createIfMissing: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or LiteException)
        {
        }
    }

    private static WorkspaceException Wrap(Exception ex, string source, string temp) =>
        new($"Could not copy '{source}' (the temp copy at '{temp}' was removed): {ex.GetType().Name}: {ex.Message}",
            WorkspaceException.StorageUnavailableCode,
            ex);

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private readonly record struct StoreRead(int Files, int Chunks, IReadOnlyList<string> RepoNames);

    private readonly record struct MetaDbStep(DatabaseCopyStats Stats, IReadOnlyList<string> RepoNames);
}
