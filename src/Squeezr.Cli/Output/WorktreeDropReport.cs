namespace Squeezr.Cli.Output;

// One store this drop cleared. RegisteredRootPath is the spelling the row carried, which is the spelling its
// collection name was hashed from, so a tree that held two rows shows two stores and two collection names
// rather than one of them silently. Failure is set on the row this pass could not remove, and it names the
// phase, never a path: a removal that did not happen must not be described by a path that still exists.
public sealed record WorktreeDroppedStore(
    string StoreKey,
    string Name,
    string RegisteredRootPath,
    IReadOnlyList<string> RemovedPaths,
    string? Collection,
    string CollectionState,
    string? Failure = null);

public sealed record WorktreeDropReport(
    string RootPath,
    string? Branch,
    string? ParentRootPath,
    bool IsLinked,
    int DependentLanes,
    IReadOnlyList<WorktreeDroppedStore> Stores,
    // Claims the plan named but this pass never reached because an earlier failure stopped the fold.
    // Without it a three-row plan that died on the second row reads as a two-row plan.
    int NotAttempted = 0);
