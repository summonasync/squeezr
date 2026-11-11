using Grpc.Core;
using Squeezr.Core.Configuration;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Embeddings;

// The semantic arm of one worktree fill: every point of the main worktree's collection lands in this
// worktree's collection, by scroll-and-put. Nothing here embeds — a point read off the source is written to
// the target as it came, and since the point id is derived from chunk content, the id that lands is the same
// id a later pass upserts or deletes by. The target's geometry is not asked of the embedding model either:
// the vector size comes off the source collection, which is the only number a copy may trust, and reading it
// anywhere else would mean reaching past this path for a provider.
//
// What must move is decided by the source COLLECTION, never by the copied meta.db. The file arms land the
// parent's rows first, so a filled lane's database already calls every one of those chunks 'ok' under a
// collection that holds nothing: 'ok' names the model that produced the vector, not a vector that exists
// here, and reading it as coverage is how a copy would print full numbers over an empty lane.
//
// Of the three numbers, one is a contract and two are measurements. The target count is this pass's own
// accounting — the writes it issued either landed in that collection or they did not — so a disagreement
// there is a failed command, never a warning: a silently short collection is the defect this feature exists
// to close. The source number is read twice, before and after the walk, off a collection the running main
// worktree owns and writes to while this pass scrolls it. A source that moved is therefore not a refusal:
// it is a retry-shaped fact, the same posture this verb takes for a model question that could not be
// answered, and the pair is reported so a reader can tell a short copy from a busy parent.
//
// Both transport shapes are decided by whether the lane's collection was touched yet. A store that stops
// answering before any write is reported, because this verb runs at every session start and a service that
// is not in the room must not fail the session over it. A store that stops answering after writes began —
// including on the count that confirms them — is the copy stopping, and that is said with the numbers it
// stopped on. Every Qdrant read on this path is inside that mapping, including the two counts and the
// geometry read: an unmapped gRPC exception would leave this command without its report on stdout, which is
// the surface a session-start hook parses.
public static class WorktreeVectorCopier
{
    private const int RecountAttempts = 3;
    private static readonly TimeSpan RecountBackoffStep = TimeSpan.FromMilliseconds(60);

    // The config is the lane's own: two trees of one repository share one vector store today, but the
    // endpoint this worktree's serve path reads is the endpoint its collection has to be written to. The two
    // handles are built here rather than passed in, so no caller can pair one tree's config with another
    // tree's collection name.
    public static async Task<WorktreeVectorCopyStats> CopyAsync(
        WorktreeVectorCopyRequest request,
        CancellationToken ct)
    {
        var config = ConfigFile.Load(request.TargetRoot).Qdrant;
        return await CopyAsync(
            new QdrantVectorStore(config, request.SourceRoot),
            new QdrantVectorStore(config, request.TargetRoot),
            request,
            config.ResolvedEndpoint,
            ct);
    }

