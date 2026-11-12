namespace Squeezr.Core.Workspace;

// What one `worktree drop` is about to take: the tree it resolved, every row that spelling folds to, and
// how many lane rows name this root as their parent. Nothing here has been removed yet — the plan is the
// part that can be argued about, so it is produced whole before the first byte moves.
public sealed record WorktreeDropPlan(
    string RootPath,
    string? Branch,
    string? ParentRootPath,
    bool IsLinked,
    int DependentLanes,
    IReadOnlyList<WorktreeDropClaim> Claims);
