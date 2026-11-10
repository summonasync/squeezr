using Squeezr.Core.Storage;

namespace Squeezr.Core.Workspace;

// The answer to "did this worktree get filled from its main worktree". A skip is not a failure: it is the
// verb reporting that it deliberately moved no bytes, with the reason the caller has to relay — and a
// fill is never a bare success either, it carries the numbers that prove what landed.
public sealed record WorktreeFillOutcome(WorktreeFillState State, string? Reason, WorkspaceStoreCopyResult? Copy)
{
    public static WorktreeFillOutcome Filled(WorkspaceStoreCopyResult copy) =>
        new(WorktreeFillState.Filled, null, copy);

    public static WorktreeFillOutcome Skipped(string reason) => new(WorktreeFillState.Skipped, reason, null);
}
