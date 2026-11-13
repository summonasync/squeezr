using Squeezr.Core.Storage;

namespace Squeezr.Core.Workspace;

// The family listing: the main worktree of the queried checkout and the rows that name it as their parent, read
// off the registry columns T1 added. No git process is spawned — the two inputs this listing needs, which root
// the family hangs on and which rows claim it, are both already in files, and a door a session hook may call
// does not pay for a `git worktree list --porcelain` round trip to learn what its own registry can say.
//
// The scope is one family, not the machine: a fleet view of lanes is this card's declared anti-scope, and the
// registry-wide sweep already has a door of its own (`workspace list`).
public static class WorktreeListing
{
    public static WorktreeFamilyOutcome List(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0', StringComparison.Ordinal))
        {
            throw new WorkspaceException(
                $"'{path}' names no path to list. Pass a directory inside a checkout, or run this verb from " +
                "inside the family you mean to see.",
                WorkspaceException.NotAWorktreeCode);
        }

        var physical = WorkspaceCanonicalPath.ResolveThroughLinks(path);
        if (File.Exists(physical))
        {
            throw new WorkspaceException(
                $"'{path}' is a file, not a directory, so no checkout family can be listed from it. Pass the " +
                "directory you mean, not a path inside it.",
                WorkspaceException.NotAWorktreeCode);
        }

        if (!Directory.Exists(physical))
        {
            // A gone path anchors no family, because the anchor is read from the tree. The refusal is the class
            // the other doors make, with the same measured fields: a listing that quietly came back empty would
            // read as 'this family has no lanes', which is a different fact from 'you asked about a directory
            // that is not there'.
            throw WorktreeGoneException.ForReadVerb(WorktreeGoneTree.Measure(path, physical));
        }

        var rows = WorkspaceRegistry.Load();
        var facts = WorktreeFacts.Resolve(path);
        var queriedRoot = facts is null ? physical : WorkspaceCanonicalPath.ResolveThroughLinks(facts.Toplevel);
        var anchor = FamilyAnchor(facts, rows, queriedRoot);

        if (anchor is null)
        {
            return WorktreeFamilyOutcome.Empty(queriedRoot,
                $"'{queriedRoot}' is no git checkout and no registry row names it, so there is no family to list " +
                $"here. Register this tree with {RemedyText.InitCommand}, or give a linked checkout its own store " +
                $"with {RemedyText.WorktreeInitCommand}.");
        }

        var family = Family(rows, anchor, queriedRoot);
        if (family.Count == 0)
        {
            return new WorktreeFamilyOutcome(anchor, queriedRoot, facts?.Branch, [],
                $"'{anchor}' owns no registry row and no row names it as a parent, so nothing in this family has a " +
                $"store yet. Give the tree you want indexed one with {RemedyText.WorktreeInitCommand}.");
        }

        return new WorktreeFamilyOutcome(anchor, queriedRoot, facts?.Branch, family, null);
    }

    // Parents before children, children ordered by root so one registry answers the same table twice. A primary
    // checkout is its own anchor, so its row lands on the parent side of its own family — the row a lane points
    // at, not a lane pointing at itself.
    private static IReadOnlyList<WorktreeFamilyRow> Family(
        IReadOnlyList<WorkspaceRegistryEntry> rows,
        string anchor,
        string queriedRoot)
    {
        var listed = new List<WorktreeFamilyRow>();
        foreach (var row in rows)
        {
            var isAnchor = WorkspaceCanonicalPath.SamePhysicalPath(row.RootPath, anchor);
            var isLane = !isAnchor && row.ParentRootPath is { } named
                && WorkspaceCanonicalPath.SamePhysicalPath(named, anchor);
            if (!isAnchor && !isLane)
            {
                continue;
            }

            listed.Add(WorktreeFamilyRow.Of(
                row,
                isAnchor ? WorktreeFamilyRole.Parent : WorktreeFamilyRole.Child,
                WorkspaceCanonicalPath.SamePhysicalPath(row.RootPath, queriedRoot)));
        }

        return [.. listed
            .OrderBy(row => row.Role == WorktreeFamilyRole.Parent ? 0 : 1)
            .ThenBy(row => row.RootPath, StringComparer.Ordinal)];
    }

    // The checkout the family hangs on: git's answer when the tree can give one, the parent link the registry
    // recorded when it cannot (a separate-git-dir checkout names no main root in its files, and a linked tree
    // whose git dir vanished is exactly the row a listing gets opened to find), and the queried root itself for a
    // primary checkout, which is its own anchor. A path with neither git facts nor a row anchors nothing, and the
    // caller says that rather than guessing a family out of the directories around it.
    private static string? FamilyAnchor(
        WorktreeFacts? facts,
        IReadOnlyList<WorkspaceRegistryEntry> rows,
        string queriedRoot)
    {
        if (facts is null)
        {
            return rows.Any(row => WorkspaceCanonicalPath.SamePhysicalPath(row.RootPath, queriedRoot))
                ? queriedRoot
                : null;
        }

        if (!facts.IsLinked)
        {
            return queriedRoot;
        }

        if (facts.MainWorktreeRoot is { } mainRoot)
        {
            return WorkspaceCanonicalPath.ResolveThroughLinks(mainRoot);
        }

        return rows
            .Where(row => WorkspaceCanonicalPath.SamePhysicalPath(row.RootPath, queriedRoot))
            .Select(row => row.ParentRootPath)
            .FirstOrDefault(link => link is not null)
            ?? queriedRoot;
    }
}
