namespace Squeezr.Core.Indexing;

// DeferredReaps/PendingReaps are the machine-readable half of "no silent orphaning" (059): the first says
// how many files this pass pushed into the reap journal, the second is what the journal still holds after
// the pass. GuardWithheld/GuardWithheldBuiltIns/GuardDropped are the secret guard's ingestion tally (064):
// paths refused entry, the half of those the built-in masks decide alone, and stored rows the same decision
// removed. Counts only — a withheld path is never named. Throttled sums the per-repo session-retry holds:
// files deferred into the next pass, not skipped. Additive ints, declared in CHANGELOG per 035.
public sealed record IndexResult(
    IReadOnlyList<RepoIndexResult> Repos,
    DateTime IndexedAt,
    IReadOnlyList<string> Warnings,
    int DeferredReaps = 0,
    int PendingReaps = 0,
    int GuardWithheld = 0,
    int GuardWithheldBuiltIns = 0,
    int GuardDropped = 0,
    int Embedded = 0,
    bool EmbeddingsCompleted = true,
    int Throttled = 0);
