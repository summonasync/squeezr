using Squeezr.Core.Storage;

namespace Squeezr.Core.Workspace;

// The death side of `worktree init`: one tree, every spelling of it, one store to clear. A tree can hold
// several rows because it was registered twice under two spellings — the physical directory and a link
// pointing at it — and those rows are one workspace, not two. So the fold is by physical identity, the
// same comparison the identity guards use, and it happens before anything is removed: a path that resolves
// to no row at all is a refusal, and a path whose rows cannot be resolved to store dirs is a refusal too,
// because half a drop is the state this verb exists to prevent.
public static class WorktreeDrop
{
    public static WorktreeDropPlan Plan(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0', StringComparison.Ordinal))
        {
            throw new WorkspaceException(
                $"'{path}' names no directory, so no worktree can be resolved from it. Pass the checkout root, " +
                "or run this verb from inside the tree you mean to drop.",
                WorkspaceException.NotAWorktreeCode);
        }

        var physical = WorkspaceCanonicalPath.ResolveThroughLinks(path);
        if (File.Exists(physical))
        {
            throw new WorkspaceException(
                $"'{path}' is a file, not a worktree root.", WorkspaceException.NotAWorktreeCode);
        }

        if (!Directory.Exists(physical))
        {
            throw new WorkspaceException(GoneTreeText(path, physical), WorkspaceException.WorktreeNotRegisteredCode);
        }

        var facts = WorktreeFacts.Resolve(path);
        if (facts is null)
        {
            throw new WorkspaceException(
                $"'{path}' carries no readable .git marker up the tree, so git calls it no worktree. Drop a plain " +
                $"workspace with {RemedyText.DropCommand} in its root.",
                WorkspaceException.NotAWorktreeCode);
        }

        var root = WorkspaceCanonicalPath.ResolveThroughLinks(facts.Toplevel);
        var rows = WorkspaceRegistry.Load();
        var claims = rows
            .Where(row => WorkspaceCanonicalPath.SamePhysicalPath(row.RootPath, root))
            .Select(row => new WorktreeDropClaim(row.StoreKey, row.Name, row.RootPath))
            .ToList();
        if (claims.Count == 0)
        {
            throw new WorkspaceException(
                $"'{root}' is a git worktree but no registry row names it, so it owns no store to drop. Give it " +
                $"one with {RemedyText.WorktreeInitCommand}.",
                WorkspaceException.WorktreeNotRegisteredCode);
        }

        var poisoned = claims
            .Where(claim => !WorkspaceStorePaths.IsSingleSegmentKey(claim.StoreKey))
            .Select(claim => $"'{claim.StoreKey}'")
            .ToList();
        if (poisoned.Count > 0)
        {
            throw new WorkspaceException(
                $"{claims.Count} registry row(s) name '{root}' and {poisoned.Count} of them carry a store key that " +
                $"is not a single path segment ({string.Join(", ", poisoned)}), so their store dirs cannot be " +
                $"resolved. Nothing was removed. Reap them with {RemedyText.WorkspaceRepairCommand}.",
                WorkspaceException.WorktreeNotRegisteredCode);
        }

        // Lanes whose parent is this tree keep their own stores: dropping a main worktree's store is not a
        // verdict on the checkouts below it, and the report has to say how many of them are still hanging here.
        var dependentLanes = RowsForParent(rows, root)
            .Where(row => !claims.Any(claim => string.Equals(claim.StoreKey, row.StoreKey, StringComparison.Ordinal)))
            .ToList();

        return new WorktreeDropPlan(
            root,
            facts.Branch,
            ParentOf(facts, rows, claims, root),
            facts.IsLinked,
            dependentLanes.Count,
            claims);
    }

    private static string? ParentOf(
        WorktreeFacts facts,
        IReadOnlyList<WorkspaceRegistryEntry> rows,
        IReadOnlyList<WorktreeDropClaim> claims,
        string root)
    {
        var named = facts.MainWorktreeRoot is { } mainRoot
            ? WorkspaceCanonicalPath.Canonicalize(mainRoot)
            : rows.Where(row => claims.Any(claim => string.Equals(claim.StoreKey, row.StoreKey, StringComparison.Ordinal)))
                .Select(row => row.ParentRootPath)
                .FirstOrDefault(link => link is not null);

        // git reports a primary checkout as its own main root, and a tree named as its own parent would read as
        // a link the drop hangs its work on. One checkout, one parent: either above it or none.
        return named is not null && WorkspaceCanonicalPath.SamePhysicalPath(named, root) ? null : named;
    }

    private static IReadOnlyList<WorkspaceRegistryEntry> RowsForParent(
        IReadOnlyList<WorkspaceRegistryEntry> rows,
        string root) =>
        [.. rows.Where(row => row.ParentRootPath is { } parent
            && WorkspaceCanonicalPath.SamePhysicalPath(parent, root))];

    private static string GoneTreeText(string path, string physical)
    {
        var rows = WorkspaceRegistry.Load()
            .Where(row => WorkspaceCanonicalPath.SamePhysicalPath(row.RootPath, physical))
            .ToList();
        if (rows.Count == 0)
        {
            return $"'{path}' does not exist and no registry row names it — nothing to drop. If a workspace store " +
                   $"outlived this directory, reap it with {RemedyText.WorkspaceRepairCommand}.";
        }

        var named = string.Join(", ", rows.Select(row => $"'{row.StoreKey}'"));
        return $"'{path}' does not exist, so no git facts can be read from it, while {rows.Count} registry row(s) " +
               $"still name it ({named}). This verb drops a live tree; reap the row and its store with " +
               $"{RemedyText.WorkspaceRepairCommand}, or clear one row directly with " +
               $"{RemedyText.DropStore(rows[0].StoreKey)}.";
    }
}
