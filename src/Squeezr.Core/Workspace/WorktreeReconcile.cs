using Squeezr.Core.Embeddings;
using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;

namespace Squeezr.Core.Workspace;

// The pass that makes a copied store answer for its own tree. A fill lands the main worktree's rows, and
// every coordinate in them — the file list, the line numbers, the chunk hashes — was measured on another
// checkout. This is the one standard incremental pass over this worktree's root that puts those numbers back
// on this branch, plus the measurement that says whether this worktree's vectors reach what its rows claim.
//
// The threshold is a comparison, never the copied database. A fill writes rows that already read 'ok', and
// 'ok' names the model that produced a vector, not a vector that exists in THIS collection; reading those
// rows as coverage is how a lane would print fresh over an empty collection. So the rows' claim is the
// obligation, counted in the unit the collection is made of: a point id comes from a chunk hash, N rows
// sharing one hash own one point, and an obligation counted in rows could never be met by any tree holding
// duplicate content. What answers the obligation is the set of ids the collection hands back, intersected
// with those hashes — a like number of foreign points covers nothing.
//
// The verb runs at every session start, so owing a pass is decided by state rather than by memory of the
// pass that filled. A copy that landed this session always owes one. A lane that kept its index owes another
// only while its collection measurably fails to cover its claim: a measurement that did not answer is not a
// gap, and re-scanning the same tree at every session because a service is down is doubled work, not
// progress. Owing no pass is not the same as being proven: the agreement measured without one is between
// copied numbers, which is what 'assumed' claims and nothing more.
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
                + $"the store stays empty until one does. Run '{RemedyText.IndexCommand}' here to fill it.");
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

        var obligation = before.ObligationChunks > 0;
        var coveredBefore = obligation ? MeasureCollection(Runtime().VectorStore, before, ct) : null;

        var owed = fill.State == WorktreeFillState.Filled
            || (obligation && coveredBefore is { Points: { }, Uncovered: > 0 });

        if (!owed)
        {
            var settled = Judge(before, noSemantic, coveredBefore, passFailure: null, passProven: false);
            return Outcome(settled, passRan: false, before, obligation, coveredBefore,
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
        var stillObligated = after.ObligationChunks > 0;
        var coveredAfter = stillObligated ? MeasureCollection(runtime.VectorStore, after, ct) : null;
        var verdict = Judge(after, noSemantic, coveredAfter, passFailure, passProven: true);
        return Outcome(verdict, passRan: true, after, stillObligated, coveredAfter, pass, runtime);
    }

    private sealed record StoreRows(
        int Files,
        int TotalChunks,
        int PendingChunks,
        IReadOnlyList<string> EmbeddedHashes,
        bool Readable)
    {
        public int ObligationChunks => EmbeddedHashes.Count;
    }

    // The collection's answer in the obligation's own unit: how many points it holds and how many of the
    // hashes this store calls embedded have no point among them. Points stay null when the store did not
    // answer, and an unanswered measurement holds no gap for anyone to act on.
    private sealed record CollectionCoverage(long? Points, int? Uncovered, string? Failure);

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
            return new StoreRows(0, 0, 0, [], Readable: false);
        }

        using (store)
        {
            var coverage = store.GetSemanticCoverage();
            return new StoreRows(
                store.CountAllFiles(),
                coverage.TotalChunks,
                coverage.PendingChunks,
                store.ListEmbeddedChunkHashes(),
                true);
        }
    }

    // A missing handle is an unanswered measurement, never a measurement of zero: a store with no vector
    // configuration cannot prove the claim its rows make, and the pending shape that says so is the one
    // named 'silent'. The ids are scrolled rather than counted because the claim is about THESE rows — a
    // collection of the right size carrying other rows' points is the failure this verb exists to refuse.
    private static CollectionCoverage MeasureCollection(IVectorStore? store, StoreRows rows, CancellationToken ct)
    {
        if (store is null)
        {
            return new CollectionCoverage(null, null, "this worktree has no vector store to ask");
        }

        try
        {
            var ids = store.ScrollPointIdsAsync(ct).GetAwaiter().GetResult().ToHashSet();
            var uncovered = rows.EmbeddedHashes.Count(hash => !ids.Contains(ChunkPointId.FromHash(hash)));
            return new CollectionCoverage(ids.Count, uncovered, null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException
                                   and not OperationCanceledException)
        {
            return new CollectionCoverage(null, null, ex.Message);
        }
    }

    private static string NoRowsYet(WorktreeFillOutcome fill) =>
        fill.Skip is WorktreeFillSkip.SourceHasNoRows or WorktreeFillSkip.SourceStoreMissing
            ? "this worktree holds no rows yet, because the main worktree has no index to inherit: nothing is "
              + "reconciled, and the session start that finds the main worktree indexed fills and reconciles "
              + "this one."
            : "this worktree's store holds no rows yet, so there is nothing here to reconcile.";

    // The obligation is the rows' own claim. A store that claims no vector cannot be short of one, and a
    // workspace that asked for no semantic work is measured on coordinates — its embedding queue is 'status'
    // business, not a reconcile that failed here.
    private static (WorktreeReconcileState State, string? Reason, string? Code) Judge(
        StoreRows rows,
        bool noSemantic,
        CollectionCoverage? coverage,
        string? passFailure,
        bool passProven)
    {
        if (passFailure is not null)
        {
            return (
                WorktreeReconcileState.Pending,
                $"the reconcile pass did not finish ({passFailure}), so this worktree still answers with the "
                + $"coordinates it copied. Run '{RemedyText.IndexCommand}' here to finish it.",
                WorktreeReconcileOutcome.PassFailedCode);
        }

        if (rows.ObligationChunks == 0)
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

            return Settled(passProven, rows);
        }

        if (coverage?.Points is not { } points)
        {
            return (
                WorktreeReconcileState.Pending,
                $"this store calls {rows.ObligationChunks} chunk hash(s) embedded and the collection did not "
                + $"answer ({coverage?.Failure ?? "no measurement"}), so nothing here proves those vectors "
                + $"exist. Start the services ('{RemedyText.ServicesUp}') and re-run this verb, or close the "
                + $"gap with '{RemedyText.VectorsReconcileApply}' in this worktree.",
                WorktreeReconcileOutcome.StoreSilentCode);
        }

        if (coverage.Uncovered is > 0)
        {
            return (
                WorktreeReconcileState.Pending,
                $"the collection holds {points} point(s) and {coverage.Uncovered} of the "
                + $"{rows.ObligationChunks} chunk hash(es) this store calls embedded have no point in it, so "
                + "semantic answers here are short of what the database claims. Re-run this verb once "
                + $"'{RemedyText.ServicesUp}' has brought the store up, or fill this worktree from its own "
                + $"tree with '{RemedyText.IndexCommand}'.",
                WorktreeReconcileOutcome.CollectionShortCode);
        }

        return Settled(passProven, rows);
    }

    // Nothing this store claims is missing, which is the whole observation. Only a pass over this tree says
    // the rows describe this tree; without one the agreement between two copied numbers is an assumption,
    // and the store-side marker 116/T7 is what turns it into a fact.
    private static (WorktreeReconcileState State, string? Reason, string? Code) Settled(
        bool passProven,
        StoreRows rows) =>
        passProven
            ? (WorktreeReconcileState.Reconciled, null, null)
            : (
                WorktreeReconcileState.Assumed,
                $"the rows and the collection agree, but no pass over this tree has shown that these "
                + $"{rows.Files} file(s) are this branch's — the agreement is between numbers copied from "
                + $"another checkout. Run '{RemedyText.IndexCommand}' here to prove it.",
                WorktreeReconcileOutcome.AssumedCode);

    private static WorktreeReconcileOutcome Outcome(
        (WorktreeReconcileState State, string? Reason, string? Code) verdict,
        bool passRan,
        StoreRows rows,
        bool obligation,
        CollectionCoverage? coverage,
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
            rows.ObligationChunks,
            coverage?.Points,
            coverage?.Uncovered is int uncovered
                ? rows.ObligationChunks - uncovered
                : null,
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
