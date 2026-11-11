namespace Squeezr.Core.Workspace;

// WHICH deliberate no-copy a fill reported, spelled for the pass that decides whether a reconcile is owed
// without reading prose. The two shapes that end with an empty lane are not the same fact: one is this verb
// refusing to touch a store that already works, the other is a lane born before its main worktree was
// indexed, and only the first of them can be reconciled at all.
public enum WorktreeFillSkip
{
    ReindexRequested,
    NothingMinted,
    LaneKeepsItsIndex,
    SourceStoreMissing,
    SourceHasNoRows,
    SourceManifestMissing,
    ModelUnresolved,
}
