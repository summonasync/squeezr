using Squeezr.Core.Storage;

namespace Squeezr.Cli.Output;

// Everything one store copy is worth reporting: the three numbers per file, and the two lists that say
// whether the copied rows are actually owned here. A fill that lands rows no manifest entry answers for is
// a store nothing can query, so 'ownedRepos' is the line a reader checks and 'unownedRepos' is the sentence
// naming the row this worktree cannot answer for and the command that fixes it.
public sealed record WorktreeCopyReport(
    WorktreeDatabaseCopyReport Database,
    WorktreeTrigramCopyReport Trigram,
    IReadOnlyList<string> OwnedRepos,
    IReadOnlyList<string> UnownedRepos)
{
    public static WorktreeCopyReport From(WorkspaceStoreCopyResult copy) => new(
        Database: WorktreeDatabaseCopyReport.From(copy.Database),
        Trigram: WorktreeTrigramCopyReport.From(copy.Trigram),
        OwnedRepos: copy.OwnedRepos,
        UnownedRepos: copy.UnownedRepos);
}
