using Squeezr.Core.Storage;

namespace Squeezr.Core.Workspace;

// The read door of the worktree verb: given any path, say which store it maps to and what that store says about
// itself — and change nothing while doing it. This is the answer a session hook is allowed to ask every start
// and on every read, so the file IO stays limited to reading git's own files and reading the registry and the
// manifests its rows name. No lock is taken, no directory is created, no row is written: a diagnostic that
// repairs what it inspects cannot be run twice a day without side effects.
//
// Identity is the physical directory, the same comparison `worktree drop` folds on, so a lane reached through a
// link and the lane itself give one answer that shows every row of that tree. The recorded branch and parent
// link come off the row rather than being recomputed here: T1's columns are the record of the pass that
// registered the tree, and a read verb that re-derives them invents a second answer for one fact.
public static class WorktreeResolve
{
    public static WorktreeResolveOutcome Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0', StringComparison.Ordinal))
        {
            throw new WorkspaceException(
                $"'{path}' names no path to resolve. Pass a directory inside a checkout, or run this verb from " +
                "inside the tree you mean to ask about.",
                WorkspaceException.NotAWorktreeCode);
        }

        var physical = WorkspaceCanonicalPath.ResolveThroughLinks(path);
        if (File.Exists(physical))
        {
            return WorktreeResolveOutcome.Of(
                WorktreeResolveState.NotAWorktree, path, physical,
                $"'{path}' is a file, not a directory, so no checkout root and no store can be resolved from it. " +
                "Pass the directory you mean, not a path inside it.");
        }

        if (!Directory.Exists(physical))
        {
            // A tree that is gone is the one answer resolve refuses rather than reports. The registry may still
            // be claiming rows for a directory nothing can measure, and that is a state with a door only a human
            // can tap. The facts are the class T8 established, shared through WorktreeGoneTree so 'the same
            // facts' means the same fields and not a paraphrase of them.
            throw WorktreeGoneException.ForReadVerb(WorktreeGoneTree.Measure(path, physical));
        }

        var rows = WorkspaceRegistry.Load();
        var facts = WorktreeFacts.Resolve(path);
        var root = facts is null ? physical : WorkspaceCanonicalPath.ResolveThroughLinks(facts.Toplevel);
        var own = RowsOf(rows, root);
        var parent = ParentOf(facts, own);
        var parentRegistered = parent is not null
            && rows.Any(row => WorkspaceCanonicalPath.SamePhysicalPath(row.RootPath, parent));

        // Lanes hanging off this root are counted on every answer, including the ones that name no row: a main
        // worktree its lanes are filled from is the fact a reader wants even when it is not itself registered.
        var dependentLanes = rows.Count(row => row.ParentRootPath is { } named
            && WorkspaceCanonicalPath.SamePhysicalPath(named, root)
            && !own.Any(claimed => claimed.StoreKey == row.StoreKey));

        if (own.Count > 0)
        {
            return WorktreeResolveOutcome.Of(
                WorktreeResolveState.Resolved, path, physical, facts, parent, parentRegistered, own,
                dependentLanes, null);
        }

        if (facts is null)
        {
            return WorktreeResolveOutcome.Of(
                WorktreeResolveState.NotAWorktree, path, physical,
                $"'{root}' carries no readable .git marker up the tree, so git calls it no checkout and no store " +
                $"answers for it. Register a plain workspace here with {RemedyText.InitCommand}.");
        }

        return WorktreeResolveOutcome.Of(
            WorktreeResolveState.Unregistered, path, physical, facts, parent, parentRegistered, [],
            dependentLanes,
            $"'{root}' is a git checkout on branch {facts.Branch ?? "detached or unknown"} but no registry row " +
            $"names it, so it owns no store. Give it one with {RemedyText.WorktreeInitCommand}" +
            (parentRegistered ? string.Empty : $", after {RemedyText.InitCommand} in '{parent}' gives the " +
            "main worktree a store to fill from") + ".");
    }

    private static IReadOnlyList<WorktreeResolvedRow> RowsOf(
        IReadOnlyList<WorkspaceRegistryEntry> rows,
        string root) =>
        [.. rows.Where(row => WorkspaceCanonicalPath.SamePhysicalPath(row.RootPath, root))
            .Select(WorktreeResolvedRow.Of)];

    // The checkout this one hangs under: git's answer for a linked tree, with the row's recorded link as the
    // fallback for the separate-git-dir case — the same order `worktree drop` reads. A primary checkout names
    // itself as its own main root, and a tree named as its own parent is no parent.
    private static string? ParentOf(WorktreeFacts? facts, IReadOnlyList<WorktreeResolvedRow> own)
    {
        if (facts is null)
        {
            return own.Select(row => row.ParentRootPath).FirstOrDefault(link => link is not null);
        }

        var named = facts.MainWorktreeRoot is { } mainRoot
            ? WorkspaceCanonicalPath.Canonicalize(mainRoot)
            : own.Select(row => row.ParentRootPath).FirstOrDefault(link => link is not null);
        return named is null || WorkspaceCanonicalPath.SamePhysicalPath(named,
            WorkspaceCanonicalPath.ResolveThroughLinks(facts.Toplevel))
            ? null
            : named;
    }
}
