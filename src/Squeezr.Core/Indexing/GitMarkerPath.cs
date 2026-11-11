namespace Squeezr.Core.Indexing;

// A linked worktree keeps '.git' as a FILE naming the real gitdir, so its one line is an absolute path into
// the main worktree — exactly the coordinate a lane must never answer with. The walk and the per-file route
// are two doors into one store, so they ask this one predicate: the second copy of a rule is the leak.
public static class GitMarkerPath
{
    public static bool Is(string path) =>
        string.Equals(Path.GetFileName(path), ".git", StringComparison.OrdinalIgnoreCase);
}
