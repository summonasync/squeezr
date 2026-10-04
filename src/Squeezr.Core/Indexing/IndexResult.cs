namespace Squeezr.Core.Indexing;

// DeferredReaps/PendingReaps are the machine-readable half of "no silent orphaning" (059): the first says
// how many files this pass pushed into the reap journal, the second is what the journal still holds after
// the pass. Additive ints, declared in CHANGELOG per 035.
public sealed record IndexResult(
    IReadOnlyList<RepoIndexResult> Repos,
    DateTime IndexedAt,
    IReadOnlyList<string> Warnings,
    int DeferredReaps = 0,
    int PendingReaps = 0);
