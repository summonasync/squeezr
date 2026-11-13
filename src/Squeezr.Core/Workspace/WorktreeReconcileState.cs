namespace Squeezr.Core.Workspace;

// Whether one worktree's store answers for its own tree. 'Pending' is the reported-not-thrown shape this
// verb always takes: the lane is usable, its coordinates are not this branch's yet, and the numbers say how
// far off. 'Deferred' is not a failure either — it is the state where there is nothing here to reconcile, a
// lane born before its main worktree was indexed being the ordinary one at a session start.
//
// 'Reconciled' means a pass over this root proved the rows, either in the session reading it or in the one
// the store's ReconcileMarker records. The state this retired — 'Assumed', the answer a session start gave
// when copied numbers agreed but nothing said they agreed about THIS checkout — existed because no proof was
// persisted anywhere, so a healthy lane paid the warning at every start until the store could remember it
// (review-116-11 D4 and §3). With the marker the same question has a real answer, and keeping a state that
// no path can reach would let a reader hedge where nothing is hedged.
public enum WorktreeReconcileState
{
    Reconciled,
    Pending,
    Deferred,
}
