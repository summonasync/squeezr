namespace Squeezr.Core.Workspace;

// The family a queried checkout belongs to, as the registry knows it: one main root and the rows that name it.
// The family is bounded by the queried tree on purpose — this card's anti-scope says no fleet view of lanes, and
// the machine-wide sweep already has its own door.
//
// FamilyRootPath is null when nothing anchors a family here: a directory that is no checkout and names no row.
// RowsOnDisk counts the checkouts that still exist, because the question a family list gets asked is usually 'how
// many of these are still alive'. An empty table with a reason is a complete answer, not an error.
public sealed record WorktreeFamilyOutcome(
    string? FamilyRootPath,
    string? QueriedRootPath,
    string? QueriedBranch,
    IReadOnlyList<WorktreeFamilyRow> Rows,
    string? Reason)
{
    public int RowsOnDisk => Rows.Count(row => row.TreePresent);

    public static WorktreeFamilyOutcome Empty(string? queriedRootPath, string? reason) =>
        new(null, queriedRootPath, null, [], reason);
}
