using Squeezr.Core.Storage;

namespace Squeezr.Core.Workspace;

// One registry row of the tree a path resolved to, with the two presence facts a reader cannot derive from the
// row itself: whether the checkout it names is still on disk, and whether the store dir its key points at is. A
// row can outlive its tree (the class `workspace repair` reaps) and a tree can outlive its store bytes (a drop
// that cleared the dir and left the row), so both are measured rather than inferred.
//
// The record is read off THIS row's store dir through its own key, never by looking the root up in the registry
// again: one tree can hold two rows under two spellings, and a second lookup can land on the other row. That is
// the same reason `SqueezrPaths.StoreDir` takes the entry.
//
// Every column is read off the row, not off the caller's claim: the branch here is the branch the registry
// recorded, which a reader compares against the branch git answers for now.
public sealed record WorktreeResolvedRow(
    string StoreKey,
    string Name,
    string RootPath,
    WorkspaceKind? Kind,
    string? ParentRootPath,
    string? Branch,
    bool TreePresent,
    bool StoreDirPresent,
    WorktreeRecord? Record)
{
    public static WorktreeResolvedRow Of(WorkspaceRegistryEntry row)
    {
        var ownable = WorkspaceStorePaths.IsSingleSegmentKey(row.StoreKey);
        var storeDir = ownable
            ? Path.Combine(SqueezrHome.WorkspacesDir, row.StoreKey)
            : string.Empty;
        return new WorktreeResolvedRow(
            row.StoreKey,
            row.Name,
            row.RootPath,
            row.Kind,
            row.ParentRootPath,
            row.Branch,
            Directory.Exists(row.RootPath),
            ownable && Directory.Exists(storeDir),
            ownable
                ? WorktreeRecord.Of(WorkspaceManifestFile.TryReadAt(
                    Path.Combine(storeDir, SqueezrPaths.StoreManifestFileName)))
                : null);
    }
}
