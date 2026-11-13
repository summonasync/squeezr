using System.Text.Json.Serialization;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Output;

// The reconcile block on the wire. 'passRan' is the field a session-start reader needs first: it separates
// the lanes where this verb really rescanned the tree from the ones where it looked and owed nothing, and a
// reader that cannot tell those apart will read the numbers as new work every session.
//
// 'obligation' is this store's own claim — at least one chunk it calls embedded — counted in 'obligationChunks'
// as the distinct chunk hashes behind that claim, because a point answers a hash, not a row. 'coveredChunks'
// is how many of those hashes the collection actually holds a point for, and 'collectionPoints' is what the
// collection answered in total. Points stay absent when nothing answered: an unanswered count is not a zero,
// and 'code' names which shape that is.
public sealed record WorktreeReconcileReport(
    string State,
    bool PassRan,
    bool Obligation,
    int ObligationChunks,
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
    int? CoveredChunks,
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
    int? Reembedded,
    // Where the verdict came from: 'pass' measured it this session, 'record' read the store's own closed
    // proof and attested it against a sample of ids. 'probedIds' is how many ids that attestation asked for,
    // so a reader can see the size of what a healthy start paid instead of a full walk.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Basis = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? ProbedIds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    WorktreeRecord? Record = null,
    // The class of the refusal, measured at the open rather than inferred from a null, for the store whose
    // meta.db would not answer at all (review-116-12 W-2).
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? StoreFailure = null)
{
    public static WorktreeReconcileReport From(WorktreeReconcileOutcome outcome) => new(
        State: outcome.State.ToString(),
        PassRan: outcome.PassRan,
        Obligation: outcome.Obligation,
        ObligationChunks: outcome.ObligationChunks,
        Reason: outcome.Reason,
        Code: outcome.Code,
        StoredFiles: outcome.StoredFiles,
        StoredChunks: outcome.StoredChunks,
        PendingChunks: outcome.PendingChunks,
        CollectionPoints: outcome.CollectionPoints,
        CoveredChunks: outcome.CoveredChunks,
        RescannedFiles: outcome.RescannedFiles,
        Added: outcome.Added,
        Modified: outcome.Modified,
        Deleted: outcome.Deleted,
        Skipped: outcome.Skipped,
        Reembedded: outcome.Reembedded,
        Basis: outcome.Basis == "none" ? null : outcome.Basis,
        ProbedIds: outcome.ProbedIds,
        Record: outcome.Record,
        StoreFailure: outcome.StoreFailure?.ToString());
}
