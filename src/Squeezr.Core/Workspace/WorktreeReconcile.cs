using Squeezr.Core.Embeddings;
using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;

namespace Squeezr.Core.Workspace;

// The pass that makes a copied store answer for its own tree. A fill lands the main worktree's rows, and
// every coordinate in them — the file list, the line numbers, the chunk hashes — was measured on another
// checkout. This is the one standard incremental pass over this worktree's root that puts those numbers back
// on this branch, plus the counts that say whether this worktree's vectors reach what its rows claim.
//
// The threshold is a comparison, never the copied database. A fill writes rows that already read 'ok', and
// 'ok' names the model that produced a vector, not a vector that exists in THIS collection; reading those
// rows as coverage is how a lane would print fresh over an empty collection. So the rows' claim is the
// obligation, and the collection is what answers it: the pass calls the lane reconciled only when the count
// the collection gives back reaches the rows that claim a vector.
//
// The verb runs at every session start, so owing a pass is decided by state rather than by memory of the pass
// that filled. A copy that landed this session always owes one. A lane that kept its index owes another only
// while its collection is MEASURABLY shorter than its claim: a count that did not answer is not a short
// count, and re-scanning the same tree at every session because a service is down is doubled work, not
// progress.
public static class WorktreeReconcile
{
    public static WorktreeReconcileOutcome Run(
        WorktreeRegistrationOutcome registered,
        WorktreeFillOutcome fill,
        bool noSemantic,
        Func<WorktreeReconcileRuntime> runtimeFactory,
        Action<string>? onStaleTakeover = null,
        CancellationToken ct = default)
    {
        if (fill.Skip == WorktreeFillSkip.ReindexRequested)
        {
            return WorktreeReconcileOutcome.Deferred(
                "--reindex: this worktree fills by indexing its own tree, so no reconcile pass ran here and "
                + "the store stays empty until that pass does.");
        }

        if (registered.StoreKey is null || registered.State
            is not (WorktreeRegistrationState.Registered or WorktreeRegistrationState.AlreadyRegistered))
        {
            return WorktreeReconcileOutcome.Deferred(
                $"this pass created no store for '{registered.RootPath}', so there is nothing to reconcile.");
        }

        var before = ReadRows(registered.RootPath);
        if (before is null)
        {
            return WorktreeReconcileOutcome.Deferred(NoRowsYet(fill));
        }

        if (before is { Readable: false })
        {
            return WorktreeReconcileOutcome.UnreadableStore(registered.RootPath);
        }

        // A store holding no rows is neither reconciled nor short of anything: it is the lane born before its
        // main worktree was indexed. Measuring a collection to prove that costs a round trip that answers no
        // question, and the deferred word keeps such a lane from reading as a failure at every session start.
        if (before.Files == 0 && before.TotalChunks == 0)
        {
            return WorktreeReconcileOutcome.Deferred(NoRowsYet(fill));
        }

        // The provider question is only asked when this pass has something to measure or to write: a lane that
        // kept its index and claims no vector owes neither, and a session start does not pay for a round trip
        // it has no use for.
        WorktreeReconcileRuntime? built = null;
        WorktreeReconcileRuntime Runtime() => built ??= runtimeFactory();

        var obligation = before.EmbeddedRows > 0;
        var pointsBefore = obligation ? CountPoints(Runtime().VectorStore, ct) : null;

        var owed = fill.State == WorktreeFillState.Filled
            || (obligation && pointsBefore?.Points is { } measured && measured < before.EmbeddedRows);

        if (!owed)
        {
            var settled = Judge(before, noSemantic, pointsBefore, passFailure: null);
            return Outcome(settled, passRan: false, before, obligation, pointsBefore,
                pass: null, runtime: Runtime());
        }

        var runtime = Runtime();

        IndexResult? pass = null;
        string? passFailure = null;
        try
        {
            pass = new WorkspaceIndexer().Index(
                registered.RootPath,
                repoName: null,
                full: false,
                noSemantic: noSemantic,
                progress: null,
                embeddings: runtime.Embeddings,
                vectorStore: runtime.VectorStore,
                reembed: false,
                onStaleTakeover: onStaleTakeover,
                embeddingProviderResolution: runtime.ProviderResolution,
                embeddingsUnavailableReason: runtime.EmbeddingsUnavailableReason,
                cancellationToken: ct);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException
                                   and not OperationCanceledException)
        {
            passFailure = ex.Message;
        }

        var after = ReadRows(registered.RootPath) ?? before;
        var stillObligated = after.EmbeddedRows > 0;
        var pointsAfter = stillObligated ? CountPoints(runtime.VectorStore, ct) : null;
        var verdict = Judge(after, noSemantic, pointsAfter, passFailure);
        return Outcome(verdict, passRan: true, after, stillObligated, pointsAfter, pass, runtime);
    }

    private sealed record StoreRows(int Files, int TotalChunks, int PendingChunks, bool Readable)
    {
        public int EmbeddedRows => TotalChunks - PendingChunks;
    }

    private sealed record PointCount(long? Points, string? Failure);

