namespace Squeezr.Core.Storage;

// What one store copy actually moved, in the numbers the caller can print. Every field is measured, not
// claimed: a run that found nothing to copy reports zeros instead of success, and the target numbers come
// from the installed files, so the report describes the store the worktree will read rather than the
// staging temp it never opens. Unowned repos are the rows the copied database holds that the lane
// manifest could not be made to answer for — the state where bytes exist and no query reaches them.
public sealed record WorkspaceStoreCopyResult(
    DatabaseCopyStats Database,
    TrigramCopyStats Trigram,
    IReadOnlyList<string> OwnedRepos,
    IReadOnlyList<string> UnownedRepos);
