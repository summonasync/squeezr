using Squeezr.Core.Storage;

namespace Squeezr.Core.Workspace;

// One row of the family a queried checkout belongs to: the row naming the main worktree, and the rows that name
// that root as their parent. The role comes off T1's parent column and git's main-root fact and nothing else — a
// registered checkout carrying no parent link is a parent, because that is what the record says.
//
// Branch and kind are the recorded columns, not what git answers now: this is the surface that shows what the
// registry was told, and comparing it against the live tree is `worktree resolve'’s job. A row whose branch column
// is empty reads as 'recorded before this column existed', which is a fact about a file rather than a guessed
// branch.
//
// TreePresent is measured, because a family list that hides the dead rows cannot answer the question the list is
// opened for: which of these checkouts still exist.
public sealed record WorktreeFamilyRow(
    WorktreeFamilyRole Role,
    string StoreKey,
    string Name,
    string RootPath,
    WorkspaceKind? Kind,
    string? Branch,
    string? ParentRootPath,
    bool TreePresent,
    bool IsCurrent)
{
    public static WorktreeFamilyRow Of(
        WorkspaceRegistryEntry row,
        WorktreeFamilyRole role,
        bool isCurrent) =>
        new(role, row.StoreKey, row.Name, row.RootPath, row.Kind, row.Branch, row.ParentRootPath,
            Directory.Exists(row.RootPath), isCurrent);
}
