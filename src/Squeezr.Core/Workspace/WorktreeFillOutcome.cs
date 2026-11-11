using Squeezr.Core.Storage;

namespace Squeezr.Core.Workspace;

// The answer to "did this worktree get filled from its main worktree". A skip is not a failure: it is the
// verb reporting that it deliberately moved no bytes, with the reason the caller has to relay — and a fill
// is never a bare success either, it carries the numbers that prove what landed.
//
// Vectors is the semantic arm beside the file arms: null when the source held no embedded chunks and the
// pass therefore had no points to inherit, set when a vector store was asked. A fill that moved files and
// was refused the collection is still a Filled store with a vector arm that says why it did not land — the
// reader must be able to tell those two apart, and this is the field that tells them.
public sealed record WorktreeFillOutcome(
    WorktreeFillState State,
    string? Reason,
    WorkspaceStoreCopyResult? Copy,
    WorktreeVectorCopyStats? Vectors = null,
    WorktreeFillSkip? Skip = null)
{
    public static WorktreeFillOutcome Filled(
        WorkspaceStoreCopyResult copy,
        WorktreeVectorCopyStats? vectors = null) =>
        new(WorktreeFillState.Filled, null, copy, vectors);

    public static WorktreeFillOutcome Skipped(string reason, WorktreeFillSkip skip) =>
        new(WorktreeFillState.Skipped, reason, null, Skip: skip);
}
