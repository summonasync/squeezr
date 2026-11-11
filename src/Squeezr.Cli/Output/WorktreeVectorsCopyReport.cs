using System.Text.Json.Serialization;
using Squeezr.Core.Storage;

namespace Squeezr.Cli.Output;

// The semantic arm of a store copy, on the wire. Three point counts: what the source held before anything
// moved, what this pass handed over, and what the target counts afterwards — the last re-read off the target
// collection, the first measured off a collection this pass does not own. Only the target number is a
// contract; a disagreement between it and the copied count fails the command. 'copied: false' is the
// reported-not-thrown shape, and the shapes that carry it are told apart by 'code' so the pass that has to
// record this lane's state never reads prose: 'worktree_vector_drift' says the source pair disagrees, and
// 'sourcePointsAfterWalk' is the second half of that pair; 'vector_point_foreign' says the source held points
// this product did not write, counted by 'foreignPoints'; 'storage_unavailable' says the store was not in the
// room at all.
public sealed record WorktreeVectorsCopyReport(
    bool Copied,
    long SourcePoints,
    long CopiedPoints,
    long TargetPoints,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Reason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? SourcePointsAfterWalk,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? ForeignPoints,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Code)
{
    public static WorktreeVectorsCopyReport From(WorktreeVectorCopyStats stats) => new(
        Copied: stats.Copied,
        SourcePoints: stats.SourcePoints,
        CopiedPoints: stats.CopiedPoints,
        TargetPoints: stats.TargetPoints,
        Reason: stats.Reason,
        SourcePointsAfterWalk: stats.SourcePointsAfterWalk,
        ForeignPoints: stats.ForeignPoints,
        Code: stats.Code);
}
