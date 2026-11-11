using Squeezr.Core.Workspace;

namespace Squeezr.Core.Storage;

// The semantic arm of one store filling, in the numbers spec decision 4 demands: what the source collection
// held before anything moved, what this pass handed over, and what the target collection counts afterwards.
// The last is re-read from the collection the writes landed in, not carried over from the scroll buffer — a
// copy that lost a batch would otherwise report the size of its own effort as success. The source is read
// twice, before and after the walk, because it belongs to a running main worktree: one number off it is a
// measurement of a moving thing, and the pair is what lets a reader tell a short copy from a busy parent.
//
// Only the target number is a contract. It is the one this pass controls: writes it issued either landed in
// that collection or they did not, and a disagreement is a failed command. The source pair is a measurement
// of somebody else's collection, so a source that moved during the walk is a retry-shaped fact, never a
// refusal — the same posture this verb already takes for a model question that could not be answered and for
// a vector store that is not in the room.
//
// A vector store that is not in the room is reported, not thrown: this verb runs at every session start, and
// a service that is not in the room must not fail the session over it. The reason then names the state the
// reader has to know — a store whose database claims vectors its collection does not hold.
//
// 'Code' says WHICH reported fact this is, for the pass that has to record it without reading prose. It
// rides only on shapes with a name of their own, so the ordinary copied/not-copied payload on the wire is
// unchanged.
public sealed record WorktreeVectorCopyStats(
    bool Copied,
    long SourcePoints,
    long CopiedPoints,
    long TargetPoints,
    string? Reason,
    long? SourcePointsAfterWalk = null,
    int? ForeignPoints = null,
    string? Code = null)
{
    public static WorktreeVectorCopyStats NotCopied(string reason, string? code = null) =>
        new(false, 0, 0, 0, reason, Code: code);

    public static WorktreeVectorCopyStats Moved(
        long sourcePoints,
        long copiedPoints,
        long targetPoints) =>
        new(true, sourcePoints, copiedPoints, targetPoints, null);

    // The source counted differently before and after the walk. Nothing is broken and nothing is decided: the
    // numbers describe two different moments of a collection this pass does not own, and the copy that ran
    // between them landed whole. Reported so the next pass can answer for the difference.
    public static WorktreeVectorCopyStats SourceMoved(
        long sourcePointsBefore,
        long sourcePointsAfter,
        long copiedPoints,
        long targetPoints,
        string reason) =>
        new(
            false,
            sourcePointsBefore,
            copiedPoints,
            targetPoints,
            reason,
            sourcePointsAfter,
            Code: WorkspaceException.WorktreeVectorDriftCode);

    // Every point this product wrote crossed, and the source held points this product did not write. Those
    // are named and counted rather than carried over as rows of zeroes or refused over: the lane's own rows
    // are all present, and the collection the parent holds is the parent's business.
    public static WorktreeVectorCopyStats ForeignGap(
        long sourcePointsBefore,
        long sourcePointsAfter,
        long copiedPoints,
        long targetPoints,
        int foreignPoints,
        string reason) =>
        new(
            false,
            sourcePointsBefore,
            copiedPoints,
            targetPoints,
            reason,
            sourcePointsAfter,
            foreignPoints,
            WorkspaceException.VectorPointForeignCode);
}
