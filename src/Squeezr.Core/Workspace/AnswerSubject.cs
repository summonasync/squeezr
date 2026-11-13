using Squeezr.Core.Storage;

namespace Squeezr.Core.Workspace;

// Which tree an answer describes, and whether a reader could mistake it for another one. This feature was opened
// because a lane checkout was served the PARENT's index and said nothing about it, so the fact has to come out of
// the answer rather than stay in the caller's memory of what it asked for.
//
// Two readings make the block owed, and both are measured here rather than assumed:
//   - LINKED LANE — the answering checkout is a linked worktree, even when the caller stands inside it. A lane's
//     coordinates are branch coordinates, and naming them is what makes an answer safe to quote elsewhere.
//   - CROSS-TREE — the caller stands in a DIFFERENT checkout than the one answering: pointing a query at another
//     store on purpose, or the accident this feature exists for (a session inside a lane served by the parent).
//
// The door is conservative by design. A caller that is standing in no checkout at all — a scratch directory, a
// plain folder workspace — earns no subject, because there is no second tree to be mistaken for this one, and an
// invented subject is the cross-tree lie wearing a label. That same reading is what `serve' already says out loud
// as a session-outside-workspace warning; this door does not repeat it as a fact about the answer.
//
// A main worktree answering a caller inside its own tree is the single-root case and answers null: an old
// consumer reads exactly the fields it read before (035), and the common path pays one stat for that decision.
public sealed record AnswerSubject(
    string RootPath,
    string? Branch,
    string? StoreKey,
    string? CallerRootPath,
    bool CrossTree)
{
    public static AnswerSubject? Describe(string answeringRoot, string callerDirectory)
    {
        // A .git that is a FILE is git's own pointer to another admin dir: one stat, and it answers "is this
        // checkout linked" without reading anything else. An unlinked answering root still owes the naming when
        // the caller's checkout is elsewhere, which is the only other reading above.
        var linkedLane = File.Exists(Path.Combine(answeringRoot, ".git"));
        var callerRoot = linkedLane ? null : WorktreeFacts.Resolve(callerDirectory)?.Toplevel;
        if (!linkedLane && (callerRoot is null || WorkspaceCanonicalPath.SamePhysicalPath(callerRoot, answeringRoot)))
        {
            return null;
        }

        var answeringFacts = WorktreeFacts.Resolve(answeringRoot);
        var callerFacts = WorktreeFacts.Resolve(callerDirectory);
        var callerTree = callerFacts?.Toplevel ?? callerRoot;
        var crossTree = callerTree is not null
            && !WorkspaceCanonicalPath.SamePhysicalPath(callerTree, answeringRoot);

        if (!crossTree && answeringFacts is not { IsLinked: true })
        {
            return null;
        }

        return new(
            answeringFacts?.Toplevel ?? WorkspaceCanonicalPath.Canonicalize(answeringRoot),
            answeringFacts?.Branch,
            StoreKeyOf(answeringRoot),
            callerTree is null
                ? null
                : WorkspaceCanonicalPath.Canonicalize(callerTree),
            crossTree);
    }

    // The key is read off the row that names this checkout, never off the caller's spelling of it: the same tree
    // reached through a link is the same store, and a subject quoting a spelling would name nothing.
    private static string? StoreKeyOf(string root) =>
        (WorkspaceRegistry.FindByRootPath(root) ?? WorkspaceRegistry.FindByAnyRootSpelling(root))?.StoreKey;
}
