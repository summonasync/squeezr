using System.Text.Json.Serialization;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Output;

// The family the queried checkout belongs to, with the two counts a reader scans the table for: how many rows the
// family carries and how many of their checkouts are still on disk. The rows are the registry's own columns —
// role, store key, name, root, kind, branch, tree presence, and which row this path resolves to — because this
// listing's subject is what was recorded, and re-deriving it one layer up would give the same fact a second owner.
//
// An empty table with a reason is a complete answer: the listing reports the family the registry knows, and a
// checkout nobody registered has one with no rows in it.
public sealed record WorktreeListReport(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? FamilyRootPath,
    string? QueriedRootPath,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? QueriedBranch,
    int RowCount,
    int RowsOnDisk,
    IReadOnlyList<WorktreeFamilyRow> Rows,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Reason)
{
    public static WorktreeListReport From(WorktreeFamilyOutcome outcome) =>
        new(
            outcome.FamilyRootPath,
            outcome.QueriedRootPath,
            outcome.QueriedBranch,
            outcome.Rows.Count,
            outcome.RowsOnDisk,
            outcome.Rows,
            outcome.Reason);
}
