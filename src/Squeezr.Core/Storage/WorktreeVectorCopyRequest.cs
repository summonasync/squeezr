namespace Squeezr.Core.Storage;

// Which two collections one worktree fill is asked to connect, named by the registry rows the pass
// resolved rather than by anything recomputed downstream: the source root is the main worktree's row and
// the target root is this tree's row, so the collection a copy writes into is the collection the worktree's
// own serve path reads afterwards. The store key rides along because the refusal text has to name the drop
// that clears a half-filled collection, and a caller that never resolved a row cannot produce one.
public sealed record WorktreeVectorCopyRequest(
    string SourceRoot,
    string TargetRoot,
    string TargetStoreKey);
