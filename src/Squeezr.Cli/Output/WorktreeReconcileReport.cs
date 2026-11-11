using System.Text.Json.Serialization;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Output;

// The reconcile block on the wire. 'passRan' is the field a session-start reader needs first: it separates
// the lanes where this verb really rescanned the tree from the ones where it looked and owed nothing, and a
// reader that cannot tell those apart will read the numbers as new work every session.
//
// 'obligation' is this store's own claim — at least one chunk it calls embedded — and 'collectionPoints' is
// what this worktree's collection answered against it. Points stay absent when nothing answered: an
// unanswered count is not a zero, and 'code' names which pending shape that is.
public sealed record WorktreeReconcileReport(
    string State,
    bool PassRan,
    bool Obligation,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Reason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Code,
    int StoredFiles,
    int StoredChunks,
    int PendingChunks,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? CollectionPoints,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? RescannedFiles,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Added,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Modified,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Deleted,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Skipped,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Reembedded)
{
    public static WorktreeReconcileReport From(WorktreeReconcileOutcome outcome) => new(
        State: outcome.State.ToString(),
        PassRan: outcome.PassRan,
        Obligation: outcome.Obligation,
        Reason: outcome.Reason,
        Code: outcome.Code,
        StoredFiles: outcome.StoredFiles,
        StoredChunks: outcome.StoredChunks,
        PendingChunks: outcome.PendingChunks,
        CollectionPoints: outcome.CollectionPoints,
        RescannedFiles: outcome.RescannedFiles,
        Added: outcome.Added,
        Modified: outcome.Modified,
        Deleted: outcome.Deleted,
        Skipped: outcome.Skipped,
        Reembedded: outcome.Reembedded);
}
