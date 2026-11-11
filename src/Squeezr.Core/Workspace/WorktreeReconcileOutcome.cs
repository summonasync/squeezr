namespace Squeezr.Core.Workspace;

// The reconcile in numbers: what the pass rescanned, what it re-embedded, and the two counts the threshold is
// made of — the chunks this store calls embedded (its own claim) and the points this worktree's collection
// hands back. The pass numbers are null when no pass ran, because a lane that owed nothing has no rescan to
// report; the store counts are read back off the installed database, never carried over from the copy.
//
// 'Obligation' is the claim in one word: this store calls at least one chunk embedded. It is what makes
// 'collectionPoints' mean something, and a reader that has it can tell 'the collection answered zero' from
// 'there was nothing to answer about'. 'Code' says WHICH pending shape this is for the pass that records it
// without reading prose, the contract the vector arm already keeps.
public sealed record WorktreeReconcileOutcome(
    WorktreeReconcileState State,
    bool PassRan,
    string? Reason,
    string? Code,
    int StoredFiles,
    int StoredChunks,
    int PendingChunks,
    bool Obligation,
    long? CollectionPoints,
    int? RescannedFiles = null,
    int? Added = null,
    int? Modified = null,
    int? Deleted = null,
    int? Skipped = null,
    int? Reembedded = null,
    IReadOnlyList<string>? Warnings = null)
{
    public const string PassFailedCode = "worktree_reconcile_pass_failed";
    public const string CollectionShortCode = "worktree_reconcile_collection_short";
    public const string RowsPendingCode = "worktree_reconcile_rows_pending";
    public const string StoreSilentCode = "worktree_reconcile_store_silent";

    public static WorktreeReconcileOutcome Deferred(string reason) =>
        new(WorktreeReconcileState.Deferred, false, reason, null, 0, 0, 0, false, null);

    // A store whose database will not open has no rows to measure and is not empty either: it is the state
    // where this worktree has bytes and no answer, and the reconcile that cannot read them says so rather
    // than printing a clean bill over an unreadable store.
    public static WorktreeReconcileOutcome UnreadableStore(string root) =>
        new(
            WorktreeReconcileState.Pending,
            PassRan: false,
            $"this worktree's meta.db would not open, so neither its rows nor its collection could be read. " +
            $"Run '{RemedyText.IndexCommand}' here, or 'squeezr workspace repair' if the store is the broken " +
            "half.",
            StoreSilentCode,
            0,
            0,
            0,
            false,
            null);

    public IReadOnlyList<string> PassWarnings => Warnings ?? [];
}
