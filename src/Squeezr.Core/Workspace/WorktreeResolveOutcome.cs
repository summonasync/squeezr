using Squeezr.Core.Storage;

namespace Squeezr.Core.Workspace;

// The answer one path gets. Everything on it is read: git's own files for the checkout, the registry for the
// rows that name it, and each row's manifest for what those rows claim about themselves. A field stays null when
// the files cannot say it rather than guessing — an answer that fills a gap with a plausible value is the
// cross-tree lie this feature was opened to close.
//
// Rows is a list because one physical checkout can carry several registry rows spelled different ways (the
// directory and a link pointing at it). Resolve shows all of them: it does not pick a favourite spelling and
// hide the rest, and it does not mint the missing one either — that half belongs to `worktree init`.
public sealed record WorktreeResolveOutcome(
    WorktreeResolveState State,
    string Path,
    string PhysicalPath,
    string? RootPath,
    string? Branch,
    bool IsLinked,
    bool IsDetached,
    string? ParentRootPath,
    bool ParentRegistered,
    IReadOnlyList<WorktreeResolvedRow> Rows,
    int DependentLanes,
    string? Reason)
{
    // Each row's store answers for itself: two spellings of one tree that disagree about their reconcile record
    // are reported as two records, never averaged into one verdict.
    public IReadOnlyList<WorktreeRecord> Records =>
        [.. Rows.Where(row => row.Record is not null).Select(row => row.Record!)];

    // A path that is a file, or a directory no git marker covers, has no checkout to describe; only the sentence
    // that says which of the two it is.
    public static WorktreeResolveOutcome Of(
        WorktreeResolveState state,
        string path,
        string physicalPath,
        string? reason) =>
        new(state, path, physicalPath, null, null, false, false, null, false, [], 0, reason);

    public static WorktreeResolveOutcome Of(
        WorktreeResolveState state,
        string path,
        string physicalPath,
        WorktreeFacts? facts,
        string? parentRootPath,
        bool parentRegistered,
        IReadOnlyList<WorktreeResolvedRow> rows,
        int dependentLanes,
        string? reason) =>
        new(
            state,
            path,
            physicalPath,
            facts?.Toplevel,
            facts?.Branch,
            facts?.IsLinked ?? false,
            facts?.IsDetached ?? false,
            parentRootPath,
            parentRegistered,
            rows,
            dependentLanes,
            reason);
}
