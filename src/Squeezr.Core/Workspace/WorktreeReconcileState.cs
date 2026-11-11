namespace Squeezr.Core.Workspace;

// Whether one worktree's store answers for its own tree. 'Pending' is the reported-not-thrown shape this
// verb always takes: the lane is usable, its coordinates are not this branch's yet, and the numbers say how
// far off. 'Deferred' is not a failure either — it is the state where there is nothing here to reconcile,
// a lane born before its main worktree was indexed being the ordinary one at a session start.
public enum WorktreeReconcileState
{
    Reconciled,
    Pending,
    Deferred,
}