    internal static async Task<WorktreeVectorCopyStats> CopyAsync(
        QdrantVectorStore source,
        QdrantVectorStore target,
        WorktreeVectorCopyRequest request,
        string endpoint,
        CancellationToken ct)
    {
        if (!await source.IsAvailableAsync(ct))
        {
            return WorktreeVectorCopyStats.NotCopied(
                $"The vector store at '{endpoint}' did not answer, so '{source.Collection}' could not be read " +
                $"and '{target.Collection}' was left untouched — while the copied database already calls this " +
                "lane's chunks embedded. Until the vectors land, semantic answers here are short of what the " +
                $"store claims. Close the gap without re-embedding: '{RemedyText.VectorsReconcileApply}' " +
                $"(run it in this worktree), or start the services ('{RemedyText.ServicesUp}') and re-run " +
                "this verb, or fill this worktree from its own tree with '--reindex'.",
                WorkspaceException.StorageUnavailableCode);
        }

        var sourcePoints = 0L;
        var sourcePointsAfterWalk = 0L;
        var copied = 0L;
        List<ForeignVectorPoint> foreign = [];
        var phase = "read the source collection's vector size";
        var askedToWrite = false;

        try
        {
            if (await source.GetVectorDimensionsAsync(ct) is not { } size)
            {
                return WorktreeVectorCopyStats.NotCopied(
                    $"'{source.Collection}' does not exist, so the main worktree has written no vectors for " +
                    $"this worktree to inherit and '{target.Collection}' was not created. Index the main " +
                    $"worktree first: {RemedyText.AnchorIndex(request.SourceRoot)}");
            }

            phase = "count the points in the source collection";
            sourcePoints = await source.CountAsync(ct);
            if (sourcePoints == 0)
            {
                return WorktreeVectorCopyStats.NotCopied(
                    $"'{source.Collection}' holds no points, so there was nothing to copy and " +
                    $"'{target.Collection}' was not created.");
            }

            askedToWrite = true;
            phase = "prepare the worktree's collection";
            await target.EnsureCollectionAsync((int)size, ct);

            phase = "move the pages";
            var walk = await MovePagesAsync(source, target, ct);
            copied = walk.Copied;
            foreign = walk.Foreign;

            phase = "count the source collection again after the walk";
            sourcePointsAfterWalk = await source.CountAsync(ct);

            phase = "count the worktree's collection after the writes";
            var targetPoints = await CountTargetSettledAsync(target, copied, ct);

            if (targetPoints != copied)
            {
                throw Incomplete(
                    request, source, target, sourcePoints, sourcePointsAfterWalk, copied, targetPoints, foreign);
            }

            if (foreign.Count > 0)
            {
                return WorktreeVectorCopyStats.ForeignGap(
                    sourcePoints,
                    sourcePointsAfterWalk,
                    copied,
                    targetPoints,
                    foreign.Count,
                    ForeignReason(source, target, sourcePoints, copied, targetPoints, foreign));
            }

            if (sourcePointsAfterWalk != sourcePoints)
            {
                return WorktreeVectorCopyStats.SourceMoved(
                    sourcePoints,
                    sourcePointsAfterWalk,
                    copied,
                    targetPoints,
                    DriftReason(source, target, sourcePoints, sourcePointsAfterWalk, copied));
            }

            return WorktreeVectorCopyStats.Moved(sourcePoints, copied, targetPoints);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && ct.IsCancellationRequested)
        {
            // Defense, not a covered path: the verb reaches this through a synchronous command that hands
            // over CancellationToken.None, so nothing in the product cancels this token today.
            throw new OperationCanceledException(
                $"The vector copy into '{target.Collection}' was canceled after {copied} of {sourcePoints} " +
                "point(s).", ex, ct);
        }
        catch (Exception ex) when (ex is RpcException or HttpRequestException)
        {
            if (askedToWrite)
            {
                throw Stopped(request, source, target, sourcePoints, copied, phase, ex);
            }

            return WorktreeVectorCopyStats.NotCopied(
                $"The vector store at '{endpoint}' stopped answering while this pass was asked to {phase} " +
                $"({ex.GetType().Name}: {ex.Message}), so nothing was written to '{target.Collection}' — the " +
                "copied database still calls this lane's chunks embedded. Re-run this verb once the store " +
                $"answers ('{RemedyText.ServicesUp}'), or close the gap without re-embedding: " +
                $"'{RemedyText.VectorsReconcileApply}'.",
                WorkspaceException.StorageUnavailableCode);
        }
        catch (VectorDimensionMismatchException ex)
        {
            throw DimensionMismatch(request, source, target, ex);
        }
    }

    // Pages, not the whole collection: a source of this product's own size holds vectors that must not all be
    // resident to move them, and UpsertAsync splits each page into its own write batches.
    private static async Task<(long Copied, List<ForeignVectorPoint> Foreign)> MovePagesAsync(
        QdrantVectorStore source,
        QdrantVectorStore target,
        CancellationToken ct)
    {
        long copied = 0;
        var foreign = new List<ForeignVectorPoint>();
        ulong? offset = null;
        while (true)
        {
            var page = await source.ScrollVectorPointsAsync(offset, ct);
            copied += page.Points.Count;
            foreign.AddRange(page.ForeignPoints);
            await target.UpsertAsync(page.Points, ct);
            if (page.NextOffset is not { } next
                || page.Points.Count == 0 && page.ForeignPoints.Count == 0)
            {
                return (copied, foreign);
            }

            offset = next;
        }
    }

    // The invariant this pass owns, counted where it claims to be. A short read is retried within a bounded
    // budget because the endpoint is configuration: a replicated Qdrant can answer the count from a peer that
    // has not applied the last batch yet, and the page that lands last is the biggest. The disagreement is
    // declared only after the budget is spent.
    private static async Task<long> CountTargetSettledAsync(
        QdrantVectorStore target,
        long written,
        CancellationToken ct)
    {
        var count = await target.CountAfterOwnWritesAsync(ct);
        for (var attempt = 1; attempt < RecountAttempts && count < written; attempt++)
        {
            await Task.Delay(RecountBackoffStep * attempt, ct);
            count = await target.CountAfterOwnWritesAsync(ct);
        }

        return count;
    }

