using Squeezr.Core.Storage;

namespace Squeezr.Core.Workspace;

// The verb-level decision about moving a store's file state into a worktree store. The file rules live in
// WorkspaceStoreCopy; what is settled here is WHICH store, WHEN again, and what gets said when no byte
// moves.
//
// One tree one store: the destination is gathered from every row that resolves to this physical root, so a
// checkout a linked ancestor also spells cannot be filled into one key while another row claims the tree
// (review-116-04 #1). The three ways that question fails answer are three facts and carry three codes: a
// root that was never named, a root no registry row resolves to, and a tree two keys both claim.
//
// This verb runs at every session start, so the fill is decided by the state of the destination, never by
// which pass created it: a store holding no rows is unfinished work and is filled again, while a store that
// already holds content keeps it — re-copying over a lane's own index would bury work the lane did. A pass
// that died before landing rows is therefore completed by a later session instead of waiting on a manual
// drop, which is the only shape a hook that reruns forever can live with. The durable 'has this store ever
// been filled' marker is 116/T7's; until it exists, content is the honest predicate.
//
// A skip is reported, not thrown. The model question this pass asks costs a provider round trip, and a
// provider that cannot answer is a reason to copy nothing this time — not a reason to fail a session start
// over a store this pass did not damage.
public static class WorktreeStoreFill
{
    public static WorktreeFillOutcome Fill(
        WorktreeRegistrationOutcome registered,
        bool copy,
        Func<string> targetModelKeyResolver,
        Action<string>? onStaleTakeover = null,
        Func<WorktreeVectorCopyRequest, WorktreeVectorCopyStats>? copyVectors = null)
    {
        if (!copy)
        {
            return WorktreeFillOutcome.Skipped(
                "--reindex: this worktree fills by indexing its own tree, so nothing was copied. No pass " +
                "runs here yet — this worktree stays empty until the reconcile pass lands. To take the main " +
                "worktree's index instead, re-run this verb without '--reindex'.",
                WorktreeFillSkip.ReindexRequested);
        }

        if (registered.State
            is not (WorktreeRegistrationState.Registered or WorktreeRegistrationState.AlreadyRegistered))
        {
            return WorktreeFillOutcome.Skipped(NothingMinted(registered), WorktreeFillSkip.NothingMinted);
        }

        var childRow = RequireSingleRow(registered.RootPath, "this worktree", registered.StoreKey);
        var parentRow = RequireSingleRow(registered.ParentRootPath!, "the main worktree it copies from", null);
        var childStoreDir = WorkspaceStorePaths.RequireStoreDir(childRow.StoreKey);
        var parentStoreDir = WorkspaceStorePaths.RequireStoreDir(parentRow.StoreKey);

        if (registered.State == WorktreeRegistrationState.AlreadyRegistered && !IsUnfilled(childStoreDir))
        {
            return WorktreeFillOutcome.Skipped(KeepsItsIndex(registered), WorktreeFillSkip.LaneKeepsItsIndex);
        }

        var source = PeekSourceState(parentStoreDir);
        if (!source.Exists)
        {
            return WorktreeFillOutcome.Skipped(
                $"The main worktree store '{parentStoreDir}' holds no readable meta.db, so this worktree has " +
                $"nothing to inherit. Index it first: {RemedyText.AnchorIndex(parentRow.RootPath)}",
                WorktreeFillSkip.SourceStoreMissing);
        }

        if (source.Files == 0 && source.Repos == 0)
        {
            return WorktreeFillOutcome.Skipped(
                $"The main worktree store '{parentStoreDir}' holds no indexed rows yet, so there is nothing " +
                $"here to inherit. Index it first: {RemedyText.AnchorIndex(parentRow.RootPath)}",
                WorktreeFillSkip.SourceHasNoRows);
        }

        if (!WorkspaceManifestFile.Exists(parentRow.RootPath))
        {
            return WorktreeFillOutcome.Skipped(
                $"'{parentRow.RootPath}' has a registry row and a store but no workspace manifest, so this " +
                "verb cannot say which repos the copied rows belong to. Nothing was copied; run 'squeezr " +
                "workspace repair' to reconcile the row.",
                WorktreeFillSkip.SourceManifestMissing);
        }

        // The provider question is only worth its round trip when the source actually holds vectors: rows
        // that were never embedded cannot be inherited incompatibly, and asking a provider that is not in
        // the room would turn a fillable lane into a skip. It is settled here, outside every lock, because
        // the answer belongs to this lane's own config and a probe inside the source lock freezes the parent
        // for its budget.
        string? targetModelKey = null;
        if (source.EmbeddedChunks > 0)
        {
            try
            {
                targetModelKey = targetModelKeyResolver();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                return WorktreeFillOutcome.Skipped(
                    $"This worktree's embeddings model could not be named ({ex.Message}), so copying the main " +
                    "worktree's vectors would be a guess. Nothing was copied; this store still reads as " +
                    "unfilled and a later run of this verb completes the fill.",
                    WorktreeFillSkip.ModelUnresolved);
            }
        }

        var request = new WorkspaceStoreCopyRequest(
            parentStoreDir,
            childStoreDir,
            childRow.RootPath,
            SqueezrPaths.ManifestPath(childRow.RootPath),
            WorkspaceManifestFile.Load(parentRow.RootPath).Repos);

        var files = WorkspaceStoreCopy.Copy(request, targetModelKey, onStaleTakeover);

        // The vector half runs after both store locks are down and never inside them: a collection of this
        // size is pages of round trips, and holding the parent's lock across them would freeze a store
        // other agents are querying. It is asked only when the source's database claims vectors — pending
        // rows carry no points to inherit, and the model answer that makes a vector copy trustworthy is
        // the same fact that makes the question worth asking.
        var vectors = targetModelKey is null || copyVectors is null
            ? null
            : copyVectors(new WorktreeVectorCopyRequest(
                parentRow.RootPath, childRow.RootPath, childRow.StoreKey));

        return WorktreeFillOutcome.Filled(files, vectors);
    }

