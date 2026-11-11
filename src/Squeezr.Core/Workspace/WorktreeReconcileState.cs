namespace Squeezr.Core.Workspace;

// Whether one worktree's store answers for its own tree. 'Pending' is the reported-not-thrown shape this
// verb always takes: the lane is usable, its coordinates are not this branch's yet, and the numbers say how
// far off. 'Deferred' is not a failure either — it is the state where there is nothing here to reconcile,
// a lane born before its main worktree was indexed being the ordinary one at a session start.
//
// 'Assumed' is the state a session start reaches when the numbers agree but no pass over this tree has run
// to say they agree about THIS checkout: it claims the measurement, never the proof, and 116/T7's store-side
// marker is what retires it into 'Reconciled'.
public enum WorktreeReconcileState
{
    Reconciled,
    Assumed,
    Pending,
    Deferred,
}