    private static WorkspaceException Incomplete(
        WorktreeVectorCopyRequest request,
        QdrantVectorStore source,
        QdrantVectorStore target,
        long sourcePoints,
        long sourcePointsAfterWalk,
        long copied,
        long targetPoints,
        IReadOnlyList<ForeignVectorPoint> foreign)
    {
        var drift = sourcePointsAfterWalk == sourcePoints
            ? string.Empty
            : $" The same source counted {sourcePointsAfterWalk} after the walk, so it moved under this pass. ";

        var skipped = foreign.Count == 0
            ? string.Empty
            : $" {foreign.Count} of the source's points were skipped as not written by this product. ";

        return new WorkspaceException(
            $"The vector copy did not land whole: '{source.Collection}' held {sourcePoints} point(s), this pass " +
            $"wrote {copied}, and '{target.Collection}' counts {targetPoints} after the writes.{drift}{skipped}" +
            "Points this pass cannot account for are points nothing here can search, and the copied database " +
            "still names them as embedded. Close the gap without throwing the lane away: run " +
            $"'{RemedyText.VectorsReconcileApply}' in this worktree first — it re-queues exactly the chunks " +
            "whose point is missing, and the next index pass fills them. Or clear the half-filled store and " +
            $"fill again: {RemedyText.DropStore(request.TargetStoreKey)}, then re-run this verb — or fill from " +
            "this worktree's own tree with '--reindex'.",
            WorkspaceException.WorktreeVectorMismatchCode);
    }

    private static WorkspaceException Stopped(
        WorktreeVectorCopyRequest request,
        QdrantVectorStore source,
        QdrantVectorStore target,
        long sourcePoints,
        long copied,
        string phase,
        Exception cause) =>
        new(
            $"The vector copy stopped while it was asked to {phase}, after writing {copied} of the " +
            $"{sourcePoints} point(s) in '{source.Collection}' ({cause.GetType().Name}: {cause.Message}). " +
            $"'{target.Collection}' holds what got through and no more, while the copied database already " +
            "calls these chunks embedded. Close the gap without throwing the lane away: first " +
            $"'{RemedyText.VectorsReconcileApply}' in this worktree — it re-queues exactly the chunks whose " +
            $"point is missing. Or clear the store and fill again: {RemedyText.DropStore(request.TargetStoreKey)}, " +
            "then re-run this verb — or fill from this worktree's own tree with '--reindex'.",
            WorkspaceException.WorktreeVectorMismatchCode,
            cause);

    // The stock dimension refusal blames the active embedding model and prescribes '--reembed'. Neither is
    // this path: the geometry asked for came off the parent's collection, and re-embedding the parent is not
    // a remedy for a lane whose collection was minted under another size.
    private static WorkspaceException DimensionMismatch(
        WorktreeVectorCopyRequest request,
        QdrantVectorStore source,
        QdrantVectorStore target,
        VectorDimensionMismatchException cause) =>
        new(
            $"'{target.Collection}' was created with vector size {cause.CollectionSize}, so the " +
            $"{cause.ModelDimensions}-dimensional vectors of '{source.Collection}' cannot be written into it. " +
            "This lane's collection predates the main " +
            "worktree's current geometry — nothing was copied and the copied database still calls these " +
            "chunks embedded. Clear this worktree's store and fill again: " +
            $"{RemedyText.DropStore(request.TargetStoreKey)}, then re-run this verb — or fill from this " +
            "worktree's own tree with '--reindex'.",
            WorkspaceException.WorktreeVectorDimensionCode,
            cause);

    private static string DriftReason(
        QdrantVectorStore source,
        QdrantVectorStore target,
        long sourcePoints,
        long sourcePointsAfterWalk,
        long copied) =>
        $"'{source.Collection}' counted {sourcePoints} before the walk and {sourcePointsAfterWalk} after it, " +
        $"so the main worktree wrote into its own collection while this pass was reading it. " +
        $"'{target.Collection}' holds the {copied} points this pass saw, all of them whole, and the copied " +
        "database calls them embedded for the rows that landed. Nothing here is broken and nothing is " +
        "decided: the difference belongs to writes the parent made after this pass stopped looking, and " +
        "those have no rows in this lane to be missing from. Re-running this verb answers Skipped — the " +
        "file arms already landed — so confirm the lane against its own rows with " +
        $"'{RemedyText.VectorsReconcileApply}'; points the parent added later are a later fill's business, " +
        "not a gap in this one.";

    private static string ForeignReason(
        QdrantVectorStore source,
        QdrantVectorStore target,
        long sourcePoints,
        long copied,
        long targetPoints,
        IReadOnlyList<ForeignVectorPoint> foreign)
    {
        var named = foreign.Take(ForeignIdsInMessage)
            .Select(point => $"'{point.Id}' (no {point.Missing})");
        var more = foreign.Count > ForeignIdsInMessage
            ? $" and {foreign.Count - ForeignIdsInMessage} more"
            : string.Empty;

        return $"'{source.Collection}' holds {foreign.Count} point(s) this product did not write — " +
            $"{string.Join(", ", named)}{more} — so they could not be copied as points of ours and were " +
            "skipped. Every point the source did name as ours crossed: " +
            $"{copied} written into '{target.Collection}', which counts {targetPoints} after the writes, " +
            $"against {sourcePoints} in the source. This " +
            "worktree's own rows are covered; the gap belongs to the source collection, and a foreign point " +
            "inside a squeezr namespace means something else wrote there. Reconcile to confirm the lane " +
            $"against its rows: '{RemedyText.VectorsReconcileApply}'.";
    }

    private const int ForeignIdsInMessage = 5;
}
