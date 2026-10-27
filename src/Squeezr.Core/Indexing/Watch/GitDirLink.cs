namespace Squeezr.Core.Indexing.Watch;

// A linked worktree's .git is a file naming the real gitdir, which lives outside the worktree
// (usually <main>/.git/worktrees/<name>): a commit writes index/HEAD/logs there, so nothing under
// the watched root moves. Resolving the pointer is what lets the watcher follow git state to where
// it actually is; a dangling or malformed pointer degrades to no link, never an error.
internal static class GitDirLink
{
    public static string? Resolve(string repoRoot)
    {
        var dotGit = Path.Combine(repoRoot, ".git");
        if (!File.Exists(dotGit))
        {
            return null;
        }

        string content;
        try
        {
            content = File.ReadAllText(dotGit);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var pointer = content
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.StartsWith("gitdir:", StringComparison.Ordinal));
        if (pointer is null)
        {
            return null;
        }

        var target = pointer["gitdir:".Length..].Trim();
        if (target.Length == 0)
        {
            return null;
        }

        string gitdir;
        try
        {
            gitdir = Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(repoRoot, target));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }

        return Directory.Exists(gitdir) ? gitdir : null;
    }
}
