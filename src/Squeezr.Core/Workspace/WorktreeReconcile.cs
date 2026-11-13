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
// duplicate content. What answers the obligation is the set of ids the collection hands back for THOSE
// hashes — a like number of other points covers nothing (review-116-10 Finding 3), which is why the claim is
// asked by id and never weighed by size.
//
// The verb runs at every session start, so owing a pass is decided by state rather than by memory of the
// pass that filled, and the state that answers now is the store's own ReconcileMarker rather than a walk of
// the collection: a record of a completed pass over this root is the proof, which is what retires the
// 'assumed' answer that made a healthy lane pay a measurement to re-ask a question it could have remembered
// (review-116-11 §3). A record is trusted for the work it attests and attested against afterwards — a
// bounded sample of the ids the claim names is what says the collection still holds this store's points and
// not somebody else's of the same size. A sample that finds a gap owes the pass again; a sample that does
// not answer is not a gap, and re-scanning the same tree at every session because a service is down is
// doubled work, not progress. Where the record and the measurement disagree — the store still calls its
// semantic arm unaccounted while nothing here says it is — the record is the half that has to answer, since
// a pending flag no verb rewrites is a warning that outlives every remedy it names (review-116-18 B1).
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

        var manifestPath = SqueezrPaths.ManifestPath(registered.RootPath);
        var marker = WorkspaceManifestFile.TryReadAt(manifestPath);

        var before = ReadRows(registered.RootPath);
        if (before is null)
        {
            return WorktreeReconcileOutcome.Deferred(NoRowsYet(fill));
        }

        if (before is { Readable: false })
        {
            return WorktreeReconcileOutcome.UnreadableStore(registered.RootPath, before.Unopenable);
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
        var owed = fill.State == WorktreeFillState.Filled || marker?.CoordinatesProven != true;
        ClaimCheck? byRecord = null;

        // A lane whose record proves the coordinates still says what its collection answers now, and asks for
        // it in the unit a record can be checked in: a spread of the ids its own claim names, one batch, never
        // the whole collection. A sample that finds those points attests the record; a sample that finds
        // none of them is the degraded collection this verb exists to catch, and it owes the pass back.
        if (!owed && obligation)
        {
            byRecord = AttestSample(Runtime().VectorStore, before, ct);
            owed = byRecord.Gap;
        }

        if (!owed)
        {
            var settled = Judge(before, noSemantic, byRecord ?? ClaimCheck.NothingToAsk(), passFailure: null);
            // The record and the measurement disagree in exactly one window: the store still says its
            // semantic arm never landed, while the rows and the ids it names answer nothing short of that.
            // A record this verb will not rewrite cannot be outlived, so the disagreement owes the pass
            // rather than printing the verdict the store refuses to believe — otherwise the pending word
            // repeats at every start and the remedy it names never closes the record it asks to close
            // (review-116-18 B1). Outside this window nothing changes: a verdict that already reads pending
            // is the same answer the record gives, and a closed record needs no measurement to be trusted.
            if (settled.State != WorktreeReconcileState.Reconciled || marker?.Pending != true)
            {
                return Outcome(settled, passRan: false, before, obligation, byRecord,
                    pass: null, runtime: Runtime(), basis: "record", marker: marker);
            }

            owed = true;
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
        catch (Exception ex) when (IsRecoverable(ex))
        {
            passFailure = ex.Message;
        }

        var after = ReadRows(registered.RootPath) ?? before;
        var stillObligated = after.ObligationChunks > 0;
        var measured = stillObligated ? MeasureClaim(runtime.VectorStore, after, ct) : ClaimCheck.NothingToAsk();
        var verdict = Judge(after, noSemantic, measured, passFailure);
        var outcome = Outcome(verdict, passRan: true, after, stillObligated, measured, pass, runtime,
            basis: "pass", marker: marker);
        return WithMarkerWrite(outcome, manifestPath, registered.RootPath, marker, after, verdict, measured,
            passCompleted: pass is not null, exactArm: ExactArmOf(manifestPath));
    }

    // Whether the exact arm stands, measured off the file the exact searches open rather than repeated from
    // the record: a lane that deleted its trigram.idx, or a parent that never built one, both say more than a
    // copied flag does, and presence is the one part of this that is cheap to look at (review-116-08 §2
    // caveat 1).
    private static bool ExactArmOf(string manifestPath) =>
        File.Exists(Path.Combine(Path.GetDirectoryName(manifestPath)!, SqueezrPaths.TrigramIndexFileName));

    private sealed record StoreRows(
        int Files,
        int TotalChunks,
        int PendingChunks,
        IReadOnlyList<string> EmbeddedHashes,
        bool Readable,
        StoreOpenCause Unopenable)
    {
        public int ObligationChunks => EmbeddedHashes.Count;
    }

    // What a measurement of the collection found, in the claim's own unit. Whether a reader may take these
    // numbers as this call's measurement or only as an attestation of the record is not carried here — the
    // outcome's 'basis' says it, and a sample never answers how much of the claim the collection reaches.
    private sealed record ClaimCheck(long? Points, int? Uncovered, string? Failure, int Probed)
    {
        public bool Gap => Uncovered is > 0;

        public bool Answered => Failure is null;

        // The shape a store with no vector claim is measured against: nothing was asked, so nothing is
        // reported, and the verdict comes from the rows alone.
        public static ClaimCheck NothingToAsk() => new(null, null, null, Probed: 0);
    }

    // Null is 'this store holds no database at all', which is the lane born before its main worktree was
    // indexed. A database that exists and will not open is a different fact and is reported as such: the lane
    // is not empty, it is unreadable, and the class of that refusal is the store's own observation rather
    // than a name this pass invented for a null (review-116-12 W-2).
    private static StoreRows? ReadRows(string workspaceRoot)
    {
        var metaDb = SqueezrPaths.MetaDbPath(workspaceRoot);
        if (!File.Exists(metaDb))
        {
            return null;
        }

        var store = MetaStore.TryOpen(metaDb, out var cause);
        if (store is null)
        {
            return new StoreRows(0, 0, 0, [], Readable: false, cause);
        }

        using (store)
        {
            var coverage = store.GetSemanticCoverage();
            return new StoreRows(
                store.CountAllFiles(),
                coverage.TotalChunks,
                coverage.PendingChunks,
                store.ListEmbeddedChunkHashes(),
                true,
                StoreOpenCause.None);
        }
    }

    // The claim asked by id, within a ceiling. The ceiling is what keeps a session start unbounded-proof:
    // past it this pass says it did not measure, instead of weighing the claim against a number that
    // answers a different question.
    private static ClaimCheck MeasureClaim(IVectorStore? store, StoreRows rows, CancellationToken ct)
    {
        if (store is null)
        {
            return new ClaimCheck(null, null, "this worktree has no vector store to ask", Probed: 0);
        }

        if (rows.ObligationChunks > ReconcileProbeBudget.FullClaimIdCeiling)
        {
            return new ClaimCheck(
                null, null,
                $"this store names {rows.ObligationChunks} chunk hash(es), past the "
                + $"{ReconcileProbeBudget.FullClaimIdCeiling} ids one reconcile pass will ask a collection "
                + "for",
                Probed: rows.ObligationChunks);
        }

        var ids = Ids(rows.EmbeddedHashes);
        try
        {
            var missing = store.MissingPointIdsAsync(ids, ct).GetAwaiter().GetResult();
            var points = store.CountAsync(ct).GetAwaiter().GetResult();
            return new ClaimCheck(points, missing.Count, null, Probed: ids.Count);
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            return new ClaimCheck(null, null, ex.Message, Probed: ids.Count);
        }
    }

    // The record's spot-check: a spread over the ids the claim names, one batch, chosen by position rather
    // than by luck so the same store asks the same questions every start.
    private static ClaimCheck AttestSample(IVectorStore? store, StoreRows rows, CancellationToken ct)
    {
        if (store is null)
        {
            return new ClaimCheck(null, null, "this worktree has no vector store to ask", Probed: 0);
        }

        var ids = Sample(rows.EmbeddedHashes, ReconcileProbeBudget.AttestationSampleIds);
        try
        {
            var missing = store.MissingPointIdsAsync(ids, ct).GetAwaiter().GetResult();
            return new ClaimCheck(null, missing.Count, null, Probed: ids.Count);
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            return new ClaimCheck(null, null, ex.Message, Probed: ids.Count);
        }
    }

    private static IReadOnlyList<ulong> Ids(IReadOnlyList<string> hashes) =>
        [.. hashes.Select(ChunkPointId.FromHash)];

    private static IReadOnlyList<ulong> Sample(IReadOnlyList<string> hashes, int take)
    {
        var distinct = hashes
            .Distinct(StringComparer.Ordinal)
            .OrderBy(hash => hash, StringComparer.Ordinal)
            .ToList();
        if (distinct.Count <= take)
        {
            return Ids(distinct);
        }

        var ids = new List<ulong>(take);
        var stride = distinct.Count / take;
        for (var seat = 0; seat < take; seat++)
        {
            ids.Add(ChunkPointId.FromHash(distinct[seat * stride]));
        }

        return ids;
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
        ClaimCheck check,
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

            return (WorktreeReconcileState.Reconciled, null, null);
        }

        if (check.Failure is not null)
        {
            return (
                WorktreeReconcileState.Pending,
                $"this store calls {rows.ObligationChunks} chunk hash(s) embedded and the collection did not "
                + $"answer ({check.Failure}), so nothing here proves those vectors exist. Start the services "
                + $"('{RemedyText.ServicesUp}') and re-run this verb, or close the gap with "
                + $"'{RemedyText.VectorsReconcileApply}' in this worktree.",
                WorktreeReconcileOutcome.StoreSilentCode);
        }

        // A sample attests the record, it does not re-measure it: the numbers a lane may read as coverage are
        // the ones the closed pass wrote. So a sample that finds a gap is not a verdict here — it is the
        // reason this pass is owed, and the measurement below is what answers for the gap.
        if (check.Gap)
        {
            return (
                WorktreeReconcileState.Pending,
                $"the collection holds {check.Points} point(s) and {check.Uncovered} of the "
                + $"{rows.ObligationChunks} chunk hash(es) this store calls embedded have no point in it, so "
                + "semantic answers here are short of what the database claims. Re-run this verb once "
                + $"'{RemedyText.ServicesUp}' has brought the store up, or fill this worktree from its own "
                + $"tree with '{RemedyText.IndexCommand}'.",
                WorktreeReconcileOutcome.CollectionShortCode);
        }

        return (WorktreeReconcileState.Reconciled, null, null);
    }

    private static WorktreeReconcileOutcome Outcome(
        (WorktreeReconcileState State, string? Reason, string? Code) verdict,
        bool passRan,
        StoreRows rows,
        bool obligation,
        ClaimCheck? check,
        IndexResult? pass,
        WorktreeReconcileRuntime runtime,
        string basis,
        ReconcileMarker? marker)
    {
        IReadOnlyList<string> passWarnings = pass?.Warnings ?? [];
        // The record answers before anything is asked of the collection, and that order is what keeps the
        // numbers honest: a verdict the record settled measured no coverage, so it reports none either. A
        // sample attests that the points the record counted are still in the collection; it never re-counts
        // them, and printing the sample's 0-uncovered as this call's 'coveredChunks' would hand a reader the
        // measurement the cost clause exists to refuse. What the record itself counted rides in 'record'.
        var measured = basis == "pass" ? check : null;
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
            measured?.Points,
            measured is { Answered: true, Uncovered: int uncovered }
                ? rows.ObligationChunks - uncovered
                : null,
            pass is null ? null : pass.Repos.Sum(repo => repo.Files),
            pass is null ? null : Sum(pass, repo => repo.Added),
            pass is null ? null : Sum(pass, repo => repo.Modified),
            pass is null ? null : Sum(pass, repo => repo.Deleted),
            pass is null ? null : Sum(pass, repo => repo.Skipped),
            pass?.Embedded,
            [.. runtime.Warnings ?? [], .. passWarnings],
            basis,
            check?.Probed,
            WorktreeRecord.Of(marker));
    }

    private static WorktreeReconcileOutcome WithMarkerWrite(
        WorktreeReconcileOutcome outcome,
        string manifestPath,
        string root,
        ReconcileMarker? marker,
        StoreRows rows,
        (WorktreeReconcileState State, string? Reason, string? Code) verdict,
        ClaimCheck check,
        bool passCompleted,
        bool exactArm)
    {
        var closed = (marker ?? ReconcileMarker.Opened()).ClosedPass(
            passCompleted,
            verdict.State == WorktreeReconcileState.Reconciled,
            rows.ObligationChunks,
            check is { Answered: true, Uncovered: int uncovered } ? rows.ObligationChunks - uncovered : null,
            verdict.Code,
            exactArmLanded: exactArm,
            filled: (marker?.Filled ?? false) || rows.Files > 0);
        try
        {
            WorkspaceManifestFile.WriteReconcileAt(manifestPath, root, closed);
            // The record this report carries is the one the store now holds, not the one this call read: a
            // reader comparing the verb's answer with the next session's 'status' must see one fact, not two.
            return outcome with { Record = WorktreeRecord.Of(closed) };
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            // The pass ran and the verdict stands, but the proof did not reach the store, so the next session
            // start will owe this pass again. That is said out loud instead of hidden behind a fresh verdict:
            // a proof that evaporates is the failure class this marker exists to close (review-116-11 D4).
            return outcome with
            {
                Warnings = [
                    .. outcome.Warnings ?? [],
                    $"this pass proved the tree but could not record it in '{manifestPath}' "
                    + $"({ex.GetType().Name}: {ex.Message}), so the store still reads as pending and the next "
                    + "run of this verb owes the pass again",
                ],
            };
        }
    }

    private static bool IsRecoverable(Exception ex) =>
        ex is not OutOfMemoryException and not StackOverflowException and not OperationCanceledException;

    private static int Sum(IndexResult pass, Func<RepoIndexResult, int> field) =>
        pass.Repos.Sum(field);
}