    // Every row that resolves to this tree is gathered, not the first hit: the fold lookup answers with
    // whichever row the registry lists first, so it cannot tell one store from a fork, and a fork is the
    // state where a filled store would be read by nobody.
    private static WorkspaceRegistryEntry RequireSingleRow(
        string rootPath,
        string subject,
        string? expectedKey)
    {
        if (string.IsNullOrEmpty(rootPath))
        {
            throw new WorkspaceException(
                $"'{subject}' names no root path, so there is no store to copy from or into and nothing was " +
                "copied. This is the verb's own bookkeeping failing, not the tree's: re-run the command.");
        }

        var rows = WorkspaceRegistry.Load().Where(row =>
            WorkspaceCanonicalPath.SamePhysicalPath(row.RootPath, rootPath)).ToList();

        if (rows.Count > 1)
        {
            throw new WorkspaceException(
                $"'{rootPath}' is {subject} under {rows.Count} store keys " +
                $"({string.Join(", ", rows.Select(row => $"'{row.StoreKey}'"))}) — one tree cannot be filled " +
                "into one store while another row claims it, so nothing was copied. Run 'squeezr workspace " +
                "repair' to reconcile them.",
                WorkspaceException.WorktreeStoreForkCode);
        }

        if (rows.Count == 0)
        {
            throw new WorkspaceException(
                $"'{rootPath}' resolves to no registry row under any spelling, so {subject} has no store here " +
                "and nothing was copied. Register this tree with 'squeezr worktree init', or run 'squeezr " +
                "workspace repair' if its row went missing.",
                WorkspaceException.WorktreeStoreUnresolvedCode);
        }

        var row = rows[0];
        if (expectedKey is not null && !string.Equals(row.StoreKey, expectedKey, StringComparison.Ordinal))
        {
            throw new WorkspaceException(
                $"The store this pass registered ('{expectedKey}') is not the store the tree at '{rootPath}' " +
                $"resolves to ('{row.StoreKey}'), so the copy would fill a store no discovery path reaches. " +
                "Nothing was copied; run 'squeezr workspace repair'.",
                WorkspaceException.WorktreeStoreForkCode);
        }

        return row;
    }

    // The database decides whether a store reads as filled: a mint that never got its copy holds an empty
    // database, and that is the state a later pass owes a fill to. A database that will not open is not an
    // empty one — it is content this verb has no business overwriting blind.
    private static bool IsUnfilled(string childStoreDir)
    {
        var store = MetaStore.TryOpen(Path.Combine(childStoreDir, SqueezrPaths.MetaDbFileName));
        if (store is null)
        {
            return !File.Exists(Path.Combine(childStoreDir, SqueezrPaths.MetaDbFileName));
        }

        using (store)
        {
            return store.CountAllFiles() == 0 && store.ListRepos().Count == 0;
        }
    }

    // Read outside the source lock on purpose: this decides whether there is anything to inherit and
    // whether the model question is worth a round trip, not whether these bytes are safe to move. The
    // authoritative counts are taken again under the lock by StoreCopyModelGuard, and a parent that changes
    // between the two is answered by the next session start rather than by a scan the lock was there to
    // make honest.
    private static SourcePeek PeekSourceState(string storeDir)
    {
        var metaDb = Path.Combine(storeDir, SqueezrPaths.MetaDbFileName);
        if (!File.Exists(metaDb))
        {
            return new SourcePeek(false, 0, 0, 0);
        }

        var store = MetaStore.TryOpen(metaDb);
        if (store is null)
        {
            return new SourcePeek(false, 0, 0, 0);
        }

        using (store)
        {
            var coverage = store.GetSemanticCoverage();
            return new SourcePeek(
                true,
                store.CountAllFiles(),
                store.ListRepos().Count,
                coverage.TotalChunks - coverage.PendingChunks);
        }
    }

    private sealed record SourcePeek(bool Exists, int Files, int Repos, int EmbeddedChunks);

    private static string KeepsItsIndex(WorktreeRegistrationOutcome registered) =>
        $"'{registered.StoreKey}' was already registered before this pass and its store already holds " +
        "content, so nothing was re-copied — this verb runs at every session start, and a re-copy would bury " +
        "the index work the lane has done since. To refill it from the main worktree: " +
        $"{RemedyText.DropStore(registered.StoreKey!)}, then re-run.";

    private static string NothingMinted(WorktreeRegistrationOutcome registered) =>
        $"This pass created no store for '{registered.RootPath}' ({registered.State}), so there is nothing to " +
        "fill and no byte moved.";
}