    // Null is 'this store holds no database at all', which is the lane born before its main worktree was
    // indexed. A database that exists and will not open is a different fact and is reported as such: the
    // lane is not empty, it is unreadable.
    private static StoreRows? ReadRows(string workspaceRoot)
    {
        var metaDb = SqueezrPaths.MetaDbPath(workspaceRoot);
        if (!File.Exists(metaDb))
        {
            return null;
        }

        var store = MetaStore.TryOpen(metaDb);
        if (store is null)
        {
            return new StoreRows(0, 0, 0, Readable: false);
        }

        using (store)
        {
            var coverage = store.GetSemanticCoverage();
            return new StoreRows(store.CountAllFiles(), coverage.TotalChunks, coverage.PendingChunks, true);
        }
    }

    // A missing handle is an unanswered count, never a count of zero: a store with no vector configuration
    // cannot prove the claim its rows make, and the pending shape that says so is the one named 'silent'.
    private static PointCount CountPoints(IVectorStore? store, CancellationToken ct)
    {
        if (store is null)
        {
            return new PointCount(null, "this worktree has no vector store to ask");
        }

        try
        {
            return new PointCount(store.CountAsync(ct).GetAwaiter().GetResult(), null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException
                                   and not OperationCanceledException)
        {
            return new PointCount(null, ex.Message);
        }
    }

    private static string NoRowsYet(WorktreeFillOutcome fill) =>
        fill.Skip is WorktreeFillSkip.SourceHasNoRows or WorktreeFillSkip.SourceStoreMissing
            ? "this worktree holds no rows yet, because the main worktree has no index to inherit: nothing is "
              + "reconciled, and the session start that finds the main worktree indexed fills and reconciles "
              + "this one."
            : "this worktree's store holds no rows yet, so there is nothing here to reconcile.";

    // The obligation is the rows' own claim. A store that claims no vector cannot be short of one, and a
    // workspace that asked for no semantic work is reported reconciled on coordinates — its embedding queue
    // is 'status' business, not a reconcile that failed here.
    private static (WorktreeReconcileState State, string? Reason, string? Code) Judge(
        StoreRows rows,
        bool noSemantic,
        PointCount? points,
        string? passFailure)
    {
        if (passFailure is not null)
        {
            return (
                WorktreeReconcileState.Pending,
                $"the reconcile pass did not finish ({passFailure}), so this worktree still answers with the "
                + $"coordinates it copied. Run '{RemedyText.IndexCommand}' here to finish it.",
                WorktreeReconcileOutcome.PassFailedCode);
        }

        var obligation = rows.EmbeddedRows > 0;
        if (!obligation)
        {
            // A store that claims no vector cannot be short of one. An embedding queue in a workspace that
            // asked for semantic work is still unfinished business, and 'pending' is the word for it.
            if (!noSemantic && rows.PendingChunks > 0)
            {
                return (
                    WorktreeReconcileState.Pending,
                    $"{rows.PendingChunks} chunk(s) here have never been embedded, so this worktree answers "
                    + $"exactly but semantically short. Run '{RemedyText.IndexCommand}' once the provider "
                    + "answers.",
                    WorktreeReconcileOutcome.RowsPendingCode);
            }

            return (WorktreeReconcileState.Reconciled, null, null);
        }

        if (points?.Points is not { } counted)
        {
            return (
                WorktreeReconcileState.Pending,
                $"this store calls {rows.EmbeddedRows} chunk(s) embedded and the collection did not answer "
                + $"({points?.Failure ?? "no count"}), so nothing here proves those vectors exist. Start the "
                + $"services ('{RemedyText.ServicesUp}') and re-run this verb, or close the gap with "
                + $"'{RemedyText.VectorsReconcileApply}' in this worktree.",
                WorktreeReconcileOutcome.StoreSilentCode);
        }

        if (counted < rows.EmbeddedRows)
        {
            return (
                WorktreeReconcileState.Pending,
                $"the collection counts {counted} point(s) under {rows.EmbeddedRows} row(s) this store calls "
                + "embedded, so semantic answers here are short of what the database claims. Re-run this verb "
                + $"once '{RemedyText.ServicesUp}' has brought the store up, or fill this worktree from its "
                + $"own tree with '{RemedyText.IndexCommand}'.",
                WorktreeReconcileOutcome.CollectionShortCode);
        }

        return (WorktreeReconcileState.Reconciled, null, null);
    }

    private static WorktreeReconcileOutcome Outcome(
        (WorktreeReconcileState State, string? Reason, string? Code) verdict,
        bool passRan,
        StoreRows rows,
        bool obligation,
        PointCount? points,
        IndexResult? pass,
        WorktreeReconcileRuntime runtime)
    {
        IReadOnlyList<string> passWarnings = pass?.Warnings ?? [];
        return new WorktreeReconcileOutcome(
            verdict.State,
            passRan,
            verdict.Reason,
            verdict.Code,
            rows.Files,
            rows.TotalChunks,
            rows.PendingChunks,
            obligation,
            points?.Points,
            pass is null ? null : pass.Repos.Sum(repo => repo.Files),
            pass is null ? null : Sum(pass, repo => repo.Added),
            pass is null ? null : Sum(pass, repo => repo.Modified),
            pass is null ? null : Sum(pass, repo => repo.Deleted),
            pass is null ? null : Sum(pass, repo => repo.Skipped),
            pass?.Embedded,
            [.. runtime.Warnings ?? [], .. passWarnings]);
    }

    private static int Sum(IndexResult pass, Func<RepoIndexResult, int> field) =>
        pass.Repos.Sum(field);
}
